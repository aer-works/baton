using Baton.Vendors;
using Baton.Dispatch;
using Baton.Domain;

namespace Baton.Cli.Tests.TestSupport;

/// <summary>
/// A CI-safe stand-in for <c>ClaudeWorkerAdapter</c> (issue #1841): resolves to a shell script that
/// echoes a fixed test-protocol line carrying <paramref name="sessionId"/> to
/// stdout (when non-null) before writing every declared output, so an end-to-end dispatch test can
/// exercise <see cref="DispatchCommand"/>'s bindings.json capture without a live vendor CLI. The
/// real Claude envelope parser is pinned separately by <c>ClaudeSessionIdParsingTests</c>; keeping
/// this protocol shell-neutral lets this fixture test dispatch plumbing on every CI platform.
/// </summary>
internal sealed class SessionIdEmittingWorkerAdapter(
    string? sessionId,
    Action? beforeSessionIdParsed = null,
    string? laterSessionId = null) : IWorkerAdapter
{
    private const string SessionPrefix = "BATON_TEST_SESSION_ID=";

    /// <summary>How many times <see cref="Resolve"/> was called — asserts a retry never re-resolves.</summary>
    public int ResolveCallCount { get; private set; }

    public bool TryParseSessionId(string rawLine, out string? parsedSessionId)
    {
        if (rawLine.StartsWith(SessionPrefix, StringComparison.Ordinal))
        {
            beforeSessionIdParsed?.Invoke();
        }

        parsedSessionId = rawLine.StartsWith(SessionPrefix, StringComparison.Ordinal)
            ? rawLine[SessionPrefix.Length..].TrimEnd()
            : null;
        return parsedSessionId is { Length: > 0 };
    }

    public CoreDispatchTarget Resolve(WorkerInvocation invocation, WorkerContract contract)
    {
        ResolveCallCount++;

        var writeCommands = contract.ProducedOutputs.Count > 0
            ? string.Join(
                OperatingSystem.IsWindows() ? " & " : " && ",
                contract.ProducedOutputs.Select(o => WriteCommand(o.Name)))
            : "exit 0";

        var reportedIds = new[] { sessionId, laterSessionId }.Where(id => id is not null).ToList();
        var echoLine = reportedIds.Count == 0
            ? null
            : string.Join(
                OperatingSystem.IsWindows() ? " & " : "; ",
                reportedIds.Select(id => EchoCommand($"{SessionPrefix}{id}")));

        var script = echoLine is null
            ? writeCommands
            : string.Join(OperatingSystem.IsWindows() ? " & " : "; ", [echoLine, writeCommands]);

        return OperatingSystem.IsWindows()
            ? new CoreDispatchTarget("cmd", ["/c", script], invocation.WorkingDirectory)
            : new CoreDispatchTarget("sh", ["-c", script], invocation.WorkingDirectory);
    }

    private static string EchoCommand(string text) => OperatingSystem.IsWindows()
        ? $"echo {text}"
        : $"echo '{text}'";

    private static string WriteCommand(string outputName) => OperatingSystem.IsWindows()
        ? $"echo x>%BATON_OUTPUT_DIR%\\{outputName}"
        : $"echo x > \"$BATON_OUTPUT_DIR/{outputName}\"";
}
