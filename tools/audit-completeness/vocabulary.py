"""Audit user-facing strings for banned engine vocabulary.

The banned list is the complement of the product's own user-facing nouns -- one vocabulary, code and
UI alike, no translation map (CLAUDE.md gate `record-once`).

Walks user-facing surfaces (.axaml markup, C# ViewModels string literals, Dart lib string literals)
and fails when banned terms appear without an inline allowlist comment `vocabulary-ok: <reason>`.
"""
from __future__ import annotations

import os
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]

BANNED_TERMS = [
    "PausePoint",
    "supersede",
    "turn-anchor",
    "turn.marker",
    "bindings",
    "Terminal",
    "adapter",
    "projection",
    "Session",
    "gemini",
    "Aer.Daemon",
    "lane",
    "prompt.txt",
]

BANNED_PATTERNS = {
    term: re.compile(r"\b" + re.escape(term) + r"\b", re.IGNORECASE)
    for term in BANNED_TERMS
}

# Only the verbatim forms can span lines in C#; Dart's triple-quoted strings likewise. The
# per-line pass cannot see into a literal that starts on one line and leaks on the next
# (found by #315's second reader), so these run over the whole file text.
CS_MULTILINE_STRING_RE = re.compile(r'(?:\$@"|@\$"|@")(?:[^"]|"")*"')

DART_MULTILINE_STRING_RE = re.compile(r'r?"""[\s\S]*?"""|r?\'\'\'[\s\S]*?\'\'\'')

CS_STRING_RE = re.compile(
    r"(?:"
    r'\$@"(?:[^"]|"")*"'
    r'|@\$"(?:[^"]|"")*"'
    r'|@"(?:[^"]|"")*"'
    r'|\$"(?:[^"\\]|\\.)*"'
    r'|"(?:[^"\\]|\\.)*"'
    r")"
)

DART_STRING_RE = re.compile(
    r"(?:"
    r'r?"""[\s\S]*?"""'
    r"|r?'''[\s\S]*?'''"
    r'|r?"(?:[^"\\]|\\.)*"'
    r"|r?'(?:[^'\\]|\\.)*'"
    r")"
)


def strip_cs_comment(line: str) -> str:
    s = line.strip()
    if s.startswith("//") or s.startswith("/*") or s.startswith("*") or s.startswith("///"):
        return ""
    in_str = False
    quote_char = None
    i = 0
    while i < len(line):
        ch = line[i]
        if not in_str:
            if ch in ('"', "'"):
                in_str = True
                quote_char = ch
            elif ch == "/" and i + 1 < len(line) and line[i + 1] == "/":
                return line[:i]
        else:
            if ch == quote_char:
                if i > 0 and line[i - 1] == "\\":
                    num_bs = 0
                    j = i - 1
                    while j >= 0 and line[j] == "\\":
                        num_bs += 1
                        j -= 1
                    if num_bs % 2 == 0:
                        in_str = False
                else:
                    in_str = False
        i += 1
    return line


def get_cs_literals(line: str) -> list[str]:
    clean_line = strip_cs_comment(line)
    return [m.group(0) for m in CS_STRING_RE.finditer(clean_line)]


def get_dart_literals(line: str) -> list[str]:
    clean_line = strip_cs_comment(line)
    return [m.group(0) for m in DART_STRING_RE.finditer(clean_line)]


def scan_multiline_literals(
    rel_path: str,
    text: str,
    lines: list[str],
    literal_re: re.Pattern[str],
    violations: list[tuple[str, int, str, str]],
) -> None:
    """Scan literals that span lines; single-line literals stay the per-line pass's job.

    The allowlist comment is honoured on the literal's opening line or the line above it,
    same contract as the per-line pass.
    """
    for m in literal_re.finditer(text):
        lit = m.group(0)
        if "\n" not in lit:
            continue
        start_line = text.count("\n", 0, m.start()) + 1
        prev_line = lines[start_line - 2] if start_line >= 2 else ""
        if "vocabulary-ok:" in lines[start_line - 1] or "vocabulary-ok:" in prev_line:
            continue
        for term, pat in BANNED_PATTERNS.items():
            if pat.search(lit):
                violations.append((rel_path, start_line, term, lines[start_line - 1].strip()))


def scan_tree(root_dir: Path) -> tuple[int, list[tuple[str, int, str, str]]]:
    """Scan AXAML, C#, and Dart populations under root_dir.

    Returns (total_files_scanned, list_of_violations).
    Each violation is (relative_path, line_number, term, line_content).
    """
    total_files = 0
    violations = []

    # Population 1: src/Aer.Ui/**/*.axaml
    ui_dir = root_dir / "src" / "Aer.Ui"
    if ui_dir.exists():
        for path in ui_dir.rglob("*.axaml"):
            total_files += 1
            rel_path = path.relative_to(root_dir).as_posix()
            lines = path.read_text(encoding="utf-8", errors="ignore").splitlines()
            in_xml_comment = False
            for idx, line in enumerate(lines, 1):
                prev_line = lines[idx - 2] if idx >= 2 else ""
                if "vocabulary-ok:" in line or "vocabulary-ok:" in prev_line:
                    if "-->" in line and in_xml_comment:
                        in_xml_comment = False
                    continue

                line_to_check = line
                if in_xml_comment:
                    if "-->" in line:
                        in_xml_comment = False
                        line_to_check = line[line.index("-->") + 3 :]
                    else:
                        continue
                else:
                    if "<!--" in line_to_check:
                        if "-->" in line_to_check:
                            line_to_check = re.sub(r"<!--[\s\S]*?-->", "", line_to_check)
                        else:
                            in_xml_comment = True
                            line_to_check = line_to_check[: line_to_check.index("<!--")]

                if not line_to_check.strip():
                    continue

                for term, pat in BANNED_PATTERNS.items():
                    if pat.search(line_to_check):
                        violations.append((rel_path, idx, term, line.strip()))

    # Population 2: src/Aer.Ui.Core/**/*.cs and src/Aer.Ui/**/*.cs
    cs_dirs = [root_dir / "src" / "Aer.Ui.Core", root_dir / "src" / "Aer.Ui"]
    for cs_dir in cs_dirs:
        if not cs_dir.exists():
            continue
        for path in cs_dir.rglob("*.cs"):
            total_files += 1
            rel_path = path.relative_to(root_dir).as_posix()
            text = path.read_text(encoding="utf-8", errors="ignore")
            lines = text.splitlines()
            scan_multiline_literals(rel_path, text, lines, CS_MULTILINE_STRING_RE, violations)
            for idx, line in enumerate(lines, 1):
                prev_line = lines[idx - 2] if idx >= 2 else ""
                if "vocabulary-ok:" in line or "vocabulary-ok:" in prev_line:
                    continue
                literals = get_cs_literals(line)
                for lit in literals:
                    for term, pat in BANNED_PATTERNS.items():
                        if pat.search(lit):
                            violations.append((rel_path, idx, term, line.strip()))

    # Population 3: src/Aer.Mobile/lib/**/*.dart
    mobile_dir = root_dir / "src" / "Aer.Mobile" / "lib"
    if mobile_dir.exists():
        for path in mobile_dir.rglob("*.dart"):
            total_files += 1
            rel_path = path.relative_to(root_dir).as_posix()
            text = path.read_text(encoding="utf-8", errors="ignore")
            lines = text.splitlines()
            scan_multiline_literals(rel_path, text, lines, DART_MULTILINE_STRING_RE, violations)
            for idx, line in enumerate(lines, 1):
                prev_line = lines[idx - 2] if idx >= 2 else ""
                if "vocabulary-ok:" in line or "vocabulary-ok:" in prev_line:
                    continue
                literals = get_dart_literals(line)
                for lit in literals:
                    for term, pat in BANNED_PATTERNS.items():
                        if pat.search(lit):
                            violations.append((rel_path, idx, term, line.strip()))

    return total_files, violations


def main() -> int:
    total_files, violations = scan_tree(ROOT)
    if violations:
        print(f"FAILED: Found {len(violations)} banned vocabulary violation(s) across {total_files} files examined:")
        for rel_path, line_num, term, content in violations:
            print(f"  !! {rel_path}:{line_num} [{term}]: {content}")
        return 1

    print(f"OK: No banned engine vocabulary found across {total_files} files examined.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
