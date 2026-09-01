using System.Text;
using Baton.Vendors;

namespace Baton.Cli;

/// <summary>
/// Formats and prints supported adapters, models, efforts, and role defaults (issue #1500).
/// <see cref="WorkerRoleCatalog.All"/> is the same catalog <c>ModelAndEffortValidationTests</c>
/// reads directly; <see cref="EffortTierMapping"/>'s tables are the exact static tables
/// <c>ClaudeWorkerAdapter.Resolve</c>/<c>AgyWorkerAdapter.Resolve</c> call into on every
/// <c>--effort</c> that suite exercises — so the role and effort sections cannot drift from what
/// dispatch actually accepts. <see cref="ClaudeWorkerAdapter.ModelAliases"/> is read live too, but
/// that list has no validation surface of its own (every alias always resolves to a vendor-current
/// model, so nothing rejects one). agy has no equivalent model-alias catalog (its models are
/// suffix-parametrized, not enumerated), so its printed model examples are illustrative text, not a
/// sourced table.
/// </summary>
public static class DispatchCapabilitiesPrinter
{
    public static string BuildText()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Adapters, Models & Efforts:");

        // Claude
        sb.AppendLine("  claude:");
        sb.AppendLine($"    Models:     {string.Join(", ", ClaudeWorkerAdapter.ModelAliases)} (aliases), or full ID (e.g. claude-opus-4-8)");
        var claudeCanonical = string.Join(
            ", ", EffortTierMapping.CanonicalWords.Select(w => $"{w} (-> {EffortTierMapping.ClaudeByCanonical[w]})"));
        sb.AppendLine($"    Canonical:  {claudeCanonical}");
        sb.AppendLine($"    Raw Effort: {string.Join(", ", EffortTierMapping.ClaudeRawValues)}");

        // Agy
        sb.AppendLine("  agy:");
        sb.AppendLine("    Models:     gemini-3.6-flash-high, gemini-3.6-flash-low, gemini-3.1-pro-high, etc.");
        var agyCanonical = string.Join(
            ", ", EffortTierMapping.CanonicalWords.Select(w => $"{w} (-> {EffortTierMapping.AgyByCanonical[w]})"));
        sb.AppendLine($"    Canonical:  {agyCanonical}");
        sb.AppendLine($"    Raw Effort: {string.Join(", ", EffortTierMapping.AgyRawValues)}");
        sb.AppendLine("    Note:       On agy, model suffix (-low, -medium, -high) and --effort must agree.");

        sb.AppendLine();
        sb.AppendLine("Role Timebox Defaults:");
        foreach (var role in WorkerRoleCatalog.All)
        {
            var timebox = $"{(int)role.Timeout.TotalMinutes}m";
            var modelPart = role.Model is not null ? $", model: {role.Model}" : "";
            var effortPart = role.Effort is not null ? $", effort: {role.Effort}" : "";
            sb.AppendLine($"  {role.Id,-12} {timebox,4}  (tier: {role.Tier}, adapter: {role.Adapter}{modelPart}{effortPart})");
        }

        return sb.ToString().TrimEnd();
    }

    public static void Print(TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteLine(BuildText());
    }
}
