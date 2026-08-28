"""Assert the tooling's own enumerable surfaces, because the checkers had no checker.

Most assertions here map to a defect that actually shipped into a draft of #627 and was caught by a
reviewer or by hand; `_instruments_self_test` is the exception -- it guards the two helpers below
rather than a shipped defect. The surfaces are enumerable -- templates x settings, booleans x flag
directions, a regex x input classes -- which is the criterion CLAUDE.md gate `record-once` names for
when something earns a checker. That criterion had been applied to docs/decisions/ and vendor-verify
and never to the tooling being written.

Runs in CI's `audit` job alongside `completeness.py`. Plain asserts, no test framework: this repo's
python tooling has none, and adding one for a handful of assertions is the ceremony the gates exist
to cut.

    pixi run audit-selfcheck

Each assertion reports the population it examined, and `main()` fails any check that does not --
because "OK" over an empty population is the failure mode this file exists to catch, and a check
that quietly stopped comparing looks exactly like one that compared and agreed. A LINT with nothing
to flag is still a pass; it just has to say what it searched.

WHAT THIS CANNOT CHECK
  * That a check's population is the RIGHT population. It asserts the join holds, never that the
    join is the one worth making.
  * Anything about prose. The defect class that dominated #627 -- a comment asserting what the code
    does not do -- is not reachable from here, and #631 is the other half of that answer.
"""
from __future__ import annotations

import ast
import contextlib
import importlib.util
import io
import json
import os
import re
import subprocess
import sys
import tempfile
import tokenize
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
CHECKS: list[tuple[str, object]] = []
FAILURES: list[str] = []


def check(name):
    """Register a named assertion. Registration only -- main() runs them, so the header prints first.

    A returned string is the population the assertion examined, printed alongside the OK. An
    assertion that examined nothing says so.
    """
    def deco(fn):
        CHECKS.append((name, fn))
        return fn
    return deco


def load(path: Path, name: str):
    """Import a tool by path without leaving a __pycache__ behind."""
    spec = importlib.util.spec_from_file_location(name, path)
    mod = importlib.util.module_from_spec(spec)
    prior, sys.dont_write_bytecode = sys.dont_write_bytecode, True
    try:
        spec.loader.exec_module(mod)
    finally:
        sys.dont_write_bytecode = prior
    return mod


# Module-level so `controls.py` can point a check at a MUTATED COPY in a temp tree. A control that
# edited these tracked files in place would leave the repo broken if it were interrupted, and the
# faults being injected are deliberately the kind that make a checker pass -- the worst kind to
# leave behind.
DISPATCH_PY = ROOT / "tools" / "aer-agy-loop" / "dispatch.py"
LINT_DIRS = (ROOT / "tools" / "audit-completeness", ROOT / "tools" / "aer-agy-loop")

dispatch = load(DISPATCH_PY, "_selfcheck_dispatch")
completeness = load(ROOT / "tools" / "audit-completeness" / "completeness.py", "_selfcheck_audit")
vocabulary = load(ROOT / "tools" / "audit-completeness" / "vocabulary.py", "_selfcheck_vocabulary")
permissionrank = load(ROOT / "tools" / "audit-completeness" / "permissionrank.py", "_selfcheck_permissionrank")



def register_models() -> set[str]:
    """The agy catalogue, from completeness.py's own parser rather than a second copy of it.

    A duplicated parse here would drift against the one step 9 actually uses, which is the failure
    the whole file is about.
    """
    accepted, why = completeness.register_models()
    assert accepted is not None, f"the register cannot be parsed, so nothing below can join to it: {why}"
    return accepted


def resolved_templates() -> dict[str, dict]:
    """Every template as `--template X` actually resolves it -- dispatch's defaults filled in."""
    return {name: dispatch.resolve(tpl) for name, tpl in dispatch.TEMPLATES.items()}


# ---------------------------------------------------------------------------------------------
# Two reusable instruments
# ---------------------------------------------------------------------------------------------

def code_tokens(text: str):
    """The file's code, ignoring its prose: every token except comments, docstrings, and whitespace.

    WHAT IT CANNOT SEE, stated because the obvious reading of the line above is wrong: NEWLINE,
    INDENT and DEDENT all strip to empty and are dropped with the rest of the whitespace, so BLOCK
    STRUCTURE is invisible. `if x:\\n    y = 1\\nz = 2` and `if x:\\n    y = 1\\n    z = 2` produce
    identical token lists. Moving a statement into or out of a conditional, a loop or a `try` is
    exactly the edit someone would want a "prose-only?" instrument to catch, and this one does not.
    The self-test below carries that case as a known-failing polarity rather than leaving it implied.

    Turns "this commit only touches comments" from a characterisation into an assertion. It was
    written after a commit was described as prose-only while it had changed two user-visible string
    literals; running this is what caught that.

    Docstrings are located by POSITION, via ast, not by quote style. A `'''...'''` string used as a
    real value is code and stays; a triple-quoted string in a docstring slot is prose and goes. The
    earlier quote-style test had both backwards, and on Python 3.12+ (PEP 701) also missed
    f-string-quoted prose entirely, since an f-string no longer tokenizes as STRING.

    An instrument for a caller to point at two revisions of a file. It has no standing population, so
    nothing here asserts anything about this repo's own files with it -- only that it works.
    """
    docstrings = set()
    for node in ast.walk(ast.parse(text)):
        if isinstance(node, (ast.Module, ast.FunctionDef, ast.AsyncFunctionDef, ast.ClassDef)):
            body = getattr(node, "body", None)
            if not body:
                continue
            first = body[0]
            if isinstance(first, ast.Expr) and isinstance(first.value, ast.Constant) \
                    and isinstance(first.value.value, str):
                docstrings.add((first.lineno, first.col_offset))

    out = []
    for tok in tokenize.generate_tokens(io.StringIO(text).readline):
        if tok.type in (tokenize.COMMENT, tokenize.NL):
            continue
        if tok.type == tokenize.STRING and tok.start in docstrings:
            continue
        if tok.string.strip():
            out.append((tok.type, tok.string))
    return out


def control_arm(baseline, mutate, restore, describe=""):
    """Run a discriminating control, refusing to report unless the baseline is green FIRST.

    `baseline()` must return True for an unmutated tree. If it does not, the harness is broken and
    the mutated result means nothing -- so this raises instead of reporting a pass. That is the
    failure it exists to prevent: a control run was once reported as three passes while the
    comparison copy was reading an empty string for every file, so every arm failed identically for
    a reason that had nothing to do with the injected fault.

    Returns the mutated-tree result, having proved the baseline was green. Note what that does and
    does not buy: it rules out a baseline that fails for every input, but a `baseline` that ignores
    the mutation entirely still returns True twice and reports a NON-discriminating pass. Only the
    caller's assertion on the returned value catches that, which is why every caller asserts False.
    """
    assert baseline() is True, (
        f"control baseline is NOT green{' for ' + describe if describe else ''}, so nothing measured "
        "after this would mean anything. Two causes, and they need different fixes: either the "
        "harness is broken (the failure this exists to catch), or the tree ALREADY fails the check "
        "being controlled -- in which case fix that first and this arm will speak again. Check the "
        "other assertions: if one of them is also red, it is the second case."
    )
    mutate()
    try:
        return baseline()
    finally:
        restore()


def is_citation(src, m):
    """True if the count sits inside a double-quoted span on its own line.

    A quoted count is reporting what some OTHER text said; an unquoted one is this file making a
    claim. The distinction is not decoration: this check's own comment recording why it exists
    quotes both historical wrong values, and the first version failed on that sentence. A check
    that cries wolf about the note explaining it gets deleted.

    Two stated costs. A genuine transcription written inside double quotes is skipped. And only
    `"` is paired -- prose apostrophes make `'` unpairable -- so a single-quoted citation still
    reads as a claim.

    Triple-quote delimiters are blanked before pairing, and that is load-bearing rather than tidy: a
    ONE-LINE docstring wraps its own contents in `"` characters, so a wrong count inside one paired
    as a quoted span and was skipped as a citation. `controls.py` caught it on its first run, with a
    planted count the lint reported as clean.

    Writing that example out with a real number here made this very file trip the lint -- the third
    fixture in a row to do so. Anything illustrating a count must not BE one.
    """
    line_start = src.rfind("\n", 0, m.start()) + 1
    line_end = src.find("\n", m.end())
    line = src[line_start:line_end if line_end != -1 else len(src)]
    # Blanked, not stripped, so every offset below still lines up with the real line.
    line = re.sub(r'"{3}', "   ", line)
    quotes = [i for i, c in enumerate(line) if c == '"']
    rel = m.start() - line_start
    return any(a < rel < b for a, b in zip(quotes[0::2], quotes[1::2]))


@check("every dispatch tells the worker the budget it is actually given")
def _dispatch_states_its_budget():
    """A worker that does not know it is being timed spends the budget as if it were unbounded.

    Nothing else can catch a preamble that stops being attached: the run still succeeds, and the
    cost shows up only as a report that was two-thirds written when the process was killed. The
    minutes are compared against the `Timeout` the same call emits, so a preamble that names a
    number the binding does not carry fails here rather than misinforming the worker.
    """
    body = "-- the operator's own prompt --"
    assert dispatch.TEMPLATES, "TEMPLATES is empty -- this compared nothing"
    for name, tpl in resolved_templates().items():
        output = f"{name}-artifact.md"
        entry = dispatch.build_bindings(
            worker_name="w", prompt_text=body, output_name=output,
            adapter=tpl["adapter"], working_directory=ROOT,
            timeout_minutes=tpl["timeout_minutes"], model=tpl["model"], effort=tpl["effort"],
            read_files=tpl["read_files"], write_files=tpl["write_files"],
            run_shell_commands=tpl["run_shell_commands"], network_access=tpl["network_access"],
        )["w"]
        prompt = entry["PromptTemplate"]
        assert prompt.endswith(body), (
            f"TEMPLATES[{name!r}]: the operator's prompt no longer arrives intact")
        hours, minutes, _ = entry["Timeout"].split(":")
        assert f"{int(hours) * 60 + int(minutes)} minutes" in prompt, (
            f"TEMPLATES[{name!r}]: the prompt does not state the budget the binding carries "
            f"({entry['Timeout']}) -- a worker told the wrong number is worse than one told none")
        assert output in prompt, (
            f"TEMPLATES[{name!r}]: the prompt never names {output}, so 'write it early' names "
            "nothing the worker can act on")
    return f"{len(dispatch.TEMPLATES)} templates x (budget stated = budget bound, prompt intact)"


@check("a shell grant carries the never-kill rule, and a read-only brief does not")
def _shell_grant_carries_never_kill():
    """#717's prompt-borne defense -- dispatch.py's shell_rules_preamble carries the measured
    incident and why the gate cannot enforce this. What only this arm pins: the rule reaches
    every shell-granted brief and must NOT pollute read-only ones, where it would be noise about
    a capability the worker does not have.
    """
    both = {True: 0, False: 0}
    for name, tpl in resolved_templates().items():
        entry = dispatch.build_bindings(
            worker_name="w", prompt_text="-- prompt --", output_name="out.md",
            adapter=tpl["adapter"], working_directory=ROOT,
            timeout_minutes=tpl["timeout_minutes"], model=tpl["model"], effort=tpl["effort"],
            read_files=tpl["read_files"], write_files=tpl["write_files"],
            run_shell_commands=tpl["run_shell_commands"], network_access=tpl["network_access"],
        )["w"]
        carries = "never kill" in entry["PromptTemplate"]
        assert carries == tpl["run_shell_commands"], (
            f"TEMPLATES[{name!r}]: shell={tpl['run_shell_commands']} but the never-kill rule "
            f"{'is missing' if tpl['run_shell_commands'] else 'leaked into a read-only brief'}")
        both[tpl["run_shell_commands"]] += 1
    assert both[True] and both[False], (
        "the template population no longer exercises both polarities, so this arm proves one side only")
    return f"{both[True]} shell-granted carry it, {both[False]} read-only do not"


@check("every gemini template pins a model `agy models` lists")
def _pins_resolve():
    accepted = register_models()
    checked = []
    for name, tpl in resolved_templates().items():
        # Resolved, not raw: a template that omits `adapter` dispatches to gemini anyway, and reading
        # the raw dict would skip exactly that one.
        if tpl["adapter"] != "agy" or not tpl["model"]:
            continue
        checked.append(name)
        assert tpl["model"] in accepted, (
            f"TEMPLATES[{name!r}] pins {tpl['model']!r}, which `agy models` does not list. "
            f"A pin the CLI rejects fails AFTER the operator has paid for the run (#547). "
            f"Accepted: {sorted(accepted)}"
        )
    assert checked, (
        "no template resolves to a gemini adapter with a model pin, so this compared nothing. "
        "Either the templates changed shape or `resolve()` stopped defaulting the adapter."
    )
    return f"{len(checked)} of {len(dispatch.TEMPLATES)} templates pin an agy model"


@check("no template is refused, and every grant the shell would over-reach is")
def _templates_are_dispatchable():
    # Calls `grant_refusal` rather than restating its conditions. The restatement asserted
    # `write_files is True` for every template -- STRICTER than the product, which refuses only when
    # write AND shell are both withheld, and with a message that contradicted what #529 measured.
    assert dispatch.TEMPLATES, "TEMPLATES is empty -- this compared nothing"
    for name, tpl in resolved_templates().items():
        refusal = dispatch.grant_refusal(tpl)
        assert refusal is None, (
            f"TEMPLATES[{name!r}] resolves to a grant dispatch.py refuses before it can spend, so "
            f"the template cannot be used at all: {refusal}"
        )

    # The templates alone CANNOT catch what this check exists for: every one of them is coherent, so
    # the loop above stays green under any rule that only ever refuses. The refusals have to be
    # asserted directly, on the grants that separate one rule from the next.
    #
    # `write_files: False` is refused under every shell setting *on an adapter whose withheld writes
    # cannot reach the outbox* -- which since #649 is a per-adapter answer, not a universal one. The
    # arms below inherit `BUILT_IN["adapter"] == "agy"`, and that is load-bearing rather than
    # incidental: the same grants dispatch on claude, which the pair above asserts directly. So
    # `grant_refusal` no longer adds up to "writes are always required", and the three conditions
    # cannot be collapsed into one predicate even in principle. The conditions are kept apart for their messages, not their verdicts. Asserted
    # here so the sum is a fact that runs, and so collapsing them has to fail this instead of quietly
    # losing the read_files and network arms.
    granted = {**dispatch.BUILT_IN, "read_files": True, "write_files": True,
               "run_shell_commands": True, "network_access": True}

    # One arm per category the rule must keep refusing, because each is separately droppable. Read
    # and network have no template behind them at all -- every template either grants both or
    # withholds the shell -- so dropping either from the rule leaves the loop above green.
    refusal_arms = {
        "writes withheld, shell granted": {**granted, "write_files": False},
        # Same category, no shell: a different rule and a different message, so it needs its own arm.
        "writes withheld, no shell": {**granted, "write_files": False, "run_shell_commands": False},
        "reads withheld, shell granted": {**granted, "read_files": False},
        "network withheld, shell granted": {**granted, "network_access": False},
    }
    # #649: on an adapter whose withheld writes reach AER_OUTPUT_DIR the "nothing here can write the
    # output" arm is false, and refusing it refuses the read-only reviewer the whole feature exists
    # for. Asserted as a pair against the identical gemini grant, so the rule cannot be satisfied by
    # allowing that shape everywhere -- which is what would silently un-refuse gemini, where the
    # vendor still denies the write before AER's hook is reached.
    reviewer = {**granted, "write_files": False, "run_shell_commands": False}
    assert dispatch.grant_refusal({**reviewer, "adapter": "claude"}) is None, (
        "a read-only claude reviewer is refused. Its declared output lands in AER_OUTPUT_DIR, which "
        "a withheld write still reaches on that adapter (#649) -- refusing it forces every reviewing "
        "template to grant a workspace write it does not need."
    )
    assert dispatch.grant_refusal({**reviewer, "adapter": "agy"}) is not None, (
        "the same grant dispatches on agy, which cannot satisfy the contract -- see #901."
    )

    for label, arm in refusal_arms.items():
        assert dispatch.grant_refusal(arm) is not None, (
            f"a grant with {label} dispatches. With the shell it is #529's over-grant -- the "
            "operator withheld a category `cat`, redirection or `curl` reaches anyway, and AER "
            "refuses it at bind time; without the shell nothing can satisfy the ProducedOutputs "
            "contract and the run burns its full budget to fail (#629)."
        )

    # The discriminating control. Without it every arm above passes on a `grant_refusal` that refuses
    # everything, which would make the whole dispatch path dead and still print OK.
    assert dispatch.grant_refusal(granted) is None, (
        "the fully-granted shell grant is refused, so no template exercising the write path can "
        "dispatch at all. The coherence rule is meant to refuse grants that withhold a category the "
        "shell reaches -- not grants that carry a shell."
    )
    return (f"{len(dispatch.TEMPLATES)} templates + {len(refusal_arms)} refusal arms "
            "+ the coherent control + the outbox-capable/incapable pair")


@check("every template dry-runs clean through the real command line")
def _templates_dry_run():
    # The checks above import `grant_refusal` and `build_parser` separately. Neither covers their
    # COMPOSITION in main(): precedence applied, then guards, then the workflow/bindings build. This
    # goes through the actual command line, which is the only surface an operator uses.
    #
    # Free to run and needs no vendor -- that is what #639's --dry-run bought. Before it, the only
    # grants testable without spending were the ones the guards REFUSE, so the allow path could only
    # be checked by paying for a run, and checking it once cost exactly that.
    assert dispatch.TEMPLATES, "TEMPLATES is empty -- this compared nothing"
    with tempfile.TemporaryDirectory() as scratch:
        def dry_run(*extra):
            cmd = [sys.executable, str(DISPATCH_PY),
                   "--prompt-file", str(ROOT / "CLAUDE.md"), "--output-name", "out",
                   "--working-directory", str(ROOT), "--scratch-root", scratch,
                   "--dry-run", *extra]
            return subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8", errors="replace", cwd=ROOT)

        for name in sorted(dispatch.TEMPLATES):
            done = dry_run("--template", name)
            assert done.returncode == 0, (
                f"`--template {name} --dry-run` exits {done.returncode}, so the template cannot be "
                f"dispatched at all:\n{done.stderr.strip()[:400]}"
            )
            # Exit 0 plus a written bindings.json is ALSO true of a real, successful dispatch. Delete
            # --dry-run's early return and, on a machine with Aer.Cli.exe built, this check becomes
            # four live vendor runs -- two agy, one opus at xhigh -- and still prints OK. The marker
            # is the only thing that distinguishes "stopped" from "spent".
            assert "DRY RUN -- nothing was dispatched" in done.stdout, (
                f"`--template {name} --dry-run` exited 0 without printing the dry-run report, so it "
                "may have DISPATCHED. Cost is the operator's call; a checker must not be able to "
                "start spending on its own."
            )
            # #763: the announce block must name the templates' own ref -- a stale checkout
            # dispatching with the wrong dispatch.py is exactly the incident this line exists for,
            # and the lane review's finding 1 was that nothing asserted the line survives.
            assert "templates ref:" in (done.stderr + done.stdout), (
                f"`--template {name} --dry-run` printed no 'templates ref:' line (#763): a dispatch "
                "that does not announce its templates' provenance re-opens the stale-checkout leak."
            )
            payload = json.loads((Path(scratch) / "bindings.json").read_text(encoding="utf-8"))
            entry = payload["worker"]
            expected = dispatch.resolve(dispatch.TEMPLATES[name])
            # The bindings are what AER actually reads. A template's dict agreeing with itself proves
            # nothing about what reached the engine -- precedence runs in between. All eight resolved
            # keys, not just the permissions: a precedence bug that dropped the MODEL pin is #547's
            # failure class, and it would be invisible to the only check that reads the bindings.
            for key, field in (("read_files", "ReadFiles"), ("write_files", "WriteFiles"),
                               ("run_shell_commands", "RunShellCommands"),
                               ("network_access", "NetworkAccess")):
                assert entry["PermissionGrant"][field] == expected[key], (
                    f"template {name!r} declares {key}={expected[key]} but the generated bindings "
                    f"carry {field}={entry['PermissionGrant'][field]} -- precedence dropped it on "
                    "the way to the engine"
                )
            assert entry["Adapter"] == expected["adapter"], (
                f"template {name!r} declares adapter={expected['adapter']!r}; bindings carry "
                f"{entry['Adapter']!r}")
            # Model and Effort are OMITTED when unset rather than written null, so absence is the
            # assertion for a template that pins nothing -- see build_bindings.
            for key, field in (("model", "Model"), ("effort", "Effort")):
                if expected[key]:
                    assert entry.get(field) == expected[key], (
                        f"template {name!r} pins {key}={expected[key]!r}; bindings carry "
                        f"{entry.get(field)!r}")
                else:
                    assert field not in entry, (
                        f"template {name!r} sets no {key}, but bindings carry "
                        f"{field}={entry.get(field)!r} -- AER would pin something nobody chose")
            # Exercises the hour-split at the same time: "00:90:00" is malformed under .NET's
            # [-][d.]hh:mm:ss, and every timeout below 60 hides it.
            hours, minutes = divmod(expected["timeout_minutes"], 60)
            assert entry["Timeout"] == f"{hours:02d}:{minutes:02d}:00", (
                f"template {name!r} sets timeout_minutes={expected['timeout_minutes']} but bindings "
                f"carry Timeout={entry['Timeout']!r}")

        # Polarity, through the same surface: a grant nothing can satisfy must be REFUSED, not
        # dry-run clean. Asserted on the MESSAGE, not the exit code -- dispatch.py returns 2 from the
        # guard AND from the missing-CLI branch, and argparse exits 2 on any command-line error, so a
        # one-character typo in the flag below would make this arm measure nothing forever.
        refused = dry_run("--no-write-files", "--no-run-shell-commands")
        assert "nothing here can write the output" in refused.stderr, (
            f"a grant withholding both writes and the shell did not produce the guard's refusal "
            f"(exit {refused.returncode}). The guards do not fire through the command line, or the "
            f"arm's own flags no longer parse:\n{refused.stderr.strip()[:300]}"
        )

        # The other refusal, on its own message for the same reason. This one would otherwise reach
        # AER and be refused there instead, after the operator has committed to the flags (#529).
        incoherent = dry_run("--no-write-files", "--run-shell-commands", "--network-access")
        assert "reaches both anyway" in incoherent.stderr, (
            f"a grant withholding writes while granting the shell dry-ran clean (exit "
            f"{incoherent.returncode}). dispatch.py would build a bindings.json that "
            f"WorkerBindingResolver refuses:\n{incoherent.stderr.strip()[:300]}"
        )
    return (f"{len(dispatch.TEMPLATES)} templates x 8 resolved keys vs the generated bindings, "
            "+ 2 refusal polarities")


@check("--lane wires the review's branch diff to the janitor's output, and refuses a non-git tree")
def _lane_dry_run():
    """#789: the lane's read-only reviewer reads THE CHANGE (a `branch.diff` input artifact the
    janitor produces), not HEAD. That wiring lives entirely in dispatch.py's step-spec build --
    janitor `Outputs`/`ProducedOutputs` and review `Inputs`/`RequiredInputs` must all carry the one
    name, resolved against each other by `ArtifactManager.FindProducer`, and the janitor prompt must
    carry the concrete base SHA (not the `origin/main...HEAD` symbolic ref #852 showed resolves
    wrong on the worker's host). None of it had a structural check before; a drop of the
    `inputs=`/`extra_outputs=` threading would silently return every lane review to auditing HEAD.

    Also pins the guard finding 4d surfaced: `--lane` in a tree whose HEAD cannot be read fails
    before it builds anything -- the same pre-build class as the missing-janitor-prompt and grant
    refusals, asserted on the guard's own message so a different exit-2 path (argparse, missing CLI)
    cannot pass for it.
    """
    diff_name = dispatch.LANE_DIFF_OUTPUT_NAME
    with tempfile.TemporaryDirectory() as scratch:
        prompt_path = Path(scratch) / "impl.md"
        prompt_path.write_text("Implement the bounded change.\n", encoding="utf-8")

        cmd = [sys.executable, str(DISPATCH_PY), "--lane",
               "--prompt-file", str(prompt_path),
               "--working-directory", str(ROOT),
               "--scratch-root", scratch, "--dry-run"]
        done = subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8", errors="replace", cwd=ROOT)
        assert done.returncode == 0, f"--lane --dry-run exits {done.returncode}:\n{done.stderr.strip()[:400]}"
        assert "DRY RUN -- nothing was dispatched" in done.stdout, (
            "--lane --dry-run exited 0 without the dry-run marker -- it may have DISPATCHED")

        workflow = json.loads((Path(scratch) / "workflow.json").read_text(encoding="utf-8"))
        steps = {s["StepId"]: s for s in workflow["Steps"]}
        assert diff_name in steps["janitor"]["Outputs"], (
            f"janitor must OUTPUT {diff_name!r} or the review's input resolves to a file no step "
            f"produces (ArtifactManager.FindProducer); got {steps['janitor']['Outputs']}")
        assert steps["review"]["Inputs"] == [diff_name], (
            f"review must take {diff_name!r} as its sole Input; got {steps['review']['Inputs']}")
        assert steps["review"]["DependsOn"] == ["janitor"], (
            "review must DependsOn janitor for the diff to resolve against janitor's Outputs")
        # Polarity: a step that opted into neither key stays empty -- the threading must not leak
        # onto every step (the #513 arrays-not-objects family, and the backward-compat claim).
        assert steps["implement"]["Inputs"] == [], "implement declares no inputs and must stay empty"

        bindings = json.loads((Path(scratch) / "bindings.json").read_text(encoding="utf-8"))
        jan_outputs = [o["Name"] for o in bindings["janitor"]["Contract"]["ProducedOutputs"]]
        assert diff_name in jan_outputs, (
            f"janitor's ProducedOutputs must include {diff_name!r} so ContractValidator.File.Exists "
            f"fails the step loudly when the diff is not written; got {jan_outputs}")
        assert bindings["review"]["Contract"]["RequiredInputs"] == [diff_name], (
            f"review's RequiredInputs must be [{diff_name!r}] (bare strings) -- that is what the "
            f"adapter surfaces as the AER_INPUT_0 pointer; got "
            f"{bindings['review']['Contract']['RequiredInputs']}")

        # The concrete base SHA, not a symbolic ref: the janitor prompt must bake the tree's real
        # HEAD (#852). Anything symbolic here is the exact defect -- a ref that resolves differently
        # on the worker's host.
        head, _ = dispatch._git_head(ROOT)
        jan_prompt = bindings["janitor"]["PromptTemplate"]
        assert head and head in jan_prompt, (
            f"janitor prompt must bake the concrete base SHA {head!r}; a symbolic ref reopens #852")
        assert f"git diff {head}..HEAD" in jan_prompt, (
            "janitor prompt must instruct the two-dot diff against the baked SHA, matching the "
            "workspace-truth block's own ref")
        assert diff_name in jan_prompt, f"janitor prompt must name {diff_name!r} as the file to write"

    # Polarity, finding 4d: a lane whose HEAD cannot be read refuses before building anything,
    # asserted on the guard's own message (exit 2 alone is also argparse / missing-CLI).
    with tempfile.TemporaryDirectory() as not_a_repo:
        prompt_path = Path(not_a_repo) / "impl.md"
        prompt_path.write_text("Implement the bounded change.\n", encoding="utf-8")
        refused = subprocess.run(
            [sys.executable, str(DISPATCH_PY), "--lane", "--prompt-file", str(prompt_path),
             "--working-directory", str(not_a_repo), "--scratch-root", not_a_repo, "--dry-run"],
            capture_output=True, text=True, encoding="utf-8", errors="replace", cwd=ROOT)
        assert refused.returncode == 2, (
            f"--lane on a non-git tree must exit 2, got {refused.returncode}")
        assert "give the reviewer the branch diff (#789)" in refused.stderr, (
            "--lane on a non-git tree must refuse with the #789 head_before guard's own message, not "
            f"some other exit-2 path:\n{refused.stderr.strip()[:300]}")

    return f"1 lane dry-run x wired {diff_name} (janitor output -> review input) + baked SHA, 1 non-git refusal"


@check("workspace truth renders probe failures loudly, never as a clean tree")
def _workspace_truth_probe_failures_are_loud():
    """#780: a failed git probe rendered identically to a clean tree -- `(none)` -- and the
    orchestrator convicted an honest worker of fabrication on that reading. The fix is one
    condition apart from the bug (`if err` before `if value`), so both polarities are pinned
    here against real git repos, not doubles a mock could satisfy either way.
    """
    def truth(head_before, head_before_err=None):
        buf = io.StringIO()
        with contextlib.redirect_stderr(buf):
            ok = dispatch._print_workspace_truth(repo, head_before, head_before_err)
        return ok, buf.getvalue()

    with tempfile.TemporaryDirectory() as scratch:
        repo = Path(scratch) / "repo"
        repo.mkdir()

        def git(*argv):
            # GIT_* scrubbed: under the pre-push hook git exports GIT_DIR/GIT_INDEX_FILE, which
            # redirect this harness's commits at the OUTER repo. Passed standalone, failed only
            # inside the hook -- the exact env leak the vendor loop already strips for.
            env = {k: v for k, v in os.environ.items() if not k.startswith("GIT_")}
            done = subprocess.run(
                ["git", "-C", str(repo), "-c", "user.email=selfcheck@localhost",
                 "-c", "user.name=selfcheck", *argv],
                capture_output=True, text=True, encoding="utf-8", errors="replace", env=env)
            assert done.returncode == 0, f"harness git {argv} failed: {done.stderr.strip()[:200]}"
            return done.stdout.strip()

        git("init", "-q")
        git("commit", "--allow-empty", "-q", "-m", "base")
        head = git("rev-parse", "HEAD")
        (repo / "w.txt").write_text("x", encoding="utf-8")
        git("add", "w.txt")
        git("commit", "-q", "-m", "worker commit")

        # Control arm, read first: on a healthy repo the block renders the added commit and
        # reports truth established. If THIS fails, the harness repo is broken, not dispatch.py.
        ok, out = truth(head)
        assert ok and "worker commit" in out and "truth unavailable" not in out, (
            f"control failed -- a healthy repo did not render its own commit:\n{out}")

        # The #780 polarity: an unresolvable head_before is a probe FAILURE. It must render
        # loudly and report truth as NOT established -- and must never fall through to the
        # clean-tree line, which is the reading the conviction was made on.
        ok, out = truth("0" * 40)
        assert not ok, "a failed probe still reported truth as established"
        assert "truth unavailable" in out, f"a failed probe rendered without the loud marker:\n{out}"
        assert "commits added: (none)" not in out, (
            f"a failed probe rendered as a clean tree:\n{out}")

        # HEAD-failure arm: the two head_before-dependent probes are unavailable, but the status
        # probe needs no head_before and must still report what the worker left uncommitted.
        (repo / "leftover.txt").write_text("uncommitted", encoding="utf-8")
        ok, out = truth(None, "git execution error: simulated")
        assert not ok, "a failed HEAD check still reported truth as established"
        assert "truth unavailable: initial HEAD check failed" in out, (
            f"a failed HEAD check rendered without its reason:\n{out}")
        assert "leftover.txt" in out, (
            f"the HEAD-independent status probe went silent on a failed HEAD check:\n{out}")
    return "3 arms (control, failed probe, failed HEAD) against a real git repo"


@check("every permission boolean can be turned OFF from the command line")
def _both_flag_directions():
    # The population comes from the templates rather than a hand-written list, so a fifth permission
    # is covered the day it is added.
    booleans = sorted({k for tpl in resolved_templates().values()
                       for k, v in tpl.items() if isinstance(v, bool)})
    assert booleans, "no boolean permissions found in TEMPLATES -- the population is empty"

    # PARSED, not grepped. The substring test this replaces passed on a source file whose arms were
    # declared in the order argparse silently mis-defaults: argparse takes a dest's default from the
    # FIRST action registered for it, so a `--no-` arm declared first makes the default False instead
    # of None and the tri-state collapses -- with the string `"--no-write-files"` still present.
    base = ["--list-templates"]  # the only argv that makes the three required args optional
    for key in booleans:
        flag = "--" + key.replace("_", "-")
        neutral = dispatch.build_parser(base).parse_args(base)
        assert getattr(neutral, key) is None, (
            f"{key} defaults to {getattr(neutral, key)!r}, not None. The tri-state is what makes "
            "'was this passed?' answerable; without it a template can never override a permission "
            "downward. Check which arm is declared first."
        )
        on = dispatch.build_parser(base + [flag]).parse_args(base + [flag])
        assert getattr(on, key) is True, f"{flag} does not set {key} True"
        off_flag = "--no-" + key.replace("_", "-")
        off = dispatch.build_parser(base + [off_flag]).parse_args(base + [off_flag])
        assert getattr(off, key) is False, (
            f"{off_flag} does not set {key} False, so a template cannot be overridden downward on "
            "it. That made `--template implement` a lock on exactly the two flags that resolve to "
            "--dangerously-skip-permissions."
        )
    return f"{len(booleans)} booleans x 3 directions (unset/on/off)"


@check("both shapes accept known pins, and PIN_SHAPE rejects English")
def _shapes_discriminate():
    # PIN_SHAPE guards the tools/ walk, where `--model` appears in prose and every following word is
    # a candidate, so it requires a digit. TOKEN_SHAPE guards the register's own fence, where
    # requiring a digit would be INVERTED -- agy serves models from several vendors and a digit-free
    # catalogue entry would be reported as a bad parse.
    english = ("read-only", "fail-closed", "cross-vendor", "skip-permissions")
    for word in english:
        assert not completeness.PIN_SHAPE.fullmatch(word), (
            f"PIN_SHAPE matches {word!r}, an English word. It is everywhere in this repo, and a "
            "match makes the walk report it as an invalid model pin."
        )
    # POSITIVE control, against a literal rather than the register. Asserting either shape over
    # `register_models()` proves nothing: that parser REJECTS any register whose tokens do not all
    # fullmatch TOKEN_SHAPE (completeness.py's `unshaped` arm), so the population arrives
    # pre-filtered and the assertion is satisfied by the filter that produced it. With no positive
    # control, `PIN_SHAPE = re.compile(r"(?!)")` -- a regex matching NOTHING -- left every assertion
    # in this file green while step 9's tools/ walk silently stopped finding any pin at all.
    known_pins = ("gemini-3.1-pro-high", "gemini-3.6-flash-low", "claude-sonnet-4-6",
                  "gpt-oss-120b-medium")
    for pin in known_pins:
        assert completeness.PIN_SHAPE.fullmatch(pin), (
            f"PIN_SHAPE rejects {pin!r}, a real agy model name. The tools/ walk gates on this, so it "
            "would stop finding pins entirely and step 9 would pass by looking at nothing."
        )
        assert completeness.TOKEN_SHAPE.fullmatch(pin), (
            f"TOKEN_SHAPE rejects {pin!r} -- the register parse would call a correct parse a bad one"
        )
    models = register_models()
    # The register is still read, for the ONE thing it can honestly say: how big PIN_SHAPE's stated
    # blind spot currently is. Its digit requirement is a deliberate cost, not a defect, so a
    # digit-free catalogue entry is measured and reported rather than failed.
    blind = sorted(m for m in models if not completeness.PIN_SHAPE.fullmatch(m))
    note = (f"{len(english)} English words rejected, {len(known_pins)} known pins accepted, "
            f"{len(models)} catalogue entries parsed")
    return note + (f"; PIN_SHAPE is blind to {blind} (digit-free, invisible to the tools/ walk)"
                   if blind else "; no catalogue entry is digit-free, so the walk's blind spot is empty")


@check("step 9's probe-input exemption excuses a marked line and nothing else")
def _probe_input_exemption():
    """An exemption asserted in ONE direction is a switch for turning the step off.

    Step 9 fails an agy model name the catalogue does not list. `effort.agy-rejection-is-per-model`
    has to pass a name the catalogue cannot list -- that is the measurement -- so the marker exists.
    What makes it safe rather than a hole is that the unmarked case still fails, which is the arm
    below that would go quiet if the exemption ever widened.
    """
    # The fixture model name is DIGIT-FREE on purpose. A realistic one here would be an unmarked
    # uncatalogued name sitting in `--model` position in a tracked file, so step 9 would flag this
    # very fixture -- and the unmarked arm cannot carry the marker without ceasing to be the unmarked
    # arm. `PIN_SHAPE` requires a digit, and no catalogue entry is digit-free, so a digit-free name is
    # outside the walk's population by construction rather than by an exemption.
    marked = '    run(["agy", "--model", "aer-fixture-model"])  # ' + completeness.UNCATALOGUED_ON_PURPOSE
    unmarked = '    run(["agy", "--model", "aer-fixture-model"])'
    assert completeness.is_probe_input(marked), (
        "step 9: a line carrying the marker was not exempted, so a deliberate probe input fails CI")
    assert not completeness.is_probe_input(unmarked), (
        "step 9: an UNMARKED uncatalogued name was exempted -- the exemption has stopped being an "
        "exemption and step 9 no longer guards a stale pin")
    # A marker anywhere on the line counts, deliberately -- it may sit in a trailing comment or in a
    # docstring line quoting an error message. What must not count is a line without it.
    assert completeness.is_probe_input(f"# {completeness.UNCATALOGUED_ON_PURPOSE} explains why"), (
        "step 9: the marker stopped being recognised in a leading comment")
    return "3 arms: marked exempt, unmarked still fails, marker position free"


@check("a PR body closes only the issues it declares, whatever the grammar around a keyword")
def _negated_close_lint():
    """Both must-fire fixtures are REAL BODIES, verbatim from the merges that auto-closed an issue.

    `NEGATED_CLOSE` in completeness.py carries which merges those were and what each cost. What
    matters here is that a CLAUDE.md note sat between the two incidents and did not prevent the
    second, so the fixtures are the incidents rather than invented shapes.

    The must-NOT-fire half carries as much weight. A deliberate `Closes #n` is the convention this
    repo runs on, and a lint that flagged it would be turned off within a week.
    """
    must_fire = [
        ("#692's body, verbatim", "**Does not close #532 or #550** - it is the measurement"),
        ("#684's body, verbatim", "filed, not fixed: #688"),
        # #694's, and the reason this lint keys on POSITION rather than on negation: past tense,
        # about a different PR, inside a table cell -- it passed the negation-only version while
        # closing #532 for the second time, in the PR that added the lint.
        ("#694's body, verbatim", "| #692 | `Does not close` | closed #532 |"),
        ("contraction", "The root cause isn't fixed: #99."),
        ("uppercase", "This does NOT resolve #123."),
        ("never", "Found but never closed #77"),
        ("descriptive, no negation", "The crash was fixed #690 in an earlier commit."),
        ("second one on a non-declaration line", "It changes nothing. Closes #12."),
    ]
    must_not_fire = [
        ("the convention itself", "Closes #675. Closes #676."),
        ("the safe rewording", "#532 remains open - see the comment thread."),
        ("the other safe rewording", "filed separately: #691"),
        ("a bare reference", "Related: #504, 0023, #479."),
        # A declaration line is exempt IN FULL, second occurrence included -- `Closes #675. Closes
        # #676.` is one deliberate act, and flagging its tail would refuse the repo's own convention.
        # The mirror case, a `Closes #n` buried mid-line, is in must_fire: under the position rule it
        # is flagged, and correctly, since a close that is meant belongs on a line of its own.
        ("bold declaration", "**Closes #12.** The rest of the body follows."),
        # The PR-template shape #975's reviewer found matched neither register: a bulleted close is
        # deliberate, GitHub closes on it, and flagging it here would fight the commonest template.
        ("bulleted declaration", "This PR:\n- Closes #12\n- Adds feature X"),
        ("starred bulleted declaration", "* Closes #13"),
        ("numbered declaration", "1. Fixes #14"),
        ("no keyword at all", "Not the same as #345, which is a different concern."),
        # GitHub links a keyword only when it sits immediately before the reference, so this closes
        # nothing and the lint must agree. Firing here would teach authors to reword around a
        # phantom, and a lint nobody believes is worse than none.
        ("'by' - GitHub ignores it", "The root cause is not fixed by #99 either."),
    ]
    for label, body in must_fire:
        assert completeness.negated_close_faults(body), (
            f"negated-close lint: [{label}] was accepted -- GitHub would close the issue: {body!r}")
    for label, body in must_not_fire:
        assert not completeness.negated_close_faults(body), (
            f"negated-close lint: [{label}] was refused, and GitHub closes nothing here: {body!r}")

    # The numbers themselves, not merely that something fired -- a lint reporting the wrong issue
    # sends the author to edit a line that is not the problem.
    assert completeness.negated_close_faults("Does not close #532 or #550") == [532], (
        "negated-close lint: reported the wrong issue number, or more than the keyword binds to")

    return (f"{len(must_fire)} must fire ({sum(1 for l, _ in must_fire if 'verbatim' in l)} real "
            f"incident bodies) + {len(must_not_fire)} must NOT fire")


@check("a declared close is refused while its target issue still carries unchecked scope boxes")
def _partial_closure_lint():
    """#975's two pure halves. The incident is #961 closing #903 with three of four scopes unbuilt;
    the strict-targeting arms pin the operator's constraint — declarations only — whose rationale
    lives on `declared_closure_targets` itself.

    The honest-scope arm is deliberate: #903's own scopes were prose headings, which this lint
    cannot see — `completeness.py`'s register comment above UNCHECKED_BOX records the companion
    convention (boxes, not headings) that makes partial closure machine-visible at all.
    """
    targets = completeness.declared_closure_targets
    assert targets("Closes #971\n\nProse about #903 and its boxes.") == [971], (
        "partial-closure lint: a bare reference was treated as a declared close, or the "
        "declaration itself was missed — strict targeting is broken in one direction or the other")
    assert targets("Closes #675. Closes #676.") == [675, 676], (
        "partial-closure lint: a two-close declaration line no longer yields both targets")
    assert targets("This PR:\n- Closes #12\n- Adds feature X") == [12], (
        "partial-closure lint: the bulleted PR-template declaration is invisible again — the exact "
        "false pass the #975 review found, where the closed issue is never inspected at all")
    assert targets("It changes nothing. Closes #12.") == [], (
        "partial-closure lint: a mid-line keyword became a declared target — that is the negated-"
        "close lint's fault to flag, and inspecting it here would widen targeting past declarations")

    boxes = completeness.unchecked_scope_lines
    open_box = "## Scopes\n- [ ] build the thing\n- [x] already done\n* [ ] starred too\n+ [ ] plussed too\n1. [ ] numbered too"
    assert boxes(open_box) == ["- [ ] build the thing", "* [ ] starred too", "+ [ ] plussed too", "1. [ ] numbered too"], (
        "partial-closure lint: unchecked boxes were missed or a checked box was counted")
    fenced = "```\n- [ ] just an example\n```\n~~~\n- [ ] tilde-fenced example\n~~~\n- [ ] real"
    assert boxes(fenced) == ["- [ ] real"], (
        "partial-closure lint: a box quoted inside a fence was counted, or the one after it missed")
    nested = "````\n```\n- [ ] quoted example\n```\nstill fenced\n````\n- [ ] real"
    assert boxes(nested) == ["- [ ] real"], (
        "partial-closure lint: a shorter same-char run closed a longer fence early, un-hiding a "
        "quoted example box")
    sticky = "```python\ncode\n```js\nstill the same block per CommonMark\n```\n- [ ] real box after"
    assert boxes(sticky) == ["- [ ] real box after"], (
        "partial-closure lint: an info-string line was taken as a closer, desyncing the tracker so "
        "the real box after the block was silently swallowed — the sticky false pass from the #975 "
        "review")
    assert boxes("## Scope one\nprose only\n## Scope two\nmore prose") == [], (
        "partial-closure lint: fired on prose headings — the documented limitation stopped holding, "
        "which means the convention comment is now wrong, not that this got better")

    faults = completeness.partial_closure_faults(
        {971: "- [x] all built\n- [x] shipped", 903: "- [ ] compaction\n- [ ] archival"})
    assert list(faults) == [903] and len(faults[903]) == 2, (
        "partial-closure lint: the join reported the wrong issue, missed one, or flagged a "
        "fully-checked target")
    return "4 targeting arms + 5 box arms + 1 join arm"


@check("a wrong repo name fails STEP 4 loudly; every other gh failure still skips")
def _step4_names_a_missing_repo():
    """The rollup excludes skips, so "the repo does not exist" and "we are offline" must not both
    be skips -- `completeness.repo_is_unreachable` records what that cost. The must-fire half is
    the wording `gh` actually returns; the must-NOT-fire half is every ordinary reason a developer
    without network or auth should still get a green local run.
    """
    must_fire = [
        ("gh GraphQL, verbatim shape", "GraphQL: Could not resolve to a Repository with the name 'aer-works/baton'."),
        ("REST wording", "gh: Not Found (HTTP 404)"),
        ("case-insensitive", "COULD NOT RESOLVE TO A REPOSITORY"),
    ]
    must_not_fire = [
        ("offline", "error connecting to api.github.com: dial tcp: lookup api.github.com: no such host"),
        ("unauthenticated", "gh: To use GitHub CLI in a GitHub Actions workflow, set the GH_TOKEN environment variable"),
        ("rate limited", "API rate limit exceeded for user ID 12345."),
        # The empty string is what a crashed-without-stderr call leaves. It must skip, not fail:
        # asserting a wrong NAME from no evidence at all is the false-confidence direction.
        ("no stderr at all", ""),
    ]
    for label, text in must_fire:
        assert completeness.repo_is_unreachable(text), (
            f"step 4: [{label}] would have SKIPPED, so a wrong repo name checks nothing while the "
            f"rollup stays green: {text!r}")
    for label, text in must_not_fire:
        assert not completeness.repo_is_unreachable(text), (
            f"step 4: [{label}] would FAIL the gate, so a developer offline or unauthenticated "
            f"cannot get a green local run: {text!r}")
    return f"{len(must_fire)} must fire + {len(must_not_fire)} must NOT fire"


@check("--pr-body refuses a path argument instead of passing over the empty stdin it leaves")
def _pr_body_reads_stdin_only():
    """#860, whose cost `completeness.pr_body_mode`'s own docstring records. The arms here are the
    two states that must stay distinguishable: a stray argument is a usage fault (loud), a piped
    empty body is a real pass (an empty body can close nothing).
    """
    def run(argv: list[str], stdin_text: str) -> tuple[int, str]:
        saved_argv, saved_stdin, saved_stdout = sys.argv, sys.stdin, sys.stdout
        sys.argv, sys.stdin, sys.stdout = argv, io.StringIO(stdin_text), io.StringIO()
        try:
            code = completeness.pr_body_mode()
            return code, sys.stdout.getvalue()
        finally:
            sys.argv, sys.stdin, sys.stdout = saved_argv, saved_stdin, saved_stdout

    faulty = "filed, not fixed: #688\n"

    code, out = run(["completeness.py", "--pr-body", "some/body.md"], "")
    assert code == 1, "a path argument was accepted, so nothing was checked and it still passed"
    assert "STDIN" in out, f"the refusal must say where the body goes; got {out!r}"

    # The control that makes the arm above mean something: the SAME faulty body that a path
    # argument would have hidden is caught the moment it actually arrives on stdin.
    code, _ = run(["completeness.py", "--pr-body"], faulty)
    assert code == 1, "a real fault on stdin stopped being caught"

    code, out = run(["completeness.py", "--pr-body"], "   \n")
    assert code == 0, "a genuinely empty piped body must pass -- it can close nothing"
    assert "empty" in out, f"an empty body's pass must say so, not read as a checked pass; got {out!r}"

    return "3 arms: path argument refused, real fault on stdin still caught, empty body passes loudly"


@check("the gate-citation lint separates a slug from an ordinal")
def _gate_lint_discriminates():
    # Step 10's population is the whole repo, so it can only ever report "0 faults" -- which is what
    # a lint pointed at nothing also reports. `gate_citation_faults` is pure for exactly this
    # reason: drive it with planted input and both directions become checkable.
    slugs = completeness.gate_slugs(completeness.read("CLAUDE.md"))
    assert slugs, "CLAUDE.md defines no gate slugs -- the lint has no expected set to judge against"

    # ASSEMBLED, NOT SPELLED OUT -- the fifth fixture in this pair of files to need it. Every checker
    # here scans the directory it lives in, so a fault written as a literal IS a fault, in a real
    # file, and the checker reports itself. Step 10 did exactly that on these two lines. The rule:
    # a fixture for a checker must not be readable BY that checker.
    ordinal = "run this before shipping -- CLAUDE.md gate " + "8."
    absent_slug = "see gate " + "`record-twice` for the rule."

    # MUST be caught. The first is what `pixi.toml` actually carried; the second is what renaming a
    # gate would leave behind everywhere.
    caught = {"an ordinal": ordinal, "a slug that does not exist": absent_slug}
    for label, text in caught.items():
        faults = completeness.gate_citation_faults({"planted.md": text}, slugs)
        assert faults, f"the lint does not flag {label}: {text!r}"

    # MUST NOT be caught. A lint that fires on correct prose gets deleted, and each of these was a
    # real false positive or a near-miss: `DependsOn` is a validity gate in milestone-history.md and
    # not a shipping gate at all, and it only matched because a blanket re.I made `[a-z]` match
    # capitals too.
    ignored = {
        "a correct slug citation": f"see gate `{sorted(slugs)[0]}` for the rule.",
        "an unrelated capitalised gate name": "the validity gate `DependsOn` walks ancestors.",
        "a hyphenated ordinal": "the gate-7 branch is unrelated.",
        "the bare word": "this Gate is a different thing entirely.",
    }
    for label, text in ignored.items():
        faults = completeness.gate_citation_faults({"planted.md": text}, slugs)
        assert not faults, f"the lint fires on {label}: {text!r} -> {faults}"

    # #1365/#1367: the name-level predicate that keeps generated changelogs out of the
    # living-document lints (step 10 here, and record-once's changed-file population).
    # Both arms: the release-please artifact is skipped; a near-name living doc is not.
    assert completeness.generated_changelog("CHANGELOG.md"), \
        "generated changelogs must be skipped by name -- release PR #309 was unmergeable without this"
    for near in ("CHANGES.md", "changelog-notes.md", "CHANGELOG.py"):
        assert not completeness.generated_changelog(near), \
            f"the skip must be exact -- {near!r} is a living document and stays policed"

    return (f"{len(slugs)} slugs; {len(caught)} fault shapes caught, "
            f"{len(ignored)} correct shapes ignored; changelog name-skip discriminates")


@check("step 9 fails CLOSED when either of its two file sources goes unreadable")
def _step9_fails_closed():
    # TWO of step 9's four sources, and the title says two rather than implying all four. The
    # uncontrolled pair: dispatch.py's own absence (a hard failure whose arm would need the import to
    # fail, not the read) and the tools/ regex walk (whose population is the whole tree).
    #
    # Monkeypatched rather than mutating the tree: a test that renames files leaves the repo broken
    # if it is interrupted, and completeness.py derives ROOT from __file__ so it cannot simply be
    # relocated -- that is what silently broke a control run into reading empty strings.
    # step 9 prints its full report on every call; the assertions read its return value, so the
    # output is noise here.
    def baseline():
        with contextlib.redirect_stdout(io.StringIO()):
            return completeness.step9_pinned_models_exist() is True

    real_read = completeness.read
    sources = {
        "docs/vendor-capabilities.md (the register)": "vendor-capabilities",
        "tools/vendor-verify/verify.py (the CHEAP pin)": "verify.py",
    }
    for label, needle in sources.items():
        result = control_arm(
            baseline,
            lambda needle=needle: setattr(completeness, "read",
                                          lambda p: "" if needle in p else real_read(p)),
            lambda: setattr(completeness, "read", real_read),
            describe=f"step9 with {label} unreadable")
        assert result is False, (
            f"step 9 passed with {label} unreadable -- a population that silently shrinks is how a "
            "check keeps printing OK about less and less"
        )
    return f"{len(sources)} of step 9's 4 sources controlled"


@check("no tooling file transcribes a count its own code computes")
def _no_transcribed_counts():
    # `record-once`: never transcribe a value that lives somewhere authoritative. Both patterns were
    # real -- a docstring said "eight steps" while main() ran nine, and a comment said "(today: 12)"
    # against the count `register_models()` computes.
    #
    # The population is every python file in this pair of tools, INCLUDING this one, which is where
    # the live instances were: this file's own docstring said "six assertions" while more were
    # registered. Those quoted counts are what `is_citation` is exercised on -- three of them, across
    # two comments, and if it misclassified any one the assert below would fire.
    #
    # SCOPE, because the report says "nothing transcribes a count" and that is a claim about these
    # two patterns, not about the tree: only `<n> steps`, `<n> assertions` and `today: <n>` are
    # searched. Prose that transcribes a computed value in any other shape is invisible here. Two
    # such were found by a reviewer and fixed by CITING the expression instead -- which is the actual
    # remedy, since no pattern list will ever cover English.
    files = sorted(f for d in LINT_DIRS for f in d.glob("*.py"))
    assert files, "no tooling files found -- the population is empty"
    steps = len(re.findall(r"^def step\d", completeness.read("tools/audit-completeness/completeness.py"), re.M))
    # `completeness.read` returns "" for a missing path, so a rename or a typo makes this 0 in
    # silence -- and the first real transcription it ever caught would be reported as "claims 9
    # steps; 0 are defined". The same "population that silently shrinks" this file checks for
    # elsewhere.
    assert steps, ("no `def stepN` functions found in completeness.py -- the value this lint "
                   "compares against is not being computed, so it cannot judge any claim")
    fence_count = len(register_models())
    words = {"one": 1, "two": 2, "three": 3, "four": 4, "five": 5, "six": 6, "seven": 7,
             "eight": 8, "nine": 9, "ten": 10, "eleven": 11, "twelve": 12}

    def claimed(tok):
        tok = tok.lower()
        return words.get(tok, int(tok) if tok.isdigit() else None)

    found = cited = 0
    for path in files:
        src = path.read_text(encoding="utf-8")
        for m in re.finditer(r"\b([a-z]+|\d+)\s+(steps|assertions)\b", src, re.I):
            n = claimed(m.group(1))
            if n is None:
                continue
            if is_citation(src, m):
                cited += 1
                continue
            found += 1
            expected = steps if m.group(2).lower() == "steps" else len(CHECKS)
            assert n == expected, (
                f"{path.name} claims {n} {m.group(2).lower()}; {expected} are defined. Cite the code "
                f"or drop the number -- this exact sentence stood at 'eight' while main() ran nine."
            )
        for m in re.finditer(r"today:\s*(\d+)", src):
            if is_citation(src, m):
                cited += 1
                continue
            found += 1
            assert int(m.group(1)) == fence_count, (
                f"{path.name} says 'today: {m.group(1)}' where the register's fence holds {fence_count}"
            )
    # Zero transcribed counts is the DESIRED state -- this is a lint, and a lint with no violations
    # is healthy. So an empty population is not a failure, but it is REPORTED as empty: the version
    # of this check that scanned one file found nothing after that file was rewritten, and went on
    # printing OK about a comparison it was no longer making.
    scanned = f"{len(files)} files scanned"
    skipped = f", {cited} quoted citation(s) skipped" if cited else ""
    return (f"{scanned}, {found} transcribed count(s) verified{skipped}" if found
            else f"{scanned}{skipped}; NOTHING transcribes a count, so nothing was compared")


@check("the two reusable instruments work on themselves")
def _instruments_self_test():
    # A table rather than a run of asserts, so the count in the population line is COUNTED. Written
    # as `4 code_tokens polarities` it was already wrong one edit later, in the file that lints for
    # exactly that -- and the lint's patterns do not cover the word "polarities", so it stayed green.
    #
    # `same=True` means the two inputs must be indistinguishable to the instrument (prose changed);
    # `same=False` means it must tell them apart (code changed). Both directions, because a
    # `code_tokens` that returned [] for everything would satisfy only the first kind.
    polarities = [
        ("comment text is invisible", "x = 1  # comment\n", "x = 1  # different comment\n", True),
        ("docstring text is invisible", '"""doc a."""\nx = 1\n', '"""doc b."""\nx = 1\n', True),
        ("a real code change is visible", "x = 1\n", "x = 2\n", False),
        # The defect it was written for: a string literal a USER sees is code, not prose, however it
        # is quoted. Triple-quoted and not in a docstring slot, so quote style cannot classify it.
        ("a triple-quoted VALUE is code", 'x = """v1"""\n', 'x = """v2"""\n', False),
        # The KNOWN blind spot, pinned in the direction it actually behaves so the docstring's stated
        # limitation is checked rather than merely claimed. If a future edit starts keeping
        # NEWLINE/INDENT/DEDENT this fails, and the docstring has to be corrected with it.
        ("block structure is NOT visible (known gap)",
         "if x:\n    y = 1\nz = 2\n", "if x:\n    y = 1\n    z = 2\n", True),
    ]
    for label, a, b, same in polarities:
        if same:
            assert code_tokens(a) == code_tokens(b), f"code_tokens: {label} -- it distinguished them"
        else:
            assert code_tokens(a) != code_tokens(b), f"code_tokens: {label} -- it missed the change"

    try:
        control_arm(lambda: False, lambda: None, lambda: None, describe="deliberately red baseline")
    except AssertionError:
        pass
    else:
        raise AssertionError("control_arm reported a result on a RED baseline -- its whole purpose")
    return (f"{len(polarities)} code_tokens polarities "
            f"({sum(1 for p in polarities if not p[3])} must discriminate) "
            "+ control_arm's red baseline")


@check("the record-once checker fires on restated prose, not on text the register prescribes")
def _recordonce_discriminates():
    """A table, not a run of asserts, so the population line is counted rather than transcribed.

    `fires=True` means the input must be reported; `fires=False` means it must not. Both directions,
    because a checker that reported everything would satisfy only the first kind -- and three
    designs of this one have now been wrong in the second, each time on text whose duplication
    `record-once` itself prescribes.
    """
    rec = load(ROOT / "tools" / "audit-completeness" / "recordonce.py", "_selfcheck_recordonce")

    # recordonce takes added lines split BY HUNK, so every fixture says which lines are contiguous.
    # Written out rather than defaulted, because contiguity is now load-bearing: a fixture that
    # silently rejoined two hunks would exercise the fabricated-shingle path the split exists to
    # close, and would do it invisibly.
    def one(*lines: str) -> list[list[str]]:
        """One hunk: every line contiguous with the next."""
        return [list(lines)]

    sentence = "the vendor refuses the call before any hook is ever consulted here"
    restated = {"src/A.cs": one(f"// {sentence}"), "docs/B.md": one(sentence)}

    # Written out as a literal, which #676 is what makes possible: the marker is now read out of a
    # file's COMMENT PROSE, so these characters sitting in a Python string are code and exempt
    # nothing. Before that, any tracked file containing them anywhere exempted itself, and this line
    # had to be assembled from fragments to avoid disabling the checker it is a fixture for.
    # The canonical path has to be a file that EXISTS -- a marker naming one that does not is
    # refused, which the last arm below asserts. So the fixture names a real one.
    marker = "// record-once-ok: #901 canonical is CLAUDE.md"

    # Genuinely different sentences, as real files citing one issue have -- a fixture that repeated
    # one sentence ten times would be restatement, and the checker would be right to say so.
    distinct = [
        "// Pre-approved either way, because the hook is what confines the target path.",
        "// Refused at bind time rather than discovered after the run is paid for.",
        "// The exemption covers write-family tools only; a read carries a path too.",
        "// Fails closed on every payload it cannot judge, including its own defects.",
        "// Resolves every component so a planted link cannot launder the target.",
        "// Adapter-agnostic, so narrowing this would refuse on one vendor at bind time.",
        "// Measured live across four consecutive dispatches with distinct task directories.",
        "// The vendor ignores the process working directory, which is why cwd is not it.",
        "// Only the deny list enforces; the allow list merely stops the prompt appearing.",
        "// Kept as its own condition so the operator learns which mistake they made.",
    ]
    title = "A reviewer's verdict is evidence for a human decision, never the decision itself"
    banner = ["// GENERATED FILE - DO NOT EDIT.", "// Regenerate: pixi run tokens",
              "// Hand edits are reverted by the next regeneration and fail CI in the meantime."]
    fenced = ["Run it like this:", "```bash", "pixi run audit-recordonce -- origin/main", "```"]

    polarities = [
        ("one sentence written into two files", restated, True),
        # A restatement that reaches a code file only through its comments still has to be found:
        # the measured case spread one corrected fact across `///` comments and markdown alike.
        ("prose restated across a comment and a doc",
         {"src/C.cs": one(f"/// {sentence}"), "docs/D.md": one(f"- {sentence}")}, True),
        # Guards contiguity from the other side: a break rule that split per line would satisfy every
        # arm below while losing the only shape the checker was built for. See `groups` in
        # recordonce.py for why a run spans consecutive comment lines.
        ("a sentence wrapped across two comment lines",
         {"src/W.cs": one("/// the vendor refuses the call before any hook",
                          "/// is ever consulted here at all"),
          "docs/W.md": one("the vendor refuses the call before any hook is ever consulted here "
                           "at all")}, True),
        # Was a FALSE POSITIVE until hunks were split apart, and measured as one: neither hunk holds
        # nine words, so the only shingle either file can produce is the join -- a word sequence
        # present in no line of either. Two files "sharing" it shared nothing. The same fabrication
        # also reached the `e.g. "..."` sample printed under real findings.
        ("two files sharing only a cross-hunk join",
         {p: [["/// the gate refuses a payload"], ["/// it cannot judge at all"]]
          for p in ("src/H1.cs", "src/H2.cs")}, False),
        # #675's coverage half. None of these carried a leader on the line holding the words, so all
        # three were invisible to the leader regex -- and a Python docstring is not an exotic case
        # here: repinning PROVEN_GROUPS on this change was caused by exactly one, in dispatch.py.
        ("a block-comment body with no leader on its lines",
         {"src/BC.cs": one("/* the vendor refuses the call before any hook",
                           "   is ever consulted here at all */"),
          "docs/BC.md": one(f"{sentence} at all")}, True),
        ("a python docstring restated into a doc",
         {"tools/x.py": one("def f():", f'    """{sentence}."""'),
          "docs/PY.md": one(sentence)}, True),
        ("an xml comment restated into a doc",
         {"src/X.csproj": one(f"<!-- {sentence} -->"), "docs/XM.md": one(sentence)}, True),
        # The other direction of the same change: context means code positions stop being read, and
        # a `#if` is not a `#` comment. Under the old leader regex this fired.
        ("a C# preprocessor directive shared by two files",
         {p: one("#if WINDOWS", "#region the vendor refuses the call before any hook is here",
                 "#endif") for p in ("src/P1.cs", "src/P2.cs")}, False),
        # An unanchored opener would find `//` inside every one of these and read the URL as prose.
        ("the same long url in code in two files",
         {p: one('var u = "https://example.com/a/b/c/d/e/f/g/h/i/j";')
          for p in ("src/U1.cs", "src/U2.cs")}, False),
        # Was a false positive under the reference-counting design: one issue cited in many files
        # is the register working, and ten different sentences share no wording.
        ("one issue cited in ten files",
         {f"src/F{i}.cs": one(f"{line} See #901.") for i, line in enumerate(distinct)}, False),
        # Was a false positive under the first shingling design: duplicated test setup is ordinary.
        ("duplicated test setup code",
         {f"tests/T{i}.cs": one("var grant = new PermissionGrant(ReadFiles: true, WriteFiles: false);",
                                "using var stderr = new StringWriter();") for i in range(3)}, False),
        # The three shapes the first draft failed CI on, for the reason recorded beside `TABLE_ROW`
        # in recordonce.py. The first of them fired on every new decision record.
        ("a decision record, its index row and its plan row",
         {"docs/decisions/0042-x.md": one(f"# 0042 - {title}"),
          "docs/decisions/README.md": one(f"| [0042](0042-x.md) | {title} | M26 |"),
          "docs/plan.md": one(f"| 0042 | {title} | done |")}, False),
        # Two decision records with DIFFERENT bodies that share only a "Relates to" footer listing the
        # same prior decisions collide on the slug words baked into the `](nnnn-slug.md)` link targets,
        # never on a shared sentence -- the exact 0046/0047 false positive `LINK_TARGET` strips. This is
        # the real shape and the reason the bodies differ here: pre-fix the footer's link run alone forms
        # a 9-gram across both files; post-fix the targets are gone and the surviving `relates to 0013
        # and 0003` is far too short to shingle, so it is the shared LINK that stops colliding, not a
        # whole line collapsing below the floor.
        ("two decisions sharing only a Relates-to link footer",
         {"docs/decisions/0090-p.md": one(
             "A place is a container and a workflow is the work that runs under it.",
             "Relates to [0013](0013-room-is-the-user-facing-noun.md) and "
             "[0003](0003-templates-collapse-to-three-shapes.md)."),
          "docs/decisions/0091-q.md": one(
             "Templates are data resolved the way the role catalog already is.",
             "Relates to [0013](0013-room-is-the-user-facing-noun.md) and "
             "[0003](0003-templates-collapse-to-three-shapes.md).")},
         False),
        ("a regenerated banner in two generated files",
         {"src/Aer.Ui.Core/Generated.cs": [banner], "src/Aer.Ui/Theme/Tokens.axaml.cs": [banner]}, False),
        ("the same command block fenced in two runbooks",
         {"docs/runbooks/a.md": [fenced], "docs/runbooks/b.md": [fenced]}, False),
        # A file with no extension is still a file with comments, and the per-language table read it
        # as nothing while every arm above stayed green. `NO_EXTENSION` in recordonce.py carries the
        # measurement; what this arm adds is that a narrowing cannot ship silently again.
        ("prose in an extensionless file restated into a doc",
         {".githooks/pre-push": one(f"# {sentence}"), "docs/EX.md": one(sentence)}, True),
    ]
    for label, by_file, fires in polarities:
        found = rec.violations(by_file)
        if fires:
            assert found, f"record-once: {label} was accepted -- the shape this exists for"
        else:
            assert not found, f"record-once: {label} was rejected -- {found}"

    # -- #676, the exemption. Its own table, because every arm needs a marker source: markers are
    # read from whole files now, not from the diff, so these say what each file CONTAINS as well as
    # what the change added.
    marked = {"src/A.cs": [f"// {sentence}", marker], "docs/B.md": [sentence]}
    added = {"src/A.cs": one(f"// {sentence}"), "docs/B.md": one(sentence)}
    at = lambda path: marked.get(path)  # noqa: E731

    # The marker is in neither file's ADDED lines. Under the old added-lines match this was flagged
    # again the moment someone reworded a copy without re-touching the marker -- the "too weak over
    # time" half of #676. The exemption is a decision about the passage, so it has to outlive the
    # commit that made it.
    assert not rec.violations(added, at), (
        "record-once: an exemption granted by an earlier change no longer holds")

    # And it must be reported, or a silenced run reads exactly like a clean one.
    notes = rec.groups(added, at)[1]
    assert notes and "#901" in notes[0] and "CLAUDE.md" in notes[0], (
        f"record-once: the exemption was not reported with its issue and canonical path -- {notes}")

    # PASSAGE-level, not file-level: a second, unmarked restatement in the SAME file is still found.
    # Under a file-granular hatch one marker in a file stopped everything else in it being compared.
    other = "a withheld write reaching the outbox is the only exemption that exists"
    marked_two = {"src/A.cs": [f"// {sentence}", marker, "", f"// {other}"],
                  "docs/B.md": [sentence], "docs/C.md": [other]}
    assert rec.violations(
        {"src/A.cs": one(f"// {sentence}", marker, "", f"// {other}"),
         "docs/B.md": one(sentence), "docs/C.md": one(other)},
        lambda path: marked_two.get(path)), (
        "record-once: a marker exempted a passage it does not sit beside")

    # The context test. The same characters in a code position -- a Python string literal, which is
    # exactly how this file writes `marker` above -- must exempt nothing.
    literal = {"tools/x.py": [f'marker = "{marker}"', f"# {sentence}"], "docs/B.md": [sentence]}
    assert rec.violations({"tools/x.py": one(f'marker = "{marker}"', f"# {sentence}"),
                           "docs/B.md": one(sentence)}, lambda path: literal.get(path)), (
        "record-once: a marker written as a code literal silenced the checker")

    # Prose ABOUT the marker is not a marker. The false positive the anchored SUPPRESS was added
    # for, and it was live rather than theoretical -- `SUPPRESS` in recordonce.py records which
    # docstring it was and what it exempted. The second assertion is the load-bearing one: a mention
    # must be inert, not merely un-honoured, or every document explaining the syntax fails the gate.
    mention = f"// see {marker[3:]} for the format"
    describes = {"src/A.cs": [f"// {sentence}", mention], "docs/B.md": [sentence]}
    at_mention = lambda path: describes.get(path)  # noqa: E731
    assert rec.violations(added, at_mention), (
        "record-once: a marker named inside a sentence exempted a passage")
    assert not rec.groups(added, at_mention)[2], (
        "record-once: prose describing the marker was reported as a broken one")

    # A marker whose canonical location does not exist, and one that does not parse at all, each
    # exempt nothing AND fail the run. Both are unambiguous typos, and both previously landed as a
    # printed note saying the passage had been exempted while it was being compared.
    typo = marker.replace("CLAUDE.md", "docs/no-such-file.md")
    absent = {"src/A.cs": [f"// {sentence}", typo], "docs/B.md": [sentence]}
    at_typo = lambda path: absent.get(path)  # noqa: E731
    assert rec.violations(added, at_typo), (
        "record-once: a marker naming a file that does not exist still exempted the passage")
    assert any("does not exist" in b for b in rec.groups(added, at_typo)[2]), (
        "record-once: a refused marker was reported as though it had been honoured")

    # One file, no shared wording: the ONLY thing wrong is the marker, so a green run here would be
    # the silent no-op itself rather than any restatement finding masking it.
    broken = {"src/A.cs": ["// record-once-ok: #901", f"// {sentence}"]}
    solo = rec.violations({"src/A.cs": one("// record-once-ok: #901", f"// {sentence}")},
                          lambda path: broken.get(path))
    assert len(solo) == 1 and "does not parse" in solo[0], (
        f"record-once: a marker with no canonical path failed silently -- {solo}")

    # -- #691, the markdown half of the hatch. Markdown is this gate's dominant population and an
    # HTML comment is the only comment form it has; before this, the comment form exempted nothing
    # AND reported nothing, which is the same silent no-op class as `broken` above, scoped to
    # exactly the files most likely to need a marker.
    md_marker = "<!-- record-once-ok: #901 canonical is CLAUDE.md -->"
    # No marker on the src side, deliberately: one side's marker exempts the pair (the #676 arm
    # above pins that), so a fixture carrying the C# marker too would pass with the markdown one
    # still dead -- which is exactly how the first draft of this arm failed to discriminate.
    md_marked = {"src/A.cs": [f"// {sentence}"], "docs/B.md": [sentence, md_marker]}
    assert not rec.violations(added, lambda path: md_marked.get(path)), (
        "record-once: an HTML-comment marker in a markdown file exempted nothing")

    # Its malformed sibling must be REPORTED, not silent -- SUPPRESS_LOOSE has to see the same
    # comment shape SUPPRESS does, or the mistyped-marker class reopens for markdown specifically.
    md_typo = {"docs/B.md": [sentence, "<!-- record-once-ok #901 CLAUDE.md -->"],
               "src/A.cs": [f"// {sentence}", marker]}
    assert any("does not parse" in b for b in rec.groups(added, lambda path: md_typo.get(path))[2]), (
        "record-once: a malformed HTML-comment marker in markdown failed silently")

    # And prose about the comment form stays inert -- mid-sentence, the `<!--` never opens the
    # line, so a doc explaining this syntax (this repo has one) neither exempts nor reports.
    md_mention = {"docs/B.md": [sentence, f"write {md_marker} beside the copy"],
                  "src/A.cs": [f"// {sentence}"]}
    at_md_mention = lambda path: md_mention.get(path)  # noqa: E731
    assert rec.violations(added, at_md_mention), (
        "record-once: an HTML-comment marker quoted mid-sentence exempted a passage")
    assert not rec.groups(added, at_md_mention)[2], (
        "record-once: prose describing the markdown marker form was reported as a broken one")

    # Buried markers -- a list bullet or a doubled opener in front of the comment, the two shapes
    # the #691 review measured as fully silent. Never honoured (the own-line rule stands), never
    # silent (both land in the malformed report). What separates these from the inert mid-sentence
    # mention above is that the bullet or the opener OPENS the line.
    for buried in (f"- {md_marker}", f"<!-- {md_marker}"):
        md_buried = {"docs/B.md": [sentence, buried], "src/A.cs": [f"// {sentence}"]}
        at_buried = lambda path, m=md_buried: m.get(path)  # noqa: E731
        assert rec.violations(added, at_buried), (
            f"record-once: a buried marker ({buried!r}) exempted a passage the own-line rule refuses")
        assert rec.groups(added, at_buried)[2], (
            f"record-once: a buried marker ({buried!r}) failed silently instead of being reported")

    # The locale-decoding crash `GIT_TEXT` in recordonce.py records (#690). Run against a real
    # tracked file whose bytes are not cp1252-decodable: the defect lives in how a subprocess pipe is
    # decoded, so no in-memory fixture can reach it, and the second assertion is what stops the arm
    # quietly ceasing to discriminate if that file is ever rewritten in ASCII.
    unmappable = "docs/vendor-doc-audit.md"
    at_head = rec.file_at(unmappable)
    assert at_head, f"record-once: file_at returned nothing for {unmappable}, so this arm tested nothing"
    # The property has to be "holds a byte cp1252 REJECTS", not "holds a non-ASCII character", and
    # the difference is not pedantic: cp1252 rejects exactly these five bytes, so an em dash
    # (`e2 80 94`) or a check mark (`e2 9c 85`) decodes cleanly under it while satisfying any
    # "outside latin-1" test. An earlier version of this guard asserted `ord(c) > 255` and would have
    # gone on passing after every rejecting character was edited out, leaving both this arm and the
    # `audit-controls` arm that depends on it silently testing nothing.
    #
    # Today the file qualifies on U+274C CROSS MARK (x10), U+2190 LEFTWARDS ARROW and U+23F8 -- the
    # arrow being the `0x90` in the crash that found the defect. Deliberately not pinned to those
    # characters: any rejecting byte does, and naming them would make an ordinary edit look like a
    # regression.
    CP1252_REJECTS = {0x81, 0x8D, 0x8F, 0x90, 0x9D}
    assert any(b in CP1252_REJECTS for line in at_head for b in line.encode("utf-8")), (
        f"record-once: {unmappable} no longer holds a byte cp1252 rejects, so neither this arm nor "
        "`audit-controls`' hostile-codec arm discriminates -- point both at a file that does")

    return (f"{len(polarities)} record-once polarities "
            f"({sum(1 for p in polarities if not p[2])} must NOT fire) + 9 exemption arms "
            f"+ a non-cp1252 file read through git")


@check("the record-once checker still finds the passages it found in a real merge")
def _recordonce_still_fires_on_real_data():
    """Fixtures above encode the failures already known. This one runs against a real diff.

    Two designs of that checker passed every fixture written for them and were useless on the merge
    they existed to catch, so the fixtures cannot be the whole test. Registered here rather than left
    as a bare CLI mode so `audit-controls` reaches it: an unadjudicated pin would otherwise sit green
    forever.
    """
    rec = load(ROOT / "tools" / "audit-completeness" / "recordonce.py", "_selfcheck_recordonce_pin")
    ok, detail = rec.prove(rec.PROVEN_SHA, rec.PROVEN_GROUPS)
    assert ok, "record-once no longer finds what it found in " + rec.PROVEN_SHA[:7] + ":\n  " \
        + "\n  ".join(detail)
    return detail[0]


@check("the vocabulary checker flags engine terms in user-facing string literals")
def _vocabulary_discriminates():
    rec = vocabulary
    with tempfile.TemporaryDirectory() as tmp:
        tmp_path = Path(tmp)
        files_scanned, violations = rec.scan_tree(tmp_path)
        assert len(violations) == 0, f"empty tree produced violations: {violations}"

        ui_dir = tmp_path / "src" / "Aer.Ui"
        ui_dir.mkdir(parents=True)
        (ui_dir / "Test.axaml").write_text('<TextBlock Text="PausePoint" />', encoding="utf-8")
        files_scanned, violations = rec.scan_tree(tmp_path)
        assert len(violations) == 1, f"planted AXAML violation not caught: {violations}"

        (ui_dir / "Test.axaml").write_text('<TextBlock Text="PausePoint" /> <!-- vocabulary-ok: test -->', encoding="utf-8")
        files_scanned, violations = rec.scan_tree(tmp_path)
        assert len(violations) == 0, f"allowlist comment failed to suppress AXAML: {violations}"

        core_dir = tmp_path / "src" / "Aer.Ui.Core"
        core_dir.mkdir(parents=True)
        (core_dir / "TestViewModel.cs").write_text('var x = "gemini";', encoding="utf-8")
        files_scanned, violations = rec.scan_tree(tmp_path)
        assert len(violations) == 1, f"planted C# violation not caught: {violations}"

        (core_dir / "TestViewModel.cs").write_text('var x = "gemini"; // vocabulary-ok: test', encoding="utf-8")
        files_scanned, violations = rec.scan_tree(tmp_path)
        assert len(violations) == 0, f"allowlist comment failed to suppress C#: {violations}"

        # Literals that span lines — the per-line pass is blind inside them (#315's second
        # reader), so the full-text pass must catch a leak on a continuation line.
        (core_dir / "TestViewModel.cs").write_text(
            'var x = @"line one\nthe Session leaks here";', encoding="utf-8"
        )
        files_scanned, violations = rec.scan_tree(tmp_path)
        assert len(violations) == 1, f"multiline C# verbatim leak not caught: {violations}"

        # A trailing comment can't sit on the opening line (it would be inside the string),
        # so a multiline literal is allowlisted from the line above it.
        (core_dir / "TestViewModel.cs").write_text(
            '// vocabulary-ok: test\nvar x = @"line one\nthe Session leaks here";', encoding="utf-8"
        )
        files_scanned, violations = rec.scan_tree(tmp_path)
        assert len(violations) == 0, f"allowlist failed to suppress multiline C#: {violations}"

    return "AXAML, C# populations + allowlist suppression + multiline literal leaks"


@check("the permissionrank checker flags permissive-primary controls paired with un-primaried deny controls")
def _permissionrank_discriminates():
    rec = permissionrank
    total_files, violations = rec.scan_tree(ROOT)
    assert len(violations) == 0, f"real tree has permissionrank violations: {violations}"

    with tempfile.TemporaryDirectory() as tmp:
        tmp_path = Path(tmp)
        files_scanned, violations = rec.scan_tree(tmp_path)
        assert len(violations) == 0, f"empty tree produced violations: {violations}"

        ui_dir = tmp_path / "src" / "Aer.Ui"
        ui_dir.mkdir(parents=True)
        (ui_dir / "TestView.axaml").write_text(
            '<StackPanel>\n'
            '  <Button Classes="accent" Content="Allow once" />\n'
            '  <Button Content="Deny once" />\n'
            '</StackPanel>',
            encoding="utf-8"
        )
        files_scanned, violations = rec.scan_tree(tmp_path)
        assert len(violations) == 1, f"planted AXAML permissive-primary violation not caught: {violations}"

        (ui_dir / "TestView.axaml").write_text(
            '<StackPanel>\n'
            '  <Button Content="Allow once" />\n'
            '  <Button Content="Deny once" />\n'
            '</StackPanel>',
            encoding="utf-8"
        )
        files_scanned, violations = rec.scan_tree(tmp_path)
        assert len(violations) == 0, f"equal-weight AXAML flagged unexpectedly: {violations}"

        # #1124 review finding A: the repo's own AccessText mnemonic idiom puts the permissive
        # label on a LATER line of the same element — a same-line-only scan ships that violation
        # green.
        (ui_dir / "TestView.axaml").write_text(
            '<StackPanel>\n'
            '  <Button Classes="accent" Command="{Binding AllowCommand}">\n'
            '    <AccessText Text="A_llow once" VerticalAlignment="Center" />\n'
            '  </Button>\n'
            '  <Button Content="Deny once" />\n'
            '</StackPanel>',
            encoding="utf-8"
        )
        files_scanned, violations = rec.scan_tree(tmp_path)
        assert len(violations) == 1, f"multi-line AccessText permissive-primary not caught: {violations}"

        # #1124 review finding B: Avalonia's attached-property conditional-class syntax,
        # already used in this tree (SettingsView.axaml), must be as visible as the list form.
        (ui_dir / "TestView.axaml").write_text(
            '<StackPanel>\n'
            '  <Button Classes.accent="{Binding IsPrimary}" Content="Allow once" />\n'
            '  <Button Content="Deny once" />\n'
            '</StackPanel>',
            encoding="utf-8"
        )
        files_scanned, violations = rec.scan_tree(tmp_path)
        assert len(violations) == 1, f"Classes.accent conditional-class violation not caught: {violations}"

    return "AXAML (same-line, AccessText multi-line, Classes.accent) permissive-primary controls + real tree verification"


def main() -> int:
    print(__doc__.strip().splitlines()[0])
    print("=" * 78)
    for line in ("Most assertions map to a defect that shipped into a draft of #627.",
                 "Cannot check: whether a population is the RIGHT population, or anything in prose."):
        print(f"  - {line}")
    print()

    for name, fn in CHECKS:
        try:
            population = fn()
        except AssertionError as e:
            FAILURES.append(f"{name}: {e}")
            print(f" !! {name}\n      {e}")
        except Exception as e:  # noqa: BLE001 -- see below
            # Not just AssertionError. A check can raise FileNotFoundError (a bindings.json that was
            # never written), JSONDecodeError, or SystemExit(2) from argparse if a flag it names is
            # removed. Any of those used to abort the whole run before the remaining checks and
            # before the summary -- the exit code stayed non-zero, so never a false pass, but the
            # file's whole premise is that a failure says what failed.
            FAILURES.append(f"{name}: {type(e).__name__}: {e}")
            print(f" !! {name}\n      raised {type(e).__name__}: {e}\n"
                  f"      (a raise, not a failed assertion -- the check itself is broken)")
        else:
            if not population:
                # An assertion that reports no population cannot be distinguished from one that
                # examined nothing, which is the whole defect class here.
                FAILURES.append(f"{name}: reported no population")
                print(f" !! {name}\n      passed without reporting a population -- it cannot be "
                      "told apart from a check that compared nothing")
            else:
                print(f" OK {name}")
                print(f"      {population}")

    if FAILURES:
        print(f"\n{len(FAILURES)} failing assertion(s).")
        return 1
    print(f"\nAll {len(CHECKS)} assertions hold.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
