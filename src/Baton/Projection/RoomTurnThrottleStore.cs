using System.Text.Json;
using System.Text.Json.Serialization;
using Baton.Store;

namespace Baton.Projection;

/// <summary>
/// Result of <see cref="RoomTurnThrottleStore.LoadWithDiagnostics"/>: the resolved throttles plus the
/// diagnostic lines <see cref="RoomTurnThrottleStore.Load"/> would otherwise print to <see
/// cref="Console.Error"/>. Empty <paramref name="Warnings"/> means the load was unremarkable --
/// including a deliberately partial file, which is the operator's normal move, not a fault.
/// </summary>
public sealed record RoomTurnThrottleLoad(RoomTurnThrottles Throttles, IReadOnlyList<string> Warnings);

/// <summary>
/// Persistence store for operator-visible turn throttle settings at <c>{room}/turn-throttles.json</c> (#778).
/// <para>
/// <b>Filename choice rationale:</b> Placed at room root as <c>turn-throttles.json</c> alongside
/// <c>room.jsonl</c> and <c>memory/</c>. NOT under <c>.baton/</c> because it is an operator-visible,
/// hand-editable file owned by the operator, whereas <c>.baton/</c> is reserved for engine-managed metadata.
/// </para>
/// </summary>
public static class RoomTurnThrottleStore
{
    public const string ThrottleFileName = "turn-throttles.json";

    public static string GetThrottleFilePath(string roomDirectoryPath)
        => Path.Combine(roomDirectoryPath, ThrottleFileName);

    // Every parameter defaults to null: FlowEventLogJson.Options sets
    // RespectRequiredConstructorParameters, under which a default-less positional parameter is
    // REQUIRED -- a deliberately partial operator file (one knob overridden) would throw instead
    // of per-field defaulting. Caught by A_partial_throttle_file_overrides_only_the_field_it_names.
    private sealed record ThrottleDto(
        [property: JsonPropertyName("minMachineTurnIntervalSeconds")] double? MinMachineTurnIntervalSeconds = null,
        [property: JsonPropertyName("maxMachineTurnsPerHour")] int? MaxMachineTurnsPerHour = null,
        [property: JsonPropertyName("failedTurnsBeforeDormancy")] int? FailedTurnsBeforeDormancy = null)
    {
        // Captures every key the operator wrote that is NOT one of the three above. A typo'd
        // setting silently ignored is indistinguishable from "not set" (#778 review) -- this is
        // the operator's own hand-edited file, so a misspelling must be loud, not swallowed.
        [JsonExtensionData]
        public Dictionary<string, System.Text.Json.JsonElement>? UnrecognizedKeys { get; init; }
    }

    private static readonly JsonSerializerOptions ReadOptions = new(FlowEventLogJson.Options)
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly JsonSerializerOptions WriteOptions = new(FlowEventLogJson.Options)
    {
        WriteIndented = true
    };

    /// <summary>
    /// Reads settings fresh on each call from <c>{room}/turn-throttles.json</c>, printing any
    /// diagnostics to <see cref="Console.Error"/> as it goes -- the single call site that owns the
    /// console for this store. Callers that need the diagnostics without the console side effect
    /// (tests, chiefly) should call <see cref="LoadWithDiagnostics"/> directly instead.
    /// </summary>
    public static RoomTurnThrottles Load(string roomDirectoryPath)
    {
        var (throttles, warnings) = LoadWithDiagnostics(roomDirectoryPath);
        foreach (var warning in warnings)
        {
            Console.Error.WriteLine(warning);
        }

        return throttles;
    }

    /// <summary>
    /// Same contract as <see cref="Load"/> -- missing file → defaults, present-but-invalid (corrupt
    /// JSON or non-positive numeric values) → defaults -- but returns the diagnostics that <see
    /// cref="Load"/> would print instead of writing them to <see cref="Console.Error"/> itself. A
    /// deliberately partial file (one knob overridden) is normal operator behaviour, not a fault, and
    /// returns no warnings.
    /// </summary>
    public static RoomTurnThrottleLoad LoadWithDiagnostics(string roomDirectoryPath)
    {
        if (string.IsNullOrEmpty(roomDirectoryPath))
        {
            return new RoomTurnThrottleLoad(RoomTurnThrottles.Default, []);
        }

        var filePath = GetThrottleFilePath(roomDirectoryPath);
        if (!File.Exists(filePath))
        {
            return new RoomTurnThrottleLoad(RoomTurnThrottles.Default, []);
        }

        try
        {
            var json = File.ReadAllText(filePath);
            var dto = JsonSerializer.Deserialize<ThrottleDto>(json, ReadOptions);
            if (dto is null)
            {
                return new RoomTurnThrottleLoad(RoomTurnThrottles.Default,
                    [$"[RoomTurnThrottles] Loud fallback to defaults: Throttle file '{filePath}' deserialized to null."]);
            }

            var warnings = new List<string>();
            if (dto.UnrecognizedKeys is { Count: > 0 } unknown)
            {
                warnings.Add(
                    $"[RoomTurnThrottles] Unrecognized key(s) in '{filePath}' IGNORED: {string.Join(", ", unknown.Keys)}. "
                    + "Valid keys: minMachineTurnIntervalSeconds, maxMachineTurnsPerHour, failedTurnsBeforeDormancy. "
                    + "A misspelled key falls back to its default.");
            }

            var intervalSeconds = dto.MinMachineTurnIntervalSeconds ?? RoomTurnThrottles.DefaultMinMachineTurnInterval.TotalSeconds;
            var maxPerHour = dto.MaxMachineTurnsPerHour ?? RoomTurnThrottles.DefaultMaxMachineTurnsPerHour;
            var failedDormancy = dto.FailedTurnsBeforeDormancy ?? RoomTurnThrottles.DefaultFailedTurnsBeforeDormancy;

            if (intervalSeconds <= 0 || maxPerHour <= 0 || failedDormancy <= 0)
            {
                warnings.Add(
                    $"[RoomTurnThrottles] Loud fallback to defaults: Throttle file '{filePath}' contains non-positive values (minIntervalSec: {intervalSeconds}, maxPerHour: {maxPerHour}, failedBeforeDormancy: {failedDormancy}).");
                return new RoomTurnThrottleLoad(RoomTurnThrottles.Default, warnings);
            }

            return new RoomTurnThrottleLoad(
                new RoomTurnThrottles(TimeSpan.FromSeconds(intervalSeconds), maxPerHour, failedDormancy),
                warnings);
        }
        catch (Exception ex)
        {
            return new RoomTurnThrottleLoad(RoomTurnThrottles.Default,
                [$"[RoomTurnThrottles] Loud fallback to defaults: Failed to load throttles from '{filePath}': {ex.Message}"]);
        }
    }

    /// <summary>
    /// Atomically persists <paramref name="throttles"/> to <c>{room}/turn-throttles.json</c>.
    /// </summary>
    public static void Save(string roomDirectoryPath, RoomTurnThrottles throttles)
    {
        ArgumentException.ThrowIfNullOrEmpty(roomDirectoryPath);
        ArgumentNullException.ThrowIfNull(throttles);

        var filePath = GetThrottleFilePath(roomDirectoryPath);
        var dto = new ThrottleDto(
            throttles.MinMachineTurnInterval.TotalSeconds,
            throttles.MaxMachineTurnsPerHour,
            throttles.FailedTurnsBeforeDormancy);

        var json = JsonSerializer.Serialize(dto, WriteOptions);
        var tempFilePath = filePath + ".tmp." + Guid.NewGuid().ToString("n");
        File.WriteAllText(tempFilePath, json);
        RetryingFileMove.Move(tempFilePath, filePath, overwrite: true);
    }
}
