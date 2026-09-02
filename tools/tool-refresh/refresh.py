"""One-command refresh of the installed `baton` global tool (#1645).

The hand sequence this replaces, and why each step exists, is the issue's own "Measured friction"
(2026-09-01/02): wait for lanes to stop holding the exe open, `pixi run pack`, uninstall, purge the
NuGet cache (mandatory -- it silently serves a stale same-version package otherwise, bit twice on
8/30), install from `bin/pack`, then verify by hand that the reinstall actually took. Skipping the
drain step is what makes `dotnet tool uninstall` fail access-denied; skipping the cache purge is what
makes the reinstall silently keep serving the old build; skipping verification is what let the
conductor run 0.25.0 all afternoon on 2026-09-01 with no telemetry while five PRs merged underneath it.

Usage:      pixi run tool-refresh [--wait] [--dry-run]
Selftest:   pixi run tool-refresh-selftest   (python tools/tool-refresh/refresh.py --selftest)

Drain predicate (item 1): a room under BATON_HOME/rooms (default ~/.baton/rooms) blocks the refresh
when it has no `terminal.json` AND `baton status <room-dir> --json` reports a step with
`liveness: "alive"` -- the same liveness `baton status`'s own human rendering and `baton resume`'s
STALLED reconciliation already compute (EngineLivenessProbe, src/Baton/Outcomes/EngineLivenessProbe.cs)
-- reused here via the CLI's own `--json` surface rather than reimplemented against a PID. A step
reporting `liveness: "unknown"` blocks too (ambiguous holder, fail closed) but is labelled separately
from `alive` in what gets printed. A room `baton status --json` cannot even read (no snapshot -- a
provisioning crash before any ledger existed) is not a live holder -- there is no engine process to be
holding the exe open -- so it is never blocking, only reported for visibility.

Dry run (--dry-run): the drain check still runs for real (read-only). Every mutating step -- pack,
uninstall, cache purge, install, verify -- prints the exact command it would run and does nothing.
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
PROGRESS_EVERY_S = 15.0


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
    rooms_root: str = ""
    nuget_packages_root: str = ""
    sleep: Callable[[float], None] = time.sleep
    monotonic: Callable[[], float] = time.monotonic
    out: "Sequence[str]" = field(default_factory=list)  # unused; printing goes straight to stdout

    def __post_init__(self) -> None:
        if not self.repo_root:
            self.repo_root = default_repo_root()
        if not self.rooms_root:
            self.rooms_root = os.path.join(default_baton_home(), "rooms")
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
# Item 1: drain -- refuse to start while a room is live.
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
    (polling every POLL_S) under --wait, otherwise returns the first read's verdict immediately."""
    while True:
        verdicts = scan_rooms(deps.rooms_root, deps.run)
        blocking = [v for v in verdicts if v.category in ("alive", "unknown")]
        unreadable = [v for v in verdicts if v.category == "unreadable"]

        for v in unreadable:
            print_fn(f"tool-refresh: skipping {v.room_dir} (not a live holder -- {v.detail})")

        if not blocking:
            if verdicts:
                print_fn(f"tool-refresh: drain clear -- {len(verdicts)} non-terminal room(s) checked, none live")
            else:
                print_fn("tool-refresh: drain clear -- no non-terminal rooms found")
            return True

        for v in blocking:
            print_fn(f"tool-refresh: BLOCKED by {v.room_dir} ({v.category}: {v.detail})")

        if not wait:
            print_fn(
                f"tool-refresh: {len(blocking)} room(s) still live -- refusing to uninstall while a "
                "lane may hold the exe open. Pass --wait to block until they finish, or re-run once "
                "they have."
            )
            return False

        print_fn(f"tool-refresh: waiting on {len(blocking)} live room(s) ({POLL_S:.0f}s poll)...")
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
        print_fn(WARNING_NOT_LEFT_UNINSTALLED)
        return 1

    smoke_result = deps.run(["baton", "templates", "--json"])
    if smoke_result.returncode != 0:
        print_fn(
            f"tool-refresh: verify failed -- 'baton templates --json' exited "
            f"{smoke_result.returncode}: {smoke_result.stderr.strip()}"
        )
        print_fn(WARNING_NOT_LEFT_UNINSTALLED)
        return 1

    print_fn(f"tool-refresh: verified -- baton {version} installed and responding")
    print_fn("tool-refresh: resume your lanes -- `baton status <room-dir>` for any that were waiting")
    return 0


def main(argv: Optional[List[str]] = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--wait", action="store_true", help="block until every live room finishes, then proceed")
    parser.add_argument("--dry-run", action="store_true", help="print every command; run none of the mutating ones")
    parser.add_argument("--selftest", action="store_true", help=argparse.SUPPRESS)
    args = parser.parse_args(argv)

    if args.selftest:
        return selftest()

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
        deps = Deps(run=run, rooms_root=rooms_root, repo_root=td, nuget_packages_root=os.path.join(td, "nuget"))
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

        def make_deps(fail_at: str) -> Deps:
            def run(cmd: List[str]) -> CommandResult:
                if cmd[:2] == ["dotnet", "tool"] and cmd[2] == "list":
                    return CommandResult(0, "Package Id      Version\nbaton           8.0.0\n")
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

            return Deps(run=run, rooms_root=rooms_root, repo_root=repo_root,
                        nuget_packages_root=os.path.join(td, "nuget"))

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

        deps = Deps(run=run, rooms_root=rooms_root, repo_root=td, nuget_packages_root=os.path.join(td, "nuget"))
        messages: List[str] = []
        code = refresh(deps, wait=False, dry_run=True, print_fn=messages.append)
        if code != 0:
            print(f"  FAILED: --dry-run against a clear drain exited {code}, want 0")
            ok = False
        if not any("[dry-run]" in m for m in messages):
            print("  FAILED: --dry-run printed no [dry-run] lines at all")
            ok = False

    return ok


def selftest() -> int:
    arms = [
        ("drain predicate (live vs terminal rooms)", _selftest_drain_predicate),
        ("version compare", _selftest_version_compare),
        ("fail-loud on uninstall/install failure", _selftest_fail_loud_on_uninstall_or_install),
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
