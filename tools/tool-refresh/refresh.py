"""One-command refresh of the installed `baton` global tool (#1645).

The hand sequence this replaces, and why each step exists, is the issue's own "Measured friction"
(2026-09-01/02): wait for lanes to stop holding the exe open, `pixi run pack`, uninstall, purge the
NuGet cache (mandatory -- it silently serves a stale same-version package otherwise, bit twice on
8/30), install from `bin/pack`, then verify by hand that the reinstall actually took. Skipping the
drain step is what makes `dotnet tool uninstall` fail access-denied; skipping the cache purge is what
makes the reinstall silently keep serving the old build; skipping verification is what let the
conductor run 0.25.0 all afternoon on 2026-09-01 with no telemetry while five PRs merged underneath it.

Usage:      pixi run tool-refresh [--wait] [--dry-run] | pixi run tool-refresh --abort
Selftest:   pixi run tool-refresh-selftest   (python tools/tool-refresh/refresh.py --selftest)

Drain is TWO halves (operator ruling on #1645, 2026-09-02), not one. spec/baton.md's C-10 entry is the
register of what that means for the CLI side; what belongs here is the file itself:

  (1) The drain MARKER, written ahead of the scan below and cleared again in a `finally`. This
      docstring is the source of its NAME and CONTENT and nothing else: BATON_HOME/draining.json,
      holding {"since": <utc ISO-8601>, "pid": <this process>, "reason": "tool-refresh"}. A `finally`
      cannot run if the interpreter is killed outright, which is what `--abort` is for.
  (2) `--wait`: block until the live-room count reaches zero, re-reading every POLL_S and reprinting
      the remaining rooms and their liveness every PROGRESS_EVERY_S.

Drain predicate (half 2): a room under BATON_HOME/rooms (default ~/.baton/rooms) blocks the refresh
when it has no `terminal.json` AND `baton status <room-dir> --json` reports a step with
`liveness: "alive"` -- the same liveness `baton status`'s own human rendering and `baton resume`'s
STALLED reconciliation already compute (EngineLivenessProbe, src/Baton/Outcomes/EngineLivenessProbe.cs)
-- reused here via the CLI's own `--json` surface rather than reimplemented against a PID. A step
reporting `liveness: "unknown"` blocks too (ambiguous holder, fail closed) but is labelled separately
from `alive` in what gets printed. A room `baton status --json` cannot even read (no snapshot -- a
provisioning crash before any ledger existed) is not a live holder -- there is no engine process to be
holding the exe open -- so it is never blocking, only reported for visibility.

Dry run (--dry-run): the drain check still runs for real (read-only). Every mutating step -- the drain
marker, pack, uninstall, cache purge, install, verify -- prints the exact command it would run and does
nothing. The marker is included in that: a dry run must not park every lane on the machine for its own
duration, and must not be able to leave a marker behind if it dies.
`pixi run pack` takes the repo's build lock (tools/buildlock.py); a dry run must never contend for it.

Fail-loud (item 3's third arm): once uninstall has actually removed the tool, every following failure
says outright that baton is currently UNINSTALLED rather than letting a non-zero exit code speak for
itself -- see WARNING_NOT_LEFT_UNINSTALLED below.
"""
import argparse
import json
import os
import re
import subprocess
import sys
import time
from dataclasses import dataclass, field
from typing import Callable, List, Optional, Sequence

VERSION_ELEMENT = re.compile(r"<Version>\s*(?P<version>\S+?)\s*</Version>")
VERSION_PROPS_RELATIVE_PATH = os.path.join("src", "Baton.Cli", "Directory.Build.props")
POLL_S = 5.0
# 30s, not the poll interval: the operator's #1645 ruling asks --wait to print "the remaining rooms and
# their lock/liveness state every 30 s". Liveness is still re-read every POLL_S -- this throttles the
# printing only, so a multi-minute wait does not bury the terminal.
PROGRESS_EVERY_S = 30.0
# The one string joining the two halves of the drain: this writes it, src/Baton/Status/BatonPaths.cs's
# DrainMarkerFileName reads it. A transcription, because a python tool cannot reference a C# const --
# so `_selftest_marker_filename_matches_the_cli` asserts the two are equal. Without that check the two
# halves drift SILENTLY (every dispatch proceeds through a drain and nothing reports it), which is the
# one failure in this tool that is not fail-closed.
DRAIN_MARKER_FILENAME = "draining.json"
DRAIN_MARKER_REASON = "tool-refresh"
ABORT_INVOCATION = "pixi run tool-refresh --abort"
# Where BatonPaths.DrainMarkerFileName lives, for the check above.
BATON_PATHS_RELATIVE_PATH = os.path.join("src", "Baton", "Status", "BatonPaths.cs")
DRAIN_MARKER_CONST = re.compile(
    r"DrainMarkerFileName\s*=\s*\"(?P<name>[^\"]+)\"")
# Same transcription shape, one severity lower: a drift here prints a recovery command that does not
# work, which an operator notices at once rather than never. Checked by the same arm.
DRAIN_MARKER_TYPE_RELATIVE_PATH = os.path.join("src", "Baton", "Status", "DrainMarker.cs")
ABORT_INVOCATION_CONST = re.compile(
    r"AbortInvocation\s*=\s*\"(?P<invocation>[^\"]+)\"")


@dataclass
class CommandResult:
    returncode: int
    stdout: str = ""
    stderr: str = ""


Runner = Callable[[List[str]], CommandResult]


def real_runner(cmd: List[str]) -> CommandResult:
    proc = subprocess.run(cmd, capture_output=True, text=True, check=False)
    return CommandResult(proc.returncode, proc.stdout, proc.stderr)


@dataclass
class Deps:
    """Everything the refresh needs from the outside world, injectable so --selftest never spawns a
    real `dotnet`/`baton`/`pixi` or touches this machine's real NuGet cache or ~/.baton."""

    run: Runner = real_runner
    repo_root: str = ""
    baton_home: str = ""
    rooms_root: str = ""
    nuget_packages_root: str = ""
    sleep: Callable[[float], None] = time.sleep
    monotonic: Callable[[], float] = time.monotonic
    out: "Sequence[str]" = field(default_factory=list)  # unused; printing goes straight to stdout

    def __post_init__(self) -> None:
        if not self.repo_root:
            self.repo_root = default_repo_root()
        if not self.baton_home:
            self.baton_home = default_baton_home()
        if not self.rooms_root:
            self.rooms_root = os.path.join(self.baton_home, "rooms")
        if not self.nuget_packages_root:
            self.nuget_packages_root = os.environ.get(
                "NUGET_PACKAGES", os.path.join(os.path.expanduser("~"), ".nuget", "packages"))


def default_repo_root() -> str:
    return os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))


def default_baton_home() -> str:
    override = os.environ.get("BATON_HOME", "").strip()
    return override if override else os.path.join(os.path.expanduser("~"), ".baton")


def read_repo_version(repo_root: str) -> Optional[str]:
    """The version `baton --version` will report once packed from this checkout -- the same
    <Version> MSBuild stamps into the assembly (VersionInfo.GetVersion, src/Baton.Cli/VersionInfo.cs).
    Read once here rather than after packing, so it names the exact nupkg/cache-dir/verify target
    every step below shares (record-once: one source, not one per step)."""
    props_path = os.path.join(repo_root, VERSION_PROPS_RELATIVE_PATH)
    try:
        with open(props_path, "r", encoding="utf-8") as f:
            text = f.read()
    except OSError:
        return None
    match = VERSION_ELEMENT.search(text)
    return match.group("version") if match else None


# ---------------------------------------------------------------------------------------------
# Drain half (1): the marker every dispatching verb refuses under.
# ---------------------------------------------------------------------------------------------

def drain_marker_path(deps: Deps) -> str:
    return os.path.join(deps.baton_home, DRAIN_MARKER_FILENAME)


def write_drain_marker(deps: Deps, dry_run: bool, print_fn: Callable[[str], None]) -> None:
    """Written BEFORE the drain scan, so the queue stops refilling while the scan is still reading --
    the gap this closes is a `baton dispatch` starting between the scan's verdict and the uninstall.
    An existing marker is adopted, not refused: the same tool wrote it for the same reason, and a stale
    one left by a killed interpreter must not make the recovery path (re-run the refresh) the one thing
    that cannot run. Chosen knowing its cost: two refreshes started at once both proceed, and whichever
    reaches its `finally` first removes the marker while the other may still be mid-uninstall, reopening
    the gap half (1) exists to close. `pixi run pack` serializes on the repo build lock; this does not.
    A single-operator machine is the whole population today -- if that stops being true, the fix is a
    real lock here, not a refusal (which would break killed-interpreter recovery)."""
    path = drain_marker_path(deps)
    if dry_run:
        print_fn(f"tool-refresh: [dry-run] would write drain marker: {path}")
        return

    os.makedirs(os.path.dirname(path), exist_ok=True)
    payload = {
        "since": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
        "pid": os.getpid(),
        "reason": DRAIN_MARKER_REASON,
    }
    with open(path, "w", encoding="utf-8") as f:
        json.dump(payload, f)
    print_fn(f"tool-refresh: drain marker written: {path} -- new lanes will refuse to start")


def remove_drain_marker(deps: Deps, dry_run: bool, print_fn: Callable[[str], None]) -> bool:
    """Returns whether a marker was actually there. Called from refresh()'s `finally`, so it runs on
    success, on a failed step, on an exception and on Ctrl-C alike."""
    path = drain_marker_path(deps)
    if dry_run:
        print_fn(f"tool-refresh: [dry-run] would remove drain marker: {path}")
        return False

    try:
        os.remove(path)
    except FileNotFoundError:
        return False
    except OSError as exc:
        # A locked or permission-denied delete (an indexer or AV holding the file on Windows) must not
        # escape the `finally` this runs in -- doing so would replace the original failure with this one
        # AND leave the marker, so the operator would see neither the real error nor the recovery.
        print_fn(
            f"tool-refresh: could not remove the drain marker {path} ({exc.__class__.__name__}: {exc}) -- "
            f"new lanes will keep refusing until you clear it: {ABORT_INVOCATION}"
        )
        return False
    print_fn(f"tool-refresh: drain marker removed: {path} -- new lanes may start again")
    return True


def abort(deps: Deps, print_fn: Callable[[str], None], dry_run: bool = False) -> int:
    """`--abort`: clear the marker and do nothing else. The recovery for a marker whose writer was
    killed outright (no `finally` runs then), which is why every refusal message names this command.

    `--dry-run --abort` removes nothing: deleting a real file is precisely what --dry-run promises not
    to do, and the marker is the one file whose deletion has a consequence for other processes."""
    if dry_run:
        remove_drain_marker(deps, dry_run=True, print_fn=print_fn)
        return 0

    if remove_drain_marker(deps, dry_run=False, print_fn=print_fn):
        return 0
    print_fn(f"tool-refresh: no drain marker at {drain_marker_path(deps)} -- nothing to abort")
    return 0


# ---------------------------------------------------------------------------------------------
# Drain half (2): refuse (or wait) while a room is live.
# ---------------------------------------------------------------------------------------------

def room_has_terminal_sentinel(room_dir: str) -> bool:
    return os.path.isfile(os.path.join(room_dir, "terminal.json"))


def classify_room(room_dir: str, run: Runner) -> "tuple[str, str]":
    """One of "alive" | "unknown" | "clear" | "unreadable". Only the first two block a drain --
    see the module docstring for why "unreadable" (no ledger `baton status` can even project) does
    not: there is no engine process behind a room that never got as far as flow.jsonl."""
    result = run(["baton", "status", room_dir, "--json"])
    if result.returncode != 0:
        detail = (result.stderr or result.stdout or "no output").strip()
        return "unreadable", f"'baton status --json' exited {result.returncode}: {detail}"

    try:
        payload = json.loads(result.stdout)
    except json.JSONDecodeError as exc:
        return "unreadable", f"'baton status --json' printed unparseable output: {exc}"

    liveness_values = [
        step.get("liveness") for step in payload.get("steps", []) if "liveness" in step
    ]
    if "alive" in liveness_values:
        return "alive", "a step reports liveness=alive"
    if "unknown" in liveness_values:
        return "unknown", "a step reports liveness=unknown (ambiguous holder, treated as live)"
    return "clear", "no step reports an alive or unknown holder"


@dataclass
class RoomVerdict:
    room_dir: str
    category: str
    detail: str


def scan_rooms(rooms_root: str, run: Runner) -> List[RoomVerdict]:
    """Every non-terminal room's verdict, "clear" ones included -- callers filter; --selftest checks
    the full population so a regression that silently drops a category is visible."""
    if not os.path.isdir(rooms_root):
        return []

    verdicts = []
    for name in sorted(os.listdir(rooms_root)):
        room_dir = os.path.join(rooms_root, name)
        if not os.path.isdir(room_dir) or room_has_terminal_sentinel(room_dir):
            continue
        category, detail = classify_room(room_dir, run)
        verdicts.append(RoomVerdict(room_dir, category, detail))
    return verdicts


def drain(deps: Deps, wait: bool, print_fn: Callable[[str], None]) -> bool:
    """Prints every non-terminal room's verdict; returns True once none are blocking. Blocks
    (polling every POLL_S) under --wait, otherwise returns the first read's verdict immediately.

    Under --wait the liveness read still happens every POLL_S, but the per-room progress lines print
    only every PROGRESS_EVERY_S (the first pass always prints, so a wait never starts silent) -- see
    that constant for the ruling it comes from. Without --wait there is exactly one pass, so nothing is
    throttled: the single verdict is the whole output."""
    last_progress: Optional[float] = None
    while True:
        verdicts = scan_rooms(deps.rooms_root, deps.run)
        blocking = [v for v in verdicts if v.category in ("alive", "unknown")]
        unreadable = [v for v in verdicts if v.category == "unreadable"]
        now = deps.monotonic()
        progress_due = not wait or last_progress is None or (now - last_progress) >= PROGRESS_EVERY_S

        # The throttle governs PROGRESS only. The pass that ends the wait is not progress -- it is the
        # verdict -- so it always prints in full, unreadable rooms included.
        if progress_due or not blocking:
            for v in unreadable:
                print_fn(f"tool-refresh: skipping {v.room_dir} (not a live holder -- {v.detail})")

        if not blocking:
            if verdicts:
                print_fn(f"tool-refresh: drain clear -- {len(verdicts)} non-terminal room(s) checked, none live")
            else:
                print_fn("tool-refresh: drain clear -- no non-terminal rooms found")
            return True

        if progress_due:
            for v in blocking:
                print_fn(f"tool-refresh: BLOCKED by {v.room_dir} ({v.category}: {v.detail})")

        if not wait:
            print_fn(
                f"tool-refresh: {len(blocking)} room(s) still live -- refusing to uninstall while a "
                "lane may hold the exe open. Pass --wait to block until they finish, or re-run once "
                "they have."
            )
            return False

        if progress_due:
            print_fn(
                f"tool-refresh: waiting on {len(blocking)} live room(s) -- re-reading every "
                f"{POLL_S:.0f}s, reprinting every {PROGRESS_EVERY_S:.0f}s..."
            )
            last_progress = now
        deps.sleep(POLL_S)


# ---------------------------------------------------------------------------------------------
# Items 1-2: pack -> uninstall -> purge cache -> install -> verify.
# ---------------------------------------------------------------------------------------------

WARNING_NOT_LEFT_UNINSTALLED = (
    "tool-refresh: baton is currently UNINSTALLED on this machine. The refresh did not complete -- "
    "see the error above. Reinstall by hand once it is fixed: "
    "dotnet tool install --global --add-source bin/pack baton"
)


def dotnet_tool_installed(deps: Deps) -> bool:
    result = deps.run(["dotnet", "tool", "list", "--global"])
    if result.returncode != 0:
        return False
    return any(line.split() and line.split()[0] == "baton" for line in result.stdout.splitlines())


def run_step(deps: Deps, cmd: List[str], dry_run: bool, print_fn: Callable[[str], None]) -> CommandResult:
    printable = " ".join(cmd)
    if dry_run:
        print_fn(f"tool-refresh: [dry-run] would run: {printable}")
        return CommandResult(0)
    print_fn(f"tool-refresh: running: {printable}")
    return deps.run(cmd)


def purge_nuget_cache(deps: Deps, version: str, dry_run: bool, print_fn: Callable[[str], None]) -> None:
    cache_dir = os.path.join(deps.nuget_packages_root, "baton", version)
    if dry_run:
        print_fn(f"tool-refresh: [dry-run] would purge NuGet cache: {cache_dir}")
        return
    if os.path.isdir(cache_dir):
        import shutil

        shutil.rmtree(cache_dir)
        print_fn(f"tool-refresh: purged NuGet cache {cache_dir}")
    else:
        print_fn(f"tool-refresh: NuGet cache {cache_dir} already absent, nothing to purge")


def refresh(deps: Deps, wait: bool, dry_run: bool, print_fn: Callable[[str], None]) -> int:
    """Half (1) of the drain wraps EVERYTHING, including the scan: the marker is written before the
    first liveness read and removed in a `finally`, so a failed step, an exception and a Ctrl-C all
    leave the queue open again. `finally` does not run if the interpreter is killed outright -- that is
    what `--abort` is for."""
    try:
        # Inside the try, not before it: a Ctrl-C landing between a successful write and the `try`
        # being entered would otherwise leave a marker refusing every dispatch on the machine.
        write_drain_marker(deps, dry_run, print_fn)
        return _refresh_under_marker(deps, wait, dry_run, print_fn)
    finally:
        remove_drain_marker(deps, dry_run, print_fn)


def _refresh_under_marker(deps: Deps, wait: bool, dry_run: bool, print_fn: Callable[[str], None]) -> int:
    if not drain(deps, wait, print_fn):
        return 1

    version = read_repo_version(deps.repo_root)
    if version is None:
        print_fn(
            f"tool-refresh: could not read a <Version> from {VERSION_PROPS_RELATIVE_PATH} under "
            f"{deps.repo_root} -- refusing to guess which nupkg/cache entry this refresh targets."
        )
        return 1
    print_fn(f"tool-refresh: checkout version is {version}")

    pack_result = run_step(deps, ["pixi", "run", "pack"], dry_run, print_fn)
    if pack_result.returncode != 0:
        print_fn(f"tool-refresh: pack failed (exit {pack_result.returncode}): {pack_result.stderr.strip()}")
        return 1

    expected_nupkg = os.path.join(deps.repo_root, "bin", "pack", f"baton.{version}.nupkg")
    if not dry_run and not os.path.isfile(expected_nupkg):
        print_fn(
            f"tool-refresh: pack reported success but {expected_nupkg} does not exist -- refusing to "
            "uninstall the working copy over a pack that did not actually produce this version."
        )
        return 1

    was_installed = dry_run or dotnet_tool_installed(deps)
    if was_installed:
        uninstall_result = run_step(deps, ["dotnet", "tool", "uninstall", "--global", "baton"], dry_run, print_fn)
        if uninstall_result.returncode != 0:
            print_fn(
                f"tool-refresh: uninstall failed (exit {uninstall_result.returncode}): "
                f"{uninstall_result.stderr.strip()} -- baton is still installed at its prior version. "
                "If this is access-denied, a lane the drain check missed is likely still holding the "
                "exe open; re-run once it settles."
            )
            return 1
    else:
        print_fn("tool-refresh: baton is not currently installed, skipping uninstall")

    if not was_installed or dry_run:
        return _purge_install_verify(deps, version, was_installed, dry_run, print_fn)

    # F2 (#1653 review): from here on the machine has NO baton on it, and every way the rest of this can
    # end must say so -- not only the non-zero exits the branches below already handle. `dotnet`
    # unresolvable on PATH (FileNotFoundError), a NuGet cache directory whose files are still open
    # (PermissionError), or Ctrl-C mid-install (KeyboardInterrupt, which is not an Exception at all):
    # each of those previously escaped as a bare traceback with the operator's tool gone and nothing
    # saying so. BaseException is deliberate, and the re-raise is unconditional -- this handler adds one
    # sentence, it never turns a failure into a success or swallows the cause.
    try:
        return _purge_install_verify(deps, version, was_installed, dry_run, print_fn)
    except BaseException:
        print_fn(WARNING_NOT_LEFT_UNINSTALLED)
        raise


def _purge_install_verify(
        deps: Deps, version: str, was_installed: bool, dry_run: bool, print_fn: Callable[[str], None]) -> int:
    purge_nuget_cache(deps, version, dry_run, print_fn)

    install_result = run_step(
        deps, ["dotnet", "tool", "install", "--global", "--add-source", "bin/pack", "baton"], dry_run, print_fn)
    if install_result.returncode != 0:
        print_fn(f"tool-refresh: install failed (exit {install_result.returncode}): {install_result.stderr.strip()}")
        if was_installed:
            print_fn(WARNING_NOT_LEFT_UNINSTALLED)
        return 1

    if dry_run:
        print_fn(
            f"tool-refresh: [dry-run] would verify: 'baton --version' == {version}, "
            "and 'baton templates --json' exits 0"
        )
        print_fn("tool-refresh: [dry-run] resume hint would print here once the real run verifies")
        return 0

    version_result = deps.run(["baton", "--version"])
    installed_version = version_result.stdout.strip()
    if version_result.returncode != 0 or installed_version != version:
        print_fn(
            f"tool-refresh: verify failed -- 'baton --version' printed "
            f"{installed_version!r} (exit {version_result.returncode}), expected {version!r}."
        )
        # Only when there WAS an install to lose. Unconditional, this told an operator whose machine
        # never had baton on it that their tool is "currently UNINSTALLED" -- a false statement in the
        # one message written to be believed.
        if was_installed:
            print_fn(WARNING_NOT_LEFT_UNINSTALLED)
        return 1

    smoke_result = deps.run(["baton", "templates", "--json"])
    if smoke_result.returncode != 0:
        print_fn(
            f"tool-refresh: verify failed -- 'baton templates --json' exited "
            f"{smoke_result.returncode}: {smoke_result.stderr.strip()}"
        )
        if was_installed:  # see the version-verify branch above
            print_fn(WARNING_NOT_LEFT_UNINSTALLED)
        return 1

    print_fn(f"tool-refresh: verified -- baton {version} installed and responding")
    print_fn("tool-refresh: resume your lanes -- `baton status <room-dir>` for any that were waiting")
    return 0


def main(argv: Optional[List[str]] = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--wait", action="store_true", help="block until every live room finishes, then proceed")
    parser.add_argument("--dry-run", action="store_true", help="print every command; run none of the mutating ones")
    parser.add_argument(
        "--abort", action="store_true",
        help="remove a stale drain marker and do nothing else (refreshes nothing, packs nothing)")
    parser.add_argument("--selftest", action="store_true", help=argparse.SUPPRESS)
    args = parser.parse_args(argv)

    if args.selftest:
        return selftest()

    if args.abort:
        return abort(Deps(), print, args.dry_run)

    return refresh(Deps(), args.wait, args.dry_run, print)


# ---------------------------------------------------------------------------------------------
# Selftest: the three behaviours item 3 asks for, each against injected fakes -- no real dotnet/
# baton/pixi process, no touching this machine's real ~/.baton or NuGet cache.
# ---------------------------------------------------------------------------------------------

def _fake_status_json(state: str, liveness: Optional[str]) -> CommandResult:
    step = {"id": "work", "state": state}
    if liveness is not None:
        step["liveness"] = liveness
    return CommandResult(0, json.dumps({"state": state, "steps": [step], "outputs": [], "error": None}))


def _assert_isolated(deps: Deps) -> None:
    """Structural guard, not decoration: a selftest that resolved this machine's REAL ~/.baton would
    write a real drain marker into it and park every live lane -- the same class of accident as the
    2026-09-02 incident where a fixture ran against the real repo (issue #1645's comment thread). Every
    arm below that constructs a Deps calls this first, so the guard cannot be forgotten silently."""
    real_home = os.path.realpath(default_baton_home())
    if os.path.realpath(deps.baton_home) == real_home:
        raise AssertionError(
            f"selftest fixture resolved the REAL baton home ({real_home}) -- refusing to run: "
            "every arm must inject a temp baton_home")
    real_nuget = os.path.realpath(
        os.environ.get("NUGET_PACKAGES", os.path.join(os.path.expanduser("~"), ".nuget", "packages")))
    if os.path.realpath(deps.nuget_packages_root) == real_nuget:
        raise AssertionError(
            f"selftest fixture resolved the REAL NuGet packages root ({real_nuget}) -- refusing to run")


def _fixture_repo(td: str, version: str) -> str:
    """A repo root with just the two files the refresh reads: the version props and a packed nupkg."""
    props_dir = os.path.join(td, "src", "Baton.Cli")
    os.makedirs(props_dir, exist_ok=True)
    with open(os.path.join(props_dir, "Directory.Build.props"), "w", encoding="utf-8") as f:
        f.write(f"<Project><PropertyGroup><Version>{version}</Version></PropertyGroup></Project>")
    pack_dir = os.path.join(td, "bin", "pack")
    os.makedirs(pack_dir, exist_ok=True)
    open(os.path.join(pack_dir, f"baton.{version}.nupkg"), "w", encoding="utf-8").close()
    return td


def _make_room(rooms_root: str, name: str, terminal: bool) -> str:
    room_dir = os.path.join(rooms_root, name)
    os.makedirs(room_dir, exist_ok=True)
    if terminal:
        with open(os.path.join(room_dir, "terminal.json"), "w", encoding="utf-8") as f:
            f.write("{}")
    return room_dir


def _selftest_drain_predicate() -> bool:
    import tempfile

    ok = True
    with tempfile.TemporaryDirectory() as td:
        rooms_root = os.path.join(td, "rooms")
        os.makedirs(rooms_root)
        terminal_room = _make_room(rooms_root, "terminal-room", terminal=True)
        alive_room = _make_room(rooms_root, "alive-room", terminal=False)
        dead_room = _make_room(rooms_root, "dead-room", terminal=False)
        unknown_room = _make_room(rooms_root, "unknown-room", terminal=False)
        crashed_room = _make_room(rooms_root, "crashed-room", terminal=False)

        def run(cmd: List[str]) -> CommandResult:
            room_dir = cmd[2]
            if room_dir == alive_room:
                return _fake_status_json("Running", "alive")
            if room_dir == dead_room:
                return _fake_status_json("Running", "dead")
            if room_dir == unknown_room:
                return _fake_status_json("Running", "unknown")
            if room_dir == crashed_room:
                return CommandResult(1, "", "Room directory has no bound snapshot")
            raise AssertionError(f"status called for {room_dir}, which should never happen (terminal room)")

        verdicts = {v.room_dir: v.category for v in scan_rooms(rooms_root, run)}

        # Control: the terminal room must never even be probed (the AssertionError above would have
        # surfaced as an exception, not a wrong verdict, if it were).
        if terminal_room in verdicts:
            print("  control FAILED: a room with terminal.json was probed at all")
            ok = False

        expected = {
            alive_room: "alive",
            dead_room: "clear",
            unknown_room: "unknown",
            crashed_room: "unreadable",
        }
        for room_dir, want in expected.items():
            got = verdicts.get(room_dir)
            if got != want:
                print(f"  FAILED: {os.path.basename(room_dir)} classified {got!r}, want {want!r}")
                ok = False

        # Polarity assertion (v-and-v): alive and unknown block; clear and unreadable do not.
        deps = Deps(run=run, baton_home=td, rooms_root=rooms_root, repo_root=td,
                    nuget_packages_root=os.path.join(td, "nuget"))
        _assert_isolated(deps)
        messages: List[str] = []
        blocked = not drain(deps, wait=False, print_fn=messages.append)
        if not blocked:
            print("  FAILED: drain() reported clear with an alive and an unknown room present")
            ok = False
        joined = "\n".join(messages)
        if "BLOCKED" not in joined or alive_room not in joined or unknown_room not in joined:
            print("  FAILED: drain() did not name the alive/unknown blockers in its own output")
            ok = False
        if crashed_room not in joined:
            print("  FAILED: drain() did not report the unreadable room at all (visibility, not blocking)")
            ok = False

    return ok


def _selftest_version_compare() -> bool:
    import tempfile

    ok = True
    with tempfile.TemporaryDirectory() as td:
        props_dir = os.path.join(td, "src", "Baton.Cli")
        os.makedirs(props_dir)
        props_path = os.path.join(props_dir, "Directory.Build.props")

        with open(props_path, "w", encoding="utf-8") as f:
            f.write("<Project>\n  <PropertyGroup>\n    <Version>1.2.3</Version>\n  </PropertyGroup>\n</Project>\n")
        if read_repo_version(td) != "1.2.3":
            print(f"  FAILED: read_repo_version did not read 1.2.3, got {read_repo_version(td)!r}")
            ok = False

        missing_version_dir = os.path.join(td, "no-props")
        os.makedirs(missing_version_dir)
        if read_repo_version(missing_version_dir) is not None:
            print("  FAILED: read_repo_version returned a value with no Directory.Build.props present")
            ok = False

    return ok


def _selftest_fail_loud_on_uninstall_or_install() -> bool:
    import tempfile

    ok = True
    with tempfile.TemporaryDirectory() as td:
        rooms_root = os.path.join(td, "rooms")
        os.makedirs(rooms_root)
        repo_root = td
        props_dir = os.path.join(repo_root, "src", "Baton.Cli")
        os.makedirs(props_dir)
        with open(os.path.join(props_dir, "Directory.Build.props"), "w", encoding="utf-8") as f:
            f.write("<Project><PropertyGroup><Version>9.9.9</Version></PropertyGroup></Project>")
        pack_dir = os.path.join(repo_root, "bin", "pack")
        os.makedirs(pack_dir)
        open(os.path.join(pack_dir, "baton.9.9.9.nupkg"), "w", encoding="utf-8").close()

        def make_deps(fail_at: str, already_installed: bool = True) -> Deps:
            def run(cmd: List[str]) -> CommandResult:
                if cmd[:2] == ["dotnet", "tool"] and cmd[2] == "list":
                    return CommandResult(
                        0,
                        "Package Id      Version\nbaton           8.0.0\n" if already_installed
                        else "Package Id      Version\n")
                if cmd[:3] == ["pixi", "run", "pack"]:
                    return CommandResult(1 if fail_at == "pack" else 0)
                if cmd[:4] == ["dotnet", "tool", "uninstall", "--global"]:
                    return CommandResult(1 if fail_at == "uninstall" else 0, "", "access is denied" if fail_at == "uninstall" else "")
                if cmd[:4] == ["dotnet", "tool", "install", "--global"]:
                    return CommandResult(1 if fail_at == "install" else 0, "", "boom" if fail_at == "install" else "")
                if cmd == ["baton", "--version"]:
                    return CommandResult(1 if fail_at == "verify-version" else 0, "" if fail_at == "verify-version" else "9.9.9")
                if cmd == ["baton", "templates", "--json"]:
                    return CommandResult(1 if fail_at == "verify-templates" else 0)
                raise AssertionError(f"unexpected command in fail-loud selftest: {cmd}")

            deps = Deps(run=run, baton_home=td, rooms_root=rooms_root, repo_root=repo_root,
                        nuget_packages_root=os.path.join(td, "nuget"))
            _assert_isolated(deps)
            return deps

        for fail_at, must_warn_uninstalled in [
            ("pack", False),
            ("uninstall", False),
            ("install", True),
            ("verify-version", True),
            ("verify-templates", True),
        ]:
            messages: List[str] = []
            code = refresh(make_deps(fail_at), wait=False, dry_run=False, print_fn=messages.append)
            joined = "\n".join(messages)
            if code == 0:
                print(f"  FAILED: refresh() exited 0 despite a forced failure at {fail_at!r}")
                ok = False
            warned = "UNINSTALLED" in joined
            if warned != must_warn_uninstalled:
                print(
                    f"  FAILED: failure at {fail_at!r} printed the UNINSTALLED warning={warned}, "
                    f"want {must_warn_uninstalled}"
                )
                ok = False

        # The other polarity of "was there an install to lose": with baton NOT previously installed,
        # a failing verify must still exit non-zero but must NOT claim the machine's tool is gone --
        # there was none. Every arm above has already-installed hardcoded, which is what hid this.
        for fail_at in ["verify-version", "verify-templates", "install"]:
            messages = []
            code = refresh(
                make_deps(fail_at, already_installed=False), wait=False, dry_run=False, print_fn=messages.append)
            joined = "\n".join(messages)
            if code == 0:
                print(f"  FAILED: refresh() exited 0 despite a forced failure at {fail_at!r} (not installed)")
                ok = False
            if "UNINSTALLED" in joined:
                print(f"  FAILED: failure at {fail_at!r} claimed baton is UNINSTALLED when it never was")
                ok = False

        # Control: nothing forced to fail -> a clean pass, proving the harness itself isn't just
        # failing every arm by accident.
        messages = []
        clean_code = refresh(make_deps("nothing"), wait=False, dry_run=False, print_fn=messages.append)
        if clean_code != 0:
            print(f"  control FAILED: an unforced run exited {clean_code}, not 0 -- {messages}")
            ok = False

    return ok


def _selftest_dry_run_touches_nothing() -> bool:
    import tempfile

    ok = True
    with tempfile.TemporaryDirectory() as td:
        rooms_root = os.path.join(td, "rooms")
        os.makedirs(rooms_root)
        props_dir = os.path.join(td, "src", "Baton.Cli")
        os.makedirs(props_dir)
        with open(os.path.join(props_dir, "Directory.Build.props"), "w", encoding="utf-8") as f:
            f.write("<Project><PropertyGroup><Version>5.5.5</Version></PropertyGroup></Project>")

        def run(cmd: List[str]) -> CommandResult:
            raise AssertionError(f"--dry-run must never invoke a real command, got: {cmd}")

        deps = Deps(run=run, baton_home=td, rooms_root=rooms_root, repo_root=td,
                    nuget_packages_root=os.path.join(td, "nuget"))
        _assert_isolated(deps)
        messages: List[str] = []
        code = refresh(deps, wait=False, dry_run=True, print_fn=messages.append)
        if code != 0:
            print(f"  FAILED: --dry-run against a clear drain exited {code}, want 0")
            ok = False
        if not any("[dry-run]" in m for m in messages):
            print("  FAILED: --dry-run printed no [dry-run] lines at all")
            ok = False
        # A dry run must not park the machine's lanes for its own duration (see the module docstring).
        if os.path.exists(drain_marker_path(deps)):
            print("  FAILED: --dry-run actually wrote a drain marker")
            ok = False
        if not any("would write drain marker" in m for m in messages):
            print("  FAILED: --dry-run did not say it would write a drain marker")
            ok = False

    return ok


def _selftest_drain_marker_lifecycle() -> bool:
    """Half (1) of the drain ruling: the marker exists while the refresh is running -- from before the
    drain scan -- and is gone on every exit path, success and failure and exception alike."""
    import tempfile

    ok = True
    with tempfile.TemporaryDirectory() as td:
        rooms_root = os.path.join(td, "rooms")
        os.makedirs(rooms_root)
        _make_room(rooms_root, "settled-room", terminal=False)
        _fixture_repo(td, "7.7.7")

        seen_during_scan: List[bool] = []

        def make_deps(fail_at: str, raise_at: str = "") -> Deps:
            def run(cmd: List[str]) -> CommandResult:
                if cmd[:2] == ["baton", "status"]:
                    # The marker must already exist by the time the FIRST liveness read happens: the gap
                    # this closes is a dispatch starting between the scan's verdict and the uninstall.
                    seen_during_scan.append(os.path.isfile(marker_path))
                    return _fake_status_json("Succeeded", "dead")
                if raise_at and cmd[:3] == ["pixi", "run", "pack"] and raise_at == "pack":
                    raise RuntimeError("pack blew up")
                if cmd[:3] == ["pixi", "run", "pack"]:
                    return CommandResult(1 if fail_at == "pack" else 0)
                if cmd[:2] == ["dotnet", "tool"] and cmd[2] == "list":
                    return CommandResult(0, "Package Id      Version\nbaton           8.0.0\n")
                if cmd[:4] == ["dotnet", "tool", "uninstall", "--global"]:
                    return CommandResult(0)
                if cmd[:4] == ["dotnet", "tool", "install", "--global"]:
                    return CommandResult(0)
                if cmd == ["baton", "--version"]:
                    return CommandResult(0, "7.7.7")
                if cmd == ["baton", "templates", "--json"]:
                    return CommandResult(0)
                raise AssertionError(f"unexpected command in marker selftest: {cmd}")

            deps = Deps(run=run, baton_home=td, rooms_root=rooms_root, repo_root=td,
                        nuget_packages_root=os.path.join(td, "nuget"))
            _assert_isolated(deps)
            return deps

        marker_path = os.path.join(td, DRAIN_MARKER_FILENAME)

        # (a) success: present during the scan, gone afterwards.
        messages: List[str] = []
        code = refresh(make_deps("nothing"), wait=False, dry_run=False, print_fn=messages.append)
        if code != 0:
            print(f"  FAILED: an unforced run exited {code}, not 0 -- {messages}")
            ok = False
        if not seen_during_scan or not all(seen_during_scan):
            print(f"  FAILED: the marker was not present during the drain scan (saw {seen_during_scan})")
            ok = False
        if os.path.exists(marker_path):
            print("  FAILED: the marker survived a successful refresh")
            ok = False

        # (b) a failed step still clears it.
        messages = []
        code = refresh(make_deps("pack"), wait=False, dry_run=False, print_fn=messages.append)
        if code == 0:
            print("  FAILED: a forced pack failure exited 0")
            ok = False
        if os.path.exists(marker_path):
            print("  FAILED: the marker survived a failed refresh")
            ok = False

        # (c) a raised exception still clears it -- the `finally`, not an except branch.
        messages = []
        raised = False
        try:
            refresh(make_deps("nothing", raise_at="pack"), wait=False, dry_run=False, print_fn=messages.append)
        except RuntimeError:
            raised = True
        if not raised:
            print("  FAILED: the injected exception did not propagate out of refresh()")
            ok = False
        if os.path.exists(marker_path):
            print("  FAILED: the marker survived an exception")
            ok = False

        # (d) --abort clears a marker written by a killed run, and is a no-op with none present.
        deps = make_deps("nothing")
        write_drain_marker(deps, dry_run=False, print_fn=lambda _: None)
        if not os.path.exists(marker_path):
            print("  control FAILED: write_drain_marker wrote nothing, so (a)-(c) prove nothing")
            ok = False
        messages = []
        if abort(deps, messages.append, dry_run=True) != 0 or not os.path.exists(marker_path):
            print("  FAILED: --dry-run --abort removed the marker it only promised to describe")
            ok = False
        messages = []
        if abort(deps, messages.append) != 0 or os.path.exists(marker_path):
            print("  FAILED: --abort did not remove the marker")
            ok = False
        messages = []
        if abort(deps, messages.append) != 0 or not any("nothing to abort" in m for m in messages):
            print(f"  FAILED: --abort with no marker present did not report cleanly -- {messages}")
            ok = False

        # (e) the marker's content is what src/Baton/Status/DrainMarker.cs reads.
        write_drain_marker(deps, dry_run=False, print_fn=lambda _: None)
        with open(marker_path, "r", encoding="utf-8") as f:
            payload = json.load(f)
        if sorted(payload) != ["pid", "reason", "since"] or payload["reason"] != DRAIN_MARKER_REASON:
            print(f"  FAILED: marker content is {payload!r}, not the documented since/pid/reason shape")
            ok = False
        abort(deps, lambda _: None)

    return ok


def _selftest_marker_filename_matches_the_cli() -> bool:
    """The HIGH finding this arm exists for: DRAIN_MARKER_FILENAME and BatonPaths.DrainMarkerFileName
    are transcriptions of one another, and a mismatch is the tool's only SILENT failure -- refresh.py
    would write a marker no verb reads, every dispatch would proceed through the drain, and nothing
    anywhere would say the drain did nothing. (The marker's CONTENT needs no such check: DrainMarker
    treats every field as optional and refuses regardless of what it can parse.)"""
    ok = True
    props_path = os.path.join(default_repo_root(), BATON_PATHS_RELATIVE_PATH)
    try:
        with open(props_path, "r", encoding="utf-8") as f:
            text = f.read()
    except OSError as exc:
        print(f"  FAILED: could not read {props_path} to check the marker filename ({exc})")
        return False

    match = DRAIN_MARKER_CONST.search(text)
    if match is None:
        # Control: if the constant is renamed or removed, this arm must fail rather than quietly find
        # nothing to compare and pass.
        print(f"  FAILED: no DrainMarkerFileName constant found in {BATON_PATHS_RELATIVE_PATH}")
        return False

    if match.group("name") != DRAIN_MARKER_FILENAME:
        print(
            f"  FAILED: this tool writes {DRAIN_MARKER_FILENAME!r} but the CLI reads "
            f"{match.group('name')!r} -- the two halves of the drain would not meet"
        )
        ok = False

    marker_type_path = os.path.join(default_repo_root(), DRAIN_MARKER_TYPE_RELATIVE_PATH)
    try:
        with open(marker_type_path, "r", encoding="utf-8") as f:
            marker_type_text = f.read()
    except OSError as exc:
        print(f"  FAILED: could not read {marker_type_path} to check the abort invocation ({exc})")
        return False

    abort_match = ABORT_INVOCATION_CONST.search(marker_type_text)
    if abort_match is None:
        print(f"  FAILED: no AbortInvocation constant found in {DRAIN_MARKER_TYPE_RELATIVE_PATH}")
        return False
    if abort_match.group("invocation") != ABORT_INVOCATION:
        print(
            f"  FAILED: every refusal tells the operator to run "
            f"{abort_match.group('invocation')!r}, which is not this tool's own {ABORT_INVOCATION!r}"
        )
        ok = False

    return ok


def _selftest_fail_loud_on_exception_after_uninstall() -> bool:
    """F2 (#1653 review): the UNINSTALLED warning must survive an EXCEPTION, not only a non-zero exit --
    including KeyboardInterrupt, which is not an Exception."""
    import tempfile

    ok = True
    with tempfile.TemporaryDirectory() as td:
        rooms_root = os.path.join(td, "rooms")
        os.makedirs(rooms_root)
        _fixture_repo(td, "9.9.9")

        def make_deps(raise_at: str, exc: BaseException) -> Deps:
            def run(cmd: List[str]) -> CommandResult:
                if cmd[:2] == ["dotnet", "tool"] and cmd[2] == "list":
                    return CommandResult(0, "Package Id      Version\nbaton           8.0.0\n")
                if cmd[:3] == ["pixi", "run", "pack"]:
                    if raise_at == "pack":
                        raise exc
                    return CommandResult(0)
                if cmd[:4] == ["dotnet", "tool", "uninstall", "--global"]:
                    return CommandResult(0)
                if cmd[:4] == ["dotnet", "tool", "install", "--global"]:
                    if raise_at == "install":
                        raise exc
                    return CommandResult(0)
                if cmd == ["baton", "--version"]:
                    if raise_at == "verify":
                        raise exc
                    return CommandResult(0, "9.9.9")
                if cmd == ["baton", "templates", "--json"]:
                    return CommandResult(0)
                raise AssertionError(f"unexpected command in fail-loud-exception selftest: {cmd}")

            deps = Deps(run=run, baton_home=td, rooms_root=rooms_root, repo_root=td,
                        nuget_packages_root=os.path.join(td, "nuget"))
            _assert_isolated(deps)
            return deps

        # Both polarities of the one condition: an exception AFTER uninstall warns; the same exception
        # BEFORE it (at pack, with the tool still installed) must not -- a warning that fires either way
        # would be telling the operator their tool is gone when it is not.
        for raise_at, exc, must_warn in [
            ("install", FileNotFoundError("dotnet"), True),
            ("verify", KeyboardInterrupt(), True),
            ("pack", FileNotFoundError("pixi"), False),
        ]:
            messages: List[str] = []
            raised = False
            try:
                refresh(make_deps(raise_at, exc), wait=False, dry_run=False, print_fn=messages.append)
            except BaseException as caught:  # noqa: BLE001 -- the arm under test re-raises on purpose
                raised = type(caught) is type(exc)
            joined = "\n".join(messages)
            if not raised:
                print(f"  FAILED: the exception injected at {raise_at!r} did not propagate unchanged")
                ok = False
            warned = "UNINSTALLED" in joined
            if warned != must_warn:
                print(
                    f"  FAILED: an exception at {raise_at!r} printed the UNINSTALLED warning={warned}, "
                    f"want {must_warn}"
                )
                ok = False
            if "install --global --add-source bin/pack baton" not in joined and must_warn:
                print(f"  FAILED: the warning at {raise_at!r} did not carry the recovery command")
                ok = False

    return ok


def _selftest_progress_is_throttled_under_wait() -> bool:
    """F4: --wait re-reads liveness every POLL_S but reprints only every PROGRESS_EVERY_S."""
    import tempfile

    ok = True
    with tempfile.TemporaryDirectory() as td:
        rooms_root = os.path.join(td, "rooms")
        os.makedirs(rooms_root)
        live_room = _make_room(rooms_root, "live-room", terminal=False)

        clock = {"now": 0.0}
        polls = {"count": 0}
        # POLL_S ticks needed to cover two progress windows, plus one that clears.
        polls_until_clear = int(PROGRESS_EVERY_S / POLL_S) * 2 + 1

        def run(cmd: List[str]) -> CommandResult:
            polls["count"] += 1
            if polls["count"] > polls_until_clear:
                return _fake_status_json("Succeeded", "dead")
            return _fake_status_json("Running", "alive")

        deps = Deps(
            run=run, baton_home=td, rooms_root=rooms_root, repo_root=td,
            nuget_packages_root=os.path.join(td, "nuget"),
            sleep=lambda seconds: clock.__setitem__("now", clock["now"] + seconds),
            monotonic=lambda: clock["now"])
        _assert_isolated(deps)

        messages: List[str] = []
        if not drain(deps, wait=True, print_fn=messages.append):
            print("  FAILED: drain() never cleared even after the room went dead")
            ok = False
        blocked_lines = [m for m in messages if "BLOCKED by" in m]
        expected_prints = 1 + int((polls_until_clear - 1) * POLL_S // PROGRESS_EVERY_S)
        if len(blocked_lines) != expected_prints:
            print(
                f"  FAILED: {len(blocked_lines)} BLOCKED line(s) over {polls_until_clear} poll(s), "
                f"want {expected_prints} at a {PROGRESS_EVERY_S:.0f}s cadence"
            )
            ok = False
        if polls["count"] <= len(blocked_lines):
            print("  control FAILED: liveness was not re-read more often than it was printed")
            ok = False
        if live_room not in "\n".join(blocked_lines):
            print("  FAILED: the throttled progress line stopped naming the remaining room")
            ok = False

        # Polarity: without --wait there is one pass, and it is never throttled.
        polls["count"] = 0
        clock["now"] = 0.0
        single: List[str] = []
        if drain(deps, wait=False, print_fn=single.append):
            print("  FAILED: a single-pass drain reported clear with a live room present")
            ok = False
        single_blocked = [m for m in single if "BLOCKED by" in m]
        if len(single_blocked) != 1:
            print(f"  FAILED: a single-pass drain printed {len(single_blocked)} BLOCKED lines, want one")
            ok = False

    return ok


def selftest() -> int:
    arms = [
        ("drain predicate (live vs terminal rooms)", _selftest_drain_predicate),
        ("drain marker written before the scan, removed on every exit path", _selftest_drain_marker_lifecycle),
        ("the marker filename this tool writes is the one the CLI reads", _selftest_marker_filename_matches_the_cli),
        ("version compare", _selftest_version_compare),
        ("fail-loud on uninstall/install failure", _selftest_fail_loud_on_uninstall_or_install),
        ("fail-loud on an exception after uninstall", _selftest_fail_loud_on_exception_after_uninstall),
        ("--wait progress is throttled to PROGRESS_EVERY_S", _selftest_progress_is_throttled_under_wait),
        ("--dry-run executes no mutating command", _selftest_dry_run_touches_nothing),
    ]
    ok = True
    for name, fn in arms:
        print(f"selftest: {name}")
        if not fn():
            ok = False

    print("selftest: pass" if ok else "selftest: FAIL")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
