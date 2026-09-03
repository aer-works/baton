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
/// <see cref="RunCommand.EchoStreamJsonLine"/>, exactly like <c>--echo-worker</c> does; a non-JSON
/// line is echoed as-is. Either way, the WHOLE rendered result is passed through
/// <see cref="StatusCommand.EscapeNonPrintable"/> before it reaches <paramref name="writer"/> --
/// escaping only the non-JSON fallback branch (this method's pre-fix shape) missed the case where a
/// JSON line's adapter-recognized <c>text</c>/<c>tool</c>/<c>status</c>/<c>result</c> cases in
/// <see cref="RunCommand.EchoStreamJsonLine"/> write a JSON-<em>decoded</em> string: a JSON string
/// escape for a control character decodes back to the literal control byte, so that branch reached
/// the writer unescaped (the review's high finding). Both call sites get this same escaped text:
/// <c>baton status --follow</c>'s real terminal writer needs it to keep a worker's raw control bytes
/// off the operator's terminal (the #1525/#1550 invariant); <c>room_detail</c>'s <see cref="StringWriter"/>
/// (later serialized as a JSON string value by <see cref="System.Text.Json"/>, which would re-escape
/// any surviving control byte on its own) does not strictly need this pass, but applying the same
/// escape uniformly means one rule instead of a second, surface-specific one -- and is a no-op there,
/// since no control byte survives this pass to be escaped a second time.
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

        var buffer = new StringWriter { NewLine = "\n" };
        if (IsJson(line))
        {
            RunCommand.EchoStreamJsonLine(line, adapter, buffer);
        }
        else
        {
            buffer.WriteLine(line);
        }

        writer.Write(StatusCommand.EscapeNonPrintable(Encoding.UTF8.GetBytes(buffer.ToString())));
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
