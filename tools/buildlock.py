"""Host-global build lock: one MSBuild-heavy command at a time, across every worktree (#1402).

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
                  lock; the wrapper exports BATON_BUILDLOCK_HELD=<pid> to its child, and skips
                  acquisition when it inherits that marker. (Child processes of the holder are
                  inside the holder's exclusion by definition.)
Timeout:          BATON_BUILDLOCK_TIMEOUT_S (default 1800) -- fails LOUDLY on expiry rather than
                  hanging past a lane's budget. BATON_BUILDLOCK_FILE overrides the lock path
                  (selftest isolation only).
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
    if env.get(HELD_MARKER) or os.name != "nt":
        # Child of the current holder (already inside the exclusion), or a non-Windows host:
        # run directly. The non-Windows arm exists only for CI's ubuntu shard until #1405
        # deletes it -- a CI runner is single-lane, so it never needed the lock anyway.
        return subprocess.run(command, env=env, check=False).returncode

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
        a.communicate(), b.communicate()
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
        crasher.communicate()
        env = dict(os.environ)
        env["BATON_BUILDLOCK_FILE"] = lock_file
        env["BATON_BUILDLOCK_TIMEOUT_S"] = "3"
        env.pop(HELD_MARKER, None)
        after = subprocess.run(
            [sys.executable, os.path.abspath(__file__), sys.executable, "-c", "pass"],
            env=env, capture_output=True, text=True, check=False,
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
            env=env, capture_output=True, text=True, check=False,
        )
        holder.communicate()
        if waiter.returncode == 0 or "TIMED OUT" not in waiter.stdout:
            print(
                f"  control FAILED: timeout path exited {waiter.returncode} "
                f"without a loud message -- {waiter.stdout!r}"
            )
            ok = False

    print("selftest: pass" if ok else "selftest: FAIL")
    return 0 if ok else 1


if __name__ == "__main__":
    if "--selftest" in sys.argv[1:2]:
        sys.exit(selftest())
    sys.exit(main())
