using System.Text.Json;
using System.Text.Json.Serialization;
using Baton.Store;

namespace Baton.Projection;

/// <summary>
/// Result of <see cref="RoomTurnUsageStore.LoadWithDiagnostics"/>: the resolved usage plus the
/// diagnostic lines <see cref="RoomTurnUsageStore.Load"/> would otherwise print to <see
/// cref="Console.Error"/>. Empty <paramref name="Warnings"/> means the load was unremarkable.
/// </summary>
public sealed record RoomTurnUsageLoad(RoomTurnUsage Usage, IReadOnlyList<string> Warnings);

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
    /// Reads usage fresh from <c>{room}/.baton/turn-usage.json</c>, printing any diagnostics to
    /// <see cref="Console.Error"/> as it goes -- the single call site that owns the console for this
    /// store. Callers that need the diagnostics without the console side effect (tests, chiefly)
    /// should call <see cref="LoadWithDiagnostics"/> directly instead.
    /// </summary>
    public static RoomTurnUsage Load(string roomDirectoryPath)
    {
        var (usage, warnings) = LoadWithDiagnostics(roomDirectoryPath);
        foreach (var warning in warnings)
        {
            Console.Error.WriteLine(warning);
        }

        return usage;
    }

    /// <summary>
    /// Same contract as <see cref="Load"/> -- missing file → <see cref="RoomTurnUsage.Empty"/>,
    /// present-but-invalid (corrupt JSON or negative consecutive failures) → <see
    /// cref="RoomTurnUsage.Empty"/> -- but returns the diagnostics that <see cref="Load"/> would print
    /// instead of writing them to <see cref="Console.Error"/> itself.
    /// </summary>
    public static RoomTurnUsageLoad LoadWithDiagnostics(string roomDirectoryPath)
    {
        if (string.IsNullOrEmpty(roomDirectoryPath))
        {
            return new RoomTurnUsageLoad(RoomTurnUsage.Empty, []);
        }

        var filePath = GetUsageFilePath(roomDirectoryPath);
        if (!File.Exists(filePath))
        {
            return new RoomTurnUsageLoad(RoomTurnUsage.Empty, []);
        }

        try
        {
            var json = File.ReadAllText(filePath);
            var dto = JsonSerializer.Deserialize<UsageDto>(json, ReadOptions);
            if (dto is null)
            {
                return new RoomTurnUsageLoad(RoomTurnUsage.Empty,
                    [$"[RoomTurnUsage] Loud fallback to empty usage: Usage file '{filePath}' deserialized to null."]);
            }

            var consecutiveFailed = dto.ConsecutiveFailedTurns ?? 0;
            if (consecutiveFailed < 0)
            {
                return new RoomTurnUsageLoad(RoomTurnUsage.Empty,
                    [$"[RoomTurnUsage] Loud fallback to empty usage: Usage file '{filePath}' has negative consecutive failed turns ({consecutiveFailed})."]);
            }

            var timestamps = dto.RecentMachineTurnTimestamps ?? [];
            var warnings = new List<string>();

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
                warnings.Add(
                    $"[RoomTurnUsage] Inconsistent usage file '{filePath}' RECONCILED loudly: lastMachineTurnAt "
                    + $"({(lastTurn is null ? "absent" : lastTurn.ToString())}) is older than the newest recorded turn ({newest}); using the newest.");
                lastTurn = newest;
            }

            return new RoomTurnUsageLoad(
                new RoomTurnUsage(timestamps.AsReadOnly(), lastTurn, consecutiveFailed),
                warnings);
        }
        catch (Exception ex)
        {
            return new RoomTurnUsageLoad(RoomTurnUsage.Empty,
                [$"[RoomTurnUsage] Loud fallback to empty usage: Failed to load usage from '{filePath}': {ex.Message}"]);
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
