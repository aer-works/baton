#!/usr/bin/env python3
"""#1691: measure billed-token RATE across the real room corpus in ~/.baton/rooms.

Why this exists
---------------
#1691 proposed arresting a runaway lane on billed tokens per unit time rather than on a total, on the
strength of three rooms. This script is the instrument that answers whether any such threshold
actually separates a runaway from normal traffic, over EVERY room on the machine rather than three.
Its answer, as of the sweep recorded in spec/baton.md SS3, is no. Re-run it rather than trusting that
paragraph: the corpus grows, and the numbers move with it.

    python tools/room-rate-sweep/sweep.py --sweep
    python tools/room-rate-sweep/sweep.py --sweep --window 2 --offsets duration
    python tools/room-rate-sweep/sweep.py --emit-fixture tests/Baton.Tests/Fixtures/billed-rate-rooms.json

What it measures, and what it assumes
-------------------------------------
Billed tokens are #1682's accounting, unchanged and deliberately not re-derived here: per usage line,
input + output + cache_creation, deduped by message.id on claude (agy lines carry no id). cache_read
is excluded -- spec/baton.md SS3 has the reason.

Time is the problem, and spec/baton.md SS3 states the vendor asymmetry behind it (as well as correcting
an earlier revision of this file, which claimed agy carries no time field at all -- it carries
`duration_seconds`, per-step elapsed rather than wall-clock). Per-line offsets come from one of three
places:

  * claude rooms          -> MEASURED, the line's own `timestamp`.
  * agy, `--offsets uniform` (default)
                          -> RECONSTRUCTED by spreading the room's usage lines uniformly across its
                             measured executionStarted..executionExited span from flow.jsonl.
  * agy, `--offsets duration`
                          -> RECONSTRUCTED by the running cumulative sum of every step's
                             `duration_seconds`, rescaled to the measured span. Closer to the truth on a
                             room whose steps run strictly back to back (over `38c24d11` the raw sum is
                             686.4s against a 698.9s measured span, a 98% match), and WRONG in a
                             different direction on a room with overlapping/backgrounded steps, where
                             the raw sum can exceed the span severalfold -- which is why it rescales
                             rather than being used raw, and why it is offered as a CROSS-CHECK on the
                             uniform reconstruction rather than as a replacement for it.

Neither agy reconstruction is authoritative. What makes the #1691 conclusion safe is that it does not
rest on either: `--sweep`'s billed-per-minute column is total / measured span, exact on both vendors
with no reconstruction at all, and the separation question is answered there.
"""

import argparse
import glob
import json
import os
import sys
from datetime import datetime, timedelta

ROOMS = os.path.join(os.path.expanduser("~"), ".baton", "rooms")

# The window --billed-rate-limit is stated in. Mirrors Baton.Mutation.TokenBudgetMonitor's own
# BilledRateWindow; that C# constant is the one the engine enforces, this is the analysis copy.
WINDOW = timedelta(minutes=5)


def _parse_time(raw):
    return datetime.fromisoformat(raw.replace("Z", "+00:00"))


def room_span(room_dir):
    """(started, exited, exit_reason, cancelled, produced_work, executions) from the room's flow.jsonl.

    `produced_work` is the one that matters, and the one an earlier revision of this script got wrong
    (#1707 review) -- spec/baton.md SS3 states why the weaker exit-reason test is not enough. Here it is
    True only for a room journalling at least one `executionSucceeded` and no `executionFailed`.
    """
    path = os.path.join(room_dir, "flow.jsonl")
    if not os.path.exists(path):
        return None
    started = exited = reason = None
    cancelled = False
    succeeded = failed = executions = 0
    with open(path, encoding="utf-8", errors="replace") as handle:
        for line in handle:
            try:
                record = json.loads(line)
            except ValueError:
                continue
            event = record.get("Event") or {}
            kind = event.get("eventType")
            if kind == "executionStarted":
                executions += 1
                if started is None:
                    started = _parse_time(record["WriterUtcTimestamp"])
            elif kind == "executionExited":
                exited = _parse_time(record["WriterUtcTimestamp"])
                reason = event.get("Reason")
            elif kind in ("cancellationRequested", "executionCancelled"):
                cancelled = True
            elif kind == "executionSucceeded":
                succeeded += 1
            elif kind == "executionFailed":
                failed += 1
    if started is None or exited is None:
        return None
    return started, exited, reason, cancelled, succeeded > 0 and failed == 0, executions


def billed_samples(stdout_log, span, offsets="uniform"):
    """(vendor, [(offset_seconds, input, output, cache_creation), ...]) using #1682's accounting.

    `offsets` selects the agy reconstruction ('uniform' or 'duration'); it is ignored on claude, whose
    offsets are measured either way. The module docstring has what each one assumes.
    """
    started = span[0]
    claude = []
    agy = []
    elapsed_before = []
    running_elapsed = 0.0
    seen_ids = set()
    vendor = None
    with open(stdout_log, encoding="utf-8", errors="replace") as handle:
        for line in handle:
            line = line.strip()
            if not line:
                continue
            try:
                record = json.loads(line)
            except ValueError:
                continue
            if record.get("type") == "assistant":
                vendor = "claude"
                message = record.get("message") or {}
                usage = message.get("usage")
                if not isinstance(usage, dict):
                    continue
                if not any(k in usage for k in (
                        "input_tokens", "output_tokens",
                        "cache_read_input_tokens", "cache_creation_input_tokens")):
                    continue
                message_id = message.get("id")
                if isinstance(message_id, str) and message_id:
                    # #1686 review F6/F4: repeated ids carry an IDENTICAL usage object (verified over
                    # these captures), so first sighting wins, exactly as the monitor does it.
                    if message_id in seen_ids:
                        continue
                    seen_ids.add(message_id)
                stamp = record.get("timestamp")
                if not stamp:
                    continue
                claude.append((
                    (_parse_time(stamp) - started).total_seconds(),
                    usage.get("input_tokens") or 0,
                    usage.get("output_tokens") or 0,
                    usage.get("cache_creation_input_tokens") or 0,
                ))
                continue
            step = record.get("step_update")
            if isinstance(step, dict):
                vendor = "agy"
                # Every DONE step carries `duration_seconds` -- its own elapsed time, not a wall-clock
                # stamp. Accumulated across ALL steps (tool steps included, since they consume wall
                # clock too) so a usage line's position reflects the work that preceded it.
                if isinstance(step.get("duration_seconds"), (int, float)):
                    running_elapsed += float(step["duration_seconds"])
                if (step.get("state") == "DONE"
                        and step.get("step_type") == "agent_response"
                        and isinstance(step.get("usage"), dict)):
                    usage = step["usage"]
                    agy.append((
                        usage.get("input_tokens") or 0,
                        usage.get("output_tokens") or 0,
                        usage.get("cache_creation_input_tokens") or 0,
                    ))
                    elapsed_before.append(running_elapsed)
    if vendor == "agy":
        duration = (span[1] - span[0]).total_seconds()
        count = len(agy)
        if count == 0:
            return vendor, []
        # RECONSTRUCTED either way, see module docstring.
        if offsets == "duration" and elapsed_before and elapsed_before[-1] > 0:
            # Rescaled onto the measured span: the raw cumulative sum matches it closely on a room
            # whose steps run back to back and overshoots severalfold on one with overlapping steps,
            # so the SHAPE is what this method contributes, never the absolute elapsed figure.
            scale = duration / elapsed_before[-1]
            return vendor, [(elapsed_before[i] * scale, a, b, c) for i, (a, b, c) in enumerate(agy)]
        return vendor, [(duration * (i + 1) / count, a, b, c) for i, (a, b, c) in enumerate(agy)]
    return vendor, claude


def peak_window(samples, window=WINDOW):
    """Largest sum of billed inside any trailing `window`."""
    width = window.total_seconds()
    best = running = 0
    oldest = 0
    for index, sample in enumerate(samples):
        running += sample[1] + sample[2] + sample[3]
        while samples[oldest][0] < sample[0] - width:
            older = samples[oldest]
            running -= older[1] + older[2] + older[3]
            oldest += 1
        best = max(best, running)
    return best


def scan(role_prefix, window=WINDOW, offsets="uniform"):
    rows = []
    for name in sorted(os.listdir(ROOMS)):
        if not name.startswith(role_prefix):
            continue
        room_dir = os.path.join(ROOMS, name)
        span = room_span(room_dir)
        if span is None:
            continue
        # Sorted, not glob order (#1707 review): a multi-execution room has several logs and the
        # unordered pick was nondeterministic. `executions` is reported alongside so a reader can see
        # when the total (one execution) and the span (all of them) disagree -- the rate is understated
        # for those rooms, which is why produced_work below matters more than the rate for them.
        logs = sorted(p for p in glob.glob(
            os.path.join(room_dir, "artifacts", "execution_*", ".stdout.log")) if os.path.getsize(p) > 0)
        if not logs:
            continue
        vendor, samples = billed_samples(logs[0], span, offsets)
        if not samples:
            continue
        total = sum(s[1] + s[2] + s[3] for s in samples)
        minutes = (span[1] - span[0]).total_seconds() / 60
        rows.append({
            "room": name,
            "vendor": vendor,
            "total": total,
            "minutes": round(minutes, 2),
            "per_minute": round(total / minutes) if minutes else 0,
            "peak_window": peak_window(samples, window),
            "samples": len(samples),
            "reason": span[2],
            "cancelled": span[3],
            "produced_work": span[4],
            "executions": span[5],
        })
    return rows


def separation_rows(rows, reference_room):
    """The rooms that refute a rate threshold: faster than the reference AND they produced their work.

    Both halves are load-bearing. Faster-and-failed says nothing (a failing lane may deserve arrest);
    produced-work-and-slower says nothing either. Only a room that burned faster than the reference and
    still delivered proves a limit catching the reference would have killed real work.
    """
    reference = next((r for r in rows if reference_room in r["room"]), None)
    if reference is None:
        return None, []
    faster = [r for r in rows if r["per_minute"] > reference["per_minute"] and r["produced_work"]]
    return reference, faster


def command_sweep(args):
    window = timedelta(minutes=args.window)
    rows = scan(args.role_prefix, window, args.offsets)
    rows.sort(key=lambda r: r["per_minute"], reverse=True)
    print("role prefix: %s   window: %g min   agy offsets: %s   rooms swept: %d"
          % (args.role_prefix, args.window, args.offsets, len(rows)))
    print("%-8s %9s %9s %10s %7s %5s %-16s %-6s %-5s %s"
          % ("vendor", "tok/min", "peakWin", "total", "min", "n", "reason", "canc", "work", "room"))
    for row in rows:
        print("%-8s %9d %9d %10d %7.1f %5d %-16s %-6s %-5s %s" % (
            row["vendor"], row["per_minute"], row["peak_window"], row["total"],
            row["minutes"], row["samples"], row["reason"], row["cancelled"],
            "yes" if row["produced_work"] else "NO", row["room"]))

    reference, faster = separation_rows(rows, args.reference_room)
    if reference is None:
        return 0
    delivered = [r for r in rows if r["produced_work"]]
    print("\nreference room %s burns %d billed tokens/min." % (args.reference_room, reference["per_minute"]))
    print("Rooms that PRODUCED THEIR WORK (>=1 executionSucceeded, 0 executionFailed) while burning "
          "faster: %d of the %d such rooms swept (%d rooms swept in all)."
          % (len(faster), len(delivered), len(rows)))
    for row in faster:
        print("   %-8s %s  %d tok/min (%.2fx the reference)"
              % (row["vendor"], row["room"], row["per_minute"], row["per_minute"] / reference["per_minute"]))
    if faster:
        print("\nNo billed-rate threshold separates the reference room from traffic that delivered: any "
              "limit low enough to arrest it also fires on each room listed above. Arrest forecloses "
              "retry (spec/baton.md SS3). Re-run with --window/--offsets to check that this does not "
              "turn on either choice.")
    return 0


def command_emit_fixture(args):
    payload = {
        "_comment": (
            "#1691 replay fixture, generated by tools/room-rate-sweep/sweep.py --emit-fixture. "
            "One entry per room: billed usage samples in emitted order as "
            "[offsetSeconds, inputTokens, outputTokens, cacheCreationTokens]. claude offsets are the "
            "line's own `timestamp` (MEASURED); agy offsets are RECONSTRUCTED -- agy stamps no "
            "wall-clock time on any line, only a per-step `duration_seconds` -- see the script's "
            "docstring for the two reconstruction methods and what each assumes. Repeated claude "
            "message.ids are collapsed to their first sighting here, the same rule "
            "TokenBudgetMonitor applies -- verified against these captures to carry an identical "
            "usage object per repeat. `separation` is the corpus-wide answer to whether any rate "
            "threshold exists, captured here so a test can read the measurement rather than restate "
            "it. Regenerate rather than hand-editing."),
        "rooms": {},
    }
    for name in args.rooms:
        matches = [d for d in sorted(os.listdir(ROOMS)) if name in d]
        if not matches:
            print("no room matching %r" % name, file=sys.stderr)
            return 1
        room_dir = os.path.join(ROOMS, matches[0])
        span = room_span(room_dir)
        logs = sorted(p for p in glob.glob(
            os.path.join(room_dir, "artifacts", "execution_*", ".stdout.log")) if os.path.getsize(p) > 0)
        vendor, samples = billed_samples(logs[0], span, args.offsets)
        payload["rooms"][matches[0]] = {
            "vendor": vendor,
            "offsetsAreMeasured": vendor == "claude",
            "durationSeconds": round((span[1] - span[0]).total_seconds(), 3),
            "exitReason": span[2],
            "cancelled": span[3],
            "producedWork": span[4],
            "totalBilled": sum(s[1] + s[2] + s[3] for s in samples),
            "peakBilledIn5MinWindow": peak_window(samples),
            "samples": [[round(s[0], 3), s[1], s[2], s[3]] for s in samples],
        }

    # The separation measurement itself, so a test can assert over CAPTURED data instead of literals
    # retyped from a terminal (#1707 review: the first version of that test compared eight hardcoded
    # doubles and could not fail).
    rows = scan(args.role_prefix, WINDOW, args.offsets)
    reference, faster = separation_rows(rows, args.reference_room)
    if reference is not None:
        payload["separation"] = {
            "_comment": (
                "Billed tokens per minute = total / measured executionStarted..executionExited span. "
                "EXACT on both vendors -- no reconstruction is involved in this block, which is why "
                "the #1691 conclusion rests on it. `fasterAndDelivered` lists every swept room that "
                "burned faster than the reference AND produced its work (>=1 executionSucceeded, 0 "
                "executionFailed)."),
            "rolePrefix": args.role_prefix,
            "referenceRoom": reference["room"],
            "referenceTokensPerMinute": reference["per_minute"],
            "roomsSwept": len(rows),
            "roomsThatDelivered": sum(1 for r in rows if r["produced_work"]),
            "fasterAndDelivered": [
                {"room": r["room"], "vendor": r["vendor"], "tokensPerMinute": r["per_minute"],
                 "totalBilled": r["total"], "minutes": r["minutes"]}
                for r in sorted(faster, key=lambda r: r["per_minute"], reverse=True)
            ],
        }
    with open(args.emit_fixture, "w", encoding="utf-8", newline="\n") as handle:
        json.dump(payload, handle, indent=2)
        handle.write("\n")
    print("wrote %s (%d rooms)" % (args.emit_fixture, len(payload["rooms"])))
    return 0


def main(argv):
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--sweep", action="store_true", help="print the per-room rate table")
    parser.add_argument("--role-prefix", default="dispatch-implement",
                        help="which rooms to sweep (default: dispatch-implement)")
    parser.add_argument("--reference-room", default="38c24d11",
                        help="the room the separation question is asked about (default: #1691's runaway)")
    parser.add_argument("--emit-fixture", metavar="PATH",
                        help="write a replay fixture for --rooms to PATH")
    parser.add_argument("--rooms", nargs="*", default=[],
                        help="room name fragments to emit into the fixture")
    parser.add_argument("--window", type=float, default=WINDOW.total_seconds() / 60, metavar="MINUTES",
                        help="trailing window width for the peak column (default: 5, the width "
                             "--billed-rate-limit is stated in). Sweep it to check the separation "
                             "answer does not turn on this choice.")
    parser.add_argument("--offsets", choices=("uniform", "duration"), default="uniform",
                        help="how agy per-line offsets are reconstructed; ignored on claude, whose "
                             "offsets are measured. See the module docstring.")
    args = parser.parse_args(argv)

    if not os.path.isdir(ROOMS):
        print("no room corpus at %s -- nothing to sweep." % ROOMS, file=sys.stderr)
        return 2
    if args.emit_fixture:
        return command_emit_fixture(args)
    if args.sweep:
        return command_sweep(args)
    parser.print_help()
    return 2


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
