"""Diff-shape CI gate validator (#1603, widened #1744).

Evaluates PR git diff against test-weakening and protected-tooling rules:
- Fails on test-only PRs containing deleted/modified lines in pre-existing test files.
- Fails on edits to the protected-tooling set -- see PROTECTED_TOOLING_PATHS and
  PIXI_PROTECTED_TASK_RULE below (the sole enumeration; spec/baton.md C-15 cites this file rather
  than restating the list).
- Lifted when the operator-merge label is attached.
"""
from __future__ import annotations

import argparse
import json
import os
import re
import subprocess
import sys
import tempfile
from pathlib import Path

# record-once (#1744): the sole enumeration of the whole-file/directory half of the
# protected-tooling set. is_protected_tooling() and the failure message both read this list
# rather than restating it. `kind` is "dir" (prefix match) or "file" (exact match).
PROTECTED_TOOLING_PATHS: tuple[tuple[str, str, str], ...] = (
    ("dir", "tools/gates/", "the gates orchestrator itself"),
    ("dir", ".github/workflows/", "the CI workflow definitions gates run under"),
    ("dir", "tools/diff-shape/", "this gate's own implementation"),
    ("dir", "tools/audit-completeness/", "the audit-* checker bodies gates.py's OVERLAP/AFTER_BUILD phases run"),
    ("file", "tools/vendor-verify/verify.py", "the @check registry the vendor gates read"),
    ("file", "tools/buildlock.py", "the build lock every gate run goes through"),
    ("dir", "tools/flake-watch/", "the flake ledger CI consults"),
    ("dir", "tests/Baton.Architecture.Tests/", "compiled enforcement: spawn gate, state vocabularies, citation pins"),
    # #1754: gates.py's own OVERLAP/AFTER_BUILD_FAST comments say each of these is a wired member
    # (not merely a pixi.toml line), contradicting #1744's "not enforcement" exclusion of their
    # directories -- protecting the specific file, not the whole directory, so a genuinely unwired
    # sibling (tools/fleet-glass/pusher.py) stays unprotected.
    ("file", "tools/fleet-glass/worker.selftest.mjs", "the only thing standing between worker.js's paging/heartbeat-merge logic and a silent revert (gates.py OVERLAP)"),
    ("file", "tools/tool-refresh/refresh.py", "tool-refresh-selftest's body (gates.py OVERLAP)"),
    ("file", "tools/baton-agy-loop/dispatch.py", "baton-dispatch-selftest's body (gates.py AFTER_BUILD_FAST)"),
    ("file", "tests/Launcher.Tests.ps1", "launcher-selftest's body -- exercises baton.cmd/baton.ps1 against a mock exe fixture (gates.py OVERLAP)"),
    ("dir", "tools/Baton.VendorProbe/", "vendor-check's actual body, the loud half of the drift grace window (gates.py AFTER_BUILD_FAST); a directory because it is a compiled project"),
)

# pixi.toml is protected at LINE level, not whole-file (#1744 narrowing of #1603's original
# whole-file rule -- see spec/baton.md C-15). A task name matching any of these rules has its
# definition lines protected; everything else in the file (an ordinary task addition/edit) passes.
PIXI_PROTECTED_TASK_RULE = (
    "pixi.toml task definitions matching gates*, gate-sabotage, diff-shape*, audit-*, "
    "*-selftest, vendor-check, vendor-verify, lint, fmt-check, or test-no-build "
    "(line-level, not the whole file)"
)


def _is_protected_pixi_task(name: str) -> bool:
    """Whether a pixi.toml task name falls under PIXI_PROTECTED_TASK_RULE."""
    if name.startswith("gates"):
        return True
    if name == "gate-sabotage":
        return True
    if name.startswith("diff-shape"):
        return True
    if name.startswith("audit-"):
        return True
    if name.endswith("-selftest"):
        return True
    if name in ("vendor-check", "vendor-verify", "lint", "fmt-check", "test-no-build"):
        return True
    return False


_PIXI_KEY_RE = re.compile(r"^([A-Za-z0-9_-]+)\s*=")
_PIXI_SUBTABLE_RE = re.compile(r"^\[tasks\.([A-Za-z0-9_-]+)\]")


def _pixi_protected_line_numbers(content: str) -> set[int]:
    """Return 1-indexed line numbers, within pixi.toml's [tasks] table, that belong to a
    protected task's own definition (see PIXI_PROTECTED_TASK_RULE)."""
    lines = content.splitlines()
    protected: set[int] = set()

    section_start = None
    section_end = len(lines)
    for i, line in enumerate(lines):
        if line.strip() == "[tasks]":
            section_start = i + 1
            continue
        if section_start is not None and re.match(r"^\[(?!tasks\.)", line.strip()):
            section_end = i
            break
    if section_start is None:
        return protected

    i = section_start
    while i < section_end:
        line = lines[i]
        m_sub = _PIXI_SUBTABLE_RE.match(line)
        m_key = _PIXI_KEY_RE.match(line)
        if m_sub:
            name = m_sub.group(1)
            start = i
            j = i + 1
            while j < section_end and not _PIXI_KEY_RE.match(lines[j]) and not _PIXI_SUBTABLE_RE.match(lines[j]):
                j += 1
            if _is_protected_pixi_task(name):
                protected.update(range(start + 1, j + 1))
            i = j
            continue
        if m_key:
            name = m_key.group(1)
            start = i
            # Follow a multi-line inline-table value by brace balance; every current task closes
            # its `{ ... }` on one line, but this stays correct if that ever changes.
            depth = line.count("{") - line.count("}")
            j = i + 1
            while depth > 0 and j < section_end:
                depth += lines[j].count("{") - lines[j].count("}")
                j += 1
            if _is_protected_pixi_task(name):
                protected.update(range(start + 1, j + 1))
            i = j
            continue
        i += 1

    return protected


_HUNK_RE = re.compile(r"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@")


def _pixi_toml_protected_hunk_touched(base: str, head: str, cwd: str | None, env: dict[str, str]) -> bool:
    """Whether the pixi.toml diff between base and head touches a protected task's definition
    lines (#1744 line-level narrowing)."""
    merge_base = subprocess.run(
        ["git", "merge-base", base, head], cwd=cwd, capture_output=True, text=True, check=True, env=env,
    ).stdout.strip()

    def _show(rev: str) -> str:
        proc = subprocess.run(
            ["git", "show", f"{rev}:pixi.toml"], cwd=cwd, capture_output=True, text=True, env=env,
        )
        return proc.stdout if proc.returncode == 0 else ""

    old_protected = _pixi_protected_line_numbers(_show(merge_base))
    new_protected = _pixi_protected_line_numbers(_show(head))

    diff_proc = subprocess.run(
        ["git", "diff", "-U0", f"{base}...{head}", "--", "pixi.toml"],
        cwd=cwd, capture_output=True, text=True, check=True, env=env,
    )

    old_line = new_line = 0
    for dline in diff_proc.stdout.splitlines():
        m = _HUNK_RE.match(dline)
        if m:
            old_line = int(m.group(1))
            new_line = int(m.group(3))
            continue
        if dline.startswith("---") or dline.startswith("+++"):
            continue
        if dline.startswith("-"):
            if old_line in old_protected:
                return True
            old_line += 1
        elif dline.startswith("+"):
            if new_line in new_protected:
                return True
            new_line += 1

    return False


def scrubbed_env() -> dict[str, str]:
    """Return environment with GIT_* variables removed to isolate git sub-commands."""
    return {k: v for k, v in os.environ.items() if not k.startswith("GIT_")}


def is_protected_tooling(path: str) -> bool:
    """Check if path belongs to the protected-tooling set (whole-file/directory half -- pixi.toml
    is handled separately, at line level, by _pixi_toml_protected_hunk_touched)."""
    p = path.replace("\\", "/")
    for kind, entry, _reason in PROTECTED_TOOLING_PATHS:
        if kind == "file":
            if p == entry:
                return True
        else:
            if p == entry.rstrip("/") or p.startswith(entry):
                return True
    return False


# record-once (#1758): the sole enumeration of rule (A)'s assertion/test-declaration pattern
# list -- spec/baton.md C-15 cites this file rather than restating the patterns. Applied to every
# deleted line regardless of extension.
_ASSERTION_PATTERNS: tuple[tuple[str, re.Pattern[str]], ...] = (
    ("Assert.", re.compile(r"Assert\.")),
    ("[Fact", re.compile(r"\[Fact")),
    ("[Theory", re.compile(r"\[Theory")),
    ("[InlineData", re.compile(r"\[InlineData")),
    ("Should", re.compile(r"Should")),
    ("Expect(", re.compile(r"Expect\(")),
)

# Only checked against .mjs files (the selftest-in-JS shape, e.g. worker.selftest.mjs).
_MJS_ASSERTION_PATTERNS: tuple[tuple[str, re.Pattern[str]], ...] = (
    ("it(", re.compile(r"\bit\(")),
    ("test(", re.compile(r"\btest\(")),
)

# Only checked against .py files.
_PY_ASSERTION_PATTERNS: tuple[tuple[str, re.Pattern[str]], ...] = (
    ("@pytest", re.compile(r"@pytest")),
    ("assert ", re.compile(r"(?:^|\s)assert\s")),
)

# Only checked when "selftest" appears in the filename -- a bare `throw` is ordinary
# plumbing-code control flow (see TestSupport/TempGitRepository.cs's Run()) everywhere else, and
# would false-positive on every helper that raises on failure.
_THROW_PATTERN: tuple[str, re.Pattern[str]] = ("throw", re.compile(r"\bthrow\b"))


def _assertion_patterns_for(path: str) -> tuple[tuple[str, re.Pattern[str]], ...]:
    """Which assertion/test-declaration patterns apply to a deleted line in this file (#1758
    narrowing of rule (A) -- a helper/fixture file with no assertion pattern in its removed lines
    is a refactor, not weakening)."""
    p = path.replace("\\", "/")
    patterns = _ASSERTION_PATTERNS
    if p.endswith(".mjs"):
        patterns += _MJS_ASSERTION_PATTERNS
    if p.endswith(".py"):
        patterns += _PY_ASSERTION_PATTERNS
    if "selftest" in p.rsplit("/", 1)[-1].lower():
        patterns += (_THROW_PATTERN,)
    return patterns


def _match_assertion_pattern(deleted_line: str, path: str) -> str | None:
    """Return the pattern name matching this deleted line's content (the line still carries its
    leading '-'), or None."""
    content = deleted_line[1:] if deleted_line.startswith("-") else deleted_line
    for name, pattern in _assertion_patterns_for(path):
        if pattern.search(content):
            return name
    return None


def _normalize_diff_line(line: str) -> str:
    """Strip the leading +/- marker and collapse whitespace, so a line moved with re-indentation
    still pairs against its counterpart."""
    content = line[1:] if line[:1] in "+-" else line
    return re.sub(r"\s+", " ", content).strip()


def is_test_file(path: str) -> bool:
    """Check if path is a test file or within a test directory."""
    p = path.replace("\\", "/")
    if p.startswith("tests/") or "/tests/" in p:
        return True
    if p.endswith("Tests.cs") or p.endswith("Test.cs") or p.endswith("Tests.ps1") or p.endswith("_test.ps1"):
        return True
    name = p.rsplit("/", 1)[-1]
    if name.startswith("test_") and name.endswith(".py"):
        return True
    if name.endswith("_test.py"):
        return True
    return False


def check_diff_shape(
    base: str,
    head: str = "HEAD",
    labels: list[str] | set[str] | None = None,
    cwd: str | Path | None = None,
    actor: str = "",
) -> tuple[bool, list[str]]:
    """Analyze git diff between base and head against diff-shape and protected-tooling rules.

    Returns (passed: bool, messages: list[str]).
    """
    env = scrubbed_env()
    cwd_str = str(cwd) if cwd else None

    if labels is None:
        labels = []
    labels_set = {label.strip().lower() for label in labels}

    # Fetch status of all changed files: `git diff --no-renames --name-status base...head`.
    # Three-dot diffs from the merge-base, not base's tip, so a base branch that moved after
    # branching does not reverse its own commits into the diff. --no-renames makes a renamed
    # file that also lost lines show up as a D/A pair instead of one clean-looking R### entry
    # whose per-path diff (below) would otherwise read the new path as freshly added.
    try:
        proc = subprocess.run(
            ["git", "diff", "--no-renames", "--name-status", f"{base}...{head}"],
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
        path = parts[-1].strip()
        touched_files.append((status, path))

    # Condition 1: Check if any src/ code was touched
    touches_src = any(path.replace("\\", "/").startswith("src/") for _, path in touched_files)

    # Protected tooling check. pixi.toml is line-level (#1744): only a hunk touching a protected
    # task's own definition trips it, so an ordinary pixi task addition/edit passes.
    protected_edits: list[str] = []
    for _, path in touched_files:
        p = path.replace("\\", "/")
        if p == "pixi.toml":
            if _pixi_toml_protected_hunk_touched(base, head, cwd_str, env):
                protected_edits.append(f"{path} (protected task definition)")
            continue
        if is_protected_tooling(path):
            protected_edits.append(path)

    # Check test files for the three narrowed criteria (#1758): an assertion/test-declaration
    # line removed, a test file deleted (or renamed away -- see --no-renames above), or a file's
    # tests/ diff going net-negative. A helper/fixture refactor that trips none of these (the
    # #1757 shape: a private method's signature changed, net-additive, no assertion touched)
    # passes -- narrower than #1603's original "any deleted line in a test file" rule.
    weakened_tests: list[tuple[str, list[tuple[str, str | None]]]] = []
    for status, path in touched_files:
        # Pure additions of test files (status starting with 'A') pass; only pre-existing files count
        if status.startswith("A"):
            continue
        if not is_test_file(path):
            continue

        if status.startswith("D"):
            weakened_tests.append((path, [("test file deleted", None)]))
            continue

        # Inspect diff for +/- lines (excluding diff headers)
        try:
            diff_proc = subprocess.run(
                ["git", "diff", "-U0", f"{base}...{head}", "--", path],
                cwd=cwd_str,
                capture_output=True,
                text=True,
                check=True,
                env=env,
            )
        except subprocess.CalledProcessError as err:
            return False, [f"git diff failed on {path}: {err.stderr.strip()}"]

        # Only lines inside a hunk (after its `@@ ... @@` marker) are added/deleted content; the
        # `--- a/path` / `+++ b/path` file headers precede the first hunk and are skipped by
        # position (in_hunk gate), not by content -- a deleted source line whose own text starts
        # with `--` (e.g. `--counter;`) must still count.
        deleted_lines: list[str] = []
        added_lines: list[str] = []
        in_hunk = False
        for dline in diff_proc.stdout.splitlines():
            if dline.startswith("@@"):
                in_hunk = True
                continue
            if not in_hunk:
                continue
            if dline.startswith("-"):
                deleted_lines.append(dline)
            elif dline.startswith("+"):
                added_lines.append(dline)

        if not deleted_lines:
            continue

        # Criterion 1: a removed line not paired with an added line of the same content modulo
        # whitespace (a moved/reindented line isn't a real removal) matches an assertion pattern.
        added_pool: dict[str, int] = {}
        for al in added_lines:
            norm = _normalize_diff_line(al)
            added_pool[norm] = added_pool.get(norm, 0) + 1
        unpaired_deleted: list[str] = []
        for dl in deleted_lines:
            norm = _normalize_diff_line(dl)
            if added_pool.get(norm, 0) > 0:
                added_pool[norm] -= 1
                continue
            unpaired_deleted.append(dl)

        hits: list[tuple[str, str | None]] = []
        for dl in unpaired_deleted:
            match = _match_assertion_pattern(dl, path)
            if match:
                quoted = dl[1:] if dl.startswith("-") else dl
                hits.append((f"removed assertion/test-declaration line ({match})", quoted))

        # Criterion 3: the file's diff removes more lines than it adds, regardless of pairing.
        if len(deleted_lines) > len(added_lines):
            hits.append((
                f"net-negative diff ({len(deleted_lines)} removed vs {len(added_lines)} added)",
                None,
            ))

        if hits:
            weakened_tests.append((path, hits))

    # Evaluate failures
    test_weakening_failed = (not touches_src) and len(weakened_tests) > 0
    protected_tooling_failed = len(protected_edits) > 0

    is_failing = test_weakening_failed or protected_tooling_failed

    has_operator_label = "operator-merge" in labels_set

    messages: list[str] = []

    if test_weakening_failed:
        messages.append("!! Test-only PR weakening existing tests (no src/ changes touched):")
        for path, hits in weakened_tests:
            for label, detail in hits:
                if detail is not None:
                    messages.append(f"   * {path} -- {label}: '{detail[:80]}'")
                else:
                    messages.append(f"   * {path} -- {label}")

    if protected_tooling_failed:
        messages.append("!! Protected tooling set was edited:")
        for path in protected_edits:
            messages.append(f"   * {path}")
        messages.append("   Protected set:")
        messages.append(f"   * {PIXI_PROTECTED_TASK_RULE}")
        for _kind, entry, reason in PROTECTED_TOOLING_PATHS:
            messages.append(f"   * {entry} -- {reason}")

    if is_failing:
        if has_operator_label:
            messages.append("OK Failure lifted by 'operator-merge' PR label.")
            if actor:
                messages.append(f"::notice::diff-shape gate lifted by the 'operator-merge' label by {actor}.")
            else:
                messages.append("::notice::diff-shape gate lifted by the 'operator-merge' label (see the PR timeline for who applied it).")
            return True, messages
        else:
            messages.append("To proceed: ask the operator to add the 'operator-merge' label to this PR if these changes are intended.")
            messages.append("See issue #1603 for ratified design details: https://github.com/philipreese/baton/issues/1603")
            return False, messages

    messages.append("OK diff-shape gate passed.")
    return True, messages


def selftest() -> int:
    """Run synthetic test fixtures for the diff-shape gate's discrimination arms."""
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

        # A realistic-shaped pixi.toml: a protected task (gates), a protected audit-* task
        # (audit-recordonce), a protected test-no-build task, and an ordinary task, so the
        # line-level rule has something to discriminate against (arms i/j/k/r below).
        pixi_file = repo / "pixi.toml"
        pixi_file.write_text(
            "[tasks]\n"
            'build = { cmd = "dotnet build" }\n'
            'gates = { cmd = "python tools/gates/gates.py" }\n'
            'audit-recordonce = { cmd = "python tools/audit-completeness/recordonce.py" }\n'
            'test-no-build = { cmd = "python tools/buildlock.py dotnet test --no-build -m:1" }\n',
            encoding="utf-8",
        )

        subprocess.run(["git", "add", "."], cwd=repo, check=True, env=env)
        subprocess.run(["git", "commit", "-q", "-m", "initial base"], cwd=repo, check=True, env=env)

        base_sha = subprocess.run(["git", "rev-parse", "HEAD"], cwd=repo, capture_output=True, text=True, check=True, env=env).stdout.strip()
        default_branch = subprocess.run(["git", "rev-parse", "--abbrev-ref", "HEAD"], cwd=repo, capture_output=True, text=True, check=True, env=env).stdout.strip()

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

        # (d) Whole-file pixi.toml deletion, through the LINE-level rule -> FAIL. #1744: pixi.toml
        # itself no longer routes through is_protected_tooling (see sabotage.py's own comment on
        # its diff-shape-selftest fixture), so this exercises _pixi_toml_protected_hunk_touched's
        # deletion path instead -- redundant with arms i/j/k's coverage of the same path, kept for
        # the "whole file gone" shape specifically.
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

        # (f) Moved base branch: base advances (its own src+test commits) after branching -> a
        # test-only weakening PR against the *old* base must still FAIL. A two-dot diff would
        # reverse the base branch's own src commit into the diff, setting touches_src=True and
        # silently disabling Condition A -- a false PASS.
        subprocess.run(["git", "checkout", "-q", "-b", "branch-f", base_sha], cwd=repo, check=True, env=env)
        test_file.write_text("line1\nline3\n", encoding="utf-8")  # line2 deleted, no src touch
        subprocess.run(["git", "commit", "-q", "-a", "-m", "weaken test on old base"], cwd=repo, check=True, env=env)
        head_f = subprocess.run(["git", "rev-parse", "HEAD"], cwd=repo, capture_output=True, text=True, check=True, env=env).stdout.strip()

        subprocess.run(["git", "checkout", "-q", default_branch], cwd=repo, check=True, env=env)
        (src_dir / "Other.cs").write_text("class Other {}\n", encoding="utf-8")
        (tests_dir / "BazTests.cs").write_text("class BazTests {}\n", encoding="utf-8")
        subprocess.run(["git", "add", "."], cwd=repo, check=True, env=env)
        subprocess.run(["git", "commit", "-q", "-m", "advance base with src and test additions"], cwd=repo, check=True, env=env)
        moved_base = subprocess.run(["git", "rev-parse", "HEAD"], cwd=repo, capture_output=True, text=True, check=True, env=env).stdout.strip()

        passed_f, msgs_f = check_diff_shape(moved_base, head_f, labels=[], cwd=repo)
        if passed_f or not any("Test-only PR weakening" in m for m in msgs_f):
            failures.append(f"(f) test-only weakening against a moved base did not fail as expected (two-dot diff bug): {msgs_f}")
        else:
            print("  OK (f) test-only weakening against moved base -> FAIL")

        # (g) Renamed test file with a deleted line -> FAIL. Rename detection would otherwise pair
        # the old and new paths as one clean R### entry whose per-path diff (against the excluded
        # old path) reads the new file as freshly added -- all '+', zero '-'.
        subprocess.run(["git", "checkout", "-q", "-b", "branch-g", base_sha], cwd=repo, check=True, env=env)
        subprocess.run(["git", "mv", "tests/FooTests.cs", "tests/FooTests2.cs"], cwd=repo, check=True, env=env)
        (tests_dir / "FooTests2.cs").write_text("line1\nline3\n", encoding="utf-8")  # line2 deleted post-rename
        subprocess.run(["git", "commit", "-q", "-a", "-m", "rename and weaken test"], cwd=repo, check=True, env=env)
        head_g = subprocess.run(["git", "rev-parse", "HEAD"], cwd=repo, capture_output=True, text=True, check=True, env=env).stdout.strip()

        passed_g, msgs_g = check_diff_shape(base_sha, head_g, labels=[], cwd=repo)
        if passed_g or not any("Test-only PR weakening" in m for m in msgs_g):
            failures.append(f"(g) renamed+weakened test file did not fail as expected (rename fail-open bug): {msgs_g}")
        else:
            print("  OK (g) renamed+weakened test file -> FAIL")

        # (i) pixi.toml: adding an unrelated task -> PASS (#1744 line-level narrowing).
        subprocess.run(["git", "checkout", "-q", "-b", "branch-i", base_sha], cwd=repo, check=True, env=env)
        with pixi_file.open("a", encoding="utf-8") as f:
            f.write('unrelated-task = { cmd = "echo hi" }\n')
        subprocess.run(["git", "commit", "-q", "-a", "-m", "add unrelated pixi task"], cwd=repo, check=True, env=env)
        head_i = subprocess.run(["git", "rev-parse", "HEAD"], cwd=repo, capture_output=True, text=True, check=True, env=env).stdout.strip()

        passed_i, msgs_i = check_diff_shape(base_sha, head_i, labels=[], cwd=repo)
        if not passed_i:
            failures.append(f"(i) pixi.toml unrelated task addition failed unexpectedly: {msgs_i}")
        else:
            print("  OK (i) pixi.toml unrelated task addition -> PASS")

        # (j) pixi.toml: editing the `gates` task's cmd -> FAIL.
        subprocess.run(["git", "checkout", "-q", "-b", "branch-j", base_sha], cwd=repo, check=True, env=env)
        pixi_file.write_text(
            pixi_file.read_text(encoding="utf-8").replace(
                'gates = { cmd = "python tools/gates/gates.py" }',
                'gates = { cmd = "python tools/gates/gates.py --extra" }',
            ),
            encoding="utf-8",
        )
        subprocess.run(["git", "commit", "-q", "-a", "-m", "edit gates task cmd"], cwd=repo, check=True, env=env)
        head_j = subprocess.run(["git", "rev-parse", "HEAD"], cwd=repo, capture_output=True, text=True, check=True, env=env).stdout.strip()

        passed_j, msgs_j = check_diff_shape(base_sha, head_j, labels=[], cwd=repo)
        if passed_j:
            failures.append("(j) editing the gates task's cmd did not fail as expected")
        else:
            print("  OK (j) pixi.toml gates task cmd edit -> FAIL")

        # (k) pixi.toml: editing an audit-* task line (audit-recordonce) -> FAIL.
        subprocess.run(["git", "checkout", "-q", "-b", "branch-k", base_sha], cwd=repo, check=True, env=env)
        pixi_file.write_text(
            pixi_file.read_text(encoding="utf-8").replace(
                'audit-recordonce = { cmd = "python tools/audit-completeness/recordonce.py" }',
                'audit-recordonce = { cmd = "python tools/audit-completeness/recordonce.py --extra" }',
            ),
            encoding="utf-8",
        )
        subprocess.run(["git", "commit", "-q", "-a", "-m", "edit audit-recordonce task line"], cwd=repo, check=True, env=env)
        head_k = subprocess.run(["git", "rev-parse", "HEAD"], cwd=repo, capture_output=True, text=True, check=True, env=env).stdout.strip()

        passed_k, msgs_k = check_diff_shape(base_sha, head_k, labels=[], cwd=repo)
        if passed_k:
            failures.append("(k) editing the audit-recordonce task line did not fail as expected")
        else:
            print("  OK (k) pixi.toml audit-recordonce task edit -> FAIL")

        # (r) pixi.toml: editing the test-no-build task line -> FAIL (#1754 F2 -- test-no-build is
        # AFTER_BUILD_FULL's test leg in gates.py; a neutered cmd here drops test coverage from
        # every `pixi run gates` without touching a test file or tripping the old rule).
        subprocess.run(["git", "checkout", "-q", "-b", "branch-r", base_sha], cwd=repo, check=True, env=env)
        pixi_file.write_text(
            pixi_file.read_text(encoding="utf-8").replace(
                'test-no-build = { cmd = "python tools/buildlock.py dotnet test --no-build -m:1" }',
                'test-no-build = { cmd = "python tools/buildlock.py dotnet test --no-build -m:1 --filter Foo!=Bar" }',
            ),
            encoding="utf-8",
        )
        subprocess.run(["git", "commit", "-q", "-a", "-m", "edit test-no-build task line"], cwd=repo, check=True, env=env)
        head_r = subprocess.run(["git", "rev-parse", "HEAD"], cwd=repo, capture_output=True, text=True, check=True, env=env).stdout.strip()

        passed_r, msgs_r = check_diff_shape(base_sha, head_r, labels=[], cwd=repo)
        if passed_r:
            failures.append("(r) editing the test-no-build task line did not fail as expected")
        else:
            print("  OK (r) pixi.toml test-no-build task edit -> FAIL")

        # (l)-(p): the widened protected-tooling directories/files (#1744) -- one arm each, FAIL.
        widened_targets = [
            ("l", "tools/audit-completeness/completeness.py"),
            ("m", "tools/vendor-verify/verify.py"),
            ("n", "tools/buildlock.py"),
            ("o", "tools/flake-watch/summarize.py"),
            ("p", "tests/Baton.Architecture.Tests/SpawnGateTests.cs"),
            # (s)-(w): the #1754 widening -- the wired selftest bodies #1744's ruling had wrongly
            # excluded as "not enforcement", plus vendor-check's actual body.
            ("s", "tools/fleet-glass/worker.selftest.mjs"),
            ("t", "tools/tool-refresh/refresh.py"),
            ("u", "tools/baton-agy-loop/dispatch.py"),
            ("v", "tests/Launcher.Tests.ps1"),
            ("w", "tools/Baton.VendorProbe/Program.cs"),
        ]
        for label, rel_path in widened_targets:
            subprocess.run(["git", "checkout", "-q", "-b", f"branch-{label}", base_sha], cwd=repo, check=True, env=env)
            target = repo / rel_path
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_text("// edit\n", encoding="utf-8")
            subprocess.run(["git", "add", "."], cwd=repo, check=True, env=env)
            subprocess.run(["git", "commit", "-q", "-m", f"edit {rel_path}"], cwd=repo, check=True, env=env)
            head_x = subprocess.run(["git", "rev-parse", "HEAD"], cwd=repo, capture_output=True, text=True, check=True, env=env).stdout.strip()

            passed_x, msgs_x = check_diff_shape(base_sha, head_x, labels=[], cwd=repo)
            if passed_x:
                failures.append(f"({label}) editing {rel_path} did not fail as expected")
            else:
                print(f"  OK ({label}) {rel_path} edit -> FAIL")

        # (q) Control: a genuinely unprotected sibling in a partly-protected directory, plus
        # .githooks/, stay unprotected -> PASS. tools/fleet-glass/glass.html (not
        # worker.selftest.mjs, protected by name since #1754) proves the widening protects the
        # specific wired file rather than the whole tools/fleet-glass/ directory.
        subprocess.run(["git", "checkout", "-q", "-b", "branch-q", base_sha], cwd=repo, check=True, env=env)
        (repo / "tools" / "fleet-glass").mkdir(parents=True, exist_ok=True)
        (repo / "tools" / "fleet-glass" / "glass.html").write_text("<!-- x -->\n", encoding="utf-8")
        (repo / ".githooks").mkdir(parents=True, exist_ok=True)
        (repo / ".githooks" / "pre-push").write_text("# x\n", encoding="utf-8")
        subprocess.run(["git", "add", "."], cwd=repo, check=True, env=env)
        subprocess.run(["git", "commit", "-q", "-m", "edit non-protected tools"], cwd=repo, check=True, env=env)
        head_q = subprocess.run(["git", "rev-parse", "HEAD"], cwd=repo, capture_output=True, text=True, check=True, env=env).stdout.strip()

        passed_q, msgs_q = check_diff_shape(base_sha, head_q, labels=[], cwd=repo)
        if not passed_q:
            failures.append(f"(q) editing tools/fleet-glass/glass.html and .githooks/ failed unexpectedly: {msgs_q}")
        else:
            print("  OK (q) tools/fleet-glass/glass.html and .githooks/ edits -> PASS")

        # (x) Helper-only refactor: a private method's signature changes, 20 lines are added, and
        # no assertion pattern is removed -> PASS (#1758's narrowing target -- this is the #1757
        # shape: TestSupport/TempGitRepository.cs's Run() gained a captured-output return with no
        # assertion touched). Fails against the pre-#1758 code (any deleted test-dir line failed).
        subprocess.run(["git", "checkout", "-q", "-b", "branch-x", base_sha], cwd=repo, check=True, env=env)
        helper_dir = repo / "tests" / "TestSupport"
        helper_dir.mkdir(parents=True, exist_ok=True)
        helper_file = helper_dir / "Helper.cs"
        helper_file.write_text(
            "public static class Helper\n{\n    private static void Run(string a)\n    {\n    }\n}\n",
            encoding="utf-8",
        )
        subprocess.run(["git", "add", "."], cwd=repo, check=True, env=env)
        subprocess.run(["git", "commit", "-q", "-m", "add helper"], cwd=repo, check=True, env=env)
        helper_base = subprocess.run(["git", "rev-parse", "HEAD"], cwd=repo, capture_output=True, text=True, check=True, env=env).stdout.strip()

        extra_lines = "\n".join(f"    // note {i}" for i in range(20))
        helper_file.write_text(
            "public static class Helper\n{\n"
            "    private static string RunCapturing(string a)\n"
            "    {\n"
            f"{extra_lines}\n"
            "        return string.Empty;\n"
            "    }\n}\n",
            encoding="utf-8",
        )
        subprocess.run(["git", "commit", "-q", "-a", "-m", "refactor helper signature"], cwd=repo, check=True, env=env)
        head_x = subprocess.run(["git", "rev-parse", "HEAD"], cwd=repo, capture_output=True, text=True, check=True, env=env).stdout.strip()

        passed_x, msgs_x = check_diff_shape(helper_base, head_x, labels=[], cwd=repo)
        if not passed_x:
            failures.append(f"(x) helper-only refactor failed unexpectedly: {msgs_x}")
        else:
            print("  OK (x) helper-only refactor (net-additive, no assertion removed) -> PASS")

        # (y) A helper file that itself contains an assertion, with that assertion line removed
        # -> FAIL (criterion 1: assertion pattern in an unpaired deleted line).
        subprocess.run(["git", "checkout", "-q", "-b", "branch-y", base_sha], cwd=repo, check=True, env=env)
        assert_helper_dir = repo / "tests" / "TestSupport"
        assert_helper_dir.mkdir(parents=True, exist_ok=True)
        assert_helper_file = assert_helper_dir / "AssertingHelper.cs"
        assert_helper_file.write_text(
            "public static class AssertingHelper\n{\n"
            "    public static void Check(int actual)\n"
            "    {\n"
            "        Assert.Equal(1, actual);\n"
            "    }\n}\n",
            encoding="utf-8",
        )
        subprocess.run(["git", "add", "."], cwd=repo, check=True, env=env)
        subprocess.run(["git", "commit", "-q", "-m", "add asserting helper"], cwd=repo, check=True, env=env)
        y_base = subprocess.run(["git", "rev-parse", "HEAD"], cwd=repo, capture_output=True, text=True, check=True, env=env).stdout.strip()

        assert_helper_file.write_text(
            "public static class AssertingHelper\n{\n"
            "    public static void Check(int actual)\n"
            "    {\n"
            "    }\n}\n",
            encoding="utf-8",
        )
        subprocess.run(["git", "commit", "-q", "-a", "-m", "remove assertion from helper"], cwd=repo, check=True, env=env)
        head_y = subprocess.run(["git", "rev-parse", "HEAD"], cwd=repo, capture_output=True, text=True, check=True, env=env).stdout.strip()

        passed_y, msgs_y = check_diff_shape(y_base, head_y, labels=[], cwd=repo)
        if passed_y:
            failures.append("(y) removed Assert. line inside a TestSupport/ file did not fail as expected")
        else:
            print("  OK (y) removed Assert. line inside TestSupport/ -> FAIL")

        # (z) A [Fact] attribute removed (test disabled by deletion, method body kept) -> FAIL.
        subprocess.run(["git", "checkout", "-q", "-b", "branch-z", base_sha], cwd=repo, check=True, env=env)
        fact_test_file = repo / "tests" / "FactTests.cs"
        fact_test_file.write_text(
            "public class FactTests\n{\n    [Fact]\n    public void Works() {}\n}\n",
            encoding="utf-8",
        )
        subprocess.run(["git", "add", "."], cwd=repo, check=True, env=env)
        subprocess.run(["git", "commit", "-q", "-m", "add fact test"], cwd=repo, check=True, env=env)
        z_base = subprocess.run(["git", "rev-parse", "HEAD"], cwd=repo, capture_output=True, text=True, check=True, env=env).stdout.strip()

        fact_test_file.write_text(
            "public class FactTests\n{\n    public void Works() {}\n}\n",
            encoding="utf-8",
        )
        subprocess.run(["git", "commit", "-q", "-a", "-m", "disable test by removing [Fact]"], cwd=repo, check=True, env=env)
        head_z = subprocess.run(["git", "rev-parse", "HEAD"], cwd=repo, capture_output=True, text=True, check=True, env=env).stdout.strip()

        passed_z, msgs_z = check_diff_shape(z_base, head_z, labels=[], cwd=repo)
        if passed_z:
            failures.append("(z) removed [Fact] attribute did not fail as expected")
        else:
            print("  OK (z) removed [Fact] attribute -> FAIL")

        # (aa) A whole test file deleted -> FAIL (criterion 2).
        subprocess.run(["git", "checkout", "-q", "-b", "branch-aa", base_sha], cwd=repo, check=True, env=env)
        doomed_test_file = repo / "tests" / "DoomedTests.cs"
        doomed_test_file.write_text("public class DoomedTests {}\n", encoding="utf-8")
        subprocess.run(["git", "add", "."], cwd=repo, check=True, env=env)
        subprocess.run(["git", "commit", "-q", "-m", "add doomed test"], cwd=repo, check=True, env=env)
        aa_base = subprocess.run(["git", "rev-parse", "HEAD"], cwd=repo, capture_output=True, text=True, check=True, env=env).stdout.strip()

        doomed_test_file.unlink()
        subprocess.run(["git", "commit", "-q", "-a", "-m", "delete doomed test file"], cwd=repo, check=True, env=env)
        head_aa = subprocess.run(["git", "rev-parse", "HEAD"], cwd=repo, capture_output=True, text=True, check=True, env=env).stdout.strip()

        passed_aa, msgs_aa = check_diff_shape(aa_base, head_aa, labels=[], cwd=repo)
        if passed_aa:
            failures.append("(aa) test file deletion did not fail as expected")
        else:
            print("  OK (aa) test file deleted -> FAIL")

        # (ab) A file with only non-assertion setup lines removed, net-negative -> FAIL
        # (criterion 3: net-negative diff, no assertion pattern involved).
        subprocess.run(["git", "checkout", "-q", "-b", "branch-ab", base_sha], cwd=repo, check=True, env=env)
        setup_dir = repo / "tests" / "TestSupport"
        setup_dir.mkdir(parents=True, exist_ok=True)
        setup_file = setup_dir / "Fixture.cs"
        setup_lines = "\n".join(f"    int setup{i} = {i};" for i in range(30))
        setup_file.write_text(f"public static class Fixture\n{{\n{setup_lines}\n}}\n", encoding="utf-8")
        subprocess.run(["git", "add", "."], cwd=repo, check=True, env=env)
        subprocess.run(["git", "commit", "-q", "-m", "add fixture with setup lines"], cwd=repo, check=True, env=env)
        ab_base = subprocess.run(["git", "rev-parse", "HEAD"], cwd=repo, capture_output=True, text=True, check=True, env=env).stdout.strip()

        setup_file.write_text(
            "public static class Fixture\n{\n    int setupA = 1;\n    int setupB = 2;\n}\n",
            encoding="utf-8",
        )
        subprocess.run(["git", "commit", "-q", "-a", "-m", "trim fixture setup"], cwd=repo, check=True, env=env)
        head_ab = subprocess.run(["git", "rev-parse", "HEAD"], cwd=repo, capture_output=True, text=True, check=True, env=env).stdout.strip()

        passed_ab, msgs_ab = check_diff_shape(ab_base, head_ab, labels=[], cwd=repo)
        if passed_ab:
            failures.append("(ab) net-negative fixture trim did not fail as expected")
        else:
            print("  OK (ab) net-negative diff, no assertion pattern removed -> FAIL")

        # (ac) .mjs selftest: a `throw new Error(` line removed -> FAIL (criterion 1, the
        # selftest-only throw pattern).
        subprocess.run(["git", "checkout", "-q", "-b", "branch-ac", base_sha], cwd=repo, check=True, env=env)
        mjs_file = repo / "tests" / "worker.selftest.mjs"
        mjs_file.write_text(
            "function check(ok) {\n  if (!ok) {\n    throw new Error('failed');\n  }\n}\n",
            encoding="utf-8",
        )
        subprocess.run(["git", "add", "."], cwd=repo, check=True, env=env)
        subprocess.run(["git", "commit", "-q", "-m", "add mjs selftest"], cwd=repo, check=True, env=env)
        ac_base = subprocess.run(["git", "rev-parse", "HEAD"], cwd=repo, capture_output=True, text=True, check=True, env=env).stdout.strip()

        mjs_file.write_text(
            "function check(ok) {\n  if (!ok) {\n  }\n}\n",
            encoding="utf-8",
        )
        subprocess.run(["git", "commit", "-q", "-a", "-m", "remove throw from mjs selftest"], cwd=repo, check=True, env=env)
        head_ac = subprocess.run(["git", "rev-parse", "HEAD"], cwd=repo, capture_output=True, text=True, check=True, env=env).stdout.strip()

        passed_ac, msgs_ac = check_diff_shape(ac_base, head_ac, labels=[], cwd=repo)
        if passed_ac:
            failures.append("(ac) removed throw in .mjs selftest did not fail as expected")
        else:
            print("  OK (ac) .mjs selftest throw removal -> FAIL")

        # (h) main()'s GITHUB_EVENT_PATH fallback, with no --labels passed -- the actual channel
        # CI uses. A synthetic event payload carries two labels including operator-merge; the
        # failing shape (head_a) must be lifted, and the negative arm (label absent) must not be.
        event_path = repo / "event.json"
        prev_event_path = os.environ.get("GITHUB_EVENT_PATH")
        prev_cwd = os.getcwd()
        try:
            event_path.write_text(
                json.dumps({"pull_request": {"labels": [{"name": "bug"}, {"name": "operator-merge"}]}}),
                encoding="utf-8",
            )
            os.environ["GITHUB_EVENT_PATH"] = str(event_path)
            os.chdir(repo)

            rc_lifted = main(["--base", base_sha, "--head", head_a])
            if rc_lifted != 0:
                failures.append(f"(h) event-path labels with operator-merge present did not lift the gate: exit {rc_lifted}")
            else:
                print("  OK (h1) event-path labels incl. operator-merge -> exit 0")

            event_path.write_text(
                json.dumps({"pull_request": {"labels": [{"name": "bug"}]}}),
                encoding="utf-8",
            )
            rc_blocked = main(["--base", base_sha, "--head", head_a])
            if rc_blocked != 1:
                failures.append(f"(h) event-path labels without operator-merge did not fail: exit {rc_blocked}")
            else:
                print("  OK (h2) event-path labels w/o operator-merge -> exit 1")
        finally:
            os.chdir(prev_cwd)
            if prev_event_path is None:
                os.environ.pop("GITHUB_EVENT_PATH", None)
            else:
                os.environ["GITHUB_EVENT_PATH"] = prev_event_path

    if failures:
        print(f"diff-shape: selftest FAIL -- {'; '.join(failures)}", file=sys.stderr)
        return 1

    print("diff-shape: selftest OK (all 29 discrimination arms passed)")
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
    except Exception as exc:
        print(f"diff-shape: failed to read labels from GITHUB_EVENT_PATH: {exc}", file=sys.stderr)
        return []


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Diff-shape CI gate check (#1603)")
    parser.add_argument("--base", type=str, default="origin/main", help="Base commit/ref to diff against")
    parser.add_argument("--head", type=str, default="HEAD", help="Head commit/ref to diff against")
    parser.add_argument("--labels", type=str, default="", help="Newline-delimited PR labels, for tests; CI reads GITHUB_EVENT_PATH instead")
    parser.add_argument("--actor", type=str, default="", help="Who to name in the lift notice, if the failure is lifted by label")
    parser.add_argument("--selftest", action="store_true", help="Run synthetic selftest fixtures")

    args = parser.parse_args(argv)

    if args.selftest:
        return selftest()

    labels_list = [lbl.strip() for lbl in args.labels.splitlines() if lbl.strip()]
    if not labels_list:
        labels_list = get_labels_from_event_path()

    actor = args.actor

    passed, messages = check_diff_shape(args.base, args.head, labels=labels_list, actor=actor)
    for msg in messages:
        print(msg)

    return 0 if passed else 1


if __name__ == "__main__":
    sys.exit(main())
