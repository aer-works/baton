"""Run every local gate, report one verdict, exit once.

WHY THIS EXISTS, and it is not convenience. Each gate below already reports correctly on its own.
The failure this removes is in how they get READ: a checker was run, its stdout filtered for a
success token, and the filtered text reported as green while the process exited 1. That has now
happened twice on this repo -- `audit-completeness` was reported passing 16/16 while exiting 1
because its output was filtered for OK/FAIL and its failure prefix is `!!`, and `audit-recordonce`
was reported as exit 0 from a stale shell variable while it was flagging 8 duplications.

Both times the gate worked and the reading of it did not. So this collapses the exit codes into
one: there is no per-gate status to sample, no shell variable to go stale between commands, and the
only thing worth reporting is this process's own exit code.

Run every gate even after one fails -- fail-fast hides the others, and a session that has to
re-run the whole set to discover the next problem starts filtering output again.
"""
import hashlib
import json
import os
import shutil
import subprocess
import sys
import tempfile
from datetime import datetime, timedelta, timezone

# Sequencing (#986): one full run used to build the .NET tree twice -- `lint` forces a full
# `--no-incremental` build and `test` then built it all again -- and every audit waited for both.
# Now the pure-file audits run DURING the build phase, and the test suite reuses lint's build
# (`test-no-build`; pixi.toml owns why that is safe under `gates` and exposed outside it).
#
# The split is deliberate, not stylistic. OVERLAP holds only gates that read files and run python:
# nothing that starts MSBuild, and nothing that touches the built Baton.Cli binary. `fmt-check`
# loads every project through MSBuild, and `audit-selfcheck`/`audit-controls` refresh a copy of
# the repo's built CLI (#717) -- overlapping either with `lint`'s build reintroduces the
# concurrent-MSBuild and torn-binary failures that MSBUILDDISABLENODEREUSE (#909) and the
# 2026-08-04 mutual-kill catalogue were paid for.
OVERLAP = [
    "audit-completeness",
    "audit-recordonce",
    "audit-staleness-ext-selftest",
    "audit-waitceiling",
    "audit-waitceiling-selftest",
    "audit-retiredphrases",
    "audit-retiredphrases-selftest",
    "audit-docsbudget",
    "audit-docsbudget-selftest",
    "audit-speccitations",
    "audit-speccitations-selftest",
    "audit-commentspecrefs",
    "audit-commentspecrefs-selftest",
    "audit-clitripwire",
    "audit-clitripwire-selftest",
    "flake-watch-selftest",
    # #1402: pure python against an isolated temp lock file -- starts no MSBuild and never touches
    # the real build lock, so it cannot interfere with the build phase it overlaps.
    "buildlock-selftest",
    # #1601: isolated sabotage suite (tools/gates/sabotage.py) overlapping the build.
    "gate-sabotage",
    # #1636: this file's own selftest -- a real temp git repo and `sh`, no MSBuild and no built
    # CLI, same shape as buildlock-selftest above. Was defined as a pixi task but never gated
    # anywhere; wired in now because it is what proves the gate-receipt logic below still
    # discriminates, not merely that it ran once at review time.
    "gates-selftest",
]

# The MSBuild owners, strictly sequential: one MSBuild at a time.
BUILD_PHASE = [
    "fmt-check",
    "lint",
]

# Sequential too, but only because they read the CLI binary `lint` writes -- they run after the
# build phase, once the overlapped audits have been joined. `baton-dispatch-selftest` belongs here for
# the same reason and used to sit in OVERLAP by mistake: `dispatch.py` loads the worker catalog from
# the built `Baton.Cli` binary AT IMPORT, so running it before `lint` produces that binary dies with
# "baton engine CLI binary not found ... Build it first". Overlapped, it raced the very build it depends
# on -- invisible everywhere a prior build had left the binary on disk, and a hard first-run FAIL in a
# fresh worktree, which is exactly the intermittent gate failure #1088 spent a session diagnosing.
AFTER_BUILD_FAST = [
    "audit-selfcheck",
    "audit-controls",
    "baton-dispatch-selftest",
    # #1487: the loud half of the drift grace window. Console.WriteLine here is inherited straight to
    # the gates output (run_gates -> pixi_runner), which a passing xunit test's ITestOutputHelper is
    # not -- dotnet test only prints a test's output when it fails, so this is the layer that can
    # actually make a fresh, still-within-grace drift visible without turning the run red.
    "vendor-check",
]

# The full run's test leg. `test-no-build` reuses the assemblies `lint` just built; if `lint`
# failed, the aggregate is already red, so a stale-assembly test result cannot turn a broken run
# green. Outside `gates`, use `pixi run test` (which force-rebuilds -- #688).
AFTER_BUILD_FULL = AFTER_BUILD_FAST + ["test-no-build"]

PASS_MARK = "GATES: PASS"
FAIL_MARK = "GATES: FAIL"

# Quiet mode (#1560): a dispatched worker that runs `gates` inherits ~2,500 tests' worth of stdout
# into its conversation context and then re-reads it on every subsequent model call -- one small
# renderer lane measured 1.25M input + 43.8M cache-read tokens, most of it this file's inherited
# output. Quiet mode drops PASSING gates' logs and prints a FAILING gate's output tail-bounded.
# This does not reintroduce the filtering the module docstring forbids: nothing here reads the
# text to DECIDE anything -- the verdict is still the exit code alone; quiet only changes how much
# of an already-decided gate's log gets echoed.
QUIET_FAIL_TAIL_LINES = 400


def emit_failure_output(name, data, tail_lines=QUIET_FAIL_TAIL_LINES):
    """Print a failing gate's captured output, tail-bounded, naming the rerun for the full log."""
    lines = data.splitlines(keepends=True)
    if len(lines) > tail_lines:
        print(f"  [{name}: {len(lines) - tail_lines} earlier line(s) elided -- "
              f"rerun `pixi run {name}` for the full log]", flush=True)
        lines = lines[-tail_lines:]
    sys.stdout.flush()
    sys.stdout.buffer.write(b"".join(lines))
    sys.stdout.buffer.flush()


def run_gates(names, runner):
    """Run each gate, print a per-gate line, return the names that failed."""
    failed = []
    for name in names:
        code = runner(name)
        print(f"  {'pass' if code == 0 else 'FAIL':>4}  {name}  (exit {code})", flush=True)
        if code != 0:
            failed.append(name)
    return failed


def join_gates(procs, quiet=False):
    """Join overlapped gates: re-print each one's output verbatim, return the names that failed.

    The re-print is byte-for-byte, no decode and no filter -- re-printing is where the filtering
    the module docstring describes creeps back in, so nothing here inspects the text. The verdict
    is the exit code alone. Under --quiet a passing gate's output is dropped and a failing
    gate's is tail-bounded (#1560); the exit-code contract is unchanged.
    """
    failed = []
    for name, proc in procs:
        out, _ = proc.communicate()
        code = proc.returncode
        if not quiet:
            sys.stdout.flush()
            sys.stdout.buffer.write(out)
            sys.stdout.buffer.flush()
        elif code != 0:
            emit_failure_output(name, out)
        print(f"  {'pass' if code == 0 else 'FAIL':>4}  {name}  (exit {code})", flush=True)
        if code != 0:
            failed.append(name)
    return failed


def summarise(names, failed):
    """The single line worth reading. Exit code, not this text, is the contract."""
    if failed:
        return f"{FAIL_MARK} {len(failed)} of {len(names)} -- {', '.join(failed)}"
    return f"{PASS_MARK} {len(names)} of {len(names)}"


def pixi_runner(name):
    # Output is inherited, not captured: a captured gate would have to be re-printed to be
    # readable, and re-printing is where the filtering that caused this file creeps back in. (The
    # overlapped audits are the deliberate exception; join_gates re-prints them raw.)
    return subprocess.run(["pixi", "run", name], check=False).returncode


def quiet_pixi_runner(name):
    # The --quiet counterpart (#1560): capture, and echo only a FAILING gate's output
    # (tail-bounded). The decision is still the exit code -- captured text is never inspected.
    proc = subprocess.run(["pixi", "run", name], check=False,
                          stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    if proc.returncode != 0:
        emit_failure_output(name, proc.stdout)
    return proc.returncode


def pixi_spawner(name):
    # stderr folded into stdout so the join-time re-print loses nothing a terminal would have
    # shown. An overlapped audit that outgrows the OS pipe buffer just blocks until join drains
    # it -- late, never lost.
    return subprocess.Popen(["pixi", "run", name], stdout=subprocess.PIPE, stderr=subprocess.STDOUT)


def run_all(after_build, spawner=pixi_spawner, runner=pixi_runner, quiet=False):
    """Overlapped audits start first, the build phase runs while they work, then everything joins."""
    procs = [(name, spawner(name)) for name in OVERLAP]
    failed = run_gates(BUILD_PHASE, runner)
    failed += join_gates(procs, quiet=quiet)
    failed += run_gates(after_build, runner)
    return OVERLAP + BUILD_PHASE + after_build, failed


RECEIPT_NAME = "baton-gate-receipt"
RECEIPT_MAX_AGE_S = 6 * 3600
RECEIPT_TIME_FORMAT = "%Y-%m-%dT%H:%M:%SZ"


def _git_dir(cwd=None):
    """The current tree's git dir -- a worktree's own, not the main checkout's (#1636)."""
    out = subprocess.run(["git", "rev-parse", "--git-dir"], cwd=cwd,
                         capture_output=True, text=True, check=True).stdout.strip()
    return out if os.path.isabs(out) else os.path.normpath(os.path.join(cwd or os.getcwd(), out))


def receipt_path(cwd=None):
    return os.path.join(_git_dir(cwd), RECEIPT_NAME)


def _tree_and_dirty(cwd=None):
    """The identity a receipt is checked against: the committed tree plus a hash of what is not.

    `git status --porcelain` decides dirty/clean (it sees untracked files; `git diff HEAD` does
    not). The diff hash is the finer-grained half -- two dirty trees with the same HEAD can still
    differ, and a receipt for one is not a receipt for the other.
    """
    tree = subprocess.run(["git", "rev-parse", "HEAD^{tree}"], cwd=cwd,
                          capture_output=True, text=True, check=True).stdout.strip()
    status = subprocess.run(["git", "status", "--porcelain"], cwd=cwd,
                            capture_output=True, text=True, check=True).stdout
    dirty = bool(status.strip())
    diff = subprocess.run(["git", "diff", "HEAD"], cwd=cwd,
                          capture_output=True, text=True, check=True).stdout
    diff_hash = hashlib.sha256(diff.encode("utf-8")).hexdigest()
    return tree, dirty, diff_hash


def write_receipt(mode, cwd=None):
    """Record that `mode` ('full'/'fast') just passed on the current tree. Overwrites any prior.

    Best-effort, like buildlock's holder-info sidecar: a receipt that failed to write just means
    the next push re-runs gates for real, which is always safe. Letting that failure propagate
    would flip an already-printed PASS verdict to a nonzero exit -- exactly the failure class this
    module's own docstring exists to stop.
    """
    try:
        tree, dirty, diff_hash = _tree_and_dirty(cwd)
        receipt = {
            "tree": tree,
            "dirty": dirty,
            "diff_hash": diff_hash,
            "mode": mode,
            "timestamp_utc": datetime.now(timezone.utc).strftime(RECEIPT_TIME_FORMAT),
        }
        with open(receipt_path(cwd), "w", encoding="utf-8") as f:
            json.dump(receipt, f)
    except (OSError, subprocess.CalledProcessError) as e:
        print(f"gates: could not write the gate receipt ({e}) -- next push will just re-run gates",
              flush=True)


def delete_receipt(cwd=None):
    """A receipt for a tree that just failed gates is worse than none -- it would say PASS."""
    try:
        os.remove(receipt_path(cwd))
    except OSError:
        pass


def _format_age(age_s):
    if age_s < 60:
        return f"{int(age_s)}s"
    if age_s < 3600:
        return f"{int(age_s // 60)}m"
    return f"{age_s / 3600:.1f}h"


def receipt_status(cwd=None, max_age_s=RECEIPT_MAX_AGE_S):
    """(valid, receipt_dict, age_seconds) for the receipt against the CURRENT tree.

    receipt_dict/age are None if invalid. Every mismatch -- missing file, unparseable JSON,
    different tree, different dirty-hash, or a timestamp older than max_age_s -- is treated the
    same way: not valid, fall back to running gates for real. A receipt only ever narrows when
    gates are skipped, never widens it.
    """
    try:
        with open(receipt_path(cwd), "r", encoding="utf-8") as f:
            receipt = json.load(f)
    except (OSError, ValueError):
        return False, None, None

    tree, dirty, diff_hash = _tree_and_dirty(cwd)
    if receipt.get("tree") != tree:
        return False, None, None
    if receipt.get("dirty") != dirty or receipt.get("diff_hash") != diff_hash:
        return False, None, None

    try:
        written = datetime.strptime(receipt["timestamp_utc"], RECEIPT_TIME_FORMAT).replace(
            tzinfo=timezone.utc)
    except (KeyError, ValueError, TypeError):
        return False, None, None
    age = (datetime.now(timezone.utc) - written).total_seconds()
    if age < 0 or age > max_age_s:
        return False, None, None
    return True, receipt, age


def check_receipt():
    """`--check-receipt` entry point: prints the skip line and exits 0 iff the receipt still holds.

    Exits 1 silently otherwise -- the pre-push hook falls through to a real `gates-fast` run on
    exit 1, and that run's own output is the message; this command adds nothing to it. Every
    failure mode (a corrupt receipt, a `git` invocation that errors) is caught rather than left to
    print a traceback: this command has exactly two honest outputs, the skip line or exit 1.
    """
    try:
        valid, receipt, age = receipt_status()
        if not valid:
            return 1
        print(f"pre-push: gates receipt for tree {receipt['tree'][:7]} "
              f"({receipt['mode']}, {_format_age(age)} old) -- skipping", flush=True)
        return 0
    except (OSError, subprocess.CalledProcessError, KeyError):
        return 1


def _init_temp_repo(path):
    """A minimal real git repo -- the receipt tests need real `git rev-parse`/`diff` answers."""
    subprocess.run(["git", "init", "-q", path], check=True)
    subprocess.run(["git", "-C", path, "config", "user.email", "test@example.com"], check=True)
    subprocess.run(["git", "-C", path, "config", "user.name", "Test"], check=True)
    with open(os.path.join(path, "file.txt"), "w", encoding="utf-8") as f:
        f.write("hello\n")
    subprocess.run(["git", "-C", path, "add", "."], check=True)
    subprocess.run(["git", "-C", path, "commit", "-q", "-m", "initial"], check=True)


def _write_stub_pixi(bin_dir, real_gates_py, call_log, fast_exit=0):
    """A fake `pixi` on PATH: forwards `run gates-check-receipt` to the REAL gates.py (so the
    forged-receipt case exercises the real check-receipt logic end to end), and records any
    `run gates-fast` call to call_log instead of actually running gates -- exiting `fast_exit`,
    configurable so a test can prove the hook still propagates a REAL gates failure, not just that
    it attempted one (a hardcoded exit 0 here would pass a hook that swallowed gates-fast's exit).
    """
    stub = os.path.join(bin_dir, "pixi")
    with open(stub, "w", encoding="utf-8", newline="\n") as f:
        f.write(
            "#!/bin/sh\n"
            'if [ "$1" = "run" ] && [ "$2" = "gates-check-receipt" ]; then\n'
            f'    exec "{sys.executable}" -u "{real_gates_py}" --check-receipt\n'
            "fi\n"
            'if [ "$1" = "run" ] && [ "$2" = "gates-fast" ]; then\n'
            f'    printf \'called\\n\' >> "{call_log}"\n'
            f'    exit {fast_exit}\n'
            "fi\n"
            'exit 1\n'
        )
    os.chmod(stub, 0o755)
    return stub


def selftest():
    """The control arm. An aggregator that cannot go red is a green light with extra steps.

    Discriminating in both directions, on BOTH paths: an all-pass run must report PASS, and a
    single failing gate must be reported and named whether it ran sequentially or overlapped.
    Without the overlapped arm, join_gates could stop collecting failures and this file would
    keep reporting PASS -- the exact class of fault it exists to stop.

    Covers the aggregation logic only. That `pixi run <gate>`'s own exit code survives the
    subprocess boundary was proven end to end by introducing a real formatting violation and
    watching `fmt-check` come back `(exit 2)` with the others still reported -- see the commit
    that added this file. The overlapped path's boundary got the same proof when #986 landed: a
    real recordonce duplication came back `FAIL audit-recordonce` from inside the overlap -- see
    that PR.
    """
    ok = True

    failed = run_gates(["a", "b"], lambda name: 0)
    line = summarise(["a", "b"], failed)
    if failed or not line.startswith(PASS_MARK):
        print(f"  control FAILED: an all-pass run did not report pass -- {line}")
        ok = False

    failed = run_gates(["a", "b"], lambda name: 1 if name == "b" else 0)
    line = summarise(["a", "b"], failed)
    if failed != ["b"] or not line.startswith(FAIL_MARK) or "b" not in line:
        print(f"  control FAILED: a failing gate was not reported -- {line}")
        ok = False

    # The overlapped path, with real subprocesses so communicate()/returncode are the real thing.
    def fake_spawner(code):
        return subprocess.Popen(
            [sys.executable, "-c", f"print('overlap-output'); raise SystemExit({code})"],
            stdout=subprocess.PIPE, stderr=subprocess.STDOUT)

    failed = join_gates([("good", fake_spawner(0)), ("bad", fake_spawner(3))])
    if failed != ["bad"]:
        print(f"  control FAILED: the overlapped path did not report the failing gate -- {failed}")
        ok = False

    # The quiet path (#1560), both directions: a failing overlapped gate must still be REPORTED
    # (named in the failed list -- quiet must never eat a red), and a passing gate's output must
    # actually be dropped. Both arms discriminate: without the quiet branch the second arm sees
    # "overlap-output"; if quiet ever stopped collecting failures the first arm goes green-blind.
    failed = join_gates([("good", fake_spawner(0)), ("bad", fake_spawner(3))], quiet=True)
    if failed != ["bad"]:
        print(f"  control FAILED: the quiet overlapped path did not report the failing gate -- {failed}")
        ok = False

    import io
    captured = io.BytesIO()

    class _Buf:
        buffer = captured
        @staticmethod
        def write(text):
            captured.write(text.encode())
        @staticmethod
        def flush():
            pass

    real_stdout = sys.stdout
    sys.stdout = _Buf()  # type: ignore[assignment]  # only .buffer/.flush are touched below
    try:
        join_gates([("good", fake_spawner(0))], quiet=True)
    finally:
        sys.stdout = real_stdout
    if b"overlap-output" in captured.getvalue():
        print("  control FAILED: quiet mode echoed a PASSING gate's output")
        ok = False

    # The tail bound: an over-long failing log is elided with the rerun named, and the tail kept.
    sys.stdout = _Buf()  # type: ignore[assignment]
    captured.seek(0); captured.truncate()
    try:
        emit_failure_output("longgate", b"".join(b"line%d\n" % i for i in range(500)), tail_lines=100)
    finally:
        sys.stdout = real_stdout
    got = captured.getvalue()
    if b"line499" not in got or b"line0\n" in got:
        print("  control FAILED: the tail bound did not keep the tail / drop the head")
        ok = False

    # The gate receipt (#1636): a real git repo, not fakes -- the whole point is that tree/dirty
    # hashes come from real `git rev-parse`/`diff` output.
    with tempfile.TemporaryDirectory() as td:
        repo = os.path.join(td, "repo")
        os.makedirs(repo)
        _init_temp_repo(repo)
        receipt_file = receipt_path(repo)

        def _forge(**overrides):
            with open(receipt_file, encoding="utf-8") as f:
                data = json.load(f)
            data.update(overrides)
            with open(receipt_file, "w", encoding="utf-8") as f:
                json.dump(data, f)

        # A pass writes a receipt that validates against the tree it was written for.
        write_receipt("fast", cwd=repo)
        valid, receipt, age = receipt_status(cwd=repo)
        if not valid or receipt.get("mode") != "fast" or age is None:
            print(f"  control FAILED: a fresh receipt did not validate -- {valid=} {receipt=}")
            ok = False

        # A fail deletes it -- no receipt left to be found valid or invalid.
        delete_receipt(cwd=repo)
        if os.path.exists(receipt_file):
            print("  control FAILED: delete_receipt left the receipt file behind")
            ok = False
        valid, _, _ = receipt_status(cwd=repo)
        if valid:
            print("  control FAILED: receipt_status validated a deleted receipt")
            ok = False

        # A receipt for a different tree does not match.
        write_receipt("full", cwd=repo)
        _forge(tree="0" * 40)
        valid, _, _ = receipt_status(cwd=repo)
        if valid:
            print("  control FAILED: a receipt for a different tree matched")
            ok = False

        # A receipt for the same tree but a different dirty-hash does not match.
        write_receipt("full", cwd=repo)
        _forge(diff_hash="0" * 64)
        valid, _, _ = receipt_status(cwd=repo)
        if valid:
            print("  control FAILED: a receipt with a mismatched dirty-hash matched")
            ok = False

        # A receipt older than the age ceiling does not match.
        write_receipt("full", cwd=repo)
        stale = datetime.now(timezone.utc) - timedelta(hours=7)
        _forge(timestamp_utc=stale.strftime(RECEIPT_TIME_FORMAT))
        valid, _, _ = receipt_status(cwd=repo)
        if valid:
            print("  control FAILED: a receipt older than the 6h ceiling matched")
            ok = False

        # The hook itself (sh): a forged, currently-valid receipt makes it exit 0 with the skip
        # line and never call `pixi run gates-fast`; no receipt makes it fall through and call it.
        sh = shutil.which("sh")
        if sh is None:
            print("  control FAILED: no `sh` on PATH -- cannot exercise .githooks/pre-push")
            ok = False
        else:
            hook = os.path.abspath(os.path.join(
                os.path.dirname(__file__), "..", "..", ".githooks", "pre-push"))
            real_gates_py = os.path.abspath(__file__)
            bin_dir = os.path.join(td, "bin")
            os.makedirs(bin_dir)
            call_log = os.path.join(td, "calls.log")
            # fast_exit=7, not 0: the miss arm below must prove the hook PROPAGATES a real gates
            # failure, not merely that it attempted one -- a hook that swallowed gates-fast's exit
            # (e.g. `pixi run gates-fast || true`) would pass a hardcoded-0 stub undetected.
            _write_stub_pixi(bin_dir, real_gates_py, call_log, fast_exit=7)
            env = dict(os.environ)
            env["PATH"] = bin_dir + os.pathsep + env.get("PATH", "")

            write_receipt("fast", cwd=repo)
            hit = subprocess.run([sh, hook], cwd=repo, env=env,
                                 capture_output=True, text=True, check=False)
            if hit.returncode != 0 or "-- skipping" not in hit.stdout:
                print(f"  control FAILED: hook did not skip on a valid receipt -- "
                      f"exit={hit.returncode} stdout={hit.stdout!r} stderr={hit.stderr!r}")
                ok = False
            if os.path.exists(call_log):
                print("  control FAILED: hook called gates-fast despite a valid receipt")
                ok = False

            delete_receipt(cwd=repo)
            miss = subprocess.run([sh, hook], cwd=repo, env=env,
                                  capture_output=True, text=True, check=False)
            if not os.path.exists(call_log):
                print(f"  control FAILED: hook did not attempt gates with no receipt -- "
                      f"exit={miss.returncode} stdout={miss.stdout!r} stderr={miss.stderr!r}")
                ok = False
            if miss.returncode != 7:
                print(f"  control FAILED: hook did not propagate gates-fast's own exit code -- "
                      f"got {miss.returncode}, gates-fast exited 7")
                ok = False

    print("selftest: pass" if ok else "selftest: FAIL")
    return 0 if ok else 1


def main():
    if "--selftest" in sys.argv:
        return selftest()
    if "--check-receipt" in sys.argv:
        return check_receipt()

    mode = "fast" if "--fast" in sys.argv else "full"
    after_build = AFTER_BUILD_FAST if mode == "fast" else AFTER_BUILD_FULL
    quiet = "--quiet" in sys.argv
    names, failed = run_all(after_build,
                            runner=quiet_pixi_runner if quiet else pixi_runner,
                            quiet=quiet)
    print()
    print(summarise(names, failed))
    if failed:
        delete_receipt()
    else:
        write_receipt(mode)
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
