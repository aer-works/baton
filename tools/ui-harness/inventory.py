"""Enumerates every interactive control in the desktop (AXAML) UI.

Ad-hoc clicking missed real capabilities this session because they sat behind
collapsed expanders, non-default tabs, or state-gated sections. A source-derived
inventory turns "did we look everywhere" into a checklist that can be walked and ticked.
"""

import os
import re
import sys
from collections import OrderedDict

# tools/ui-harness/inventory.py -> repo root -> src/
ROOT = os.path.join(
    os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))), "src"
)

# Desktop: Avalonia control types that a user can actuate, plus containers that HIDE
# things (Expander/TabItem) since those are where capability went missing.
AXAML_KINDS = [
    ("Expander", r"<Expander\b[^>]*"),
    ("TabItem", r"<TabItem\b[^>]*"),
    ("Button", r"<Button\b[^>]*"),
    ("SplitButton", r"<SplitButton\b[^>]*"),
    ("HyperlinkButton", r"<HyperlinkButton\b[^>]*"),
    ("ToggleButton", r"<ToggleButton\b[^>]*"),
    ("CheckBox", r"<CheckBox\b[^>]*"),
    ("RadioButton", r"<RadioButton\b[^>]*"),
    ("ComboBox", r"<ComboBox\b[^>]*"),
    ("ToggleSwitch", r"<ToggleSwitch\b[^>]*"),
    ("MenuItem", r"<MenuItem\b[^>]*"),
    ("ListBox", r"<ListBox\b[^>]*"),
]

LABEL_PATTERNS = [
    re.compile(r'\bContent\s*=\s*"([^"]+)"'),
    re.compile(r'\bHeader\s*=\s*"([^"]+)"'),
    re.compile(r'\bText\s*=\s*"([^"]+)"'),
    re.compile(r'\bx:Name\s*=\s*"([^"]+)"'),
    re.compile(r'\bCommand\s*=\s*"\{Binding ([^}"]+)\}"'),
]


def walk(exts):
    for base, _dirs, files in os.walk(ROOT):
        if any(seg in base for seg in ("\\bin\\", "\\obj\\", "\\build\\")):
            continue
        for f in files:
            if f.endswith(exts):
                yield os.path.join(base, f)


def label_for(fragment):
    for pat in LABEL_PATTERNS:
        m = pat.search(fragment)
        if m:
            return m.group(1)
    return ""


def scan(files, kinds):
    rows = []
    for path in files:
        try:
            text = open(path, encoding="utf-8").read()
        except Exception:
            continue
        rel = os.path.relpath(path, ROOT)
        for kind, pattern in kinds:
            for m in re.finditer(pattern, text):
                line = text.count("\n", 0, m.start()) + 1
                label = label_for(m.group(0))
                rows.append((rel, line, kind, label))
    return rows


def report(title, rows):
    print("=" * 78)
    print(title)
    print("=" * 78)
    by_file = OrderedDict()
    for rel, line, kind, label in sorted(rows):
        by_file.setdefault(rel, []).append((line, kind, label))
    total = 0
    for rel, items in by_file.items():
        counts = {}
        for _l, kind, _lab in items:
            counts[kind] = counts.get(kind, 0) + 1
        summary = ", ".join("%s x%d" % (k, v) for k, v in sorted(counts.items()))
        print("\n%s  (%d)" % (rel, len(items)))
        print("    %s" % summary)
        total += len(items)
    print("\nTOTAL interactive controls: %d across %d files" % (total, len(by_file)))
    return total, by_file


def main():
    xaml_rows = scan(list(walk((".axaml",))), AXAML_KINDS)
    d_total, _ = report("DESKTOP (Avalonia .axaml)", xaml_rows)

    print("\n" + "=" * 78)
    print("DISCLOSURE CONTAINERS — where capability hides")
    print("=" * 78)
    for rel, line, kind, label in sorted(xaml_rows):
        if kind in ("Expander", "TabItem"):
            print("  %-46s :%-5s %-14s %s" % (rel, line, kind, label))

    print("\nGRAND TOTAL: %d" % d_total)


main()
