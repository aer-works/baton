using System.Text;
using System.Text.Json;
using Baton.Domain;
using Baton.Vendors;

namespace Baton.Cli;

/// <summary>
/// Resolves the <see cref="IWorkerAdapter"/> that produced a given execution's stdout, from the
/// room's already-loaded <c>bindings.json</c> plus <c>flow.jsonl</c>'s own record of which worker (and,
/// when a rebind overrode it, which adapter) each execution actually ran under -- issue #1574's
/// architectural constraint 1. Mirrors <see cref="Baton.Status.ExecutionUsageProjector"/>'s own
/// recorded-adapter-wins-over-binding priority rather than sharing it: that type lives in
/// <c>Baton</c>, which Architecture Rule 2 forbids from depending on <c>Baton.Vendors</c>'s
/// <see cref="IWorkerAdapter"/>.
/// </summary>
internal static class RoomAdapterLookup
{
    private const string BindingsFileName = "bindings.json";

    private static readonly IReadOnlyDictionary<string, WorkerBindingConfigEntry> EmptyBindings =
        new Dictionary<string, WorkerBindingConfigEntry>(StringComparer.Ordinal);

    /// <summary>
    /// Fail open on rendering (this is display only): a missing, unreadable, or malformed
    /// <c>bindings.json</c> resolves every execution's adapter to null, which routes through
    /// <see cref="RunCommand.EchoStreamJsonLine"/>'s own "no adapter" fallback -- raw JSON
    /// passthrough, never a throw.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, WorkerBindingConfigEntry>> TryLoadBindingsAsync(
        string roomDirectoryPath, CancellationToken cancellationToken)
    {
        var bindingsPath = Path.Combine(roomDirectoryPath, BindingsFileName);
        if (!File.Exists(bindingsPath))
        {
            return EmptyBindings;
        }

        try
        {
            return await WorkerBindingConfigParser.LoadFromFileAsync(bindingsPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WorkerBindingConfigException or IOException or UnauthorizedAccessException)
        {
            return EmptyBindings;
        }
    }

    /// <summary>
    /// Execution id -&gt; adapter name. A rebind's <see cref="FlowEvent.StepRebound.NewAdapter"/>
    /// overrides <paramref name="bindings"/>'s worker-&gt;adapter entry, same priority
    /// <see cref="Baton.Status.ExecutionUsageProjector"/> uses for usage figures.
    /// </summary>
    public static IReadOnlyDictionary<string, string> BuildAdapterNameByExecutionId(
        IReadOnlyList<FlowEvent> events, IReadOnlyDictionary<string, WorkerBindingConfigEntry> bindings)
    {
        var workerByExecution = new Dictionary<string, string>(StringComparer.Ordinal);
        var recordedAdapterByExecution = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var @event in events)
        {
            switch (@event)
            {
                case FlowEvent.ExecutionRequestAccepted accepted:
                    workerByExecution[accepted.Request.ExecutionId.Value] = accepted.Request.Worker;
                    if (accepted.Request.Adapter is { Length: > 0 } recordedAdapter)
                    {
                        recordedAdapterByExecution[accepted.Request.ExecutionId.Value] = recordedAdapter;
                    }

                    break;
                case FlowEvent.StepRebound rebound:
                    if (rebound.NewAdapter is { Length: > 0 } newAdapter)
                    {
                        recordedAdapterByExecution[rebound.ForExecutionId.Value] = newAdapter;
                    }
                    else
                    {
                        recordedAdapterByExecution.Remove(rebound.ForExecutionId.Value);
                    }

                    break;
            }
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (executionId, workerName) in workerByExecution)
        {
            if (recordedAdapterByExecution.TryGetValue(executionId, out var recordedAdapter))
            {
                result[executionId] = recordedAdapter;
            }
            else if (bindings.TryGetValue(workerName, out var entry))
            {
                result[executionId] = entry.Adapter;
            }
        }

        return result;
    }

    public static IWorkerAdapter? ResolveAdapter(
        string executionId,
        IReadOnlyDictionary<string, string> adapterNameByExecutionId,
        IReadOnlyDictionary<string, IWorkerAdapter> adapters) =>
        adapterNameByExecutionId.TryGetValue(executionId, out var adapterName)
            && adapters.TryGetValue(adapterName, out var adapter)
            ? adapter
            : null;
}

/// <summary>
/// Renders one complete worker-stdout line (#1574): a valid-JSON line routes through
/// <see cref="RunCommand.EchoStreamJsonLine"/>, exactly like <c>--echo-worker</c> does. A non-JSON
/// line keeps <see cref="StatusCommand.EscapeNonPrintable"/>'s control-character safety net instead
/// of <see cref="RunCommand.EchoStreamJsonLine"/>'s own "echo verbatim" fallback -- <c>baton status
/// --follow</c> and <c>room_detail</c> have relied on that escaping since #1525/#1550 (a worker's raw
/// ANSI/binary output must never reach a terminal, or an MCP client's JSON string, unescaped).
/// </summary>
internal static class WorkerStreamLineRenderer
{
    public static void RenderLine(string line, IWorkerAdapter? adapter, TextWriter writer)
    {
        if (line.Length == 0)
        {
            writer.WriteLine();
            return;
        }

        if (IsJson(line))
        {
            RunCommand.EchoStreamJsonLine(line, adapter, writer);
        }
        else
        {
            writer.WriteLine(StatusCommand.EscapeNonPrintable(Encoding.UTF8.GetBytes(line)));
        }
    }

    private static bool IsJson(string line)
    {
        try
        {
            using var _ = JsonDocument.Parse(line);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
