using Baton.Vendors;

namespace Baton.Cli.Tests;

[Collection(SerializedEnvironmentCollection.Name)]
public class DispatchCapabilitiesTests
{
    [Fact]
    public void Capabilities_text_contains_adapters_models_and_efforts()
    {
        var text = DispatchCapabilitiesPrinter.BuildText();

        // Adapters
        Assert.Contains("claude:", text);
        Assert.Contains("agy:", text);

        // Claude model aliases and full ID example — the exact joined line, not a per-alias loose
        // Contains: "opus" alone is a substring of the hardcoded "claude-opus-4-8" example, so a
        // per-value check would pass even if the printer's alias list went stale (#1500 second-reader).
        Assert.Contains(
            $"Models:     {string.Join(", ", ClaudeWorkerAdapter.ModelAliases)} (aliases), or full ID (e.g. claude-opus-4-8)",
            text);

        // Claude raw efforts — the exact joined line. Per-value loose checks are vacuous here too:
        // every one of "low"/"medium"/"high" also appears in the agy section regardless of what
        // Claude's own list contains.
        Assert.Contains($"Raw Effort: {string.Join(", ", EffortTierMapping.ClaudeRawValues)}", text);
        Assert.Contains($"Raw Effort: {string.Join(", ", EffortTierMapping.AgyRawValues)}", text);

        // Canonical efforts, both vendors — exact "word (-> vendor-value)" pairs.
        foreach (var word in EffortTierMapping.CanonicalWords)
        {
            Assert.Contains($"{word} (-> {EffortTierMapping.ClaudeByCanonical[word]})", text);
            Assert.Contains($"{word} (-> {EffortTierMapping.AgyByCanonical[word]})", text);
        }

        // agy models (illustrative only — agy has no alias catalog to source from)
        Assert.Contains("gemini-3.6-flash-high", text);

        // Role timebox defaults — the exact formatted line per role, so two roles sharing a timebox
        // (25m) can't let one role's missing/mismatched line hide behind another's.
        foreach (var role in WorkerRoleCatalog.All)
        {
            var timebox = $"{(int)role.Timeout.TotalMinutes}m";
            var modelPart = role.Model is not null ? $", model: {role.Model}" : "";
            var effortPart = role.Effort is not null ? $", effort: {role.Effort}" : "";
            Assert.Contains(
                $"{role.Id,-12} {timebox,4}  (tier: {role.Tier}, adapter: {role.Adapter}{modelPart}{effortPart})",
                text);
        }
    }

    [Fact]
    public void Print_writes_capabilities_to_writer()
    {
        using var sw = new StringWriter();
        DispatchCapabilitiesPrinter.Print(sw);

        var output = sw.ToString();
        Assert.NotEmpty(output);
        Assert.Contains("Adapters, Models & Efforts:", output);
        Assert.Contains("Role Timebox Defaults:", output);
    }
}
