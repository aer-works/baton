using System.Text.Json;

namespace Aer.Cli;

/// <summary>
/// <c>terminal.json</c> (#1356): written into a room directory the moment its workflow reaches a
/// terminal state, so an agent can watch one file instead of polling <c>aer status</c> prose or
/// racing a process exit. A fifth room-identifying filename beside the four #1271 documents as having
/// no canonical home yet — this one gets a single constant from the day it is introduced rather than
/// repeating that drift, but does not attempt #1271's own broader cleanup (a separate, already-filed
/// concern with its own blast radius).
/// </summary>
public static class TerminalSentinelWriter
{
    public const string TerminalSentinelFileName = "terminal.json";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>
    /// Writes <paramref name="view"/> as the room's terminal sentinel. Callers write this <b>last</b> —
    /// after every output an outcome could reference already exists on disk — so a watcher that wakes
    /// on this file's appearance never reads a room mid-write (#1356 point 4).
    /// </summary>
    public static async Task WriteAsync(string roomDirectoryPath, WorkflowStatusView view, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(roomDirectoryPath);
        ArgumentNullException.ThrowIfNull(view);

        Directory.CreateDirectory(roomDirectoryPath);
        var path = Path.Combine(roomDirectoryPath, TerminalSentinelFileName);
        var json = JsonSerializer.Serialize(view, JsonOptions);
        await File.WriteAllTextAsync(path, json, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The pre-ledger case (#1356 point 3): provisioning/validation failed before <c>flow.jsonl</c> —
    /// and sometimes before <c>snapshot.json</c> — ever existed, so there is no <see cref="Aer.Flow.Domain.FlowState"/>
    /// to project a normal <see cref="WorkflowStatusView"/> from. Writes the coarse outcome directly:
    /// <see cref="WorkflowOutcome.Failed"/>, no steps, no outputs, <paramref name="reason"/> as the
    /// error — enough for "aer status on such a room says Failed and why" without inventing per-step
    /// detail for steps that were never reached.
    /// </summary>
    public static Task WriteValidationRefusedAsync(string roomDirectoryPath, string reason, CancellationToken cancellationToken)
    {
        var view = new WorkflowStatusView(WorkflowOutcome.Failed, [], [], reason);
        return WriteAsync(roomDirectoryPath, view, cancellationToken);
    }

    /// <summary>Deletes a stale sentinel from a prior terminal attempt, if any, before a fresh dispatch begins.</summary>
    /// <remarks>
    /// Without this, retrying a room that previously failed pre-ledger (or resuming one that was
    /// already terminal) leaves the old <c>terminal.json</c> in place for the whole duration of the
    /// new, genuinely in-progress attempt — exactly the false "already done" signal a file-watcher
    /// (this file's whole reason to exist) must never see. Best-effort: <see cref="File.Delete"/> is
    /// already a silent no-op when the file is absent.
    /// </remarks>
    public static void DeleteStaleSentinel(string roomDirectoryPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(roomDirectoryPath);
        var path = Path.Combine(roomDirectoryPath, TerminalSentinelFileName);
        File.Delete(path);
    }

    /// <summary>Reads a room's terminal sentinel, or <c>null</c> when the room has not reached one yet.</summary>
    public static async Task<WorkflowStatusView?> TryReadAsync(string roomDirectoryPath, CancellationToken cancellationToken)
    {
        var path = Path.Combine(roomDirectoryPath, TerminalSentinelFileName);
        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<WorkflowStatusView>(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }
}
