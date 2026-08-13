"""Render the interaction-state register into 03-interaction-depth's table (#616).

The register (design/interaction-states.json) is the one home for the states — it holds however
many there are, and this script is what keeps anything else from having to count them; the
corpus's table is a rendering of it, regenerated here between sentinel markers so the two cannot
drift — the same generate-and-check shape as genregister.py, checked by completeness.py.
Loud on anything malformed: a table silently missing a state is the drift class this ends.
"""

import json
import os
import sys

ROOT = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))
REGISTER = os.path.join(ROOT, "design", "interaction-states.json")
DOC = os.path.join(ROOT, "docs", "design", "03-interaction-depth.md")
BEGIN = "<!-- generated: interaction-states (pixi run gen-states; edits here are overwritten) -->"
END = "<!-- /generated: interaction-states -->"


def parse_states() -> list[tuple[str, str, str]]:
    """(name, behaviour, provenance) per state, in register order; loud on any malformed entry."""
    with open(REGISTER, encoding="utf-8") as fh:
        register = json.load(fh)
    states = register.get("states")
    if not states:
        raise SystemExit(f"{REGISTER}: no states parsed — has its format changed?")
    rows = []
    for key, state in states.items():
        for field in ("name", "behaviour", "provenance"):
            if not state.get(field, "").strip():
                raise SystemExit(f"{REGISTER}: state '{key}' is missing '{field}'")
        rows.append((state["name"], state["behaviour"], state["provenance"]))
    return rows


def generated_block() -> str:
    lines = [BEGIN, "", "State | What the surface does |", ""]
    for name, behaviour, provenance in parse_states():
        lines.append(f"{name} | {behaviour} *({provenance})* |")
        lines.append("")
    lines.append(END)
    return "\n".join(lines)


def split_doc(text: str) -> tuple[str, str]:
    # Exactly one marker pair, or refuse: a duplicated pair (bad merge, double paste) would
    # regenerate only the first block and leave a stale second one that --check then blesses —
    # the silent-drift shape this generator exists to end (found by #616's second reader).
    if text.count(BEGIN) != 1 or text.count(END) != 1:
        raise SystemExit(f"{DOC}: expected exactly one sentinel pair, found {text.count(BEGIN)} BEGIN / {text.count(END)} END")
    begin = text.find(BEGIN)
    end = text.find(END)
    if end < begin:
        raise SystemExit(f"{DOC}: sentinel markers misordered — cannot regenerate the table")
    return text[:begin], text[end + len(END):]


def main(argv: list[str]) -> int:
    with open(DOC, encoding="utf-8") as fh:
        current = fh.read()
    before, after = split_doc(current)
    desired = before + generated_block() + after
    if desired == current:
        return 0
    if "--check" in argv:
        print(f"!! {DOC} is out of date with {REGISTER}. Regenerate it: pixi run gen-states")
        return 1
    with open(DOC, "w", encoding="utf-8", newline="\n") as fh:
        fh.write(desired)
    print(f"written {DOC}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
