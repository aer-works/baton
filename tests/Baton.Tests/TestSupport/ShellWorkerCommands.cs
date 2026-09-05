using Baton.Dispatch;

namespace Baton.Tests.TestSupport;

/// <summary>
/// Tiny <c>cmd</c> <see cref="CoreDispatchTarget"/>s standing in for real workers in integration
/// tests that dispatch through the real, managed <c>BatonTask</c> engine (no mocking of Baton.Core
/// itself, per M7 Phase 7's acceptance criteria). Windows-only, like the product and its CI (#1405).
/// </summary>
internal static class ShellWorkerCommands
{
    public static CoreDispatchTarget WriteFile(string outputName, string content) =>
        new("cmd", ["/c", $"echo {content}>%BATON_OUTPUT_DIR%\\{outputName}"]);

    public static CoreDispatchTarget CopyFirstInputTo(string outputName) =>
        new("cmd", ["/c", $"type %BATON_INPUT_0% >%BATON_OUTPUT_DIR%\\{outputName}"]);

    /// <summary>Concatenates both resolved inputs (declaration order) into one output — the diamond DAG's join step.</summary>
    public static CoreDispatchTarget ConcatBothInputsTo(string outputName) =>
        new("cmd", ["/c", $"copy /b %BATON_INPUT_0%+%BATON_INPUT_1% %BATON_OUTPUT_DIR%\\{outputName}"]);

    public static CoreDispatchTarget ExitCleanlyWithoutWriting() =>
        new("cmd", ["/c", "exit 0"]);

    public static CoreDispatchTarget ExitWithFailureCode(int exitCode = 1) =>
        new("cmd", ["/c", $"exit {exitCode}"]);

    /// <summary>
    /// Sleeps for at least <paramref name="duration"/> before writing <paramref name="outputName"/>
    /// and exiting 0 — M10 Phase 4's real long-running worker, giving a test enough real wall-clock
    /// time to observe it genuinely still executing (via <c>CoreEvent.ExecutionStarted</c>) before
    /// cancelling or otherwise acting on it. Uses <c>ping</c> as the sleep primitive, not
    /// <c>timeout</c>: the latter requires an interactive console on stdin and fails immediately
    /// ("Input redirection is not supported") under a spawned, non-console process — and chained
    /// with <c>&amp;</c> rather than <c>&amp;&amp;</c>, that failure was silently swallowed and the
    /// echo ran anyway, so the worker "succeeded" almost instantly instead of actually sleeping.
    /// <c>ping -n</c> has no such dependency and reliably blocks for approximately one second per
    /// echo request regardless of how the process was spawned.
    /// </summary>
    public static CoreDispatchTarget SleepThenWriteFile(TimeSpan duration, string outputName, string content) =>
        new(
            "cmd",
            ["/c", $"ping -n {(int)duration.TotalSeconds + 1} 127.0.0.1 >nul & echo {content}>%BATON_OUTPUT_DIR%\\{outputName}"]);

    /// <summary>
    /// Fails its first invocation and succeeds every one after, keyed off a marker file at a fixed
    /// path outside <c>BATON_OUTPUT_DIR</c> — each attempt's output directory is fresh by design,
    /// so durable state across attempts has to live somewhere else.
    /// </summary>
    // No quotes around markerFilePath: BatonTask's spawn path assembles each ArgumentList entry
    // through .NET's own Windows quoting rules, which would rewrite an embedded '"' rather than pass
    // it through untouched -- and a GUID-based temp path never contains spaces, so quoting buys
    // nothing here — matches this file's other commands, none of which quote a path either.
    public static CoreDispatchTarget FailOnFirstAttemptThenSucceed(string markerFilePath, string outputName, string content) =>
        new(
            "cmd",
            ["/c", $"if exist {markerFilePath} (echo {content}>%BATON_OUTPUT_DIR%\\{outputName}) else (echo marker>{markerFilePath} & exit 1)"]);

    /// <summary>
    /// Writes <paramref name="body"/> to a script file under <paramref name="scriptDirectory"/> and
    /// returns a target that runs it, instead of inlining the body into a process argument. Two of
    /// the workers below need to emit literal <c>"</c> characters (JSON), which the single-cmd-argument
    /// approach above deliberately avoids (see <see cref="FailOnFirstAttemptThenSucceed"/>'s comment) —
    /// a script file sidesteps that entirely, since its content is written directly via
    /// <see cref="File.WriteAllText(string, string)"/> and never re-parsed as a command line.
    /// </summary>
    private static CoreDispatchTarget FromScript(string scriptDirectory, string body)
    {
        Directory.CreateDirectory(scriptDirectory);
        var scriptPath = Path.Combine(scriptDirectory, $"{Guid.NewGuid():N}.cmd");
        File.WriteAllText(scriptPath, body);

        return new CoreDispatchTarget("cmd", ["/c", scriptPath]);
    }

    /// <summary>
    /// The (now-defunct, F3 #1593 review) bounded self-iteration pattern: writes
    /// <paramref name="verdictFileName"/> with <c>{"status":"needs_revision"}</c> on its first
    /// invocation and <c>{"status":"approved"}</c> on every one after, keyed off a marker file outside
    /// <c>BATON_OUTPUT_DIR</c> — each attempt's output directory is fresh by design, so durable state
    /// across attempts has to live elsewhere, same as <see cref="FailOnFirstAttemptThenSucceed"/>.
    /// Exits 0 both times: only the caller's declared <c>OutputCondition</c> on the produced output
    /// distinguishes the two attempts. F3: the retry this pattern relied on no longer happens — see
    /// spec/baton.md's #1593 register entry for the reasoning. Kept for
    /// <see cref="Baton.Tests.EndToEnd.WorkflowEndToEndTests.An_exit_0_worker_whose_OutputCondition_fails_settles_Indeterminate_and_is_not_retried"/>,
    /// which exercises the FIRST attempt only and never reaches the "approved" branch.
    /// </summary>
    public static CoreDispatchTarget WriteVerdictNeedsRevisionThenApproved(
        string scriptDirectory, string markerFilePath, string verdictFileName)
    {
        var outputPath = $"%BATON_OUTPUT_DIR%\\{verdictFileName}";

        var body = "@echo off\n" +
            $"if exist \"{markerFilePath}\" (\n" +
            $"  echo {{\"status\":\"approved\"}}>\"{outputPath}\"\n" +
            ") else (\n" +
            $"  echo marker>\"{markerFilePath}\"\n" +
            $"  echo {{\"status\":\"needs_revision\"}}>\"{outputPath}\"\n" +
            ")\n";

        return FromScript(scriptDirectory, body);
    }

    /// <summary>
    /// The worker-reported short-circuit: always fails, self-reporting
    /// <see cref="Domain.FailureClassification.Permanent"/> through <paramref name="metadataFileName"/>
    /// regardless of remaining retry budget.
    /// </summary>
    public static CoreDispatchTarget FailPermanently(string scriptDirectory, string metadataFileName)
    {
        var outputPath = $"%BATON_OUTPUT_DIR%\\{metadataFileName}";

        var body = "@echo off\n" +
            $"echo {{\"FailureClassification\":\"Permanent\"}}>\"{outputPath}\"\n" +
            "exit /b 1\n";

        return FromScript(scriptDirectory, body);
    }

    /// <summary>
    /// The supplement convention (<c>BATON_SUPPLEMENTARY_INPUT</c>): copies
    /// <paramref name="supplementaryFileName"/> from the supplementary execution's output directory
    /// to <paramref name="outputName"/> when a <see cref="Domain.DecisionType.RetryWithRevision"/>
    /// consequence attached one; exits non-zero otherwise, standing in for a worker with nothing to
    /// retry against — exercises the retry-vs-decision seam end to end (M9 Phase 5, issue #61).
    /// </summary>
    public static CoreDispatchTarget ConsumeSupplementaryInputElseFail(
        string scriptDirectory, string outputName, string supplementaryFileName)
    {
        var supplementaryPath = $"%BATON_SUPPLEMENTARY_INPUT%\\{supplementaryFileName}";
        var outputPath = $"%BATON_OUTPUT_DIR%\\{outputName}";

        var body = "@echo off\n" +
            "if defined BATON_SUPPLEMENTARY_INPUT (\n" +
            $"  copy /y \"{supplementaryPath}\" \"{outputPath}\" >nul\n" +
            ") else (\n" +
            "  exit /b 1\n" +
            ")\n";

        return FromScript(scriptDirectory, body);
    }

    /// <summary>
    /// Copies <c>BATON_SUPPLEMENTARY_INPUT</c>'s <paramref name="supplementaryFileName"/> to
    /// <paramref name="outputName"/> when present (a <see cref="Domain.DecisionType.Supersede"/>
    /// consequence); otherwise writes <paramref name="baseContent"/>. The architect–critic
    /// loop's Architect: its second run must consume the critic's feedback rather than repeat its
    /// first run's output, so the cascade is observably driven by the supplement, not coincidence.
    /// </summary>
    public static CoreDispatchTarget ConsumeSupplementaryInputElseWrite(
        string scriptDirectory, string outputName, string supplementaryFileName, string baseContent)
    {
        var supplementaryPath = $"%BATON_SUPPLEMENTARY_INPUT%\\{supplementaryFileName}";
        var outputPath = $"%BATON_OUTPUT_DIR%\\{outputName}";

        var body = "@echo off\n" +
            "if defined BATON_SUPPLEMENTARY_INPUT (\n" +
            $"  copy /y \"{supplementaryPath}\" \"{outputPath}\" >nul\n" +
            ") else (\n" +
            $"  echo {baseContent}>\"{outputPath}\"\n" +
            ")\n";

        return FromScript(scriptDirectory, body);
    }

    /// <summary>
    /// #1586 S1 (the #1594 ruling's tripwire): emits a verbatim agy-shaped terminal result line to
    /// stdout — real turns/tokens, so <c>OutcomeClassifier</c>'s usage-parser read finds genuine
    /// evidence — then exits 0 without ever writing the declared output. Standing in for the #1594
    /// shape end to end through the real dispatch pipeline (<c>ExecutionStreamLogger</c>'s own stdout
    /// capture, not a directly-written <c>.stdout.log</c>), so
    /// <see cref="Domain.FlowEvent.ZeroOutputsDespiteSubstantialWork"/>'s wiring in
    /// <c>MutationInterface</c> is proven live, not merely at <c>OutcomeClassifier.Classify</c>'s unit
    /// level (<c>OutcomeClassifierTests</c> already pins that half with a fake usage parser).
    /// </summary>
    public static CoreDispatchTarget EmitSubstantialUsageThenExitWithoutWriting(string scriptDirectory)
    {
        const string resultLine = """{"event":"result","result":{"conversation_id":"test","status":"SUCCESS","response":"did real work","duration_seconds":1.0,"num_turns":4,"usage":{"input_tokens":100,"output_tokens":500,"thinking_tokens":0,"cache_read_tokens":0,"total_tokens":600}}}""";

        var body = "@echo off\n" +
            $"echo {resultLine}\n" +
            "exit /b 0\n";

        return FromScript(scriptDirectory, body);
    }

    /// <summary>
    /// #1709: emits one live claude-shaped usage line — real bytes for a <c>TokenBudgetMonitor</c>
    /// watching the stream to accumulate, the same "assistant" shape the arrest tests above use — then
    /// writes the declared output and exits 0. Standing in for an ordinary Succeeded execution that ran
    /// with a live budget monitor in scope but never crossed it, so
    /// <see cref="Domain.FlowEvent.ExecutionSucceeded.PeakBilledInWindow"/>'s wiring in
    /// <c>MutationInterface</c> is proven through the real dispatch pipeline, not only at the projector.
    /// </summary>
    public static CoreDispatchTarget EmitUsageLineThenWriteFile(
        string scriptDirectory, long cacheCreationInputTokens, string outputName, string content)
    {
        var usageLine = "{\"type\":\"assistant\",\"message\":{\"usage\":{\"input_tokens\":2,"
            + $"\"cache_creation_input_tokens\":{cacheCreationInputTokens},\"cache_read_input_tokens\":0,\"output_tokens\":3"
            + "}}}";
        var outputPath = $"%BATON_OUTPUT_DIR%\\{outputName}";

        var body = "@echo off\n" +
            $"echo {usageLine}\n" +
            $"echo {content}>\"{outputPath}\"\n" +
            "exit /b 0\n";

        return FromScript(scriptDirectory, body);
    }

    /// <summary>
    /// Appends <paramref name="suffix"/> to the first resolved input's content instead of a bare
    /// copy — the architect–critic loop's Critic, so its output visibly differs across reruns and
    /// assertions can tell "fed the new plan back in" apart from "produced the same file again".
    /// </summary>
    public static CoreDispatchTarget AppendSuffixToFirstInput(string scriptDirectory, string outputName, string suffix)
    {
        var outputPath = $"%BATON_OUTPUT_DIR%\\{outputName}";

        var body = "@echo off\n" +
            "set /p content=<%BATON_INPUT_0%\n" +
            $"echo %content%{suffix}>\"{outputPath}\"\n";

        return FromScript(scriptDirectory, body);
    }
}
