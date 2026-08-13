using Aer.Adapters;
using Aer.Flow.Dispatch;
using Aer.Flow.Domain;

namespace Aer.Ui.Tests.TestSupport;

/// <summary>
/// Retroactive M24 Phase 1/2 test-gap-fill (#262/#263): the deterministic, CI-safe
/// <see cref="IWorkerAdapter"/> session-turn branching tests need. Unlike
/// <see cref="ShellCommandWorkerAdapter"/>, this ignores <c>WorkerInvocation.PromptTemplate</c>
/// entirely rather than running it as a shell command — a vendor-handoff or compact turn's
/// <c>PromptTemplate</c> is <c>InteractiveSessionMaterializer.SynthesizeContextSummary</c>'s
/// natural-language output, not a valid command line, so a literal-command adapter would fail
/// dispatch and silently swallow the failure before <c>ExecuteSessionTurnAsync</c>'s metadata write
/// ever runs. This adapter always succeeds, writing a fixed response file regardless of what the
/// prompt template says, so every turn — handoff, ceiling, or ordinary — reaches and exercises the
/// observable metadata (<c>VendorHandoffSynthesized</c>, <c>NativeSessionResumed</c>,
/// <c>CurrentAdapter</c>, <c>TurnCount</c>).
/// </summary>
internal sealed class SessionTurnStubAdapter : IWorkerAdapter
{
    /// <summary>
    /// Sentinel a test's message text can embed to force this turn to fail closed (#285's resume-
    /// gating regression tests need a deterministic, CI-safe way to simulate "the vendor rejected
    /// this turn" -- e.g. a real `claude --resume` of an unestablished id -- without a live CLI).
    /// </summary>
    public const string FailureSentinel = "STUB_FORCE_FAILURE";

    /// <summary>
    /// Sentinel forcing the turn to SUCCEED while writing no output file, printing its answer only
    /// on stdout as a <c>type: result</c> object (#534).
    /// </summary>
    /// <remarks>
    /// This is not a hypothetical. It is what the real <c>claude</c> CLI does on every
    /// directory-less chat session: <see cref="InteractiveSessionMaterializer.DefaultGrantForWorkingDirectory"/>
    /// returns an all-deny grant for a session with no working directory (fail-closed, #321). When
    /// this was measured that grant became <c>--disallowedTools Edit,Write,NotebookEdit,Bash</c>, so
    /// the model genuinely could not write <c>response.md</c>, said so, and exited
    /// <c>is_error: false</c> with the answer in <c>result</c>. Measured identically on claude-opus-5
    /// and claude-haiku-4-5.
    /// <para>
    /// <b>#649 changed the primary path, and this stub deliberately still reproduces the old one.</b>
    /// The write tools now leave <c>--disallowedTools</c> and ride the <c>PreToolUse</c> hook, which
    /// allows a write landing in <c>AER_OUTPUT_DIR</c> — and <c>response.md</c> is addressed there
    /// (<see cref="InteractiveSessionMaterializer.ResponseFileInstruction"/>), so a directory-less
    /// session can now produce the file. What this stub covers is the case where it does not: a
    /// vendor that refuses for its own reasons, a hook that denied, a model that simply answered
    /// without writing. That path must keep working, which is why the stub stays — but it is no
    /// longer what "every directory-less chat session" does.
    /// </para>
    /// <para>
    /// Every pre-existing stub wrote the output file, so no test covered the case the product
    /// actually hits. The sentinel exists to make that case deterministic and CI-safe.
    /// </para>
    /// </remarks>
    public const string NoOutputFileSentinel = "STUB_NO_OUTPUT_FILE";

    /// <summary>
    /// Sentinel forcing an agy turn to SUCCEED while writing no output file, writing a fake agy log
    /// file containing a comma-tailed <c>conversation=&lt;id&gt;,</c> line (#837) to
    /// <see cref="WorkerInvocation.LogFilePath"/> instead (#545).
    /// </summary>
    public const string AgyNoOutputFileSentinel = "STUB_AGY_NO_OUTPUT_FILE";

    /// <summary>The agy conversation id written to the log file by an agy no-output-file turn.</summary>
    public const string StubAgyConversationId = "stub-agy-conv-123";

    /// <summary>
    /// Sentinel forcing an agy turn to exit cleanly while producing absolutely nothing -- no
    /// output file, no log line, nothing <see cref="AgyNoOutputFileSentinel"/> would leave behind.
    /// Distinct from <see cref="FailureSentinel"/> (exit 1): this is a turn that exits 0 but still
    /// genuinely produced no answer and established nothing (#545, found by review).
    /// </summary>
    public const string AgySilentSuccessSentinel = "STUB_AGY_SILENT_SUCCESS";

    /// <summary>The answer text the no-output-file turn puts on stdout, and nowhere else.</summary>
    public const string StdoutOnlyAnswer = "stub answer that only ever reached stdout";

    /// <summary>
    /// Sentinel (issue #1180) forcing a turn to fail (exit 1, like <see cref="FailureSentinel"/>)
    /// while printing a stdout result envelope this adapter's own <see cref="TryClassifyFailure"/>
    /// override recognizes as exhausted plan/quota -- the daemon seam's (Program.cs,
    /// <c>ExecuteSessionTurnCoreAsync</c>) two-tail classifier consultation needs a deterministic,
    /// CI-safe way to reach the <see cref="FailureClassification.ExhaustedUntil"/> branch without a
    /// live vendor CLI, the same way <see cref="FailureSentinel"/> stands in for an ordinary vendor
    /// rejection.
    /// </summary>
    public const string ExhaustionSentinel = "STUB_FORCE_EXHAUSTION";

    /// <summary>The marker <see cref="TryClassifyFailure"/> looks for in the stdout tail to recognize <see cref="ExhaustionSentinel"/>'s payload.</summary>
    private const string ExhaustionMarker = "STUB_EXHAUSTION_MARKER";

    /// <summary>The fixed reset instant a classified <see cref="ExhaustionSentinel"/> turn reports -- known, so tests can assert on it exactly.</summary>
    public static readonly DateTimeOffset ExhaustionResetInstant = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// The payload the no-output-file turn prints. Written once per test run rather than once per
    /// dispatch — the content is constant, and a fresh Guid-named file per dispatch left one small
    /// file behind in <c>%TEMP%</c> for every turn any test in the suite ran.
    /// </summary>
    private static readonly Lazy<string> ResultPayloadFile = new(() =>
    {
        var path = Path.Combine(Path.GetTempPath(), $"aer-stub-result-{Environment.ProcessId}.json");
        File.WriteAllText(path,
            "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"result\":\""
            + StdoutOnlyAnswer + "\"}");
        return path;
    });

    /// <summary>
    /// A script that prints a failed (<c>is_error: true</c>) result envelope carrying
    /// <see cref="ExhaustionMarker"/> in its <c>result</c> text -- shaped like a real vendor's
    /// refusal prose (#1128's "Resets in 1h39m10s" measured live on agy), which is what
    /// <c>TryExtractVendorErrorMessage</c> (Program.cs) reads into the turn's raw
    /// <c>ErrorMessage</c>, and what this adapter's own <see cref="TryClassifyFailure"/> override
    /// then reclassifies as exhausted -- then exits 1, reaching Program.cs's failed-turn branch
    /// exactly like <see cref="FailureSentinel"/> does.
    /// <para>
    /// A SCRIPT file, deliberately, not a payload file combined inline with <c>&amp;</c>/<c>;</c>
    /// the way <see cref="NoOutputFileSentinel"/>'s single-command payload is read: cmd's <c>/c</c>
    /// quote-stripping of a combined "print, then exit 1" command line is exactly the "unusually
    /// finicky" quoting <see cref="NoOutputFileSentinel"/>'s own comment already warns about, and
    /// this needs two effects (a stdout line, then a non-zero exit) rather than that sentinel's one.
    /// A script file removes quoting from the problem entirely, the same fix that comment applied.
    /// </para>
    /// </summary>
    private static readonly Lazy<string> ExhaustionScriptFile = new(() =>
    {
        var payload = "{\"type\":\"result\",\"is_error\":true,\"result\":\"" + ExhaustionMarker + " out of plan\"}";
        var extension = OperatingSystem.IsWindows() ? "cmd" : "sh";
        var path = Path.Combine(Path.GetTempPath(), $"aer-stub-exhaustion-{Environment.ProcessId}.{extension}");
        var script = OperatingSystem.IsWindows()
            ? $"@echo off{Environment.NewLine}echo {payload}{Environment.NewLine}exit /b 1{Environment.NewLine}"
            : $"#!/bin/sh{Environment.NewLine}echo '{payload}'{Environment.NewLine}exit 1{Environment.NewLine}";
        File.WriteAllText(path, script);
        return path;
    });

    public CoreDispatchTarget Resolve(WorkerInvocation invocation, WorkerContract contract)
    {
        var outputName = contract.ProducedOutputs.Count > 0
            ? contract.ProducedOutputs[0].Name
            : InteractiveSessionMaterializer.DefaultOutputFileName;

        if (invocation.PromptTemplate.Contains(FailureSentinel, StringComparison.Ordinal))
        {
            return OperatingSystem.IsWindows()
                ? new CoreDispatchTarget("cmd", ["/c", "exit 1"])
                : new CoreDispatchTarget("sh", ["-c", "exit 1"]);
        }

        if (invocation.PromptTemplate.Contains(NoOutputFileSentinel, StringComparison.Ordinal))
        {
            // Shaped like the real thing: exit 0, `is_error: false`, `subtype: success`, the answer
            // in `result`, and NO output file.
            //
            // The payload is written to a file and printed, rather than passed as a JSON literal on
            // the command line. Quoting a JSON literal differs between cmd and sh, `cmd` does not
            // treat backslash as an escape, and an earlier version of this stub emitted malformed
            // JSON as a result -- which made the stub look exactly like the product defect it is
            // supposed to reproduce. A test double that can fail the same way as the thing under
            // test cannot discriminate, so the quoting is removed from the problem entirely.
            var payload = ResultPayloadFile.Value;
            return OperatingSystem.IsWindows()
                ? new CoreDispatchTarget("cmd", ["/c", "type", payload])
                : new CoreDispatchTarget("sh", ["-c", $"cat \"{payload}\""]);
        }

        if (invocation.PromptTemplate.Contains(AgyNoOutputFileSentinel, StringComparison.Ordinal))
        {
            // Written directly from C#, not via a dispatched shell redirect: an embedded `>` inside
            // a single combined "cmd /c \"...\"" argv element silently produced no file at all --
            // measured, not assumed (this is a test stub simulating agy's log file, not the real
            // CLI, so there is no requirement that a subprocess be the one to write it). This is the
            // same lesson NoOutputFileSentinel's own comment above already recorded for this exact
            // file: cmd's quoting is unusually finicky, and the working fix there was to remove
            // quoting from the problem entirely rather than get the escaping right.
            var logPath = invocation.LogFilePath ?? Path.Combine(Path.GetTempPath(), "agy-log.txt");
            if (Path.GetDirectoryName(logPath) is { } dir && !string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            // #837: match agy's real `--log-file` format (see Program.cs's scrape comment).
            File.WriteAllText(logPath, $"conversation={StubAgyConversationId}, model=stub\n");
            return OperatingSystem.IsWindows()
                ? new CoreDispatchTarget("cmd", ["/c", "exit 0"])
                : new CoreDispatchTarget("sh", ["-c", "exit 0"]);
        }

        if (invocation.PromptTemplate.Contains(ExhaustionSentinel, StringComparison.Ordinal))
        {
            // See ExhaustionScriptFile's own remarks for why this runs a script rather than an
            // inline combined command.
            var script = ExhaustionScriptFile.Value;
            return OperatingSystem.IsWindows()
                ? new CoreDispatchTarget("cmd", ["/c", script])
                : new CoreDispatchTarget("sh", [script]);
        }

        if (invocation.PromptTemplate.Contains(AgySilentSuccessSentinel, StringComparison.Ordinal))
        {
            // Exits 0, writes nothing at all -- no output file, no log line. Reproduces a genuinely
            // failed/no-op agy turn that nonetheless exits cleanly, distinct from FailureSentinel
            // (exit 1, caught earlier as a workflow-level run failure before establishment logic
            // ever runs). This is the case #545's review found: on turn 2+, `vendorSessionId` is
            // already non-null (carried over from an earlier established turn), so a turn producing
            // nothing at all was still wrongly reported as established.
            return OperatingSystem.IsWindows()
                ? new CoreDispatchTarget("cmd", ["/c", "exit 0"])
                : new CoreDispatchTarget("sh", ["-c", "exit 0"]);
        }

        return OperatingSystem.IsWindows()
            ? new CoreDispatchTarget("cmd", ["/c", $"echo stub-turn-response>%AER_OUTPUT_DIR%\\{outputName}"])
            : new CoreDispatchTarget("sh", ["-c", $"echo stub-turn-response > \"$AER_OUTPUT_DIR/{outputName}\""]);
    }

    /// <summary>
    /// How many times <see cref="TryClassifyFailure(string?, string?, TimeProvider, out FailureClassification?, out DateTimeOffset?)"/>
    /// has seen <see cref="ExhaustionMarker"/> for THIS instance -- see that method's remarks for why
    /// this counts calls at all. Instance-scoped, not static: <c>SessionTurnBranchingTests</c>
    /// constructs a fresh <see cref="SessionTurnStubAdapter"/> per test (its <c>InitializeAsync</c>),
    /// so this never leaks between tests.
    /// </summary>
    private int _exhaustionClassifyCalls;

    /// <summary>
    /// The two-tail overload BOTH Program.cs's <c>ExecuteSessionTurnCoreAsync</c> (#1180's daemon
    /// seam) and <c>Aer.Flow.Outcomes.OutcomeClassifier</c> (the already-shipped dispatch path,
    /// #1115/#1119/#1128) consult -- the SAME <see cref="IWorkerAdapter"/> instance is wired as
    /// BOTH the daemon's per-vendor adapter AND, via <c>WorkerBindingResolver</c>, the flow pump's
    /// own <c>WorkerBinding.Process.FailureClassifier</c>, since a chat turn dispatches through the
    /// identical <c>MutationInterface</c> pump every workflow step does.
    /// <para>
    /// <b>Why this returns false on the FIRST call and only classifies on the SECOND:</b> measured,
    /// not assumed -- returning <see cref="FailureClassification.ExhaustedUntil"/> unconditionally
    /// here made <c>RetryEngine.MayRetry</c> bypass the chat step's <c>RetryPolicy(1)</c> entirely
    /// (by 0026 §1's own design: an exhausted quota must never burn retry budget), so the flow pump
    /// auto-parks and re-dispatches this same failing stub forever -- with a FIXED
    /// <see cref="ExhaustionResetInstant"/>, every re-check after the first wait sees a
    /// past deadline and spins with no further wait at all. <c>session.RunAsync</c> never returns,
    /// and #1180's daemon-seam code (which only runs AFTER it returns) is never reached. That
    /// engine-level auto-retry behavior is real, already shipped, and explicitly out of scope here
    /// (see #1180's SCOPE BOUNDARIES) -- it already has its own coverage on the dispatch path, and
    /// this stub does not need to re-exercise it. Returning false on the first (flow-level) call
    /// instead lets the chat step fail terminally after its one allowed attempt, exactly like
    /// <see cref="FailureSentinel"/> does today -- <c>session.RunAsync</c> returns promptly, and
    /// ONLY THEN does the daemon seam's own (second) consultation classify it as exhausted, which is
    /// the one thing this sentinel exists to test.
    /// </para>
    /// </summary>
    public bool TryClassifyFailure(
        string? stderrTail,
        string? stdoutTail,
        TimeProvider timeProvider,
        out FailureClassification? classification,
        out DateTimeOffset? retryNotBefore)
    {
        if (stdoutTail?.Contains(ExhaustionMarker, StringComparison.Ordinal) == true
            && Interlocked.Increment(ref _exhaustionClassifyCalls) >= 2)
        {
            classification = FailureClassification.ExhaustedUntil;
            retryNotBefore = ExhaustionResetInstant;
            return true;
        }

        classification = null;
        retryNotBefore = null;
        return false;
    }
}
