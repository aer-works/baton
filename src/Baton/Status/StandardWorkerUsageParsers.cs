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
/// Parses claude's <c>stream-json</c> terminal <c>"type":"result"</c> line (issue #1360, extended by
/// #1569). The sole implementation for this vendor (#1599) -- <c>ClaudeWorkerAdapter.TryParseFinalUsage</c>
/// delegates here rather than re-implementing the same read, closing the drift #1590's fix left
/// behind: an all-null result (no tokens, no turns, no cache/thinking figures) now returns
/// <see langword="false"/> here too, matching the guard the adapter carried before it delegated here, because a usage record with
/// nothing in it claims nothing.
/// <c>usage.input_tokens</c>/<c>output_tokens</c>/<c>cache_creation_input_tokens</c>/
/// <c>cache_read_input_tokens</c>, the nested <c>usage.output_tokens_details.thinking_tokens</c>, and
/// top-level <c>num_turns</c> are each read independently: a line reporting some and not others yields
/// exactly the fields it reported, never a fabricated zero (docs/vendor-capabilities.md's "Usage,
/// cost and quota" section is the register this reads against). <c>total_cost_usd</c> is real on this
/// vendor but outside #1569's additive shape, so it is read by nothing here.
/// <para>
/// <b>Scope, measured (docs/vendor-doc-audit.md, #479): this is a top-level figure, not a whole-tree
/// one.</b> <c>usage.output_tokens</c> excludes tokens spent by any subagent the dispatched worker
/// itself fans out to -- confirmed at a 22% shortfall against the same result's <c>modelUsage</c>
/// object on a single subagent, growing with the tree. AER caps a worker's own subagent fan-out at
/// depth 1 (<c>ClaudeWorkerAdapter.MaxSubagentSpawnDepthVariable</c>) rather than zero, so this
/// undercount is a real, reachable case here, not a hypothetical. <c>modelUsage</c> is left unread:
/// summing it correctly needs a per-model breakdown this shape's scalars cannot carry without
/// inventing a field neither #1360 nor #1569 asked for. Per <c>spec/baton.md</c> §7, none of this
/// shape is the reset-time source of truth -- it is attribution, and the fleet-level <c>/usage</c>
/// poll is what that section rules authoritative.
/// </para>
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

            if (tokensIn is null && tokensOut is null && turns is null
                && cacheReadTokens is null && cacheCreationTokens is null && thinkingTokens is null)
            {
                return false;
            }

            usage = new WorkerUsage(tokensIn, tokensOut, turns, cacheReadTokens, cacheCreationTokens, thinkingTokens);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// #1623: reads <c>message.usage</c> off a mid-stream <c>"type":"assistant"</c> line — measured
    /// 2026-09-01 (docs/vendor-capabilities.md's "The terminal result line is not the only place this
    /// lives" finding) to carry the same four keys this class's own <see cref="TryParseFinalUsage"/>
    /// reads off the terminal line: <c>input_tokens</c>/<c>output_tokens</c>/
    /// <c>cache_creation_input_tokens</c>/<c>cache_read_input_tokens</c>. <c>num_turns</c> and
    /// <c>output_tokens_details.thinking_tokens</c> are NOT claimed on this line (that finding did not
    /// re-check them), so this deliberately leaves <see cref="WorkerUsage.Turns"/>/
    /// <see cref="WorkerUsage.ThinkingTokens"/> null here rather than reusing the terminal-line reader.
    /// The per-line/per-turn summing contract is <see cref="IWorkerUsageParser.TryParseIncrementalUsage"/>'s.
    /// </summary>
    public bool TryParseIncrementalUsage(string rawLine, out WorkerUsage? usage)
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
                || typeProp.GetString() != "assistant"
                || !root.TryGetProperty("message", out var message)
                || message.ValueKind != JsonValueKind.Object
                || !message.TryGetProperty("usage", out var usageProp)
                || usageProp.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            long? tokensIn = usageProp.TryGetProperty("input_tokens", out var inProp) && inProp.TryGetInt64(out var inTokens) ? inTokens : null;
            long? tokensOut = usageProp.TryGetProperty("output_tokens", out var outProp) && outProp.TryGetInt64(out var outTokens) ? outTokens : null;
            long? cacheReadTokens = usageProp.TryGetProperty("cache_read_input_tokens", out var cacheReadProp) && cacheReadProp.TryGetInt64(out var cacheReadValue) ? cacheReadValue : null;
            long? cacheCreationTokens = usageProp.TryGetProperty("cache_creation_input_tokens", out var cacheCreationProp) && cacheCreationProp.TryGetInt64(out var cacheCreationValue) ? cacheCreationValue : null;

            if (tokensIn is null && tokensOut is null && cacheReadTokens is null && cacheCreationTokens is null)
            {
                return false;
            }

            usage = new WorkerUsage(tokensIn, tokensOut, CacheReadTokens: cacheReadTokens, CacheCreationTokens: cacheCreationTokens);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// #1623: a <c>"type":"assistant"</c> message's <c>tool_use</c> content block name, per the
    /// standard Anthropic Messages API streaming shape claude's own <c>stream-json</c> output is built
    /// on — not independently doc-audited the way the usage fields above are (docs/vendor-capabilities.md
    /// carries no dedicated finding for this specific field), so this degrades to null on any shape
    /// drift rather than throwing. First matching block wins; a message with several tool calls in one
    /// turn is a real but rare shape this simplifies rather than enumerating.
    /// </summary>
    public string? TryParseToolName(string rawLine)
    {
        if (string.IsNullOrWhiteSpace(rawLine))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(rawLine);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var typeProp) || typeProp.GetString() != "assistant"
                || !root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object
                || !message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var block in content.EnumerateArray())
            {
                if (block.ValueKind == JsonValueKind.Object
                    && block.TryGetProperty("type", out var blockType) && blockType.GetString() == "tool_use"
                    && block.TryGetProperty("name", out var nameProp) && nameProp.GetString() is { Length: > 0 } name)
                {
                    return name;
                }
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// Parses agy's <c>stream-json</c> terminal <c>"event":"result"</c> line (issue #1360, extended by
/// #1569). The sole implementation for this vendor (#1599) -- <c>AgyWorkerAdapter.TryParseFinalUsage</c>
/// delegates here rather than re-implementing the same read. agy's <c>result.usage</c> shape is
/// inconsistent across observed captures (#1088, docs/vendor-capabilities.md): sometimes a full
/// breakdown (<c>input_tokens</c>/<c>output_tokens</c>/<c>thinking_tokens</c>/<c>cache_read_tokens</c>/
/// <c>total_tokens</c>), sometimes only <c>total_tokens</c>. Only <c>input_tokens</c>/
/// <c>output_tokens</c> map to this shape's <c>tokensIn</c>/<c>tokensOut</c> -- a lone
/// <c>total_tokens</c> is a real number but not a direction, and splitting it would fabricate a
/// breakdown agy never reported. <c>thinking_tokens</c>/<c>cache_read_tokens</c> read the same way,
/// independently of each other and of the input/output split. agy has never been observed reporting a
/// cache-creation figure (docs/vendor-capabilities.md), so this parser has no field to bind
/// <see cref="WorkerUsage.CacheCreationTokens"/> to and leaves it null rather than inventing one.
/// Turns come from <c>result.num_turns</c>, read independently of the usage object. This shape is
/// attribution, never the reset-time source of truth -- see <see cref="ClaudeUsageParser"/>'s own doc
/// comment for the <c>spec/baton.md</c> §7 ruling this rests on.
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

    /// <summary>
    /// #1623: reads <c>step_update.usage</c> off a DONE-state <c>"event":"step_update"</c> line —
    /// measured live against a real agy lane's captured <c>.stdout.log</c> (2026-09-02): a
    /// <c>step_type":"agent_response"</c> step's DONE update carries the identical
    /// <c>input_tokens</c>/<c>output_tokens</c>/<c>thinking_tokens</c>/<c>cache_read_tokens</c>/
    /// <c>total_tokens</c> shape this class's own <see cref="TryParseFinalUsage"/> reads off the
    /// terminal <c>result</c> event's <c>usage</c> object -- same field names, different envelope. One
    /// line = one step's own usage, not a running total -- a caller sums across calls.
    /// </summary>
    public bool TryParseIncrementalUsage(string rawLine, out WorkerUsage? usage)
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
                || !root.TryGetProperty("event", out var eventProp) || eventProp.GetString() != "step_update"
                || !root.TryGetProperty("step_update", out var stepUpdate) || stepUpdate.ValueKind != JsonValueKind.Object
                || !stepUpdate.TryGetProperty("state", out var stateProp) || stateProp.GetString() != "DONE"
                || !stepUpdate.TryGetProperty("usage", out var usageProp) || usageProp.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            long? tokensIn = usageProp.TryGetProperty("input_tokens", out var inProp) && inProp.TryGetInt64(out var inTokens) ? inTokens : null;
            long? tokensOut = usageProp.TryGetProperty("output_tokens", out var outProp) && outProp.TryGetInt64(out var outTokens) ? outTokens : null;
            long? cacheReadTokens = usageProp.TryGetProperty("cache_read_tokens", out var cacheReadProp) && cacheReadProp.TryGetInt64(out var cacheReadValue) ? cacheReadValue : null;
            long? thinkingTokens = usageProp.TryGetProperty("thinking_tokens", out var thinkingProp) && thinkingProp.TryGetInt64(out var thinkingValue) ? thinkingValue : null;

            if (tokensIn is null && tokensOut is null && cacheReadTokens is null && thinkingTokens is null)
            {
                return false;
            }

            usage = new WorkerUsage(tokensIn, tokensOut, CacheReadTokens: cacheReadTokens, ThinkingTokens: thinkingTokens);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// #1623: a <c>"step_type":"tool"</c> step_update's own <c>tool_name</c> — measured against the
    /// same real agy lane capture <see cref="TryParseIncrementalUsage"/>'s doc names.
    /// </summary>
    public string? TryParseToolName(string rawLine)
    {
        if (string.IsNullOrWhiteSpace(rawLine))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(rawLine);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("event", out var eventProp) || eventProp.GetString() != "step_update"
                || !root.TryGetProperty("step_update", out var stepUpdate) || stepUpdate.ValueKind != JsonValueKind.Object
                || !stepUpdate.TryGetProperty("step_type", out var stepTypeProp) || stepTypeProp.GetString() != "tool"
                || !stepUpdate.TryGetProperty("tool_name", out var toolNameProp))
            {
                return null;
            }

            return toolNameProp.GetString() is { Length: > 0 } name ? name : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
