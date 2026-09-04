using System.Text.Json;
using Baton.Domain;

namespace Baton.Status;

/// <summary>
/// Parses the per-turn usage on Codex CLI JSONL <c>turn.completed</c> events (#1853). Codex reports
/// <c>input_tokens</c> inclusive of <c>cached_input_tokens</c>; Baton's additive shape keeps those
/// dimensions disjoint, so <see cref="WorkerUsage.TokensIn"/> is the non-cached remainder.
/// </summary>
public sealed class CodexUsageParser : IWorkerUsageParser
{
    public bool TryParseFinalUsage(string rawLine, out WorkerUsage? usage) =>
        TryParse(rawLine, out usage);

    public bool TryParseIncrementalUsage(string rawLine, out WorkerUsage? usage) =>
        TryParse(rawLine, out usage);

    private static bool TryParse(string rawLine, out WorkerUsage? usage)
    {
        usage = null;
        if (string.IsNullOrWhiteSpace(rawLine))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(rawLine);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var type)
                || type.GetString() != "turn.completed"
                || !root.TryGetProperty("usage", out var reported)
                || reported.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var totalInput = ReadLong(reported, "input_tokens");
            var cachedInput = ReadLong(reported, "cached_input_tokens");
            var cacheWrite = ReadLong(reported, "cache_write_input_tokens");
            var output = ReadLong(reported, "output_tokens");
            var reasoning = ReadLong(reported, "reasoning_output_tokens");
            if (totalInput is null && cachedInput is null && cacheWrite is null
                && output is null && reasoning is null)
            {
                return false;
            }

            long? nonCachedInput = totalInput;
            if (totalInput is { } total && cachedInput is { } cached)
            {
                // An impossible vendor reading stays conservative rather than creating a negative token count.
                nonCachedInput = Math.Max(0, total - cached);
            }

            usage = new WorkerUsage(
                TokensIn: nonCachedInput,
                TokensOut: output,
                Turns: 1,
                CacheReadTokens: cachedInput,
                CacheCreationTokens: cacheWrite,
                ThinkingTokens: reasoning);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static long? ReadLong(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property)
        && property.ValueKind == JsonValueKind.Number
        && property.TryGetInt64(out var value)
            ? value
            : null;
}
