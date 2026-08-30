using System.Text.Json.Serialization;
using Aer.Flow.Domain;

namespace Aer.Adapters;

public sealed record RoleTemplateOutputExport(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("schema")] string Schema,
    [property: JsonPropertyName("instruction")] string Instruction);

public sealed record RoleTemplateExport(
    [property: JsonPropertyName("adapter")] string Adapter,
    [property: JsonPropertyName("model")] string? Model,
    [property: JsonPropertyName("effort")] string? Effort,
    [property: JsonPropertyName("read_files")] bool ReadFiles,
    [property: JsonPropertyName("write_files")] bool WriteFiles,
    [property: JsonPropertyName("run_shell_commands")] bool RunShellCommands,
    [property: JsonPropertyName("network_access")] bool NetworkAccess,
    [property: JsonPropertyName("timeout_minutes")] int TimeoutMinutes,
    [property: JsonPropertyName("verdict_schema")] bool VerdictSchema,
    [property: JsonPropertyName("_use")] string Use,
    [property: JsonPropertyName("_outputs")] IReadOnlyList<RoleTemplateOutputExport> Outputs);

/// <summary>
/// Information describing a built-in workflow template (M22 Phase 1).
/// </summary>
public sealed record BuiltInTemplateInfo(
    string Id,
    string Title,
    string Description,
    bool RequiresSecondaryVendor);

/// <summary>
/// Pre-authored workflow template catalog (M22 Phase 1): the built-in template and dispatch-role
/// metadata <c>aer templates</c> reports.
/// </summary>
public static class BuiltInWorkflowTemplates
{
    public static readonly BuiltInTemplateInfo ChatSession = new(
        Id: "chat-session",
        Title: "Chat (Interactive Session)",
        Description: "Interactive 1-on-1 session with an AI worker (Claude or agy) with live turn streaming and session resumption.",
        RequiresSecondaryVendor: false);

    public static readonly BuiltInTemplateInfo CodebaseSession = new(
        Id: "codebase-session",
        Title: "Codebase Session",
        Description: "Interactive AI agent session bound to a project directory with conservative file/command permissions.",
        RequiresSecondaryVendor: false);

    public static readonly BuiltInTemplateInfo SoloRun = new(
        Id: "solo-run",
        Title: "Solo Run (Advanced)",
        Description: "Single-step execution using an installed AI worker (Claude or agy).",
        RequiresSecondaryVendor: false);

    public static readonly BuiltInTemplateInfo ReviewRun = new(
        Id: "review-run",
        Title: "Review Run (Advanced)",
        Description: "Two-step workflow where one AI worker drafts content and another AI worker reviews it with human sign-off.",
        RequiresSecondaryVendor: true);

    // The dispatch roles (advise/implement/review/fact-check/janitor) are deliberately NOT
    // BuiltInTemplateInfo entries: Catalog feeds the daemon's /api/templates and from there the
    // desktop and mobile start pickers, and putting roles in front of a person is a UI-arc
    // decision, not a #887-stage-1 side effect. They are exported to machine consumers via
    // GetRoleTemplates() below.
    public static IReadOnlyList<BuiltInTemplateInfo> Catalog { get; } = [ChatSession, CodebaseSession, SoloRun, ReviewRun];

    public static IReadOnlyDictionary<string, RoleTemplateExport> GetRoleTemplates()
    {
        var roles = WorkerRoleCatalog.All;
        var dict = new Dictionary<string, RoleTemplateExport>(StringComparer.Ordinal);
        foreach (var role in roles)
        {
            dict[role.Id] = new RoleTemplateExport(
                Adapter: role.Adapter,
                Model: role.Model,
                Effort: role.Effort,
                ReadFiles: role.Grant.ReadFiles,
                WriteFiles: role.Grant.WriteFiles,
                RunShellCommands: role.Grant.RunShellCommands,
                NetworkAccess: role.Grant.NetworkAccess,
                TimeoutMinutes: (int)role.Timeout.TotalMinutes,
                VerdictSchema: role.ProducesVerdict,
                Use: role.Purpose,
                Outputs: role.Outputs.Select(o => new RoleTemplateOutputExport(
                    Name: o.Name,
                    Schema: o.Schema switch
                    {
                        OutputSchema.ReviewVerdict => "review_verdict",
                        OutputSchema.Diff => "diff",
                        _ => "none",
                    },
                    Instruction: o.Instruction)).ToList());
        }
        return dict;
    }
}
