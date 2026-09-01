using System.Text.Json;
using Baton.Domain;

namespace Baton.Status;

/// <summary>
/// Built-in usage parsers for vendor CLI streaming logs (issue #1360).
/// </summary>
public static class StandardWorkerUsageParsers
{
    public static IReadOnlyDictionary<string, IWorkerUsageParser> Default { get; } =
        new Dictionary<string, IWorkerUsageParser>(StringComparer.Ordinal)
        {
            ["claude"] = new ClaudeUsageParser(),
            ["agy"] = new AgyUsageParser(),
        };
}

/// <summary>
/// Parses terminal usage from Claude Code stream-json output.
/// </summary>
public sealed class ClaudeUsageParser : IWorkerUsageParser
{
    public bool TryParseFinalUsage(string rawLine, out WorkerUsage? usage)
    {
        usage = null;
        if (string.IsNullOrWhiteSpace(rawLine))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(rawLine);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var typeProp)
                || typeProp.GetString() != "result")
            {
                return false;
            }

            long? tokensIn = null;
            long? tokensOut = null;
            long? cacheReadTokens = null;
            long? cacheCreationTokens = null;
            long? thinkingTokens = null;
            if (root.TryGetProperty("usage", out var usageProp) && usageProp.ValueKind == JsonValueKind.Object)
            {
                if (usageProp.TryGetProperty("input_tokens", out var inProp) && inProp.TryGetInt64(out var inTokens))
                {
                    tokensIn = inTokens;
                }

                if (usageProp.TryGetProperty("output_tokens", out var outProp) && outProp.TryGetInt64(out var outTokens))
                {
                    tokensOut = outTokens;
                }

                if (usageProp.TryGetProperty("cache_read_input_tokens", out var cacheReadProp) && cacheReadProp.TryGetInt64(out var cacheReadValue))
                {
                    cacheReadTokens = cacheReadValue;
                }

                if (usageProp.TryGetProperty("cache_creation_input_tokens", out var cacheCreationProp) && cacheCreationProp.TryGetInt64(out var cacheCreationValue))
                {
                    cacheCreationTokens = cacheCreationValue;
                }

                if (usageProp.TryGetProperty("output_tokens_details", out var outputDetailsProp)
                    && outputDetailsProp.ValueKind == JsonValueKind.Object
                    && outputDetailsProp.TryGetProperty("thinking_tokens", out var thinkingProp)
                    && thinkingProp.TryGetInt64(out var thinkingValue))
                {
                    thinkingTokens = thinkingValue;
                }
            }

            int? turns = null;
            if (root.TryGetProperty("num_turns", out var turnsProp) && turnsProp.TryGetInt32(out var turnCount))
            {
                turns = turnCount;
            }

            usage = new WorkerUsage(tokensIn, tokensOut, turns, cacheReadTokens, cacheCreationTokens, thinkingTokens);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

/// <summary>
/// Parses terminal usage from Gemini/Agy stream-json output.
/// </summary>
public sealed class AgyUsageParser : IWorkerUsageParser
{
    public bool TryParseFinalUsage(string rawLine, out WorkerUsage? usage)
    {
        usage = null;
        if (string.IsNullOrWhiteSpace(rawLine))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(rawLine);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("event", out var eventProp) || eventProp.GetString() != "result"
                || !root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            long? tokensIn = null;
            long? tokensOut = null;
            long? cacheReadTokens = null;
            long? thinkingTokens = null;
            if (result.TryGetProperty("usage", out var usageProp) && usageProp.ValueKind == JsonValueKind.Object)
            {
                if (usageProp.TryGetProperty("input_tokens", out var inProp) && inProp.TryGetInt64(out var inTokens))
                {
                    tokensIn = inTokens;
                }

                if (usageProp.TryGetProperty("output_tokens", out var outProp) && outProp.TryGetInt64(out var outTokens))
                {
                    tokensOut = outTokens;
                }

                if (usageProp.TryGetProperty("cache_read_tokens", out var cacheReadProp) && cacheReadProp.TryGetInt64(out var cacheReadValue))
                {
                    cacheReadTokens = cacheReadValue;
                }

                if (usageProp.TryGetProperty("thinking_tokens", out var thinkingProp) && thinkingProp.TryGetInt64(out var thinkingValue))
                {
                    thinkingTokens = thinkingValue;
                }
            }

            int? turns = result.TryGetProperty("num_turns", out var turnsProp) && turnsProp.TryGetInt32(out var turnsValue)
                ? turnsValue
                : null;

            if (tokensIn is null && tokensOut is null && turns is null && cacheReadTokens is null && thinkingTokens is null)
            {
                return false;
            }

            usage = new WorkerUsage(tokensIn, tokensOut, turns, cacheReadTokens, ThinkingTokens: thinkingTokens);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
