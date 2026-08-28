"""Prove every assertion in `selfcheck.py` discriminates, by breaking the thing it checks.

    pixi run audit-controls

A green checker means nothing on its own. Four of `selfcheck.py`'s assertions were once satisfied by
construction rather than by comparison -- one of them re-asserted a filter that had already selected
the population, and one could not fail for any input of any kind. All four printed OK. A reviewer
found them by reading; nothing in the repo could have.

So: for each registered check, inject the fault it exists to catch and require it to go RED. A check
with no control is itself a failure here, which is what stops this file from quietly falling behind
`selfcheck.py` as assertions are added.

WHY THIS IS A SEPARATE FILE, AND WHY IT IS IN THE REPO
The arms that verified the previous two rounds of fixes lived in a scratch directory, were reported
in a commit message as verification, and were preserved nowhere. That is exactly the failure
`dispatch.py`'s own header exists to stop -- "established once, in a temp directory, then thrown away
with the session" -- reproduced while fixing the file written to prevent it. The consequence was
concrete rather than theoretical: the fix for the over-strict permission guard was protected only by
a throwaway script, so the defect could be restored and `audit-selfcheck` stayed green.

It is separate from `selfcheck.py` because the two answer different questions. `selfcheck.py` asks
"is the tooling correct?" and belongs in CI's `audit` job on every PR. This asks "would we know if it
were not?" -- it mutates copies, spawns subprocesses, and is slower.

NOTHING HERE MUTATES A TRACKED FILE. Faults are injected in-process, or into a copy of the tree in a
temp directory. A control that edited `dispatch.py` in place would, if interrupted, leave behind
precisely the fault it was injecting -- a change that makes a checker pass.
"""
from __future__ import annotations

import contextlib
import io
import os
import re
import shutil
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.dont_write_bytecode = True
sys.path.insert(0, str(Path(__file__).resolve().parent))
import selfcheck  # noqa: E402

CONTROLS: dict[str, list] = {}
FAILURES: list[str] = []


def control(check_name: str, describe: str):
    """Register a fault for a named check. The decorated function is a context manager body."""
    def deco(fn):
        CONTROLS.setdefault(check_name, []).append((describe, contextlib.contextmanager(fn)))
        return fn
    return deco


@contextlib.contextmanager
def swap(obj, attr, value):
    """Temporarily replace an attribute, restoring it even if the check raises."""
    missing = object()
    prior = getattr(obj, attr, missing)
    setattr(obj, attr, value)
    try:
        yield
    finally:
        if prior is missing:
            delattr(obj, attr)
        else:
            setattr(obj, attr, prior)


@contextlib.contextmanager
def env_override(name: str, value: str):
    """Temporarily set an env var, restoring (or deleting) it even if the check raises."""
    missing = object()
    prior = os.environ.get(name, missing)
    os.environ[name] = value
    try:
        yield
    finally:
        if prior is missing:
            os.environ.pop(name, None)
        else:
            os.environ[name] = prior


@contextlib.contextmanager
def mutated_tree(relative: str, edit):
    """A copy of the repo's tools/ at the same depth, with one file edited. Yields the new path.

    Same depth because `dispatch.py` derives its repo root from `__file__.parents[2]` -- a copy at
    the wrong depth silently reads a different tree, which is the exact harness fault that once made
    three control arms fail identically for a reason unrelated to the injected fault.
    """
    with tempfile.TemporaryDirectory() as tmp:
        dest_root = Path(tmp) / "repo"
        shutil.copytree(ROOT / "tools", dest_root / "tools")
        shutil.copy2(ROOT / "CLAUDE.md", dest_root / "CLAUDE.md")
        target = dest_root / relative
        original = target.read_text(encoding="utf-8")
        edited = edit(original)
        assert edited != original, f"the edit to {relative} did not apply -- this arm measures nothing"
        target.write_text(edited, encoding="utf-8")
        yield target


# ---------------------------------------------------------------------------------------------
# The faults. Each is the defect its check was written for, or one the check must be able to see.
# ---------------------------------------------------------------------------------------------

# Assembled rather than written as a literal, and this is not cosmetic: step 9 of
# `audit-completeness` walks tools/ TEXTUALLY for anything in a `"model":` position, so a bad pin
# spelled out here would be read as a real pin in a real file and fail that step. The fixture would
# break the checker it is meant to exercise. Verified: with the literal in place, step 9 went red.
BAD_PIN = "gemini-3.1-" + "pro"  # a real prefix, not an accepted value -- the #547 near-miss
PLANTED_COUNT = 42               # deliberately not the number of steps in completeness.py


@control("every gemini template pins a model `agy models` lists",
         "a template pins a name the register does not list (#547's class)")
def _bad_pin():
    broken = {**selfcheck.dispatch.TEMPLATES,
              "control": {"adapter": "agy", "model": BAD_PIN, "write_files": True}}
    with swap(selfcheck.dispatch, "TEMPLATES", broken):
        yield


@control("workspace truth renders probe failures loudly, never as a clean tree",
         "git errors are discarded again (#780's shape), so a failed probe falls through to (none)")
def _truth_discards_errors():
    orig = selfcheck.dispatch._git_cmd

    def discards_err(workdir, *argv, **kwargs):
        value, _err = orig(workdir, *argv, **kwargs)
        return value, None

    with swap(selfcheck.dispatch, "_git_cmd", discards_err):
        yield


@control("workspace truth renders probe failures loudly, never as a clean tree",
         "a failed HEAD check returns before the status probe, so uncommitted work goes unreported")
def _truth_head_failure_skips_status():
    orig = selfcheck.dispatch._print_workspace_truth

    def early_return(workdir, head_before, head_before_err=None):
        if head_before_err or head_before is None:
            print(f"\n[dispatch.py] workspace truth ({workdir}):", file=sys.stderr)
            print("  truth unavailable: initial HEAD check failed", file=sys.stderr)
            return False
        return orig(workdir, head_before, head_before_err)

    with swap(selfcheck.dispatch, "_print_workspace_truth", early_return):
        yield


@control("no template is refused, and every grant the shell would over-reach is",
         "the coherence rule is dropped, so a withheld write dispatches whenever the shell is granted")
def _no_coherence_rule():
    # The pre-#529 guard, verbatim. Every template is coherent, so the loop over templates cannot see
    # this -- only the explicit refusal arms can.
    def pre_529(grant):
        if grant["run_shell_commands"] and not grant["network_access"]:
            return "shell without network"
        if not grant["write_files"] and not grant["run_shell_commands"]:
            return "nothing here can write the output"
        return None
    with swap(selfcheck.dispatch, "grant_refusal", pre_529):
        yield


@control("no template is refused, and every grant the shell would over-reach is",
         "the coherence rule keeps writes but drops reads, which no template would notice")
def _coherence_rule_forgets_reads():
    # Reads are the arm with nothing behind them: every template either grants read or withholds the
    # shell. Written as its own arm because the check must fail on the CATEGORY being dropped, not
    # only on the rule vanishing.
    def writes_only(grant):
        if grant["run_shell_commands"] and not grant["network_access"]:
            return "shell without network"
        if grant["run_shell_commands"] and not grant["write_files"]:
            return "reaches both anyway"
        if not grant["write_files"] and not grant["run_shell_commands"]:
            return "nothing here can write the output"
        return None
    with swap(selfcheck.dispatch, "grant_refusal", writes_only):
        yield


@control("no template is refused, and every grant the shell would over-reach is",
         "the network condition is deleted, which no template and no other arm would notice")
def _no_network_arm():
    # Walked before it was written: with only the read/write coherence rule and the unsatisfiable
    # rule left, every template stays coherent, the write arms still fire on write_files, and the
    # read arm still fires on read_files. Nothing reddens -- so `--run-shell-commands` without
    # `--network-access` would dispatch a bindings.json the engine refuses at bind time, which is
    # exactly the failure moving the rule to the caller was meant to prevent.
    def no_network(grant):
        if grant["run_shell_commands"] and not (grant["read_files"] and grant["write_files"]):
            return "reaches both anyway"
        if not grant["write_files"] and not grant["run_shell_commands"]:
            return "nothing here can write the output"
        return None
    with swap(selfcheck.dispatch, "grant_refusal", no_network):
        yield


@control("no template is refused, and every grant the shell would over-reach is",
         "the rule over-corrects and refuses every grant carrying a shell")
def _refuses_all_shell():
    # The opposite defect, and the one a refusal-only check cannot see without its control arm: this
    # makes `implement` -- the only template exercising the write path -- undispatchable.
    def any_shell(grant):
        return "reaches both anyway" if grant["run_shell_commands"] else None
    with swap(selfcheck.dispatch, "grant_refusal", any_shell):
        yield


@control("no template is refused, and every grant the shell would over-reach is",
         "the guard stops refusing anything at all")
def _no_guard():
    with swap(selfcheck.dispatch, "grant_refusal", lambda grant: None):
        yield


@control("every template dry-runs clean through the real command line",
         "--dry-run stops reporting, so a real dispatch would be indistinguishable")
def _dry_run_unmarked():
    # Removes only the marker line, leaving the early return in place: the check must not rely on
    # exit 0 plus a written bindings.json, both of which a REAL dispatch also produces.
    with mutated_tree(
        "tools/aer-agy-loop/dispatch.py",
        lambda s: s.replace('print("[dispatch.py] DRY RUN -- nothing was dispatched and nothing was spent.")',
                            'pass')
    ) as path:
        with swap(selfcheck, "DISPATCH_PY", path):
            yield


@control("every template dry-runs clean through the real command line",
         "precedence stops carrying the template into the generated bindings")
def _precedence_dropped():
    with mutated_tree(
        "tools/aer-agy-loop/dispatch.py",
        lambda s: s.replace("for key, value in resolve(TEMPLATES.get(args.template, {})).items():",
                            "for key, value in resolve({}).items():")
    ) as path:
        with swap(selfcheck, "DISPATCH_PY", path):
            yield


@control("--lane wires the review's branch diff to the janitor's output, and refuses a non-git tree",
         "the review step stops declaring the janitor's diff as an input, so it reviews HEAD again (#789)")
def _lane_review_input_dropped():
    # The exact #789 regression: drop the one line threading branch.diff into the review step and the
    # reviewer is back to auditing HEAD with no diff -- the state the whole issue is about. The check
    # must go red on review's empty Inputs/RequiredInputs.
    with mutated_tree(
        "tools/aer-agy-loop/dispatch.py",
        lambda s: s.replace('"inputs": [LANE_DIFF_OUTPUT_NAME],', '"inputs": [],')
    ) as path:
        with swap(selfcheck, "DISPATCH_PY", path):
            yield


@control("--lane wires the review's branch diff to the janitor's output, and refuses a non-git tree",
         "the head_before guard stops firing, so a lane in a non-git tree no longer refuses (#789 finding 4d)")
def _lane_head_guard_neutered():
    # Neuter only the guard, leaving everything else: a non-git --lane then builds a diff command
    # with no real base SHA and dry-runs clean instead of exiting 2. The check's non-git polarity arm
    # must catch that it stopped refusing.
    with mutated_tree(
        "tools/aer-agy-loop/dispatch.py",
        lambda s: s.replace("if head_before is None:", "if head_before is None and False:")
    ) as path:
        with swap(selfcheck, "DISPATCH_PY", path):
            yield


@control("every permission boolean can be turned OFF from the command line",
         "the --no- arm is declared FIRST, so argparse takes the default from it")
def _flag_order_swapped():
    real = selfcheck.dispatch.build_parser

    def swapped(argv=None):
        parser = real(argv)
        # Re-register write_files with the negative arm first, reproducing the argparse trap: a
        # dest's default comes from the FIRST action registered for it, so this collapses the
        # tri-state to False while both flag strings remain present in the source.
        for action in parser._actions:
            if action.dest == "write_files":
                action.default = False
        return parser
    with swap(selfcheck.dispatch, "build_parser", swapped):
        yield


@control("both shapes accept known pins, and PIN_SHAPE rejects English",
         "PIN_SHAPE becomes a regex that matches NOTHING")
def _pin_shape_matches_nothing():
    # Left every assertion in selfcheck.py green while step 9's tools/ walk stopped finding any pin,
    # because both loops asserting PIN_SHAPE were tautologies over a pre-filtered population.
    with swap(selfcheck.completeness, "PIN_SHAPE", re.compile(r"(?!)")):
        yield


@control("both shapes accept known pins, and PIN_SHAPE rejects English",
         "PIN_SHAPE loosens until it matches English words")
def _pin_shape_matches_english():
    with swap(selfcheck.completeness, "PIN_SHAPE", re.compile(r"[a-z][a-z0-9.]*(?:-[a-z0-9.]+)+")):
        yield


@control("step 9's probe-input exemption excuses a marked line and nothing else",
         "the exemption widens to every line, so step 9 stops guarding a stale pin entirely")
def _probe_exemption_swallows_everything():
    # The direction that matters. An exemption that quietly grew would leave step 9 printing OK over
    # a population it had stopped examining -- the failure `completeness.py` names throughout, here
    # in the newest thing added to it.
    with swap(selfcheck.completeness, "is_probe_input", lambda line: True):
        yield


@control("a PR body closes only the issues it declares, whatever the grammar around a keyword",
         "the lint stops flagging anything, so a negated keyword closes the issue again")
def _negated_close_blind():
    with swap(selfcheck.completeness, "negated_close_faults", lambda body: []):
        yield


@control("a PR body closes only the issues it declares, whatever the grammar around a keyword",
         "the lint flags every body, so `Closes #n` cannot be written")
def _negated_close_cries_wolf():
    # The direction that gets a lint turned OFF rather than the one that lets a fault through. This
    # repo's convention is `Closes #n` in every PR body, so a lint refusing that is unusable inside a
    # day -- which is why half the arms it guards are must-NOT-fire.
    with swap(selfcheck.completeness, "negated_close_faults", lambda body: [999]):
        yield


@control("a wrong repo name fails STEP 4 loudly; every other gh failure still skips",
         "the missing-repo case is treated as just another skip, so a wrong name checks nothing")
def _step4_missing_repo_skips_again():
    # The defect restored: every gh failure is a skip, which is the state that let a repointed
    # repo name silence STEP 4 while the rollup stayed green.
    with swap(selfcheck.completeness, "repo_is_unreachable", lambda stderr: False):
        yield


@control("a wrong repo name fails STEP 4 loudly; every other gh failure still skips",
         "every gh failure is called a missing repo, so being offline reds the whole gate")
def _step4_calls_every_failure_a_missing_repo():
    # The opposite direction, and the one that gets a check disabled rather than trusted: if
    # offline and unauthenticated runs fail the gate, the first developer without network deletes
    # the step.
    with swap(selfcheck.completeness, "repo_is_unreachable", lambda stderr: True):
        yield


@control("a declared close is refused while its target issue still carries unchecked scope boxes",
         "the box scan goes blind, so a declared close sails past an issue full of unbuilt scopes")
def _partial_closure_blind():
    with swap(selfcheck.completeness, "unchecked_scope_lines", lambda body: []):
        yield


@control("a declared close is refused while its target issue still carries unchecked scope boxes",
         "the declaration parse goes blind, so no close is ever a target and the whole lint is silently inert")
def _partial_closure_declarations_blind():
    # The inert direction the #975 review found uncontrolled: zero targets short-circuits the mode
    # to OK, so a regex regression here would never fire again without this arm going red.
    with swap(selfcheck.completeness, "declared_closure_targets", lambda body: []):
        yield


@control("a declared close is refused while its target issue still carries unchecked scope boxes",
         "the box scan cries wolf, so a fully-checked issue reds its own closing PR")
def _partial_closure_box_cries_wolf():
    with swap(selfcheck.completeness, "unchecked_scope_lines", lambda body: ["- [ ] phantom"]):
        yield


@control("a declared close is refused while its target issue still carries unchecked scope boxes",
         "every reference becomes a declared target, so strict targeting is gone and any referenced issue's boxes red the PR")
def _partial_closure_targets_everything():
    # The direction the operator constrained explicitly (2026-08-04): an umbrella issue that is
    # merely referenced must never trip the lint. If targeting widens to all references, every PR
    # that cites a multi-scope issue reds, and the lint is off within a week.
    def every_reference(body):
        import re
        return [int(n) for n in dict.fromkeys(re.findall(r"#(\d+)", body or ""))]

    with swap(selfcheck.completeness, "declared_closure_targets", every_reference):
        yield


@control("--pr-body refuses a path argument instead of passing over the empty stdin it leaves",
         "the stray-argument refusal is removed, so a path argument reads empty stdin and passes")
def _pr_body_takes_a_path_again():
    # The #860 defect restored exactly: read stdin regardless of what argv carried, so
    # `--pr-body some/body.md` prints OK over a body it never opened. What that cost is recorded
    # once, in `completeness.pr_body_mode`.
    def reads_stdin_whatever_the_argv() -> int:
        faults = selfcheck.completeness.negated_close_faults(sys.stdin.read())
        print("OK" if not faults else "!!")
        return 1 if faults else 0

    with swap(selfcheck.completeness, "pr_body_mode", reads_stdin_whatever_the_argv):
        yield


@control("--pr-body refuses a path argument instead of passing over the empty stdin it leaves",
         "an empty piped body is refused too, so a usage fault and a real pass stop being distinct")
def _pr_body_refuses_everything():
    # The opposite direction, and the one that makes the guard unusable rather than blind: if an
    # empty body fails as loudly as a misuse, CI reds on a legitimately empty PR body and the
    # distinction the arm asserts -- usage fault loud, empty body a real pass -- is gone.
    def refuses_everything() -> int:
        print("!! nothing was checked")
        return 1

    with swap(selfcheck.completeness, "pr_body_mode", refuses_everything):
        yield


@control("the gate-citation lint separates a slug from an ordinal",
         "the lint stops flagging anything (a numeric citation walks past it)")
def _gate_lint_blind():
    with swap(selfcheck.completeness, "gate_citation_faults", lambda files, slugs: []):
        yield


@control("the gate-citation lint separates a slug from an ordinal",
         "the lint flags everything (correct slug citations become faults)")
def _gate_lint_cries_wolf():
    # The direction that gets a lint DELETED rather than the one that lets a fault through, and the
    # one it actually shipped with: a blanket re.I made `[a-z]` match capitals, so prose about a
    # validity gate named in CamelCase was reported as citing a gate that does not exist.
    with swap(selfcheck.completeness, "gate_citation_faults",
              lambda files, slugs: [("planted.md", 1, "everything is a fault", "x", "x")]):
        yield


@control("step 9 fails CLOSED when either of its two file sources goes unreadable",
         "step 9 returns True regardless of what it can read")
def _step9_always_true():
    with swap(selfcheck.completeness, "step9_pinned_models_exist", lambda: True):
        yield


@control("no tooling file transcribes a count its own code computes",
         "a file in the population claims a step count that is wrong")
def _planted_wrong_count():
    with tempfile.TemporaryDirectory() as tmp:
        planted = Path(tmp) / "planted.py"
        # Interpolated for the same reason BAD_PIN is assembled: this file sits IN the lint's own
        # population, so a fixture spelled out as a literal is read as a real claim in a real file.
        # It was, and the lint fired on controls.py itself. Every fixture here has to be invisible to
        # the checker it feeds.
        planted.write_text(f'"""This runs all {PLANTED_COUNT} steps of the audit."""\n',
                           encoding="utf-8")
        with swap(selfcheck, "LINT_DIRS", (*selfcheck.LINT_DIRS, Path(tmp))):
            yield


@control("no tooling file transcribes a count its own code computes",
         "is_citation misclassifies, so the live quoted counts read as claims")
def _citation_always_false():
    # The lint finds no unquoted transcription today, so its only live exercise is the quoted
    # citations it skips. This is what proves that exercise is real: if `is_citation` stopped
    # recognising them they would each be read as a claim and fail their assert. Without this arm,
    # "nothing was compared" and "the comparison works" look identical from the output.
    with swap(selfcheck, "is_citation", lambda src, m: False):
        yield


@control("the two reusable instruments work on themselves",
         "code_tokens stops ignoring comments")
def _code_tokens_keeps_comments():
    with swap(selfcheck, "code_tokens", lambda t: [(0, t)]):
        yield


# The check under these two loads `recordonce.py` itself, so the fault has to be injected into what
# `load` hands back rather than onto a module attribute.
def _loading_recordonce_as(mutate):
    real = selfcheck.load

    def patched(path, name):
        mod = real(path, name)
        if path.name == "recordonce.py":
            mutate(mod)
        return mod
    return swap(selfcheck, "load", patched)


def replacing(mod, name, value):
    """setattr, but refuse to invent an attribute that is not already there.

    A mutation is only a mutation if something reads what it replaced. Renaming `prose_words` to
    `prose_runs` left `setattr(mod, "prose_words", ...)` quietly defining a function nobody calls, so
    the arm below ran an UNMUTATED checker and reported the green as evidence the check discriminates.
    `audit-controls` caught it, one layer up, which is the only reason it is not still there.

    Bare `setattr` cannot tell a rename from a working control, and neither can a reader. This can.
    """
    assert hasattr(mod, name), (
        f"control tried to replace {mod.__name__}.{name}, which does not exist -- renamed? "
        "A mutation of an attribute nothing reads is not a control.")
    setattr(mod, name, value)


BUDGET = "every dispatch tells the worker the budget it is actually given"


@control(BUDGET, "the preamble is dropped, so every worker is timed without being told")
def _dispatch_says_nothing():
    with swap(selfcheck.dispatch, "budget_preamble", lambda minutes, output: ""):
        yield


@control(BUDGET, "the preamble names a fixed number instead of the budget the binding carries")
def _dispatch_states_the_wrong_budget():
    # The direction a "does it mention minutes?" test cannot see. Every template but one would still
    # read correctly, and the worker that is misinformed is the one with the longest run to lose.
    with swap(selfcheck.dispatch, "budget_preamble",
              lambda minutes, output: f"BUDGET: you have 25 minutes. Write {output} early.\n\n"):
        yield


NEVER_KILL = "a shell grant carries the never-kill rule, and a read-only brief does not"


@control(NEVER_KILL, "the rule is dropped, so a shell-granted worker is briefed like a read-only one")
def _shell_rules_say_nothing():
    with swap(selfcheck.dispatch, "shell_rules_preamble", lambda run_shell_commands: ""):
        yield


@control(NEVER_KILL, "the rule leaks into every brief, shell-granted or not")
def _shell_rules_leak_everywhere():
    # The polarity a "does the string appear?" test cannot see on its own: a preamble attached
    # unconditionally still satisfies the shell-granted half while briefing read-only workers
    # about a capability they do not have.
    with swap(selfcheck.dispatch, "shell_rules_preamble",
              lambda run_shell_commands: "SHELL RULES: never kill anything.\n\n"):
        yield


RECORDONCE = "the record-once checker fires on restated prose, not on text the register prescribes"
RECORDONCE_PIN = "the record-once checker still finds the passages it found in a real merge"


@control(RECORDONCE, "the checker stops finding anything, so every restatement ships green")
def _recordonce_blind():
    with _loading_recordonce_as(lambda m: replacing(m, "violations", lambda by_file: [])):
        yield


@control(RECORDONCE, "the checker reads code as prose, so ordinary duplicated test setup is flagged")
def _recordonce_reads_code():
    # The false-positive direction, and the one a fires-on-restatement check cannot see alone: a
    # checker that flags every shared `using var stderr = new StringWriter();` blocks real work
    # while looking exactly as healthy as one that works.
    #
    # Reads every line as prose while leaving contiguity intact -- one run per hunk, as the real
    # thing produces. Injecting both faults at once would let a contiguity regression masquerade as
    # this one, and this arm is named for exactly one of them.
    def read_everything(mod):
        replacing(mod, "prose_runs",
                  lambda path, hunks: [[w for line in hunk for w in mod.normalise(line)]
                                       for hunk in hunks])
    with _loading_recordonce_as(read_everything):
        yield


@control(RECORDONCE, "comment context is lost, so docstrings and block bodies go invisible again")
def _recordonce_reads_leaders_only():
    # The pre-#675 reader, verbatim: a leader match per line, with no notion of what a line is inside.
    # Distinct from the arm above, which is the false-positive direction -- this is the one that
    # silently NARROWS the population, and narrowing is the failure that ships green. Repinning
    # PROVEN_GROUPS on #675 was caused by one docstring this cannot see.
    leader = re.compile(r"^\s*(///|//|/\*|\*|#|--|<!--)")

    def leaders_only(mod):
        replacing(mod, "comment_text",
                  lambda lines, openers, blocks:
                      (line if leader.match(line) else None for line in lines))
    with _loading_recordonce_as(leaders_only):
        yield


@control(RECORDONCE, "one marker mutes a whole file again, so unrelated restatement in it ships green")
def _recordonce_marker_mutes_file():
    # #676's primary defect, restored. The dangerous direction of a hatch is that it is too WIDE:
    # a marker placed for one deliberate second copy stopped every other passage the change added to
    # that file from being compared, and nothing said so.
    def file_granular(mod):
        real = mod.exemptions

        def whole_file(path, at):
            shingles, notes, bad = real(path, at)
            if not notes:
                return shingles, notes, bad
            every = set()
            for words in mod.prose_runs(path, [at(path) or []]):
                for i in range(len(words) - mod.SHINGLE + 1):
                    every.add(tuple(words[i:i + mod.SHINGLE]))
            return every, notes, bad
        replacing(mod, "exemptions", whole_file)
    with _loading_recordonce_as(file_granular):
        yield


@control(RECORDONCE, "the marker is matched on raw lines, so a code literal silences the checker")
def _recordonce_marker_ignores_context():
    # The other half of #676: with no context test, the marker's characters anywhere in a tracked
    # file exempted that file. See `marked_runs` in recordonce.py for what that cost.
    #
    # Carries the pre-anchor pattern rather than borrowing `mod.SUPPRESS`, because the anchor added
    # later would refuse `marker = "// record-once-ok: ..."` before the missing context test ever
    # mattered, and the arm would go green while naming a defect it had stopped modelling. Two
    # independent defences now stand between a code literal and an exemption; this one is named for
    # the context test, so it holds the other constant.
    unanchored = re.compile(r"record-once-ok:\s*#(\d{3,})\s+(?:canonical\s+is\s+)?(\S+)")

    def raw_lines(mod):
        real = mod.marked_runs

        def marks_anything(path, hunks):
            runs = real(path, hunks)
            found = next((m for hunk in hunks for line in hunk
                          if (m := unanchored.search(line)) is not None), None)
            if found is None:
                return runs
            return [(words, (found.group(1), found.group(2))) for words, _ in runs]
        replacing(mod, "marked_runs", marks_anything)
    with _loading_recordonce_as(raw_lines):
        yield


@control(RECORDONCE, "the marker matches mid-sentence, so prose explaining it becomes a decision")
def _recordonce_marker_unanchored():
    # Not hypothetical: this checker's own docstring, describing what the marker looks like, was read
    # as one naming a file that does not exist -- and the run printed that a passage had been
    # exempted while comparing it. Documentation about a marker is the text certain to contain it.
    unanchored = re.compile(r"record-once-ok:\s*#(\d{3,})\s+(?:canonical\s+is\s+)?(\S+)")
    with _loading_recordonce_as(lambda m: replacing(m, "SUPPRESS", unanchored)):
        yield


@control(RECORDONCE, "a mistyped marker exempts nothing and says nothing, as it used to")
def _recordonce_malformed_marker_is_silent():
    # The `setattr` shape one commit earlier, in the hatch instead of the harness: the author reads a
    # green gate as their exemption being recorded, the gate reads no marker at all, and the two
    # states are indistinguishable from either side. A never-matching pattern restores exactly that.
    with _loading_recordonce_as(lambda m: replacing(m, "SUPPRESS_LOOSE", re.compile(r"(?!)"))):
        yield


@control(RECORDONCE, "an extensionless file reads as nothing, so its comments go uncompared")
def _recordonce_drops_extensionless():
    # Restores the narrowing this change was measured to have shipped, by emptying the fallback.
    # `NO_EXTENSION` in recordonce.py is where the measurement lives.
    with _loading_recordonce_as(lambda m: replacing(m, "NO_EXTENSION", ())):
        yield


@control(RECORDONCE, "git output is decoded with a codec that cannot represent it")
def _recordonce_hostile_codec_decodes_git():
    # A PROXY for the shipped defect, not the shipped defect itself, and the difference is the whole
    # reason this arm is written this way.
    #
    # What shipped was `text=True` with no `encoding`, which decodes with the LOCALE codec. Emptying
    # GIT_TEXT restores that call exactly -- and on a UTF-8 host it injects nothing at all, because
    # the locale codec then decodes the bytes fine. CI runs `audit-controls` on ubuntu-latest, where
    # that is the case, so the faithful injection is a no-op there and this arm would report STAYED
    # GREEN: a control failing for a reason with nothing to do with the defect it names.
    #
    # Pinning cp1252 models the general fault the shipped one was an instance of -- git output
    # decoded by a codec that cannot represent it -- and does so identically on every platform. The
    # surfaced SHAPE still differs by platform (see GIT_TEXT in recordonce.py); this arm only needs
    # the checker to go red, and both shapes do.
    #
    # It buys that at a cost worth naming: discrimination now depends on the target file holding a
    # byte cp1252 REJECTS, which is a narrower property than "not ASCII". The selfcheck arm's guard
    # is what holds that, so that guard is a precondition of this arm rather than an independent
    # check -- if it ever weakens to "some non-ASCII character", this arm can pass while testing
    # nothing.
    with _loading_recordonce_as(lambda m: replacing(m, "GIT_TEXT", {"encoding": "cp1252"})):
        yield


@control(RECORDONCE, "an index row counts as prose, so adding a decision record fails CI")
def _recordonce_reads_index_rows():
    # Why a row is excluded at all is recorded beside `TABLE_ROW` in recordonce.py.
    with _loading_recordonce_as(lambda m: replacing(m, "TABLE_ROW", re.compile(r"(?!)"))):
        yield


@control(RECORDONCE_PIN, "the pin is emptied, so the checker can stop finding anything and stay green")
def _recordonce_pin_is_vacuous():
    with _loading_recordonce_as(lambda m: replacing(m, "PROVEN_GROUPS", ())):
        yield


PERMISSIONRANK = "the permissionrank checker flags permissive-primary controls paired with un-primaried deny controls"


@control(PERMISSIONRANK, "the checker stops finding permissive-primary pairs, so planted violations ship green")
def _permissionrank_checker_blind():
    with swap(selfcheck.permissionrank, "PERMISSIVE_RE", re.compile(r"(?!)")):
        yield


@control(PERMISSIONRANK, "the checker flags every button pair, so a legitimate equal-weight gate cannot ship")
def _permissionrank_cries_wolf():
    # #1124 review finding D: the over-broad direction. A primary marker matching ANY control
    # makes the selfcheck's fixed equal-weight fixtures (and the real, fixed tree) flag — the
    # unusable-inside-a-day failure the blind arm alone cannot catch.
    with swap(selfcheck.permissionrank, "AXAML_ACCENT_RE", re.compile(r"<Button")):
        yield



def main() -> int:
    print(__doc__.strip().splitlines()[0])
    print("=" * 78)

    names = [n for n, _ in selfcheck.CHECKS]
    checks = dict(selfcheck.CHECKS)

    # A check with no control is a failure. Otherwise this file silently falls behind selfcheck.py,
    # which is the same "population that quietly shrinks" defect it exists to catch.
    uncontrolled = [n for n in names if n not in CONTROLS]
    orphans = [n for n in CONTROLS if n not in checks]

    total = 0
    for name in names:
        arms = CONTROLS.get(name, [])
        if not arms:
            continue
        print(f"\n{name}")
        # GREEN BASELINE FIRST, per arm. Without it a check that fails for an unrelated reason reads
        # as a discriminating control -- three arms were once reported as passes while every one of
        # them was failing because the harness could not read the tree at all.
        try:
            checks[name]()
        except Exception as e:  # noqa: BLE001
            FAILURES.append(f"{name}: baseline NOT green ({type(e).__name__}: {e})")
            print(f"   !! baseline is not green, so no arm below can mean anything: {e}")
            continue

        for describe, fault in arms:
            total += 1
            try:
                with fault():
                    checks[name]()
            except Exception:  # noqa: BLE001 -- any raise means the check noticed
                print(f"   OK  red under: {describe}")
            else:
                FAILURES.append(f"{name}: STAYED GREEN under {describe}")
                print(f"   !!  STAYED GREEN under: {describe}")
                print("       the check does not discriminate against the defect it names")

    print("\n" + "=" * 78)
    for name in uncontrolled:
        FAILURES.append(f"{name}: no control")
        print(f" !! no control registered for: {name}")
    for name in orphans:
        FAILURES.append(f"control for a check that no longer exists: {name}")
        print(f" !! control registered for a check that does not exist: {name}")

    if FAILURES:
        print(f"\n{len(FAILURES)} problem(s) across {total} arms over {len(names)} checks.")
        return 1
    print(f"\nAll {total} control arms discriminate, across {len(names)} checks.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())


