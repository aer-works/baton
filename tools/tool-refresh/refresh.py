"""Side-by-side per-commit refresh of the `baton` tool (#1668).

Replaces the single-global-tool drain cycle (#1645) with isolated side-by-side installs:
- Pack `baton` from this checkout.
- Install into `~/.baton/tools/<short-sha>` via `dotnet tool install baton --tool-path ... --add-source bin/pack`.
- Run sanity invocations against the newly installed executable.
- Atomically flip `~/.baton/tools/current` pointer file (temp file + rename).
- Ensure launcher scripts (`baton.cmd`, `baton.ps1`, `baton`) are installed in `~/.dotnet/tools` on PATH,
- Build Debug CLI for the pusher task.
- Restart the `fleet-glass-pusher` scheduled task.
- Prune unreferenced versions older than the top 3 installs.
- No drain wait; no `draining.json` write. Keep `draining.json` honoured by dispatch as an operator-invoked stop only.

Usage:      pixi run tool-refresh [--dry-run] | pixi run tool-refresh --abort
Selftest:   pixi run tool-refresh-selftest   (python tools/tool-refresh/refresh.py --selftest)
"""
import argparse
import json
import os
import re
import shutil
import subprocess
import sys
import time
import uuid
from dataclasses import dataclass, field
from typing import Callable, List, Optional, Sequence, Set

VERSION_ELEMENT = re.compile(r"<Version>\s*(?P<version>\S+?)\s*</Version>")
VERSION_PROPS_RELATIVE_PATH = os.path.join("src", "Baton.Cli", "Directory.Build.props")

DRAIN_MARKER_FILENAME = "draining.json"
DRAIN_MARKER_REASON = "tool-refresh"
ABORT_INVOCATION = "pixi run tool-refresh --abort"

BATON_PATHS_RELATIVE_PATH = os.path.join("src", "Baton", "Status", "BatonPaths.cs")
DRAIN_MARKER_CONST = re.compile(r"DrainMarkerFileName\s*=\s*\"(?P<name>[^\"]+)\"")
DRAIN_MARKER_TYPE_RELATIVE_PATH = os.path.join("src", "Baton", "Status", "DrainMarker.cs")
ABORT_INVOCATION_CONST = re.compile(r"AbortInvocation\s*=\s*\"(?P<invocation>[^\"]+)\"")


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
    real `dotnet`/`baton`/`pixi` or touches this machine's real NuGet cache, ~/.baton or ~/.dotnet/tools."""

    run: Runner = real_runner
    repo_root: str = ""
    baton_home: str = ""
    rooms_root: str = ""
    tools_root: str = ""
    dotnet_tools_root: str = ""
    nuget_packages_root: str = ""
    sleep: Callable[[float], None] = time.sleep
    monotonic: Callable[[], float] = time.monotonic
    out: "Sequence[str]" = field(default_factory=list)

    def __post_init__(self) -> None:
        if not self.repo_root:
            self.repo_root = default_repo_root()
        if not self.baton_home:
            self.baton_home = default_baton_home()
        if not self.rooms_root:
            self.rooms_root = os.path.join(self.baton_home, "rooms")
        if not self.tools_root:
            self.tools_root = os.path.join(self.baton_home, "tools")
        if not self.dotnet_tools_root:
            self.dotnet_tools_root = os.path.join(os.path.expanduser("~"), ".dotnet", "tools")
        if not self.nuget_packages_root:
            self.nuget_packages_root = os.environ.get(
                "NUGET_PACKAGES", os.path.join(os.path.expanduser("~"), ".nuget", "packages"))


def default_repo_root() -> str:
    return os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))


def default_baton_home() -> str:
    override = os.environ.get("BATON_HOME", "").strip()
    return override if override else os.path.join(os.path.expanduser("~"), ".baton")


def read_repo_version(repo_root: str) -> Optional[str]:
    """The version `baton --version` will report once packed from this checkout."""
    props_path = os.path.join(repo_root, VERSION_PROPS_RELATIVE_PATH)
    try:
        with open(props_path, "r", encoding="utf-8") as f:
            text = f.read()
    except OSError:
        return None
    match = VERSION_ELEMENT.search(text)
    return match.group("version") if match else None


def read_repo_commit_sha(deps: Deps) -> Optional[str]:
    """Resolves the current git commit short SHA for this checkout."""
    result = deps.run(["git", "-C", deps.repo_root, "rev-parse", "--short", "HEAD"])
    if result.returncode == 0 and result.stdout.strip():
        return result.stdout.strip()
    # Fallback to full rev-parse truncated
    res_full = deps.run(["git", "-C", deps.repo_root, "rev-parse", "HEAD"])
    if res_full.returncode == 0 and res_full.stdout.strip():
        return res_full.stdout.strip()[:8]
    return None


def drain_marker_path(deps: Deps) -> str:
    return os.path.join(deps.baton_home, DRAIN_MARKER_FILENAME)


def remove_drain_marker(deps: Deps, dry_run: bool, print_fn: Callable[[str], None]) -> bool:
    """Removes a drain marker if present. Used by `--abort`."""
    path = drain_marker_path(deps)
    if dry_run:
        print_fn(f"tool-refresh: [dry-run] would remove drain marker: {path}")
        return False

    try:
        os.remove(path)
    except FileNotFoundError:
        return False
    except OSError as exc:
        print_fn(f"tool-refresh: could not remove the drain marker {path} ({exc})")
        return False
    print_fn(f"tool-refresh: drain marker removed: {path}")
    return True


def abort(deps: Deps, print_fn: Callable[[str], None], dry_run: bool = False) -> int:
    """`--abort`: clear an operator-written drain marker and do nothing else."""
    if dry_run:
        remove_drain_marker(deps, dry_run=True, print_fn=print_fn)
        return 0

    if remove_drain_marker(deps, dry_run=False, print_fn=print_fn):
        return 0
    print_fn(f"tool-refresh: no drain marker at {drain_marker_path(deps)} -- nothing to abort")
    return 0


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
        try:
            shutil.rmtree(cache_dir)
            print_fn(f"tool-refresh: purged NuGet cache {cache_dir}")
        except OSError as exc:
            print_fn(f"tool-refresh: warning: could not purge NuGet cache {cache_dir}: {exc}")
    else:
        print_fn(f"tool-refresh: NuGet cache {cache_dir} already absent, nothing to purge")


def read_current_pointer(tools_root: str) -> Optional[str]:
    current_file = os.path.join(tools_root, "current")
    if not os.path.isfile(current_file):
        return None
    try:
        with open(current_file, "r", encoding="utf-8") as f:
            return f.read().strip() or None
    except OSError:
        return None


def verify_target_exe(deps: Deps, tool_dir: str, version: str) -> bool:
    """Returns True iff tool_dir's binary reports `version` via --version and passes the
    `templates --json` smoke check. Shared by the fresh-install verify gate and by the idempotent
    re-refresh check (F4, #1670 review) that decides whether an already-installed SHA can be reused
    as-is."""
    target_exe = os.path.join(tool_dir, "baton.exe" if (sys.platform == "win32" or os.name == "nt") else "baton")
    if not os.path.isfile(target_exe):
        return False
    version_result = deps.run([target_exe, "--version"])
    if version_result.returncode != 0 or version_result.stdout.strip() != version:
        return False
    smoke_result = deps.run([target_exe, "templates", "--json"])
    return smoke_result.returncode == 0


def write_current_pointer(tools_root: str, sha: str, dry_run: bool, print_fn: Callable[[str], None]) -> bool:
    """Atomically updates ~/.baton/tools/current to point at sha."""
    current_path = os.path.join(tools_root, "current")
    if dry_run:
        print_fn(f"tool-refresh: [dry-run] would atomically write current pointer '{sha}' to {current_path}")
        return True

    os.makedirs(tools_root, exist_ok=True)
    tmp_path = os.path.join(tools_root, f"current.tmp.{uuid.uuid4().hex}")
    try:
        with open(tmp_path, "w", encoding="utf-8") as f:
            f.write(f"{sha}\n")
        os.replace(tmp_path, current_path)
        print_fn(f"tool-refresh: flipped current pointer to {sha}")
        return True
    except OSError as exc:
        print_fn(f"tool-refresh: could not write current pointer to {current_path}: {exc}")
        return False
    finally:
        if os.path.exists(tmp_path):
            try:
                os.remove(tmp_path)
            except OSError:
                pass


def sweep_stale_launcher_backups(deps: Deps, dry_run: bool, print_fn: Callable[[str], None]) -> None:
    """F6 (#1670 review): install_launcher's rename fallback leaves baton.exe.old.<guid> files behind
    on every failed-uninstall/failed-delete event; nothing else in this tool ever cleaned them up.
    Sweeps them on each refresh, skipping any still locked by a live process."""
    if dry_run or not os.path.isdir(deps.dotnet_tools_root):
        return
    for name in os.listdir(deps.dotnet_tools_root):
        if not name.startswith("baton.exe.old."):
            continue
        path = os.path.join(deps.dotnet_tools_root, name)
        try:
            os.remove(path)
            print_fn(f"tool-refresh: swept stale launcher backup {path}")
        except OSError:
            pass  # still locked -- leave it for a later refresh


def install_launcher(deps: Deps, dry_run: bool, print_fn: Callable[[str], None]) -> bool:
    """Installs the baton launcher scripts into ~/.dotnet/tools, uninstalling any legacy global tool.
    Returns False (refresh must fail closed, F5) if a stale baton.exe is still present afterward --
    PATHEXT resolves .exe before .cmd/.ps1, so a leftover global-tool shim would silently shadow the
    launcher on every bare `baton` invocation."""
    if dry_run:
        print_fn("tool-refresh: [dry-run] would uninstall legacy global baton tool if present")
    elif dotnet_tool_installed(deps):
        uninstall_res = run_step(deps, ["dotnet", "tool", "uninstall", "--global", "baton"], dry_run, print_fn)
        if uninstall_res.returncode != 0:
            print_fn(f"tool-refresh: warning: failed to uninstall global baton tool (exit {uninstall_res.returncode}): {uninstall_res.stderr.strip()}")
            legacy_exe = os.path.join(deps.dotnet_tools_root, "baton.exe")
            if os.path.isfile(legacy_exe):
                try:
                    os.remove(legacy_exe)
                    print_fn("tool-refresh: removed legacy baton.exe shim from ~/.dotnet/tools")
                except OSError:
                    try:
                        old_exe = os.path.join(deps.dotnet_tools_root, f"baton.exe.old.{uuid.uuid4().hex}")
                        os.rename(legacy_exe, old_exe)
                        print_fn("tool-refresh: renamed legacy baton.exe shim to allow launcher scripts to resolve")
                    except OSError as exc2:
                        print_fn(f"tool-refresh: warning: could not remove legacy baton.exe: {exc2}")
        else:
            print_fn("tool-refresh: uninstalled legacy global baton tool to allow launcher scripts to resolve on PATH")

    if not dry_run:
        legacy_exe = os.path.join(deps.dotnet_tools_root, "baton.exe")
        if os.path.isfile(legacy_exe):
            print_fn(
                f"tool-refresh: baton.exe is still present at {legacy_exe} after uninstall/remove/rename -- "
                "PATHEXT resolves .exe before .cmd/.ps1, so a bare `baton` would keep silently running the "
                "stale global tool instead of the launcher. Refusing to declare the refresh done; close "
                "whatever process holds it and re-run."
            )
            return False

    sweep_stale_launcher_backups(deps, dry_run, print_fn)

    launcher_dir = os.path.join(deps.repo_root, "tools", "tool-refresh", "launcher")
    if not dry_run:
        os.makedirs(deps.dotnet_tools_root, exist_ok=True)

    for name in ["baton.cmd", "baton.ps1", "baton"]:
        src = os.path.join(launcher_dir, name)
        dst = os.path.join(deps.dotnet_tools_root, name)
        if dry_run:
            print_fn(f"tool-refresh: [dry-run] would copy {src} to {dst}")
            continue
        if os.path.isfile(src):
            shutil.copy2(src, dst)

    if not dry_run:
        print_fn(f"tool-refresh: launcher scripts installed in {deps.dotnet_tools_root}")
    return True


def scan_live_room_shas(rooms_root: str) -> Set[str]:
    """Finds all tool SHAs referenced in live (non-terminal) rooms."""
    live_shas: Set[str] = set()
    if not os.path.isdir(rooms_root):
        return live_shas

    for rname in os.listdir(rooms_root):
        rdir = os.path.join(rooms_root, rname)
        if not os.path.isdir(rdir):
            continue
        # Non-terminal check
        if os.path.isfile(os.path.join(rdir, "terminal.json")):
            continue
        bpath = os.path.join(rdir, "bindings.json")
        if os.path.isfile(bpath):
            try:
                with open(bpath, "r", encoding="utf-8") as f:
                    bdata = json.load(f)
                if isinstance(bdata, dict):
                    for entry in bdata.values():
                        if isinstance(entry, dict):
                            sha = entry.get("ToolSha") or entry.get("tool_sha")
                            if sha and isinstance(sha, str):
                                live_shas.add(sha.strip())
            except Exception:
                pass
    return live_shas


def prune_tools(deps: Deps, dry_run: bool, print_fn: Callable[[str], None], keep_count: int = 3) -> List[str]:
    """Cleans legacy tool installations while retaining the newest keep_count and active room versions."""
    tools_root = deps.tools_root
    if not os.path.isdir(tools_root):
        return []

    current_file = os.path.join(tools_root, "current")
    current_sha: Optional[str] = None
    if os.path.isfile(current_file):
        try:
            with open(current_file, "r", encoding="utf-8") as f:
                current_sha = f.read().strip()
        except OSError:
            pass

    entries = []
    for name in os.listdir(tools_root):
        dir_path = os.path.join(tools_root, name)
        if os.path.isdir(dir_path) and name != "current":
            try:
                mtime = os.path.getmtime(dir_path)
            except OSError:
                mtime = 0.0
            entries.append((name, mtime, dir_path))

    # Sort descending by mtime (newest first)
    entries.sort(key=lambda x: x[1], reverse=True)

    live_shas = scan_live_room_shas(deps.rooms_root)
    pruned: List[str] = []

    for idx, (sha, _, dir_path) in enumerate(entries):
        if idx < keep_count:
            continue
        if sha in live_shas:
            continue
        if current_sha and sha == current_sha:
            continue

        if dry_run:
            print_fn(f"tool-refresh: [dry-run] would prune old tool directory: {dir_path}")
            pruned.append(sha)
        else:
            try:
                shutil.rmtree(dir_path)
                print_fn(f"tool-refresh: pruned old tool directory: {dir_path}")
                pruned.append(sha)
            except OSError as exc:
                print_fn(f"tool-refresh: warning: could not prune {dir_path}: {exc}")

    return pruned


def refresh(deps: Deps, dry_run: bool, print_fn: Callable[[str], None]) -> int:
    """Executes the side-by-side refresh: pack -> install -> verify -> flip pointer -> launcher -> pusher -> prune."""
    version = read_repo_version(deps.repo_root)
    if version is None:
        print_fn(
            f"tool-refresh: could not read a <Version> from {VERSION_PROPS_RELATIVE_PATH} under "
            f"{deps.repo_root} -- refusing to proceed."
        )
        return 1

    sha = read_repo_commit_sha(deps)
    if sha is None:
        print_fn(f"tool-refresh: could not resolve current git commit SHA under {deps.repo_root}")
        return 1

    print_fn(f"tool-refresh: checkout version is {version}, commit is {sha}")

    pack_result = run_step(deps, ["pixi", "run", "pack"], dry_run, print_fn)
    if pack_result.returncode != 0:
        print_fn(f"tool-refresh: pack failed (exit {pack_result.returncode}): {pack_result.stderr.strip()}")
        return 1

    expected_nupkg = os.path.join(deps.repo_root, "bin", "pack", f"baton.{version}.nupkg")
    if not dry_run and not os.path.isfile(expected_nupkg):
        print_fn(f"tool-refresh: pack reported success but {expected_nupkg} does not exist -- refusing to install.")
        return 1

    purge_nuget_cache(deps, version, dry_run, print_fn)

    tool_dir = os.path.join(deps.tools_root, sha)
    skip_install = False

    # F4 (#1670 review): a re-refresh at an unchanged HEAD must never rmtree a directory a live lane
    # loaded from. If the SHA is already installed and verifies, this is a no-op -- skip straight to
    # the pointer flip. If it exists but fails verify, only remove it when nothing live references it
    # (current pointer or a non-terminal room's ToolSha); otherwise install into a fresh `<sha>-<n>`
    # side path and flip there instead of touching the directory in place.
    if not dry_run and os.path.isdir(tool_dir):
        if verify_target_exe(deps, tool_dir, version):
            print_fn(f"tool-refresh: {sha} is already installed and verified at {tool_dir} -- skipping reinstall")
            skip_install = True
        else:
            current_sha = read_current_pointer(deps.tools_root)
            live_shas = scan_live_room_shas(deps.rooms_root)
            is_live = sha == current_sha or sha in live_shas
            if is_live:
                suffix = 1
                candidate = f"{tool_dir}-{suffix}"
                while os.path.isdir(candidate):
                    suffix += 1
                    candidate = f"{tool_dir}-{suffix}"
                tool_dir = candidate
                print_fn(
                    f"tool-refresh: {sha} exists at {os.path.join(deps.tools_root, sha)} but failed "
                    f"verification and is live -- installing into {tool_dir} instead of touching a "
                    "directory a running lane may be using"
                )
            else:
                try:
                    shutil.rmtree(tool_dir)
                    print_fn(f"tool-refresh: {sha} exists but failed verification and is not live -- reinstalling at {tool_dir}")
                except OSError as exc:
                    print_fn(f"tool-refresh: warning: could not clean existing tool directory {tool_dir}: {exc}")

    if not skip_install:
        install_cmd = [
            "dotnet", "tool", "install", "baton",
            "--tool-path", tool_dir,
            "--add-source", "bin/pack",
        ]
        install_result = run_step(deps, install_cmd, dry_run, print_fn)
        if install_result.returncode != 0:
            print_fn(f"tool-refresh: install failed (exit {install_result.returncode}): {install_result.stderr.strip()}")
            return 1

        target_exe = os.path.join(tool_dir, "baton.exe" if (sys.platform == "win32" or os.name == "nt") else "baton")

        if dry_run:
            print_fn(f"tool-refresh: [dry-run] would verify directly: '{target_exe} --version' == {version} and 'templates --json'")
        else:
            version_result = deps.run([target_exe, "--version"])
            installed_version = version_result.stdout.strip()
            if version_result.returncode != 0 or installed_version != version:
                print_fn(
                    f"tool-refresh: verify failed -- '{target_exe} --version' printed "
                    f"{installed_version!r} (exit {version_result.returncode}), expected {version!r}."
                )
                return 1

            smoke_result = deps.run([target_exe, "templates", "--json"])
            if smoke_result.returncode != 0:
                print_fn(
                    f"tool-refresh: verify failed -- '{target_exe} templates --json' exited "
                    f"{smoke_result.returncode}: {smoke_result.stderr.strip()}"
                )
                return 1

    # Atomically flip pointer -- the actual installed directory name, which may be a `<sha>-<n>`
    # side path rather than `sha` itself (see the live-directory guard above).
    pointer_sha = sha if dry_run else os.path.basename(tool_dir)
    if not write_current_pointer(deps.tools_root, pointer_sha, dry_run, print_fn):
        return 1

    # Ensure launcher on PATH -- fails closed (F5) if a stale global-tool baton.exe still shadows it
    if not install_launcher(deps, dry_run, print_fn):
        return 1

    # Rebuild Baton.Cli Debug for the pusher
    rebuild_result = run_step(deps, ["dotnet", "build", "src/Baton.Cli"], dry_run, print_fn)
    if rebuild_result.returncode != 0:
        print_fn(
            f"tool-refresh: warning: rebuilding Baton.Cli Debug failed (exit {rebuild_result.returncode}): "
            f"{rebuild_result.stderr.strip()}"
        )
    else:
        print_fn("tool-refresh: rebuilt Baton.Cli Debug for fleet-glass pusher")

    # Restart scheduled task
    if sys.platform == "win32" or os.name == "nt":
        restart_cmd = [
            "powershell", "-NoProfile", "-Command",
            "Stop-ScheduledTask -TaskName fleet-glass-pusher -ErrorAction SilentlyContinue; "
            "Start-ScheduledTask -TaskName fleet-glass-pusher -ErrorAction SilentlyContinue",
        ]
        run_step(deps, restart_cmd, dry_run, print_fn)
        print_fn("tool-refresh: restarted fleet-glass-pusher scheduled task")

    # Prune old tool installations
    prune_tools(deps, dry_run, print_fn, keep_count=3)

    print_fn(f"tool-refresh: verified -- baton {version} ({pointer_sha}) installed at {tool_dir} and active")
    return 0


def main(argv: Optional[List[str]] = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--dry-run", action="store_true", help="print every command; run none of the mutating ones")
    parser.add_argument(
        "--abort", action="store_true",
        help="remove a manual drain marker and do nothing else")
    parser.add_argument("--selftest", action="store_true", help=argparse.SUPPRESS)
    args = parser.parse_args(argv)

    if args.selftest:
        return selftest()

    if args.abort:
        return abort(Deps(), print, args.dry_run)

    return refresh(Deps(), args.dry_run, print)


# ---------------------------------------------------------------------------------------------
# Selftest
# ---------------------------------------------------------------------------------------------

def _assert_isolated(deps: Deps) -> None:
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
    real_dotnet_tools = os.path.realpath(os.path.join(os.path.expanduser("~"), ".dotnet", "tools"))
    if os.path.realpath(deps.dotnet_tools_root) == real_dotnet_tools:
        raise AssertionError(
            f"selftest fixture resolved the REAL dotnet tools root ({real_dotnet_tools}) -- refusing to run")


def _fixture_repo(td: str, version: str) -> str:
    props_dir = os.path.join(td, "src", "Baton.Cli")
    os.makedirs(props_dir, exist_ok=True)
    with open(os.path.join(props_dir, "Directory.Build.props"), "w", encoding="utf-8") as f:
        f.write(f"<Project><PropertyGroup><Version>{version}</Version></PropertyGroup></Project>")
    pack_dir = os.path.join(td, "bin", "pack")
    os.makedirs(pack_dir, exist_ok=True)
    open(os.path.join(pack_dir, f"baton.{version}.nupkg"), "w", encoding="utf-8").close()

    launcher_dir = os.path.join(td, "tools", "tool-refresh", "launcher")
    os.makedirs(launcher_dir, exist_ok=True)
    for name in ["baton.cmd", "baton.ps1", "baton"]:
        open(os.path.join(launcher_dir, name), "w", encoding="utf-8").close()
    return td


def _make_room(rooms_root: str, name: str, terminal: bool, tool_sha: Optional[str] = None) -> str:
    room_dir = os.path.join(rooms_root, name)
    os.makedirs(room_dir, exist_ok=True)
    if terminal:
        with open(os.path.join(room_dir, "terminal.json"), "w", encoding="utf-8") as f:
            f.write("{}")
    if tool_sha is not None:
        bindings = {
            "worker": {
                "Adapter": "claude",
                "ToolSha": tool_sha,
            }
        }
        with open(os.path.join(room_dir, "bindings.json"), "w", encoding="utf-8") as f:
            json.dump(bindings, f)
    return room_dir


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


def _selftest_pointer_flip_atomic() -> bool:
    import tempfile

    ok = True
    with tempfile.TemporaryDirectory() as td:
        tools_dir = os.path.join(td, "tools")
        messages: List[str] = []
        if not write_current_pointer(tools_dir, "abc12345", dry_run=False, print_fn=messages.append):
            print("  FAILED: write_current_pointer failed")
            ok = False

        current_file = os.path.join(tools_dir, "current")
        if not os.path.isfile(current_file):
            print("  FAILED: current pointer file was not created")
            ok = False
        else:
            with open(current_file, "r", encoding="utf-8") as f:
                content = f.read().strip()
            if content != "abc12345":
                print(f"  FAILED: current pointer has {content!r}, want 'abc12345'")
                ok = False

        # Flip to another sha
        if not write_current_pointer(tools_dir, "def67890", dry_run=False, print_fn=messages.append):
            print("  FAILED: secondary write_current_pointer failed")
            ok = False
        with open(current_file, "r", encoding="utf-8") as f:
            content = f.read().strip()
        if content != "def67890":
            print(f"  FAILED: updated current pointer has {content!r}, want 'def67890'")
            ok = False

    return ok


def _selftest_prune_logic() -> bool:
    import tempfile

    ok = True
    with tempfile.TemporaryDirectory() as td:
        baton_home = td
        tools_root = os.path.join(td, "tools")
        rooms_root = os.path.join(td, "rooms")
        dotnet_tools_root = os.path.join(td, "dotnet_tools")
        nuget_root = os.path.join(td, "nuget")
        os.makedirs(tools_root)
        os.makedirs(rooms_root)

        # Create 5 tool directories with different mtimes
        shas = ["sha1_newest", "sha2", "sha3", "sha4_referenced", "sha5_old"]
        for idx, sha in enumerate(shas):
            sdir = os.path.join(tools_root, sha)
            os.makedirs(sdir)
            # Higher mtime for earlier index (newest)
            mtime = 1000.0 - (idx * 100.0)
            os.utime(sdir, (mtime, mtime))

        # Write current pointer to sha1_newest
        with open(os.path.join(tools_root, "current"), "w", encoding="utf-8") as f:
            f.write("sha1_newest\n")

        # Create live room referencing sha4_referenced
        _make_room(rooms_root, "live-room-1", terminal=False, tool_sha="sha4_referenced")
        # Create terminal room referencing sha5_old
        _make_room(rooms_root, "term-room-1", terminal=True, tool_sha="sha5_old")

        deps = Deps(
            run=real_runner, baton_home=baton_home, rooms_root=rooms_root,
            tools_root=tools_root, dotnet_tools_root=dotnet_tools_root,
            nuget_packages_root=nuget_root, repo_root=td
        )
        _assert_isolated(deps)

        messages: List[str] = []
        pruned = prune_tools(deps, dry_run=False, print_fn=messages.append, keep_count=3)

        # sha1, sha2, sha3 are top 3 -> kept.
        # sha4 is 4th, but referenced in live room -> kept.
        # sha5 is 5th, referenced only in terminal room -> pruned!
        if pruned != ["sha5_old"]:
            print(f"  FAILED: prune_tools pruned {pruned!r}, want ['sha5_old']")
            ok = False

        if os.path.exists(os.path.join(tools_root, "sha5_old")):
            print("  FAILED: sha5_old directory still exists on disk")
            ok = False
        if not os.path.exists(os.path.join(tools_root, "sha4_referenced")):
            print("  FAILED: sha4_referenced was wrongly deleted")
            ok = False
        if not os.path.exists(os.path.join(tools_root, "sha1_newest")):
            print("  FAILED: sha1_newest was wrongly deleted")
            ok = False

    return ok


def _selftest_refresh_end_to_end_mocked() -> bool:
    import tempfile

    ok = True
    with tempfile.TemporaryDirectory() as td:
        baton_home = os.path.join(td, "baton")
        tools_root = os.path.join(baton_home, "tools")
        rooms_root = os.path.join(baton_home, "rooms")
        dotnet_tools_root = os.path.join(td, "dotnet_tools")
        nuget_root = os.path.join(td, "nuget")
        repo_root = _fixture_repo(os.path.join(td, "repo"), "2.3.4")

        commands_run: List[List[str]] = []

        def run(cmd: List[str]) -> CommandResult:
            commands_run.append(cmd)
            if cmd[:3] == ["git", "-C", repo_root] and cmd[3:5] == ["rev-parse", "--short"]:
                return CommandResult(0, "c0ffee11\n")
            if cmd[:3] == ["pixi", "run", "pack"]:
                return CommandResult(0)
            if cmd[:3] == ["dotnet", "tool", "list"]:
                return CommandResult(0, "Package Id      Version\nbaton           1.0.0\n")
            if cmd[:4] == ["dotnet", "tool", "uninstall", "--global"]:
                return CommandResult(0)
            if cmd[:4] == ["dotnet", "tool", "install", "baton"]:
                # Create fake target exe
                tool_dir = os.path.join(tools_root, "c0ffee11")
                os.makedirs(tool_dir, exist_ok=True)
                target_exe = os.path.join(tool_dir, "baton.exe" if (sys.platform == "win32" or os.name == "nt") else "baton")
                open(target_exe, "w").close()
                return CommandResult(0)
            if len(cmd) == 2 and cmd[1] == "--version":
                return CommandResult(0, "2.3.4\n")
            if len(cmd) == 3 and cmd[1:] == ["templates", "--json"]:
                return CommandResult(0, "[]\n")
            if cmd[:3] == ["dotnet", "build", "src/Baton.Cli"]:
                return CommandResult(0)
            if cmd[0] == "powershell":
                return CommandResult(0)
            raise AssertionError(f"unexpected command in refresh selftest: {cmd}")

        deps = Deps(
            run=run, baton_home=baton_home, rooms_root=rooms_root,
            tools_root=tools_root, dotnet_tools_root=dotnet_tools_root,
            nuget_packages_root=nuget_root, repo_root=repo_root
        )
        _assert_isolated(deps)

        messages: List[str] = []
        code = refresh(deps, dry_run=False, print_fn=messages.append)
        if code != 0:
            print(f"  FAILED: refresh exited {code}, want 0. Messages: {messages}")
            ok = False

        current_file = os.path.join(tools_root, "current")
        if not os.path.isfile(current_file):
            print("  FAILED: current pointer file missing after refresh")
            ok = False
        else:
            with open(current_file, "r", encoding="utf-8") as f:
                if f.read().strip() != "c0ffee11":
                    print("  FAILED: current pointer did not contain 'c0ffee11'")
                    ok = False

        # Launcher files must be copied to dotnet_tools_root
        for name in ["baton.cmd", "baton.ps1", "baton"]:
            if not os.path.isfile(os.path.join(dotnet_tools_root, name)):
                print(f"  FAILED: launcher file {name} was not installed in {dotnet_tools_root}")
                ok = False

        # Global tool uninstall was run
        if not any("uninstall" in " ".join(c) for c in commands_run):
            print("  FAILED: dotnet tool uninstall --global baton was not executed")
            ok = False

    return ok


def _selftest_fail_closed_on_verify_failure() -> bool:
    import tempfile

    ok = True
    with tempfile.TemporaryDirectory() as td:
        baton_home = os.path.join(td, "baton")
        tools_root = os.path.join(baton_home, "tools")
        rooms_root = os.path.join(baton_home, "rooms")
        dotnet_tools_root = os.path.join(td, "dotnet_tools")
        nuget_root = os.path.join(td, "nuget")
        repo_root = _fixture_repo(os.path.join(td, "repo"), "9.9.9")

        def run(cmd: List[str]) -> CommandResult:
            if cmd[:3] == ["git", "-C", repo_root]:
                return CommandResult(0, "badsha99\n")
            if cmd[:3] == ["pixi", "run", "pack"]:
                return CommandResult(0)
            if cmd[:3] == ["dotnet", "tool", "list"]:
                return CommandResult(0, "")
            if cmd[:4] == ["dotnet", "tool", "install", "baton"]:
                tool_dir = os.path.join(tools_root, "badsha99")
                os.makedirs(tool_dir, exist_ok=True)
                target_exe = os.path.join(tool_dir, "baton.exe" if (sys.platform == "win32" or os.name == "nt") else "baton")
                open(target_exe, "w").close()
                return CommandResult(0)
            if len(cmd) == 2 and cmd[1] == "--version":
                return CommandResult(1, "", "crash")
            if len(cmd) == 3 and cmd[1:] == ["templates", "--json"]:
                return CommandResult(0)
            raise AssertionError(f"unexpected command: {cmd}")

        deps = Deps(
            run=run, baton_home=baton_home, rooms_root=rooms_root,
            tools_root=tools_root, dotnet_tools_root=dotnet_tools_root,
            nuget_packages_root=nuget_root, repo_root=repo_root
        )
        _assert_isolated(deps)

        messages: List[str] = []
        code = refresh(deps, dry_run=False, print_fn=messages.append)
        if code == 0:
            print("  FAILED: refresh exited 0 despite failing verification")
            ok = False

        current_file = os.path.join(tools_root, "current")
        if os.path.exists(current_file):
            print("  FAILED: current pointer was flipped despite failing verification")
            ok = False

    return ok


def _selftest_dry_run_touches_nothing() -> bool:
    import tempfile

    ok = True
    with tempfile.TemporaryDirectory() as td:
        baton_home = os.path.join(td, "baton")
        tools_root = os.path.join(baton_home, "tools")
        rooms_root = os.path.join(baton_home, "rooms")
        dotnet_tools_root = os.path.join(td, "dotnet_tools")
        nuget_root = os.path.join(td, "nuget")
        repo_root = _fixture_repo(os.path.join(td, "repo"), "5.5.5")

        def run(cmd: List[str]) -> CommandResult:
            if cmd[:3] == ["git", "-C", repo_root]:
                return CommandResult(0, "drysha55\n")
            raise AssertionError(f"--dry-run must not run commands, got: {cmd}")

        deps = Deps(
            run=run, baton_home=baton_home, rooms_root=rooms_root,
            tools_root=tools_root, dotnet_tools_root=dotnet_tools_root,
            nuget_packages_root=nuget_root, repo_root=repo_root
        )
        _assert_isolated(deps)

        messages: List[str] = []
        code = refresh(deps, dry_run=True, print_fn=messages.append)
        if code != 0:
            print(f"  FAILED: --dry-run exited {code}, want 0")
            ok = False
        if not any("[dry-run]" in m for m in messages):
            print("  FAILED: --dry-run printed no [dry-run] lines")
            ok = False
        if os.path.exists(os.path.join(tools_root, "current")):
            print("  FAILED: --dry-run wrote current pointer")
            ok = False

    return ok


def _selftest_abort() -> bool:
    import tempfile

    ok = True
    with tempfile.TemporaryDirectory() as td:
        deps = Deps(baton_home=td, rooms_root=os.path.join(td, "rooms"),
                    tools_root=os.path.join(td, "tools"),
                    dotnet_tools_root=os.path.join(td, "dotnet_tools"),
                    nuget_packages_root=os.path.join(td, "nuget"),
                    repo_root=td)
        _assert_isolated(deps)

        marker = drain_marker_path(deps)
        with open(marker, "w") as f:
            f.write("{}")

        messages: List[str] = []
        if abort(deps, messages.append) != 0 or os.path.exists(marker):
            print("  FAILED: abort did not remove drain marker")
            ok = False

        messages = []
        if abort(deps, messages.append) != 0:
            print("  FAILED: secondary abort failed")
            ok = False

    return ok


def _selftest_marker_filename_matches_the_cli() -> bool:
    """F7 (#1670 review): reinstates the cross-check the deleted drain-scan selftest used to run.
    DRAIN_MARKER_FILENAME/ABORT_INVOCATION are transcriptions of BatonPaths.DrainMarkerFileName /
    DrainMarker.AbortInvocation -- a mismatch is silent (refresh.py would write/name a marker no
    verb reads or a recovery command that doesn't work), and nothing else catches a future rename on
    the C# side. Reads this repo's own real source files, not a fixture -- there is nothing to fake
    a cross-check against."""
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


def _selftest_rerefresh_skips_reinstall_when_sha_already_verified() -> bool:
    """F4 (#1670 review): a same-head re-refresh of an already-installed, already-verified, LIVE
    SHA must be a no-op for that directory -- never rmtree it, never reinstall into it."""
    import tempfile

    ok = True
    with tempfile.TemporaryDirectory() as td:
        baton_home = os.path.join(td, "baton")
        tools_root = os.path.join(baton_home, "tools")
        rooms_root = os.path.join(baton_home, "rooms")
        dotnet_tools_root = os.path.join(td, "dotnet_tools")
        nuget_root = os.path.join(td, "nuget")
        repo_root = _fixture_repo(os.path.join(td, "repo"), "7.7.7")

        install_calls: List[List[str]] = []

        def run(cmd: List[str]) -> CommandResult:
            if cmd[:3] == ["git", "-C", repo_root] and cmd[3:5] == ["rev-parse", "--short"]:
                return CommandResult(0, "deadbeef\n")
            if cmd[:3] == ["pixi", "run", "pack"]:
                return CommandResult(0)
            if cmd[:3] == ["dotnet", "tool", "list"]:
                return CommandResult(0, "")
            if cmd[:4] == ["dotnet", "tool", "install", "baton"]:
                install_calls.append(cmd)
                install_dir = cmd[cmd.index("--tool-path") + 1]
                os.makedirs(install_dir, exist_ok=True)
                target_exe = os.path.join(install_dir, "baton.exe" if (sys.platform == "win32" or os.name == "nt") else "baton")
                open(target_exe, "w").close()
                return CommandResult(0)
            if len(cmd) == 2 and cmd[1] == "--version":
                return CommandResult(0, "7.7.7\n")
            if len(cmd) == 3 and cmd[1:] == ["templates", "--json"]:
                return CommandResult(0, "[]\n")
            if cmd[:3] == ["dotnet", "build", "src/Baton.Cli"]:
                return CommandResult(0)
            if cmd[0] == "powershell":
                return CommandResult(0)
            raise AssertionError(f"unexpected command in re-refresh selftest: {cmd}")

        deps = Deps(
            run=run, baton_home=baton_home, rooms_root=rooms_root,
            tools_root=tools_root, dotnet_tools_root=dotnet_tools_root,
            nuget_packages_root=nuget_root, repo_root=repo_root
        )
        _assert_isolated(deps)

        messages: List[str] = []
        if refresh(deps, dry_run=False, print_fn=messages.append) != 0:
            print(f"  FAILED: first refresh did not exit 0. Messages: {messages}")
            ok = False

        # A live room now references this SHA, as dispatch would have recorded after the first refresh.
        _make_room(rooms_root, "live-room", terminal=False, tool_sha="deadbeef")

        tool_dir = os.path.join(tools_root, "deadbeef")
        marker = os.path.join(tool_dir, "marker.txt")
        with open(marker, "w", encoding="utf-8") as f:
            f.write("byte-identical-sentinel")

        messages2: List[str] = []
        if refresh(deps, dry_run=False, print_fn=messages2.append) != 0:
            print(f"  FAILED: second (same-head, live) refresh did not exit 0. Messages: {messages2}")
            ok = False

        if len(install_calls) != 1:
            print(f"  FAILED: 'dotnet tool install' ran {len(install_calls)} times, want 1 -- re-refresh must skip reinstall")
            ok = False

        if not os.path.isfile(marker):
            print("  FAILED: re-refresh touched/removed the live tool directory -- marker.txt is gone")
            ok = False

        current_file = os.path.join(tools_root, "current")
        with open(current_file, "r", encoding="utf-8") as f:
            if f.read().strip() != "deadbeef":
                print("  FAILED: current pointer is not 'deadbeef' after the idempotent re-refresh")
                ok = False

    return ok


def _selftest_rerefresh_sidepaths_when_live_and_broken() -> bool:
    """F4 (#1670 review): if the existing tool_dir for this SHA fails verify AND is live (current or
    a room's ToolSha), refresh must install into a fresh `<sha>-<n>` side path rather than rmtree the
    directory a lane may be running from."""
    import tempfile

    ok = True
    with tempfile.TemporaryDirectory() as td:
        baton_home = os.path.join(td, "baton")
        tools_root = os.path.join(baton_home, "tools")
        rooms_root = os.path.join(baton_home, "rooms")
        dotnet_tools_root = os.path.join(td, "dotnet_tools")
        nuget_root = os.path.join(td, "nuget")
        repo_root = _fixture_repo(os.path.join(td, "repo"), "8.8.8")

        os.makedirs(tools_root, exist_ok=True)
        broken_dir = os.path.join(tools_root, "broken01")
        os.makedirs(broken_dir, exist_ok=True)
        with open(os.path.join(broken_dir, "marker.txt"), "w", encoding="utf-8") as f:
            f.write("must-survive")
        with open(os.path.join(tools_root, "current"), "w", encoding="utf-8") as f:
            f.write("broken01\n")

        install_calls: List[List[str]] = []

        def run(cmd: List[str]) -> CommandResult:
            if cmd[:3] == ["git", "-C", repo_root] and cmd[3:5] == ["rev-parse", "--short"]:
                return CommandResult(0, "broken01\n")
            if cmd[:3] == ["pixi", "run", "pack"]:
                return CommandResult(0)
            if cmd[:3] == ["dotnet", "tool", "list"]:
                return CommandResult(0, "")
            if cmd[:4] == ["dotnet", "tool", "install", "baton"]:
                install_calls.append(cmd)
                install_dir = cmd[cmd.index("--tool-path") + 1]
                os.makedirs(install_dir, exist_ok=True)
                target_exe = os.path.join(install_dir, "baton.exe" if (sys.platform == "win32" or os.name == "nt") else "baton")
                open(target_exe, "w").close()
                return CommandResult(0)
            if len(cmd) == 2 and cmd[1] == "--version":
                # broken01's own exe is a stub with nothing behind it -- only a freshly-installed
                # exe (under a side path) reports the right version.
                if os.path.normcase(os.path.dirname(cmd[0])) == os.path.normcase(broken_dir):
                    return CommandResult(1, "", "not a real exe")
                return CommandResult(0, "8.8.8\n")
            if len(cmd) == 3 and cmd[1:] == ["templates", "--json"]:
                return CommandResult(0, "[]\n")
            if cmd[:3] == ["dotnet", "build", "src/Baton.Cli"]:
                return CommandResult(0)
            if cmd[0] == "powershell":
                return CommandResult(0)
            raise AssertionError(f"unexpected command in side-path selftest: {cmd}")

        deps = Deps(
            run=run, baton_home=baton_home, rooms_root=rooms_root,
            tools_root=tools_root, dotnet_tools_root=dotnet_tools_root,
            nuget_packages_root=nuget_root, repo_root=repo_root
        )
        _assert_isolated(deps)

        messages: List[str] = []
        code = refresh(deps, dry_run=False, print_fn=messages.append)
        if code != 0:
            print(f"  FAILED: refresh did not exit 0. Messages: {messages}")
            ok = False

        if not os.path.isfile(os.path.join(broken_dir, "marker.txt")):
            print("  FAILED: the live-but-broken directory was touched (marker.txt gone) instead of side-pathed")
            ok = False

        side_path = os.path.join(tools_root, "broken01-1")
        if not os.path.isdir(side_path):
            print(f"  FAILED: expected a side-installed directory at {side_path}, found none")
            ok = False
        elif not any(cmd[cmd.index("--tool-path") + 1] == side_path for cmd in install_calls if "--tool-path" in cmd):
            print(f"  FAILED: 'dotnet tool install' was never targeted at {side_path}")
            ok = False

        current_file = os.path.join(tools_root, "current")
        with open(current_file, "r", encoding="utf-8") as f:
            if f.read().strip() != "broken01-1":
                print("  FAILED: current pointer was not flipped to the side-installed directory")
                ok = False

    return ok


def _selftest_install_launcher_fails_closed_on_stale_exe() -> bool:
    """F5 (#1670 review): install_launcher must fail closed, naming the holder, if baton.exe is
    still present in the launcher directory after the uninstall/remove/rename fallback chain --
    PATHEXT resolves .exe before .cmd/.ps1, so a stale shim would silently shadow the launcher."""
    import tempfile

    ok = True
    with tempfile.TemporaryDirectory() as td:
        dotnet_tools_root = os.path.join(td, "dotnet_tools")
        os.makedirs(dotnet_tools_root, exist_ok=True)
        stale_exe = os.path.join(dotnet_tools_root, "baton.exe")
        with open(stale_exe, "w", encoding="utf-8") as f:
            f.write("stale global-tool shim")

        def run(cmd: List[str]) -> CommandResult:
            if cmd[:3] == ["dotnet", "tool", "list"]:
                return CommandResult(0, "Package Id      Version\nbaton           1.0.0\n")
            if cmd[:4] == ["dotnet", "tool", "uninstall", "--global"]:
                # Simulate the uninstall failing while something still holds baton.exe open, and the
                # remove/rename fallback ALSO failing (both raise inside install_launcher naturally
                # if the file is genuinely locked; here we monkeypatch os.remove/os.rename instead).
                return CommandResult(1, "", "process cannot access the file")
            raise AssertionError(f"unexpected command: {cmd}")

        real_remove = os.remove
        real_rename = os.rename

        def locked_remove(path):
            if os.path.normcase(path) == os.path.normcase(stale_exe):
                raise PermissionError("locked")
            return real_remove(path)

        def locked_rename(src, dst):
            if os.path.normcase(src) == os.path.normcase(stale_exe):
                raise PermissionError("locked")
            return real_rename(src, dst)

        deps = Deps(run=run, dotnet_tools_root=dotnet_tools_root, repo_root=td,
                    baton_home=td, rooms_root=os.path.join(td, "rooms"),
                    tools_root=os.path.join(td, "tools"), nuget_packages_root=os.path.join(td, "nuget"))
        _assert_isolated(deps)

        os.remove, os.rename = locked_remove, locked_rename
        try:
            messages: List[str] = []
            result = install_launcher(deps, dry_run=False, print_fn=messages.append)
        finally:
            os.remove, os.rename = real_remove, real_rename

        if result is not False:
            print("  FAILED: install_launcher returned truthy despite a stale baton.exe still present")
            ok = False
        if not any(stale_exe in m for m in messages):
            print(f"  FAILED: install_launcher's failure message did not name the holder path {stale_exe}. Messages: {messages}")
            ok = False

    return ok


def selftest() -> int:
    arms = [
        ("version compare", _selftest_version_compare),
        ("pointer flip atomic", _selftest_pointer_flip_atomic),
        ("prune logic", _selftest_prune_logic),
        ("refresh end-to-end mocked", _selftest_refresh_end_to_end_mocked),
        ("fail closed on verify failure", _selftest_fail_closed_on_verify_failure),
        ("dry-run touches nothing", _selftest_dry_run_touches_nothing),
        ("abort clears drain marker", _selftest_abort),
        ("the marker filename this tool writes is the one the CLI reads", _selftest_marker_filename_matches_the_cli),
        ("re-refresh skips reinstall when the SHA is already verified", _selftest_rerefresh_skips_reinstall_when_sha_already_verified),
        ("re-refresh side-paths when live and broken", _selftest_rerefresh_sidepaths_when_live_and_broken),
        ("install_launcher fails closed on a stale exe", _selftest_install_launcher_fails_closed_on_stale_exe),
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
