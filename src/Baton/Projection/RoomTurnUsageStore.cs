using System.Text.Json;
using System.Text.Json.Serialization;
using Baton.Store;

namespace Baton.Projection;

/// <summary>
/// Persistence store for engine usage counters under <c>{room}/.baton/turn-usage.json</c> (#778).
/// </summary>
public static class RoomTurnUsageStore
{
    private const string BatonDirectoryName = ".baton";
    private const string UsageFileName = "turn-usage.json";

    public static string GetUsageFilePath(string roomDirectoryPath)
        => Path.Combine(roomDirectoryPath, BatonDirectoryName, UsageFileName);

    // Defaults for the same reason ThrottleDto's parameters carry them: under
    // RespectRequiredConstructorParameters a default-less positional parameter is required, and
    // an older or partial file must per-field-default, not throw.
    private sealed record UsageDto(
        [property: JsonPropertyName("recentMachineTurnTimestamps")] List<DateTimeOffset>? RecentMachineTurnTimestamps = null,
        [property: JsonPropertyName("lastMachineTurnAt")] DateTimeOffset? LastMachineTurnAt = null,
        [property: JsonPropertyName("consecutiveFailedTurns")] int? ConsecutiveFailedTurns = null);

    private static readonly JsonSerializerOptions ReadOptions = new(FlowEventLogJson.Options)
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Reads usage fresh from <c>{room}/.baton/turn-usage.json</c>.
    /// Missing file → returns <see cref="RoomTurnUsage.Empty"/> silently.
    /// Present-but-invalid (corrupt JSON or negative consecutive failures) → LOUD stderr message + returns <see cref="RoomTurnUsage.Empty"/>.
    /// </summary>
    public static RoomTurnUsage Load(string roomDirectoryPath)
    {
        if (string.IsNullOrEmpty(roomDirectoryPath))
        {
            return RoomTurnUsage.Empty;
        }

        var filePath = GetUsageFilePath(roomDirectoryPath);
        if (!File.Exists(filePath))
        {
            return RoomTurnUsage.Empty;
        }

        try
        {
            var json = File.ReadAllText(filePath);
            var dto = JsonSerializer.Deserialize<UsageDto>(json, ReadOptions);
            if (dto is null)
            {
                Console.Error.WriteLine($"[RoomTurnUsage] Loud fallback to empty usage: Usage file '{filePath}' deserialized to null.");
                return RoomTurnUsage.Empty;
            }

            var consecutiveFailed = dto.ConsecutiveFailedTurns ?? 0;
            if (consecutiveFailed < 0)
            {
                Console.Error.WriteLine($"[RoomTurnUsage] Loud fallback to empty usage: Usage file '{filePath}' has negative consecutive failed turns ({consecutiveFailed}).");
                return RoomTurnUsage.Empty;
            }

            var timestamps = dto.RecentMachineTurnTimestamps ?? [];

            // The two fields are one fact stored twice (the newest list entry IS the last turn),
            // and nothing else prevents a hand edit or partial write making them disagree --
            // decisions computed from inconsistent state would refuse or allow wrongly (#778
            // review). Reconciled here to the LATER of the two, loudly, so the conservative
            // reading wins: LastMachineTurnAt can only move forward. It is stored at all (not
            // derived) because the hourly window prunes entries older than one hour, while an
            // operator may set a min interval LONGER than an hour -- deriving from the pruned
            // list would forget a last turn the longer interval still needs to see.
            var newestListed = timestamps.Count > 0 ? timestamps.Max() : (DateTimeOffset?)null;
            var lastTurn = dto.LastMachineTurnAt;
            if (newestListed is { } newest && (lastTurn is null || lastTurn < newest))
            {
                Console.Error.WriteLine(
                    $"[RoomTurnUsage] Inconsistent usage file '{filePath}' RECONCILED loudly: lastMachineTurnAt "
                    + $"({(lastTurn is null ? "absent" : lastTurn.ToString())}) is older than the newest recorded turn ({newest}); using the newest.");
                lastTurn = newest;
            }

            return new RoomTurnUsage(
                timestamps.AsReadOnly(),
                lastTurn,
                consecutiveFailed);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[RoomTurnUsage] Loud fallback to empty usage: Failed to load usage from '{filePath}': {ex.Message}");
            return RoomTurnUsage.Empty;
        }
    }

    /// <summary>
    /// Persists <paramref name="usage"/> atomically to <c>{room}/.baton/turn-usage.json</c>.
    /// </summary>
    public static void Save(string roomDirectoryPath, RoomTurnUsage usage)
    {
        ArgumentException.ThrowIfNullOrEmpty(roomDirectoryPath);
        ArgumentNullException.ThrowIfNull(usage);

        var batonDir = Path.Combine(roomDirectoryPath, BatonDirectoryName);
        Directory.CreateDirectory(batonDir);

        var filePath = GetUsageFilePath(roomDirectoryPath);
        var dto = new UsageDto(
            usage.RecentMachineTurnTimestamps.ToList(),
            usage.LastMachineTurnAt,
            usage.ConsecutiveFailedTurns);

        var json = JsonSerializer.Serialize(dto, FlowEventLogJson.Options);
        var tempFilePath = filePath + ".tmp." + Guid.NewGuid().ToString("n");
        File.WriteAllText(tempFilePath, json);
        RetryingFileMove.Move(tempFilePath, filePath, overwrite: true);
    }
}
