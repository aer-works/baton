using System.Diagnostics;
using System.Text.Json;
using Baton.Domain;
using Baton.Outcomes;

namespace Baton.Cli;

/// <summary>Atomic room-side <c>cancel.request</c> file operations.</summary>
public static class CancelRequestFile
{
    public const string FileName = "cancel.request";
    public const string LatestTarget = "latest";
    private static readonly JsonSerializerOptions JsonOptions = new();

    public sealed record Content(
        string Target,
        int? WriterPid = null,
        DateTimeOffset? WriterProcessStartTimeUtc = null,
        DateTimeOffset? WrittenAtUtc = null,
        string? RequestId = null,
        string? RequestedBy = null);

    public sealed record RejectedContent(
        string Target,
        string Reason,
        string? RequestId = null,
        string? ExecutionId = null,
        string? RequestedBy = null,
        DateTimeOffset? RequestedAtUtc = null,
        DateTimeOffset? RejectedAtUtc = null);

    internal sealed record RequestDetails(
        string RequestId,
        string Target,
        string RequestedBy,
        DateTimeOffset RequestedAt);

    public static string GetPath(string roomDirectoryPath) => Path.Combine(roomDirectoryPath, FileName);

    public static async Task WriteAsync(string roomDirectoryPath, string target, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(roomDirectoryPath);
        ArgumentException.ThrowIfNullOrEmpty(target);
        Directory.CreateDirectory(roomDirectoryPath);

        var path = GetPath(roomDirectoryPath);
        var previousLastWriteUtc = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : (DateTime?)null;
        var previous = previousLastWriteUtc is { } ? await TryReadAsync(path, cancellationToken).ConfigureAwait(false) : null;
        var now = DateTimeOffset.UtcNow;
        var content = new Content(
            target,
            Environment.ProcessId,
            new DateTimeOffset(Process.GetCurrentProcess().StartTime).ToUniversalTime(),
            now,
            Guid.NewGuid().ToString("N"),
            "cli");
        var tempPath = Path.Combine(roomDirectoryPath, $"{FileName}.{Guid.NewGuid():N}.tmp");

        await File.WriteAllTextAsync(tempPath, JsonSerializer.Serialize(content, JsonOptions), cancellationToken).ConfigureAwait(false);
        File.Move(tempPath, path, overwrite: true);

        if (previous is not null && previousLastWriteUtc is { } previousWrite)
        {
            var prior = Describe(previous, previousWrite);
            await ArrestLedger.RecordExpiredAsync(
                    roomDirectoryPath,
                    prior.RequestId,
                    prior.Target,
                    prior.RequestedBy,
                    prior.RequestedAt,
                    executionId: null,
                    "superseded by a newer cancel.request",
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var details = Describe(content, File.GetLastWriteTimeUtc(path));
        await ArrestLedger.RecordRequestedAsync(
                roomDirectoryPath,
                details.RequestId,
                details.Target,
                details.RequestedBy,
                details.RequestedAt,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public static async Task<Content?> TryReadAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var content = await JsonSerializer.DeserializeAsync<Content>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
            return content is null || string.IsNullOrWhiteSpace(content.Target) ? null : content;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static async Task<RejectedContent?> TryReadRejectedAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return await JsonSerializer.DeserializeAsync<RejectedContent>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static RequestDetails Describe(Content content, DateTime lastWriteUtc) =>
        new(
            string.IsNullOrWhiteSpace(content.RequestId) ? $"legacy-{lastWriteUtc.Ticks:x}" : content.RequestId,
            content.Target,
            string.IsNullOrWhiteSpace(content.RequestedBy) ? "cli" : content.RequestedBy,
            content.WrittenAtUtc ?? new DateTimeOffset(lastWriteUtc, TimeSpan.Zero));

    internal static RequestDetails DescribeMalformed(DateTime lastWriteUtc) =>
        new($"malformed-{lastWriteUtc.Ticks:x}", "(malformed)", "unknown", new DateTimeOffset(lastWriteUtc, TimeSpan.Zero));

    public static void Consume(string path) => RenameBestEffort(path, $"{path}.consumed");

    internal static async Task ConsumeDeliveredAsync(
        string roomDirectoryPath,
        string path,
        RequestDetails request,
        ExecutionId executionId,
        CancellationToken cancellationToken)
    {
        await ArrestLedger.RecordDeliveredAsync(
                roomDirectoryPath,
                request.RequestId,
                request.Target,
                request.RequestedBy,
                request.RequestedAt,
                executionId,
                cancellationToken)
            .ConfigureAwait(false);
        Consume(path);
    }

    internal static async Task ConsumeExpiredAsync(
        string roomDirectoryPath,
        string path,
        RequestDetails request,
        ExecutionId? executionId,
        string reason,
        CancellationToken cancellationToken)
    {
        await ArrestLedger.RecordExpiredAsync(
                roomDirectoryPath,
                request.RequestId,
                request.Target,
                request.RequestedBy,
                request.RequestedAt,
                executionId,
                reason,
                cancellationToken)
            .ConfigureAwait(false);
        Consume(path);
    }

    public static async Task DeleteStalePendingRequestAsync(
        string roomDirectoryPath,
        DateTimeOffset invocationStartUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(roomDirectoryPath);
        var path = GetPath(roomDirectoryPath);
        if (!File.Exists(path))
        {
            return;
        }

        var lastWriteUtc = File.GetLastWriteTimeUtc(path);
        var content = await TryReadAsync(path, cancellationToken).ConfigureAwait(false);
        if (content is not { WrittenAtUtc: { } writtenAtUtc, WriterPid: { } writerPid })
        {
            var malformed = content is null ? DescribeMalformed(lastWriteUtc) : Describe(content, lastWriteUtc);
            await ArrestLedger.RecordExpiredAsync(
                    roomDirectoryPath,
                    malformed.RequestId,
                    malformed.Target,
                    malformed.RequestedBy,
                    malformed.RequestedAt,
                    executionId: null,
                    "stale pending cancel.request was swept at pump start",
                    cancellationToken)
                .ConfigureAwait(false);
            RenameBestEffort(path, $"{path}.swept");
            return;
        }

        var deadWriter = EngineLivenessProbe.Probe(writerPid, content.WriterProcessStartTimeUtc).Status == EngineLivenessStatus.Dead;
        if (writtenAtUtc < invocationStartUtc && deadWriter)
        {
            var request = Describe(content, lastWriteUtc);
            await ArrestLedger.RecordExpiredAsync(
                    roomDirectoryPath,
                    request.RequestId,
                    request.Target,
                    request.RequestedBy,
                    request.RequestedAt,
                    executionId: null,
                    "stale pending cancel.request was swept at pump start",
                    cancellationToken)
                .ConfigureAwait(false);
            RenameBestEffort(path, $"{path}.swept");
        }
    }

    public static void Reject(string path, string? target, string reason) =>
        Reject(path, target, reason, requestId: null, executionId: null, requestedBy: null, requestedAtUtc: null);

    public static void Reject(string path, string reason) => Reject(path, null, reason);

    internal static async Task RejectAsync(
        string roomDirectoryPath,
        string path,
        RequestDetails request,
        ExecutionId? executionId,
        string reason,
        CancellationToken cancellationToken)
    {
        await ArrestLedger.RecordRejectedAsync(
                roomDirectoryPath,
                request.RequestId,
                request.Target,
                request.RequestedBy,
                request.RequestedAt,
                executionId,
                reason,
                cancellationToken)
            .ConfigureAwait(false);
        Reject(path, request.Target, reason, request.RequestId, executionId?.Value, request.RequestedBy, request.RequestedAt);
    }

    private static void Reject(
        string path,
        string? target,
        string reason,
        string? requestId,
        string? executionId,
        string? requestedBy,
        DateTimeOffset? requestedAtUtc)
    {
        try
        {
            Console.Error.WriteLine($"cancel.request at '{path}' rejected: {reason}");
            var roomDirectory = Path.GetDirectoryName(path) ?? string.Empty;
            var tempPath = Path.Combine(roomDirectory, $"{FileName}.{Guid.NewGuid():N}.rejected.tmp");
            var rejectedPath = $"{path}.rejected";
            var body = new RejectedContent(target ?? string.Empty, reason, requestId, executionId, requestedBy, requestedAtUtc, DateTimeOffset.UtcNow);
            File.WriteAllText(tempPath, JsonSerializer.Serialize(body, JsonOptions));
            File.Move(tempPath, rejectedPath, overwrite: true);
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try
            {
                Console.Error.WriteLine($"Could not write rejected cancel.request: {ex.Message}");
            }
            catch
            {
            }
        }
    }

    private static void RenameBestEffort(string path, string destinationPath)
    {
        try
        {
            File.Move(path, destinationPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try
            {
                Console.Error.WriteLine($"Could not rename '{path}' to '{destinationPath}': {ex.Message}");
            }
            catch
            {
            }
        }
    }
}