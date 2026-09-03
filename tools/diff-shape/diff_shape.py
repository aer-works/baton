"""Diff-shape CI gate validator (#1603).

Evaluates PR git diff against test-weakening and protected-tooling rules:
- Fails on test-only PRs containing deleted/modified lines in pre-existing test files.
- Fails on edits to protected tooling paths (tools/gates/, pixi.toml, .github/workflows/, tools/diff-shape/).
- Lifted when the operator-merge label is attached.
"""
from __future__ import annotations

import argparse
import json
import os
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path


def scrubbed_env() -> dict[str, str]:
    """Return environment with GIT_* variables removed to isolate git sub-commands."""
    return {k: v for k, v in os.environ.items() if not k.startswith("GIT_")}


def is_protected_tooling(path: str) -> bool:
    """Check if path belongs to the protected-tooling set."""
    p = path.replace("\\", "/")
    if p == "pixi.toml":
        return True
    if p.startswith("tools/gates/") or p == "tools/gates":
        return True
    if p.startswith(".github/workflows/") or p == ".github/workflows":
        return True
    if p.startswith("tools/diff-shape/") or p == "tools/diff-shape":
        return True
    return False


def is_test_file(path: str) -> bool:
    """Check if path is a test file or within a test directory."""
    p = path.replace("\\", "/")
    if p.startswith("tests/") or "/tests/" in p:
        return True
    if p.endswith("Tests.cs") or p.endswith("Test.cs") or p.endswith("Tests.ps1") or p.endswith("_test.py"):
        return True
    return False


def check_diff_shape(
    base: str,
    head: str = "HEAD",
    labels: list[str] | set[str] | None = None,
    cwd: str | Path | None = None,
) -> tuple[bool, list[str]]:
    """Analyze git diff between base and head against diff-shape and protected-tooling rules.

    Returns (passed: bool, messages: list[str]).
    """
    env = scrubbed_env()
    cwd_str = str(cwd) if cwd else None

    if labels is None:
        labels = []
    labels_set = {label.strip().lower() for label in labels}

    # Fetch status of all changed files: `git diff --name-status base head`
    try:
        proc = subprocess.run(
            ["git", "diff", "--name-status", base, head],
            cwd=cwd_str,
            capture_output=True,
            text=True,
            check=True,
            env=env,
        )
    except subprocess.CalledProcessError as err:
        return False, [f"git diff failed: {err.stderr.strip()}"]

    lines = proc.stdout.splitlines()
    touched_files: list[tuple[str, str]] = []  # (status, path)
    for line in lines:
        if not line.strip():
            continue
        parts = line.split("\t")
        status = parts[0].strip()
        path = parts[-1].strip()  # If renamed (R100 old new), parts[-1] is new path
        touched_files.append((status, path))

    # Condition 1: Check if any src/ code was touched
    touches_src = any(path.replace("\\", "/").startswith("src/") for _, path in touched_files)

    # Protected tooling check
    protected_edits = [path for _, path in touched_files if is_protected_tooling(path)]

    # Check for deleted/changed lines in pre-existing test files
    weakened_tests: list[tuple[str, list[str]]] = []
    for status, path in touched_files:
        # Pure additions of test files (status starting with 'A') pass; only pre-existing files count
        if status.startswith("A"):
            continue
        if not is_test_file(path):
            continue

        # Inspect diff for deleted lines starting with '-' (excluding diff headers)
        try:
            diff_proc = subprocess.run(
                ["git", "diff", "-U0", base, head, "--", path],
                cwd=cwd_str,
                capture_output=True,
                text=True,
                check=True,
                env=env,
            )
        except subprocess.CalledProcessError:
            continue

        deleted_lines: list[str] = []
        for dline in diff_proc.stdout.splitlines():
            if dline.startswith("-") and not dline.startswith("---"):
                deleted_lines.append(dline)

        if deleted_lines:
            weakened_tests.append((path, deleted_lines))

    # Evaluate failures
    test_weakening_failed = (not touches_src) and len(weakened_tests) > 0
    protected_tooling_failed = len(protected_edits) > 0

    is_failing = test_weakening_failed or protected_tooling_failed

    has_operator_label = "operator-merge" in labels_set

    messages: list[str] = []

    if test_weakening_failed:
        messages.append("!! Test-only PR weakening existing tests (no src/ changes touched):")
        for path, deleted in weakened_tests:
            sample = deleted[0][:80] if deleted else ""
            messages.append(f"   * {path} ({len(deleted)} deleted/changed line(s), e.g. '{sample}')")

    if protected_tooling_failed:
        messages.append("!! Protected tooling set was edited:")
        for path in protected_edits:
            messages.append(f"   * {path}")

    if is_failing:
        if has_operator_label:
            messages.append("OK Failure lifted by 'operator-merge' PR label.")
            return True, messages
        else:
            messages.append("To proceed: ask the operator to add the 'operator-merge' label to this PR if these changes are intended.")
            messages.append("See issue #1603 for ratified design details: https://github.com/philipreese/baton/issues/1603")
            return False, messages

    messages.append("OK diff-shape gate passed.")
    return True, messages


def selftest() -> int:
    """Run synthetic test fixtures for the 5 diff-shape gate scenarios."""
    print("diff-shape: running selftest fixtures")
    failures: list[str] = []

    with tempfile.TemporaryDirectory() as td:
        repo = Path(td)
        env = scrubbed_env()

        # Initialize synthetic git repo
        subprocess.run(["git", "init", "-q"], cwd=repo, check=True, env=env)
        subprocess.run(["git", "config", "user.email", "test@example.com"], cwd=repo, check=True, env=env)
        subprocess.run(["git", "config", "user.name", "Test"], cwd=repo, check=True, env=env)

        # Setup base commit
        tests_dir = repo / "tests"
        tests_dir.mkdir(parents=True)
        test_file = tests_dir / "FooTests.cs"
        test_file.write_text("line1\nline2\nline3\n", encoding="utf-8")

        src_dir = repo / "src"
        src_dir.mkdir(parents=True)
        src_file = src_dir / "Engine.cs"
        src_file.write_text("class Engine {}\n", encoding="utf-8")

        subprocess.run(["git", "add", "."], cwd=repo, check=True, env=env)
        subprocess.run(["git", "commit", "-q", "-m", "initial base"], cwd=repo, check=True, env=env)

        base_sha = subprocess.run(["git", "rev-parse", "HEAD"], cwd=repo, capture_output=True, text=True, check=True, env=env).stdout.strip()

        # (a) Test-only weakening -> FAIL
        subprocess.run(["git", "checkout", "-q", "-b", "branch-a"], cwd=repo, check=True, env=env)
        test_file.write_text("line1\nline3\n", encoding="utf-8")  # line2 deleted
        subprocess.run(["git", "commit", "-q", "-a", "-m", "weaken test"], cwd=repo, check=True, env=env)
        head_a = subprocess.run(["git", "rev-parse", "HEAD"], cwd=repo, capture_output=True, text=True, check=True, env=env).stdout.strip()

        passed_a, msgs_a = check_diff_shape(base_sha, head_a, labels=[], cwd=repo)
        if passed_a:
            failures.append("(a) test-only weakening did not fail as expected")
        else:
            print("  OK (a) test-only weakening -> FAIL")

        # (b) Test-only additions -> PASS
        subprocess.run(["git", "checkout", "-q", "-b", "branch-b", base_sha], cwd=repo, check=True, env=env)
        test_file.write_text("line1\nline2\nline3\nline4\n", encoding="utf-8")  # new line
        new_test = tests_dir / "BarTests.cs"
        new_test.write_text("class BarTests {}\n", encoding="utf-8")
        subprocess.run(["git", "add", "."], cwd=repo, check=True, env=env)
        subprocess.run(["git", "commit", "-q", "-m", "add test line"], cwd=repo, check=True, env=env)
        head_b = subprocess.run(["git", "rev-parse", "HEAD"], cwd=repo, capture_output=True, text=True, check=True, env=env).stdout.strip()

        passed_b, msgs_b = check_diff_shape(base_sha, head_b, labels=[], cwd=repo)
        if not passed_b:
            failures.append(f"(b) test-only additions failed unexpectedly: {msgs_b}")
        else:
            print("  OK (b) test-only additions -> PASS")

        # (c) Mixed src+test change -> PASS
        subprocess.run(["git", "checkout", "-q", "-b", "branch-c", base_sha], cwd=repo, check=True, env=env)
        src_file.write_text("class Engine { void Run() {} }\n", encoding="utf-8")
        test_file.write_text("line1\nline3\n", encoding="utf-8")  # line2 deleted
        subprocess.run(["git", "commit", "-q", "-a", "-m", "engine and test update"], cwd=repo, check=True, env=env)
        head_c = subprocess.run(["git", "rev-parse", "HEAD"], cwd=repo, capture_output=True, text=True, check=True, env=env).stdout.strip()

        passed_c, msgs_c = check_diff_shape(base_sha, head_c, labels=[], cwd=repo)
        if not passed_c:
            failures.append(f"(c) mixed src+test change failed unexpectedly: {msgs_c}")
        else:
            print("  OK (c) mixed src+test change -> PASS")

        # (d) Protected tooling edit -> FAIL
        subprocess.run(["git", "checkout", "-q", "-b", "branch-d", base_sha], cwd=repo, check=True, env=env)
        pixi_file = repo / "pixi.toml"
        pixi_file.write_text("[tasks]\n", encoding="utf-8")
        subprocess.run(["git", "add", "."], cwd=repo, check=True, env=env)
        subprocess.run(["git", "commit", "-q", "-m", "edit protected tooling"], cwd=repo, check=True, env=env)
        head_d = subprocess.run(["git", "rev-parse", "HEAD"], cwd=repo, capture_output=True, text=True, check=True, env=env).stdout.strip()

        passed_d, msgs_d = check_diff_shape(base_sha, head_d, labels=[], cwd=repo)
        if passed_d:
            failures.append("(d) protected tooling edit did not fail as expected")
        else:
            print("  OK (d) protected tooling edit -> FAIL")

        # (e) Failing shape + operator-merge label -> PASS
        passed_e1, _ = check_diff_shape(base_sha, head_a, labels=["operator-merge"], cwd=repo)
        passed_e2, _ = check_diff_shape(base_sha, head_d, labels=["operator-merge"], cwd=repo)
        if not (passed_e1 and passed_e2):
            failures.append("(e) operator-merge label did not lift failure")
        else:
            print("  OK (e) failing shapes + operator-merge label -> PASS")

    if failures:
        print(f"diff-shape: selftest FAIL -- {'; '.join(failures)}", file=sys.stderr)
        return 1

    print("diff-shape: selftest OK (all 5 discrimination arms passed)")
    return 0


def get_labels_from_event_path() -> list[str]:
    """Attempt to extract PR labels from GITHUB_EVENT_PATH if set."""
    event_path = os.environ.get("GITHUB_EVENT_PATH")
    if not event_path or not os.path.exists(event_path):
        return []
    try:
        with open(event_path, encoding="utf-8") as f:
            data = json.load(f)
        pr = data.get("pull_request") or {}
        labels = pr.get("labels") or []
        return [lbl.get("name", "") for lbl in labels if isinstance(lbl, dict) and "name" in lbl]
    except Exception:
        return []


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Diff-shape CI gate check (#1603)")
    parser.add_argument("--base", type=str, default="origin/main", help="Base commit/ref to diff against")
    parser.add_argument("--head", type=str, default="HEAD", help="Head commit/ref to diff against")
    parser.add_argument("--labels", type=str, default="", help="Comma-separated PR labels")
    parser.add_argument("--selftest", action="store_true", help="Run synthetic selftest fixtures")

    args = parser.parse_args(argv)

    if args.selftest:
        return selftest()

    labels_list = [lbl.strip() for lbl in args.labels.split(",") if lbl.strip()]
    if not labels_list:
        labels_list = get_labels_from_event_path()

    passed, messages = check_diff_shape(args.base, args.head, labels=labels_list)
    for msg in messages:
        print(msg)

    return 0 if passed else 1


if __name__ == "__main__":
    sys.exit(main())
