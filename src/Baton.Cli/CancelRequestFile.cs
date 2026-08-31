using System.Text.Json;

namespace Baton.Cli;

/// <summary>
/// <c>cancel.request</c> (#1495): the out-of-band arrest channel a live <c>baton run</c> pump polls
/// without ever touching <c>flow.lock</c> — the whole point, since the pump already holds that guard
/// for its entire duration (<see cref="Baton.Concurrency.ConcurrencyGuard.Acquire"/>), which is exactly
/// what makes <see cref="MutationInterface.RequestCancellationAsync"/> unreachable from a second
/// process. <see cref="CancelCommand"/> writes this file when it catches
/// <see cref="Baton.Concurrency.WorkflowLockedException"/> from that path; <see cref="CancelRequestPoller"/>
/// is the pump-side reader.
/// </summary>
/// <remarks>
/// Same atomic-write discipline as <see cref="Baton.Status.TerminalSentinelWriter"/>: serialize to a
/// per-call-GUID <c>.tmp</c> sibling, then <see cref="File.Move(string, string, bool)"/> into place, so
/// a poller mid-tick never observes a torn write. Consumed by renaming to <c>.consumed</c> (a settled
/// request, delivered or a too-late no-op) or <c>.rejected</c> (malformed content) rather than
/// deleting outright — either rename lets a second, later <c>cancel.request</c> write land clean, and
/// leaves the acted-on one on disk for a bystander to inspect.
/// </remarks>
public static class CancelRequestFile
{
    public const string FileName = "cancel.request";

    /// <summary>The literal <see cref="Content.Target"/> meaning "whichever single execution is Running right now" — resolved at poll time by <see cref="RunningExecutionResolver"/>, not at write time.</summary>
    public const string LatestTarget = "latest";

    private static readonly JsonSerializerOptions JsonOptions = new();

    /// <param name="Target">Either <see cref="LatestTarget"/> or an explicit <c>ExecutionId</c> value.</param>
    public sealed record Content(string Target);

    public static string GetPath(string roomDirectoryPath) => Path.Combine(roomDirectoryPath, FileName);

    /// <summary>Atomic write (temp + rename) of a fresh request. Overwrites any prior file at this path.</summary>
    public static async Task WriteAsync(string roomDirectoryPath, string target, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(roomDirectoryPath);
        ArgumentException.ThrowIfNullOrEmpty(target);

        Directory.CreateDirectory(roomDirectoryPath);
        var path = GetPath(roomDirectoryPath);
        var tempPath = Path.Combine(roomDirectoryPath, $"{FileName}.{Guid.NewGuid():N}.tmp");
        var json = JsonSerializer.Serialize(new Content(target), JsonOptions);
        await File.WriteAllTextAsync(tempPath, json, cancellationToken).ConfigureAwait(false);
        File.Move(tempPath, path, overwrite: true);
    }

    /// <summary>
    /// Reads and parses the request file at <paramref name="path"/>. Returns <c>null</c> for anything
    /// malformed — invalid JSON, no <c>Target</c> field, or a blank one — fail closed: a caller sees
    /// "no valid request", never an exception, so a malformed file can never crash the pump's poll loop.
    /// </summary>
    public static async Task<Content?> TryReadAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var content = await JsonSerializer.DeserializeAsync<Content>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            return content is null || string.IsNullOrWhiteSpace(content.Target) ? null : content;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Marks a request as settled (delivered, or a too-late no-op) so a fresh write is expressible.</summary>
    public static void Consume(string path) => RenameBestEffort(path, $"{path}.consumed");

    /// <summary>
    /// Best-effort delete of any PENDING request left over from a prior pump (#1495 review finding 5):
    /// the file carries no timestamp/pid/generation, and a crash-recovery resubmission
    /// (<c>ProcessCrashRecoveryDetector</c>) can re-dispatch a step under the SAME <c>ExecutionId</c> a
    /// stale request already named — letting that request survive into the fresh pump risks arresting
    /// the resubmission instead of whatever it was actually asking to cancel. Called once, at pump
    /// start, before this pump's own <see cref="CancelRequestPoller"/> begins — never touches an
    /// already-settled <c>.consumed</c>/<c>.rejected</c> sibling, which is historical record, not a
    /// pending request.
    /// </summary>
    public static void DeleteStalePendingRequest(string roomDirectoryPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(roomDirectoryPath);
        try
        {
            File.Delete(GetPath(roomDirectoryPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Could not delete a stale cancel.request under '{roomDirectoryPath}': {ex.Message}");
        }
    }

    /// <summary>Fail-closed outcome for malformed content: logs why, then gets it out of the poller's way.</summary>
    public static void Reject(string path, string reason)
    {
        Console.Error.WriteLine($"cancel.request at '{path}' rejected: {reason}");
        RenameBestEffort(path, $"{path}.rejected");
    }

    private static void RenameBestEffort(string path, string destinationPath)
    {
        try
        {
            File.Move(path, destinationPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort, same doctrine as TerminalSentinelWriter.DeleteStaleSentinel: a rename that
            // cannot land (the file vanished, or is transiently held) must not crash the poll loop —
            // the worst case is this same request being read again next tick.
            Console.Error.WriteLine($"Could not rename '{path}' to '{destinationPath}': {ex.Message}");
        }
    }
}
