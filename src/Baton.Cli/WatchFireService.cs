using System.ComponentModel;
using System.Net.Http;
using System.Text.Json;
using Baton.Status;

namespace Baton.Cli;

/// <summary>
/// The one place a <c>baton watch</c> registration is checked against its room and, if terminal,
/// fired — shared by <see cref="WatchCommand"/>'s own immediate check at registration time
/// (spec/baton.md §2's "no lost wake-up" guarantee: an already-terminal room fires without waiting
/// for a sweep) and <c>Baton.Cli.Daemon.WatchSweep</c>'s periodic check of every still-pending watch.
/// One implementation means the two call sites can never drift on what "terminal" means or how
/// firing is claimed.
/// </summary>
public static class WatchFireService
{
    /// <summary>
    /// Terminal detection reuses <see cref="TerminalSentinelWriter.TryReadAsync"/> exactly —
    /// <c>terminal.json</c> present and parseable — never a second definition; spec/baton.md §2 names
    /// every other consumer this same primitive already answers "is this room done" for.
    /// </summary>
    /// <returns><c>true</c> only when this call actually fired the notification (claimed the watch
    /// and sent it). <c>false</c> covers three distinct cases the caller does not need to
    /// distinguish: already fired, room not yet terminal, or another caller won the claim race.</returns>
    public static async Task<bool> TryFireIfTerminalAsync(
        string watchesDirectoryPath,
        WatchRecord watch,
        IWatchNotifier notifier,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(watch);
        ArgumentNullException.ThrowIfNull(notifier);

        if (watch.FiredAt is not null)
        {
            return false;
        }

        var sentinel = await TerminalSentinelWriter
            .TryReadAsync(watch.RoomDirectoryPath, cancellationToken)
            .ConfigureAwait(false);
        if (sentinel is null)
        {
            return false;
        }

        var firedAtUtc = DateTime.UtcNow;

        // Exactly-once: the claim is marked durable BEFORE the notify send below, so a crash in
        // between loses the notification rather than risking two. WatchStore.TryClaimAsync's own
        // per-file lock is what actually prevents a concurrent registration-time check and a daemon
        // sweep iteration from both claiming the same already-terminal watch (spec/baton.md §2).
        var claimed = await WatchStore
            .TryClaimAsync(watchesDirectoryPath, watch.WatchId, firedAtUtc, cancellationToken)
            .ConfigureAwait(false);
        if (!claimed)
        {
            return false;
        }

        var payload = BuildPayload(watch.RoomDirectoryPath, sentinel);
        try
        {
            await notifier.NotifyAsync(watch.NotifyTarget, payload, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or Win32Exception)
        {
            Console.Error.WriteLine(
                $"baton watch: notify failed for watch '{watch.WatchId}' ({watch.NotifyTarget}): {ex.Message}");
        }

        return true;
    }

    /// <summary>One pass over every registered watch — what <c>WatchSweep</c>'s periodic loop calls.
    /// Returns how many watches this pass actually fired.</summary>
    public static async Task<int> SweepAsync(
        string watchesDirectoryPath, IWatchNotifier notifier, CancellationToken cancellationToken)
    {
        var watches = await WatchStore.ListAsync(watchesDirectoryPath, cancellationToken).ConfigureAwait(false);
        var fired = 0;
        foreach (var watch in watches)
        {
            if (await TryFireIfTerminalAsync(watchesDirectoryPath, watch, notifier, cancellationToken)
                .ConfigureAwait(false))
            {
                fired++;
            }
        }

        return fired;
    }

    /// <summary>
    /// <c>{room, state, verdict, outputs, terminalAt}</c> (spec/baton.md §2). <paramref name="sentinel"/>'s
    /// own <c>Outputs</c> — already-resolved output file paths, the same list <c>baton status --json</c>
    /// prints — is searched for a file literally named <c>verdict.json</c>; when one exists and parses,
    /// its content is carried verbatim rather than re-derived, since this service has no opinion on
    /// what a workflow's verdict output means beyond "a file the workflow declared as an output". No
    /// such file is silence, not an error — most workflows have none.
    /// </summary>
    internal static WatchNotifyPayload BuildPayload(string roomDirectoryPath, WorkflowStatusView sentinel)
    {
        var terminalSentinelPath = Path.Combine(roomDirectoryPath, TerminalSentinelWriter.TerminalSentinelFileName);
        var terminalAtUtc = File.Exists(terminalSentinelPath)
            ? File.GetLastWriteTimeUtc(terminalSentinelPath)
            : DateTime.UtcNow;

        JsonElement? verdict = null;
        var verdictOutputPath = sentinel.Outputs.FirstOrDefault(
            p => string.Equals(Path.GetFileName(p), "verdict.json", StringComparison.OrdinalIgnoreCase));
        if (verdictOutputPath is not null && File.Exists(verdictOutputPath))
        {
            try
            {
                verdict = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(verdictOutputPath));
            }
            catch (JsonException)
            {
                // Malformed/partial verdict.json: the payload omits it rather than failing the whole
                // notification over a file this service does not own the writing of.
            }
        }

        return new WatchNotifyPayload(roomDirectoryPath, sentinel.State, verdict, sentinel.Outputs, terminalAtUtc);
    }
}
