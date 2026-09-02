using Baton.Dispatch;

namespace Baton.Tests.TestSupport;

/// <summary>
/// Tiny cross-platform shell <see cref="CoreDispatchTarget"/>s standing in for real workers in
/// integration tests that dispatch through the real, managed <c>BatonTask</c> engine (no mocking of
/// Baton.Core itself, per M7 Phase 7's acceptance criteria).
/// </summary>
internal static class ShellWorkerCommands
{
    public static CoreDispatchTarget WriteFile(string outputName, string content) => OperatingSystem.IsWindows()
        ? new CoreDispatchTarget("cmd", ["/c", $"echo {content}>%BATON_OUTPUT_DIR%\\{outputName}"])
        : new CoreDispatchTarget("sh", ["-c", $"echo {content} > \"$BATON_OUTPUT_DIR/{outputName}\""]);

    public static CoreDispatchTarget CopyFirstInputTo(string outputName) => OperatingSystem.IsWindows()
        ? new CoreDispatchTarget("cmd", ["/c", $"type %BATON_INPUT_0% >%BATON_OUTPUT_DIR%\\{outputName}"])
        : new CoreDispatchTarget("sh", ["-c", $"cat \"$BATON_INPUT_0\" > \"$BATON_OUTPUT_DIR/{outputName}\""]);

    /// <summary>Concatenates both resolved inputs (declaration order) into one output — the diamond DAG's join step.</summary>
    public static CoreDispatchTarget ConcatBothInputsTo(string outputName) => OperatingSystem.IsWindows()
        ? new CoreDispatchTarget("cmd", ["/c", $"copy /b %BATON_INPUT_0%+%BATON_INPUT_1% %BATON_OUTPUT_DIR%\\{outputName}"])
        : new CoreDispatchTarget("sh", ["-c", $"cat \"$BATON_INPUT_0\" \"$BATON_INPUT_1\" > \"$BATON_OUTPUT_DIR/{outputName}\""]);

    public static CoreDispatchTarget ExitCleanlyWithoutWriting() => OperatingSystem.IsWindows()
        ? new CoreDispatchTarget("cmd", ["/c", "exit 0"])
        : new CoreDispatchTarget("sh", ["-c", "exit 0"]);

    public static CoreDispatchTarget ExitWithFailureCode(int exitCode = 1) => OperatingSystem.IsWindows()
        ? new CoreDispatchTarget("cmd", ["/c", $"exit {exitCode}"])
        : new CoreDispatchTarget("sh", ["-c", $"exit {exitCode}"]);

    /// <summary>
    /// Sleeps for at least <paramref name="duration"/> before writing <paramref name="outputName"/>
    /// and exiting 0 — M10 Phase 4's real long-running worker, giving a test enough real wall-clock
    /// time to observe it genuinely still executing (via <c>CoreEvent.ExecutionStarted</c>) before
    /// cancelling or otherwise acting on it. Windows uses <c>ping</c> as the sleep primitive, not
    /// <c>timeout</c>: the latter requires an interactive console on stdin and fails immediately
    /// ("Input redirection is not supported") under a spawned, non-console process — and chained
    /// with <c>&amp;</c> rather than <c>&amp;&amp;</c>, that failure was silently swallowed and the
    /// echo ran anyway, so the worker "succeeded" almost instantly instead of actually sleeping.
    /// <c>ping -n</c> has no such dependency and reliably blocks for approximately one second per
    /// echo request regardless of how the process was spawned.
    /// </summary>
    public static CoreDispatchTarget SleepThenWriteFile(TimeSpan duration, string outputName, string content) => OperatingSystem.IsWindows()
        ? new CoreDispatchTarget(
            "cmd",
            ["/c", $"ping -n {(int)duration.TotalSeconds + 1} 127.0.0.1 >nul & echo {content}>%BATON_OUTPUT_DIR%\\{outputName}"])
        : new CoreDispatchTarget("sh", ["-c", $"sleep {duration.TotalSeconds} && echo {content} > \"$BATON_OUTPUT_DIR/{outputName}\""]);

    /// <summary>
    /// Fails its first invocation and succeeds every one after, keyed off a marker file at a fixed
    /// path outside <c>BATON_OUTPUT_DIR</c> — each attempt's output directory is fresh by design,
    /// so durable state across attempts has to live somewhere else.
    /// </summary>
    public static CoreDispatchTarget FailOnFirstAttemptThenSucceed(string markerFilePath, string outputName, string content) => OperatingSystem.IsWindows()
        ? new CoreDispatchTarget(
            // No quotes around markerFilePath: BatonTask's spawn path assembles each ArgumentList
            // entry through .NET's own Windows quoting rules, which would rewrite an embedded '"'
            // rather than pass it through untouched -- and a GUID-based temp path never contains
            // spaces, so quoting buys nothing here — matches
            // this file's other Windows commands, none of which quote a path either.
            "cmd",
            ["/c", $"if exist {markerFilePath} (echo {content}>%BATON_OUTPUT_DIR%\\{outputName}) else (echo marker>{markerFilePath} & exit 1)"])
        : new CoreDispatchTarget(
            "sh",
            ["-c", $"if [ -f \"{markerFilePath}\" ]; then echo {content} > \"$BATON_OUTPUT_DIR/{outputName}\"; else touch \"{markerFilePath}\"; exit 1; fi"]);

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
        var extension = OperatingSystem.IsWindows() ? ".cmd" : ".sh";
        var scriptPath = Path.Combine(scriptDirectory, $"{Guid.NewGuid():N}{extension}");
        File.WriteAllText(scriptPath, body);

        return OperatingSystem.IsWindows()
            ? new CoreDispatchTarget("cmd", ["/c", scriptPath])
            : new CoreDispatchTarget("sh", [scriptPath]);
    }

    /// <summary>
    /// The bounded self-iteration pattern: writes <paramref name="verdictFileName"/> with
    /// <c>{"status":"needs_revision"}</c> on its first invocation and <c>{"status":"approved"}</c>
    /// on every one after, keyed off a marker file outside <c>BATON_OUTPUT_DIR</c> — each attempt's
    /// output directory is fresh by design, so durable state across attempts has to live
    /// elsewhere, same as <see cref="FailOnFirstAttemptThenSucceed"/>. Exits 0 both times: only the
    /// caller's declared <c>OutputCondition</c> on the produced output distinguishes the two attempts.
    /// </summary>
    public static CoreDispatchTarget WriteVerdictNeedsRevisionThenApproved(
        string scriptDirectory, string markerFilePath, string verdictFileName)
    {
        var outputPath = OperatingSystem.IsWindows()
            ? $"%BATON_OUTPUT_DIR%\\{verdictFileName}"
            : $"$BATON_OUTPUT_DIR/{verdictFileName}";

        var body = OperatingSystem.IsWindows()
            ? "@echo off\n" +
              $"if exist \"{markerFilePath}\" (\n" +
              $"  echo {{\"status\":\"approved\"}}>\"{outputPath}\"\n" +
              ") else (\n" +
              $"  echo marker>\"{markerFilePath}\"\n" +
              $"  echo {{\"status\":\"needs_revision\"}}>\"{outputPath}\"\n" +
              "  exit /b 1\n" +
              ")\n"
            : "#!/bin/sh\n" +
              $"if [ -f \"{markerFilePath}\" ]; then\n" +
              $"  echo '{{\"status\":\"approved\"}}' > \"{outputPath}\"\n" +
              "else\n" +
              $"  touch \"{markerFilePath}\"\n" +
              $"  echo '{{\"status\":\"needs_revision\"}}' > \"{outputPath}\"\n" +
              "  exit 1\n" +
              "fi\n";

        return FromScript(scriptDirectory, body);
    }

    /// <summary>
    /// The worker-reported short-circuit: always fails, self-reporting
    /// <see cref="Domain.FailureClassification.Permanent"/> through <paramref name="metadataFileName"/>
    /// regardless of remaining retry budget.
    /// </summary>
    public static CoreDispatchTarget FailPermanently(string scriptDirectory, string metadataFileName)
    {
        var outputPath = OperatingSystem.IsWindows()
            ? $"%BATON_OUTPUT_DIR%\\{metadataFileName}"
            : $"$BATON_OUTPUT_DIR/{metadataFileName}";

        var body = OperatingSystem.IsWindows()
            ? "@echo off\n" +
              $"echo {{\"FailureClassification\":\"Permanent\"}}>\"{outputPath}\"\n" +
              "exit /b 1\n"
            : "#!/bin/sh\n" +
              $"echo '{{\"FailureClassification\":\"Permanent\"}}' > \"{outputPath}\"\n" +
              "exit 1\n";

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
        var supplementaryPath = OperatingSystem.IsWindows()
            ? $"%BATON_SUPPLEMENTARY_INPUT%\\{supplementaryFileName}"
            : $"$BATON_SUPPLEMENTARY_INPUT/{supplementaryFileName}";
        var outputPath = OperatingSystem.IsWindows()
            ? $"%BATON_OUTPUT_DIR%\\{outputName}"
            : $"$BATON_OUTPUT_DIR/{outputName}";

        var body = OperatingSystem.IsWindows()
            ? "@echo off\n" +
              "if defined BATON_SUPPLEMENTARY_INPUT (\n" +
              $"  copy /y \"{supplementaryPath}\" \"{outputPath}\" >nul\n" +
              ") else (\n" +
              "  exit /b 1\n" +
              ")\n"
            : "#!/bin/sh\n" +
              "if [ -n \"$BATON_SUPPLEMENTARY_INPUT\" ]; then\n" +
              $"  cp \"{supplementaryPath}\" \"{outputPath}\"\n" +
              "else\n" +
              "  exit 1\n" +
              "fi\n";

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
        var supplementaryPath = OperatingSystem.IsWindows()
            ? $"%BATON_SUPPLEMENTARY_INPUT%\\{supplementaryFileName}"
            : $"$BATON_SUPPLEMENTARY_INPUT/{supplementaryFileName}";
        var outputPath = OperatingSystem.IsWindows()
            ? $"%BATON_OUTPUT_DIR%\\{outputName}"
            : $"$BATON_OUTPUT_DIR/{outputName}";

        var body = OperatingSystem.IsWindows()
            ? "@echo off\n" +
              "if defined BATON_SUPPLEMENTARY_INPUT (\n" +
              $"  copy /y \"{supplementaryPath}\" \"{outputPath}\" >nul\n" +
              ") else (\n" +
              $"  echo {baseContent}>\"{outputPath}\"\n" +
              ")\n"
            : "#!/bin/sh\n" +
              "if [ -n \"$BATON_SUPPLEMENTARY_INPUT\" ]; then\n" +
              $"  cp \"{supplementaryPath}\" \"{outputPath}\"\n" +
              "else\n" +
              $"  echo {baseContent} > \"{outputPath}\"\n" +
              "fi\n";

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

        var body = OperatingSystem.IsWindows()
            ? "@echo off\n" +
              $"echo {resultLine}\n" +
              "exit /b 0\n"
            : "#!/bin/sh\n" +
              $"echo '{resultLine}'\n" +
              "exit 0\n";

        return FromScript(scriptDirectory, body);
    }

    /// <summary>
    /// Appends <paramref name="suffix"/> to the first resolved input's content instead of a bare
    /// copy — the architect–critic loop's Critic, so its output visibly differs across reruns and
    /// assertions can tell "fed the new plan back in" apart from "produced the same file again".
    /// </summary>
    public static CoreDispatchTarget AppendSuffixToFirstInput(string scriptDirectory, string outputName, string suffix)
    {
        var outputPath = OperatingSystem.IsWindows()
            ? $"%BATON_OUTPUT_DIR%\\{outputName}"
            : $"$BATON_OUTPUT_DIR/{outputName}";

        var body = OperatingSystem.IsWindows()
            ? "@echo off\n" +
              "set /p content=<%BATON_INPUT_0%\n" +
              $"echo %content%{suffix}>\"{outputPath}\"\n"
            : "#!/bin/sh\n" +
              "content=$(cat \"$BATON_INPUT_0\")\n" +
              $"echo \"${{content}}{suffix}\" > \"{outputPath}\"\n";

        return FromScript(scriptDirectory, body);
    }
}
