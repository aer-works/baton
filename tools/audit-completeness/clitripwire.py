"""Fail when `docs/agents/invoking-baton.md`'s command lines drift from the CLI parsers' own usage.

#1400: that doc is the cold-start register for any agent driving Baton -- including a non-Claude
orchestrator taking the seat -- and it has a documented rot history (caught stale once by an Opus
review; Gemini-tier implementers measurably skip doc registers). Prose saying "keep this in sync"
enforces nothing on its own -- this is the check that does, per the project's rule that anything
which must not regress needs one that runs and fails.

THE TWO SOURCES, READ FROM CODE RATHER THAN HARDCODED
- The CLI parsers: every `src/Baton.Cli/*OptionsParser.cs` is enumerated by glob, and each one's own
  `Usage` string constant (the same text it prints on a malformed invocation) is parsed for its verb
  and its flags. A flag inside `[...]` is optional; a flag inside a bare `(a | b)` alternation group
  needs only one member present; everything else is required. This is the same shape
  `spec/baton.md`'s CLI table was verified against.
- The doc: every fenced or single-backtick `baton <verb> ...` span in invoking-baton.md is extracted,
  with its starting line, verb, and the `--flag` tokens it uses.

TWO DIRECTIONS OF DRIFT
1. A doc span uses a flag its verb's parser does not recognise at all (a rename, a typo, or a
   feature -- like `--help` -- the CLI never implemented). Reported at the doc line.
2. A verb the doc actually demonstrates with a real invocation (not just named in passing) omits one
   of that parser's REQUIRED flags from every one of its doc invocations -- a reader who copies the
   doc's example gets a `CliArgumentException`. Reported at the parser's own Usage line, since there
   is no single doc line the omission belongs to.
   Deliberately scoped to REQUIRED flags only: invoking-baton.md is the quickstart, not the
   reference (`docs/dispatch.md` and `spec/baton.md` own that job, and CLAUDE.md's record-once gate
   forbids restating one in the other) -- demanding every optional flag appear here would turn the
   quickstart into the reference it explicitly defers to.
A verb the doc never demonstrates with a flag-bearing invocation (`baton cancel`, `baton supply` are
never shown at all) is out of scope for direction 2 -- there is nothing there to be missing FROM.

Bare mentions (`baton templates`, `` `baton run` `` with no arguments) still have their verb checked
against Program.cs's own `knownSubcommands` list, so a doc reference to a retired or misspelled verb
is still caught even though it carries no flags to validate.
"""
from __future__ import annotations

import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]

CLI_DIR = ROOT / "src" / "Baton.Cli"
PROGRAM_CS = CLI_DIR / "Program.cs"
DOC = ROOT / "docs" / "agents" / "invoking-baton.md"

FLAG_RE = re.compile(r"--[a-z][a-z0-9-]*")
BRACKET_RE = re.compile(r"\[[^\[\]]*\]")
PAREN_RE = re.compile(r"\([^()]*\)")
VERB_RE = re.compile(r"\bbaton\s+([a-z][a-z0-9-]*)")

USAGE_CONST_RE = re.compile(
    r'const\s+string\s+Usage\s*=\s*((?:"(?:[^"\\]|\\.)*"\s*\+?\s*)+);')
STRING_LITERAL_RE = re.compile(r'"((?:[^"\\]|\\.)*)"')
CLASS_RE = re.compile(r"class\s+(\w+OptionsParser)\b")

FENCE_RE = re.compile(r"```[a-zA-Z]*\n(.*?)```", re.S)
INLINE_RE = re.compile(r"`(baton [^`]*)`", re.S)

# Sanity floor (#common-sense): a checker that silently extracted nothing is worse than none.
MIN_PARSERS = 5
MIN_PARSER_FLAGS = 15
MIN_KNOWN_VERBS = 5
MIN_DOC_INVOCATIONS = 10


class ParserContract:
    """One `*OptionsParser`'s usage contract, parsed from its own `Usage` string."""

    def __init__(self, cls_name, file_rel, line, verb, all_flags, required_flags, or_groups):
        self.cls_name = cls_name
        self.file_rel = file_rel
        self.line = line
        self.verb = verb
        self.all_flags = all_flags
        self.required_flags = required_flags
        self.or_groups = or_groups  # list[frozenset[str]]


class DocInvocation:
    """One `baton <verb> ...` span found in the doc."""

    def __init__(self, line, verb, flags, raw):
        self.line = line
        self.verb = verb
        self.flags = flags
        self.raw = raw


def parse_usage_text(usage_text: str):
    """(verb, all_flags, required_flags, or_groups) from one parser's `Usage:` string.

    Pure text -> data, so `_selftest` can drive it with fixtures instead of real parser files.
    """
    verb_match = VERB_RE.search(usage_text)
    verb = verb_match.group(1) if verb_match else None
    all_flags = set(FLAG_RE.findall(usage_text))

    without_optional = BRACKET_RE.sub(" ", usage_text)
    or_groups = [frozenset(FLAG_RE.findall(g)) for g in PAREN_RE.findall(without_optional)]
    or_groups = [g for g in or_groups if g]
    without_groups = PAREN_RE.sub(" ", without_optional)
    required_flags = set(FLAG_RE.findall(without_groups))

    return verb, all_flags, required_flags, or_groups


def parse_parser_file(text: str, file_rel: str) -> ParserContract | None:
    """A `ParserContract` from one `*OptionsParser.cs`'s source text, or None if it has no
    recognisable `Usage` constant -- callers treat that as a hard extraction failure, not a skip.
    """
    class_match = CLASS_RE.search(text)
    usage_match = USAGE_CONST_RE.search(text)
    if class_match is None or usage_match is None:
        return None

    line = text.count("\n", 0, usage_match.start()) + 1
    usage_text = "".join(STRING_LITERAL_RE.findall(usage_match.group(1)))
    verb, all_flags, required_flags, or_groups = parse_usage_text(usage_text)
    if verb is None:
        return None
    return ParserContract(class_match.group(1), file_rel, line, verb, all_flags, required_flags, or_groups)


def parse_known_subcommands(program_cs_text: str) -> set[str]:
    """`Program.cs`'s own `knownSubcommands` array -- the verb list, including ones with no
    dedicated `*OptionsParser` (`templates`), which the parser-file scan cannot see at all.
    """
    m = re.search(r"knownSubcommands\s*=\s*new\s*\[\]\s*\{([^}]*)\}", program_cs_text)
    if not m:
        return set()
    return set(STRING_LITERAL_RE.findall(m.group(1)))


def parse_doc(text: str) -> list[DocInvocation]:
    """Every `baton <verb> ...` span in the doc, fenced blocks and inline code spans alike."""
    invocations = []

    for m in FENCE_RE.finditer(text):
        block = m.group(1)
        if not block.strip().startswith("baton "):
            continue
        line = text.count("\n", 0, m.start(1)) + 1
        verb_match = VERB_RE.search(block)
        if verb_match is None:
            continue
        invocations.append(DocInvocation(line, verb_match.group(1), set(FLAG_RE.findall(block)), block.strip()))

    # Inline spans are searched over the doc with every fenced block blanked out (same line count
    # preserved, so line numbers below still line up) -- otherwise a single-backtick match starting
    # on a ``` fence line can run through fenced content and re-report it a second time, garbled.
    text_no_fences = FENCE_RE.sub(lambda m: "\n" * m.group(0).count("\n"), text)
    for m in INLINE_RE.finditer(text_no_fences):
        span = m.group(1)
        line = text_no_fences.count("\n", 0, m.start()) + 1
        verb_match = VERB_RE.match(span)
        if verb_match is None:
            continue
        invocations.append(DocInvocation(line, verb_match.group(1), set(FLAG_RE.findall(span)), span.strip()))

    return invocations


def find_drift(parsers: dict[str, ParserContract], known_verbs: set[str],
                invocations: list[DocInvocation]) -> list[str]:
    """Every drift finding, doc-line-first for direction 1, parser-line for direction 2."""
    problems = []

    for inv in invocations:
        if inv.verb not in known_verbs:
            problems.append(
                f"docs/agents/invoking-baton.md:{inv.line}: `baton {inv.verb}` -- not a known "
                f"subcommand (Program.cs's knownSubcommands). Invocation: `{inv.raw}`")
            continue
        parser = parsers.get(inv.verb)
        if parser is None:
            continue  # a known verb with no OptionsParser (`templates`) carries no flags to check
        unknown_flags = sorted(inv.flags - parser.all_flags)
        for flag in unknown_flags:
            problems.append(
                f"docs/agents/invoking-baton.md:{inv.line}: `baton {inv.verb}` doc invocation uses "
                f"'{flag}', which {parser.cls_name}.Usage does not recognise. Invocation: `{inv.raw}`")

    # Direction 2: only verbs the doc actually demonstrates with a real (flag-bearing) invocation.
    covered_flags: dict[str, set[str]] = {}
    for inv in invocations:
        if inv.flags:
            covered_flags.setdefault(inv.verb, set()).update(inv.flags)

    for verb, doc_flags in covered_flags.items():
        parser = parsers.get(verb)
        if parser is None:
            continue
        for missing in sorted(parser.required_flags - doc_flags):
            problems.append(
                f"{parser.file_rel}:{parser.line}: {parser.cls_name}.Usage requires '{missing}' "
                f"for `baton {verb}`, but no invoking-baton.md invocation of `baton {verb}` ever shows it")
        for group in parser.or_groups:
            if not (group & doc_flags):
                problems.append(
                    f"{parser.file_rel}:{parser.line}: {parser.cls_name}.Usage requires one of "
                    f"{sorted(group)} for `baton {verb}`, but no invoking-baton.md invocation of "
                    f"`baton {verb}` shows any of them")

    return problems


def main(argv: list[str]) -> int:
    if "--selftest" in argv:
        return _selftest()

    parser_files = sorted(CLI_DIR.glob("*OptionsParser.cs"))
    parsers: dict[str, ParserContract] = {}
    total_flags = 0
    for path in parser_files:
        contract = parse_parser_file(path.read_text(encoding="utf-8"), str(path.relative_to(ROOT)).replace("\\", "/"))
        if contract is None:
            print(f" !! {path.relative_to(ROOT)}: found no `Usage` constant to extract -- this checker's "
                  "extraction anchor no longer matches the source")
            return 1
        parsers[contract.verb] = contract
        total_flags += len(contract.all_flags)

    known_verbs = parse_known_subcommands(PROGRAM_CS.read_text(encoding="utf-8"))
    if not DOC.is_file():
        print(f" !! {DOC.relative_to(ROOT)}: missing -- this check's target moved without it")
        return 1
    invocations = parse_doc(DOC.read_text(encoding="utf-8"))

    print(f"clitripwire: {len(parsers)} parser(s), {total_flags} flag(s), {len(known_verbs)} known "
          f"verb(s), {len(invocations)} doc invocation(s)")

    if len(parsers) < MIN_PARSERS or total_flags < MIN_PARSER_FLAGS:
        print(f" !! sanity floor tripped: expected >= {MIN_PARSERS} parsers and >= {MIN_PARSER_FLAGS} "
              f"flags extracted from {CLI_DIR.relative_to(ROOT)}, got {len(parsers)} and {total_flags} -- "
              "the extraction anchor likely no longer matches the source")
        return 1
    if len(known_verbs) < MIN_KNOWN_VERBS:
        print(f" !! sanity floor tripped: expected >= {MIN_KNOWN_VERBS} known subcommands from "
              f"Program.cs, got {len(known_verbs)}")
        return 1
    if len(invocations) < MIN_DOC_INVOCATIONS:
        print(f" !! sanity floor tripped: expected >= {MIN_DOC_INVOCATIONS} `baton <verb> ...` spans "
              f"in {DOC.relative_to(ROOT)}, got {len(invocations)}")
        return 1

    problems = find_drift(parsers, known_verbs, invocations)
    if problems:
        print(f" !! {len(problems)} problem(s):")
        for p in problems:
            print(f"  {p}")
        return 1
    print(" OK every doc command line matches its parser's usage string, in both directions")
    return 0


def _selftest() -> int:
    """Red/green arms for each rule, against synthetic parser/doc text -- never the real tree, so
    this proves the RULES discriminate independently of whatever invoking-baton.md says today.
    """
    failures = []

    fake_run_usage = (
        'public const string Usage =\n'
        '    "Usage: baton run <workflow-file> --bindings <bindings-file> [--room-dir <dir>] '
        '[--echo-worker]";')
    fake_run_source = f'namespace Baton.Cli;\npublic static class RunOptionsParser {{\n{fake_run_usage}\n}}\n'
    run_contract = parse_parser_file(fake_run_source, "src/Baton.Cli/RunOptionsParser.cs")
    if run_contract is None or run_contract.verb != "run":
        failures.append("could not parse the fixture RunOptionsParser -- extraction anchor broke on a fixture")
    else:
        parsers = {"run": run_contract}
        known_verbs = {"run"}

        # Arm 1: unknown doc flag caught.
        bad_doc = parse_doc("```\nbaton run wf.json --bindings b.json --frobnicate\n```\n")
        problems = find_drift(parsers, known_verbs, bad_doc)
        if not any("--frobnicate" in p for p in problems):
            failures.append("arm 1 FAILED: an unknown doc flag was not caught")

        # Arm 2: parser flag the doc's covered verb never mentions (required --bindings omitted).
        incomplete_doc = parse_doc("```\nbaton run wf.json --room-dir /tmp/x\n```\n")
        problems = find_drift(parsers, known_verbs, incomplete_doc)
        if not any("--bindings" in p and "RunOptionsParser" in p for p in problems):
            failures.append("arm 2 FAILED: a required parser flag missing from every doc invocation was not caught")

        # Arm 2 control: an optional flag (--room-dir) omitted from the doc must NOT fail --
        # the quickstart is not required to demonstrate every optional flag.
        minimal_doc = parse_doc("```\nbaton run wf.json --bindings b.json\n```\n")
        problems = find_drift(parsers, known_verbs, minimal_doc)
        if problems:
            failures.append(f"arm 2 control FAILED: omitting an optional flag fired: {problems}")

        # Arm 3: unknown verb caught even with no flags.
        bad_verb_doc = parse_doc("see `baton defenestrate <room-dir>` for details")
        problems = find_drift(parsers, known_verbs, bad_verb_doc)
        if not any("defenestrate" in p for p in problems):
            failures.append("arm 3 FAILED: an unknown verb was not caught")

        # Arm 4: clean doc passes.
        clean_doc = parse_doc("```\nbaton run wf.json --bindings b.json --room-dir /tmp/x --echo-worker\n```\n")
        problems = find_drift(parsers, known_verbs, clean_doc)
        if problems:
            failures.append(f"arm 4 FAILED: a clean doc invocation was reported as drift: {problems}")

    # Arm 5: OR-group satisfied by either alternative, not both.
    fake_resume_usage = (
        'public const string Usage =\n'
        '    "Usage: baton resume <room-dir> --worker <role> (--message <text> | --message-file <path>) '
        '--bindings <bindings-file>";')
    fake_resume_source = (
        f'namespace Baton.Cli;\npublic static class ResumeOptionsParser {{\n{fake_resume_usage}\n}}\n')
    resume_contract = parse_parser_file(fake_resume_source, "src/Baton.Cli/ResumeOptionsParser.cs")
    if resume_contract is None:
        failures.append("could not parse the fixture ResumeOptionsParser")
    elif resume_contract.or_groups != [frozenset({"--message", "--message-file"})]:
        failures.append(f"arm 5 FAILED: OR-group not parsed correctly: {resume_contract.or_groups}")
    else:
        parsers = {"resume": resume_contract}
        one_alt_doc = parse_doc(
            "`baton resume <room-dir> --worker <role> --message <text> --bindings <file>`")
        problems = find_drift(parsers, {"resume"}, one_alt_doc)
        if problems:
            failures.append(f"arm 5 FAILED: satisfying one OR-alternative still fired: {problems}")

        neither_alt_doc = parse_doc("`baton resume <room-dir> --worker <role> --bindings <file>`")
        problems = find_drift(parsers, {"resume"}, neither_alt_doc)
        if not any("--message" in p for p in problems):
            failures.append("arm 5 FAILED: satisfying neither OR-alternative did not fire")

    # Arm 6: sanity floor trips on an extraction that yields nothing.
    empty_contract = parse_parser_file("namespace Baton.Cli;\npublic static class EmptyOptionsParser {}\n",
                                        "src/Baton.Cli/EmptyOptionsParser.cs")
    if empty_contract is not None:
        failures.append("arm 6 FAILED: a parser file with no Usage constant was not treated as an "
                         "extraction failure")

    # Arm 7: verbs the doc never demonstrates with a flag are out of scope for direction 2.
    if run_contract is not None:
        bare_mention_doc = parse_doc("see `baton run` for details")
        problems = find_drift({"run": run_contract}, {"run"}, bare_mention_doc)
        if problems:
            failures.append(f"arm 7 FAILED: a bare mention with no flags was treated as a covered "
                             f"invocation: {problems}")

    if failures:
        print(" !! clitripwire selftest FAILED:")
        for f in failures:
            print(f"  {f}")
        return 1
    print("clitripwire: selftest OK")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
