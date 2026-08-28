"""User-global build lock: one MSBuild-heavy command at a time, across every worktree (#1402).

("User" rather than "host": the lock file lives in the per-user temp dir. One user on one
Windows box is the whole deployment (#1405), so the distinction is academic here.)

Two concurrent MSBuild runs on this machine kill each other (MSB4166, zero-test test legs,
vanished obj/ -- the 2026-08-04 mutual-kill catalogue). The old protection was doctrine: "one
implement lane at a time", which serializes WHOLE lanes to protect the ~20% of their wall-clock
that is build. This puts the check in the tool instead: every MSBuild-owning pixi task runs
through this wrapper, so any number of concurrent lanes queue automatically at the build itself,
and a worker that never heard of the rule still obeys it.

Windows-only, deliberately (#1405): the lock is an OS-level region lock (msvcrt.locking) on a
file in the machine's temp directory. The kernel releases it the instant the holding process
dies, however it dies -- so there is no stale-lock file to detect, no PID-liveness check, and no
steal logic. A crashed holder frees the lock by crashing.

Usage:            python tools/buildlock.py <command> [args...]
Diagnostics:      a sidecar .info file (never locked) names the holder -- PID, command, start
                  time -- so the wait message can say WHO it is waiting on.
Nesting:          a wrapped command that itself runs wrapped tasks would deadlock on its own
                  lock; the wrapper exports BATON_BUILDLOCK_HELD=<pid> to its child. The marker
                  is only an env var and can outlive its setter (a detached grandchild, a
                  debugging shell that exported it by hand), so it is treated as a HINT, not a
                  grant: an inheritor probes the lock with one non-blocking acquire. Free lock
                  means the marker was stale -- take the lock properly. Held lock means the
                  holder is overwhelmingly the ancestor that set the marker -- run directly.
                  Residual risk, accepted: a process carrying a stale marker while an UNRELATED
                  build holds the lock skips the queue; that needs the marker to leak AND the
                  race to land in the same window, strictly narrower than trusting the marker.
Timeout:          BATON_BUILDLOCK_TIMEOUT_S (default 1800) -- fails LOUDLY on expiry rather than
                  hanging past a lane's budget. BATON_BUILDLOCK_FILE overrides the lock path;
                  anyone may set it, but its intended use is selftest isolation -- overriding it
                  elsewhere opts that process out of the shared exclusion.
"""
import json
import os
import subprocess
import sys
import tempfile
import time
from typing import BinaryIO

HELD_MARKER = "BATON_BUILDLOCK_HELD"
POLL_S = 2.0
PROGRESS_EVERY_S = 10.0


def lock_path() -> str:
    return os.environ.get(
        "BATON_BUILDLOCK_FILE",
        os.path.join(tempfile.gettempdir(), "baton-build.lock"),
    )


def read_holder_info(path: str) -> str:
    try:
        with open(path + ".info", "r", encoding="utf-8") as f:
            info = json.load(f)
        return f"PID {info['pid']} ({info['command']}) since {info['since']}"
    except (OSError, ValueError, KeyError):
        return "an unidentified process (no .info sidecar)"


def write_holder_info(path: str, command: list[str]) -> None:
    info = {
        "pid": os.getpid(),
        "command": " ".join(command),
        "since": time.strftime("%Y-%m-%d %H:%M:%S"),
    }
    try:
        with open(path + ".info", "w", encoding="utf-8") as f:
            json.dump(info, f)
    except OSError:
        pass  # diagnostics only; never a reason to fail the build


def try_acquire_once(path: str, command: list[str]) -> "BinaryIO | None":
    """One non-blocking acquire: the handle if the lock was free, None if someone holds it."""
    import msvcrt

    handle = open(path, "a+b")  # noqa: SIM115 -- on success, held for the process lifetime
    try:
        handle.seek(0)
        msvcrt.locking(handle.fileno(), msvcrt.LK_NBLCK, 1)
    except OSError:
        handle.close()
        return None
    write_holder_info(path, command)
    return handle


def acquire(path: str, command: list[str], timeout_s: float) -> BinaryIO:
    """Block until the region lock on byte 0 is ours; return the open handle keeping it.

    The handle is intentionally leaked to the end of the process: the OS releases the region
    lock at process death, which is the entire crash-safety story.
    """
    import msvcrt

    handle = open(path, "a+b")  # noqa: SIM115 -- held for the process lifetime, see above
    deadline = time.monotonic() + timeout_s
    last_progress = 0.0
    while True:
        try:
            handle.seek(0)
            msvcrt.locking(handle.fileno(), msvcrt.LK_NBLCK, 1)
            write_holder_info(path, command)
            return handle
        except OSError:
            now = time.monotonic()
            if now >= deadline:
                handle.close()
                print(
                    f"buildlock: TIMED OUT after {timeout_s:.0f}s waiting for the build lock "
                    f"held by {read_holder_info(path)} -- raise BATON_BUILDLOCK_TIMEOUT_S or "
                    f"find out why the holder is stuck",
                    flush=True,
                )
                sys.exit(1)
            if now - last_progress >= PROGRESS_EVERY_S:
                last_progress = now
                print(
                    f"buildlock: waiting for the build lock held by {read_holder_info(path)} "
                    f"({deadline - now:.0f}s until timeout)",
                    flush=True,
                )
            time.sleep(POLL_S)


def main() -> int:
    command = sys.argv[1:]
    if not command:
        print("buildlock: no command given -- usage: python tools/buildlock.py <command> [args...]")
        return 2

    env = dict(os.environ)
    if os.name != "nt":
        # Transitional arm for CI's ubuntu shard until #1405 deletes it -- a CI runner is
        # single-lane, so it never needed the lock anyway.
        return subprocess.run(command, env=env, check=False).returncode

    if env.get(HELD_MARKER):
        # Marker inherited: probe, don't trust (see the module docstring's Nesting section).
        handle = try_acquire_once(lock_path(), command)
        if handle is None:
            # Lock held -- by our ancestor, per the docstring's stated residual. Run inside
            # its exclusion.
            return subprocess.run(command, env=env, check=False).returncode
    else:
        timeout_s = float(env.get("BATON_BUILDLOCK_TIMEOUT_S", "1800"))
        handle = acquire(lock_path(), command, timeout_s)
    env[HELD_MARKER] = str(os.getpid())
    try:
        return subprocess.run(command, env=env, check=False).returncode
    finally:
        import msvcrt

        try:
            handle.seek(0)
            msvcrt.locking(handle.fileno(), msvcrt.LK_UNLCK, 1)
            handle.close()
        except OSError:
            pass  # process exit releases it regardless


# ---------------------------------------------------------------------------------------------
# Selftest: the three behaviours the mechanism is FOR, each proven with real processes.
# ---------------------------------------------------------------------------------------------

_CHILD_HOLD_AND_STAMP = """
import os, sys, time
sys.argv = [sys.argv[0], sys.executable, "-c",
    "import time,sys; open(sys.argv[1],'a').write(f'{time.monotonic()} start\\\\n'); "
    "time.sleep(0.6); open(sys.argv[1],'a').write(f'{time.monotonic()} end\\\\n')",
    sys.argv[1]]
sys.exit(__import__('buildlock').main())
"""

_CHILD_ACQUIRE_AND_DIE = """
import os, sys
import buildlock
handle = buildlock.acquire(buildlock.lock_path(), ["deliberate-crash"], 5.0)
os._exit(0)  # dies holding the lock -- the OS must release it
"""

_CHILD_ACQUIRE_AND_SLEEP = """
import time
import buildlock
handle = buildlock.acquire(buildlock.lock_path(), ["slow-holder"], 5.0)
time.sleep(3)
"""


def _spawn_selftest_child(code: str, lock_file: str, *args: str) -> subprocess.Popen:
    env = dict(os.environ)
    env["BATON_BUILDLOCK_FILE"] = lock_file
    env["BATON_BUILDLOCK_TIMEOUT_S"] = "20"
    env.pop(HELD_MARKER, None)
    env["PYTHONPATH"] = os.path.dirname(os.path.abspath(__file__))
    return subprocess.Popen(
        [sys.executable, "-c", code, *args], env=env,
        stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
    )


def selftest() -> int:
    if os.name != "nt":
        # Same #1405 transitional arm as main(): the mechanism under test is Windows-only.
        print("selftest: skipped (non-Windows host; the lock is a pass-through here)")
        return 0
    ok = True
    with tempfile.TemporaryDirectory() as td:
        lock_file = os.path.join(td, "selftest.lock")
        stamps = os.path.join(td, "stamps.txt")

        # 1. Two wrapped commands started together must serialize (no interval overlap).
        a = _spawn_selftest_child(_CHILD_HOLD_AND_STAMP, lock_file, stamps)
        b = _spawn_selftest_child(_CHILD_HOLD_AND_STAMP, lock_file, stamps)
        # Every wait below is bounded: a regression that makes the mechanism HANG (the exact
        # anti-pattern the timeout exists to prevent) must fail this selftest loudly, not hang
        # the gate that runs it.
        a.communicate(timeout=30), b.communicate(timeout=30)
        if a.returncode != 0 or b.returncode != 0:
            print(f"  control FAILED: wrapped commands exited {a.returncode}/{b.returncode}")
            ok = False
        else:
            with open(stamps, encoding="utf-8") as f:
                lines = [line.split() for line in f.read().splitlines()]
            intervals, current = [], None
            for ts, kind in lines:
                if kind == "start":
                    current = float(ts)
                else:
                    intervals.append((current, float(ts)))
            intervals.sort()
            if len(intervals) != 2 or intervals[0][1] > intervals[1][0]:
                print(f"  control FAILED: hold intervals overlap -- {intervals}")
                ok = False

        # 2. A holder that dies without releasing must free the lock (OS-level release).
        crasher = _spawn_selftest_child(_CHILD_ACQUIRE_AND_DIE, lock_file)
        crasher.communicate(timeout=30)
        env = dict(os.environ)
        env["BATON_BUILDLOCK_FILE"] = lock_file
        env["BATON_BUILDLOCK_TIMEOUT_S"] = "3"
        env.pop(HELD_MARKER, None)
        after = subprocess.run(
            [sys.executable, os.path.abspath(__file__), sys.executable, "-c", "pass"],
            env=env, capture_output=True, text=True, check=False, timeout=30,
        )
        if after.returncode != 0:
            print(f"  control FAILED: lock survived its holder's death -- {after.stdout}")
            ok = False

        # 3. The timeout path must fail loudly, not hang: waiter with a 1s budget against a
        #    holder that sleeps well past it.
        holder = _spawn_selftest_child(_CHILD_ACQUIRE_AND_SLEEP, lock_file)
        time.sleep(0.2)  # let the holder win the race for the lock
        env["BATON_BUILDLOCK_TIMEOUT_S"] = "1"
        waiter = subprocess.run(
            [sys.executable, os.path.abspath(__file__), sys.executable, "-c", "pass"],
            env=env, capture_output=True, text=True, check=False, timeout=30,
        )
        holder.communicate(timeout=30)
        if waiter.returncode == 0 or "TIMED OUT" not in waiter.stdout:
            print(
                f"  control FAILED: timeout path exited {waiter.returncode} "
                f"without a loud message -- {waiter.stdout!r}"
            )
            ok = False

        # 4. A stale inherited marker with a FREE lock must be probed, not trusted: the run
        #    must take the lock properly (visible via the .info sidecar it writes) rather than
        #    skipping acquisition.
        try:
            os.remove(lock_file + ".info")
        except OSError:
            pass
        env[HELD_MARKER] = "999999"  # nobody's pid; simulates a marker that outlived its setter
        env["BATON_BUILDLOCK_TIMEOUT_S"] = "5"
        stale = subprocess.run(
            [sys.executable, os.path.abspath(__file__), sys.executable, "-c", "pass"],
            env=env, capture_output=True, text=True, check=False, timeout=30,
        )
        if stale.returncode != 0 or not os.path.exists(lock_file + ".info"):
            print(
                f"  control FAILED: stale marker + free lock exited {stale.returncode}; "
                f".info written: {os.path.exists(lock_file + '.info')} -- the probe path "
                f"trusted the marker instead of taking the free lock"
            )
            ok = False

    print("selftest: pass" if ok else "selftest: FAIL")
    return 0 if ok else 1


if __name__ == "__main__":
    if "--selftest" in sys.argv[1:2]:
        sys.exit(selftest())
    sys.exit(main())
