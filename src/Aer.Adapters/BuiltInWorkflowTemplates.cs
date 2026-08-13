using System.Text.Json;
using System.Text.Json.Serialization;
using Aer.Flow.Domain;
using Aer.Flow.Templates;
using Aer.Workers.Dialogue;

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
/// Pre-authored workflow template catalog and materialization engine (M22 Phase 1).
/// Provides Solo Run and Review Run templates that materialize valid workflow definitions
/// and worker bindings against available vendor CLIs.
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

    public static readonly BuiltInTemplateInfo TwoVendorDialogue = new(
        Id: "two-vendor-dialogue",
        Title: "Two-Vendor Dialogue",
        Description: "Multi-vendor dialogue exchange between Claude and agy with turn-by-turn context synthesis.",
        RequiresSecondaryVendor: true);

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
    public static IReadOnlyList<BuiltInTemplateInfo> Catalog { get; } = [ChatSession, CodebaseSession, TwoVendorDialogue, SoloRun, ReviewRun];

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

    /// <summary>
    /// Materializes a built-in template's <see cref="WorkflowDefinition"/> and worker bindings.
    /// </summary>
    public static (WorkflowDefinition Definition, IReadOnlyDictionary<string, WorkerBindingConfigEntry> Bindings) Materialize(
        string templateId,
        string primaryAdapter,
        string? secondaryAdapter = null,
        string? customPrompt = null,
        string? secondaryCustomPrompt = null,
        string? roomDirectoryPath = null)
    {
        var normalizedPrimary = string.IsNullOrWhiteSpace(primaryAdapter) ? "claude" : primaryAdapter.Trim().ToLowerInvariant();
        var normalizedSecondary = string.IsNullOrWhiteSpace(secondaryAdapter) ? normalizedPrimary : secondaryAdapter.Trim().ToLowerInvariant();

        if (string.Equals(templateId, ChatSession.Id, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(templateId, CodebaseSession.Id, StringComparison.OrdinalIgnoreCase))
        {
            var (def, bindings, _) = InteractiveSessionMaterializer.Materialize(
                sessionId: Guid.NewGuid().ToString("N")[..12],
                roomDirectoryPath: string.Empty,
                adapter: normalizedPrimary,
                initialMessage: customPrompt);
            return (def, bindings);
        }

        if (string.Equals(templateId, TwoVendorDialogue.Id, StringComparison.OrdinalIgnoreCase))
        {
            // M23 Phase 1's real N-party dialogue worker (Aer.Workers.Dialogue), not a hand-rolled
            // draft/review DAG: a two-vendor dialogue is a single bounded exchange the worker itself
            // round-robins through DialogueWorkerConfig.Participants, so this is one step, one
            // binding, dispatched through the "dialogue" adapter -- exactly the shape
            // NewWorkflowViewModel's guided authoring already produces (Aer.Ui.Core/NewWorkflowViewModel.cs).
            const string finalOutputName = "transcript.md";

            var dialogueConfig = new DialogueWorkerConfig(
                SeedPrompt: string.IsNullOrWhiteSpace(customPrompt) ? "Discuss the topic thoroughly, considering multiple angles." : customPrompt,
                TurnBudget: 6,
                FinalOutputName: finalOutputName,
                Participants:
                [
                    DialogueParticipantPresets.For(
                        normalizedPrimary,
                        "initiator",
                        string.IsNullOrWhiteSpace(customPrompt) ? "You are the initiator of this dialogue. Open with your position and respond to the other side's points." : customPrompt,
                        model: null),
                    DialogueParticipantPresets.For(
                        normalizedSecondary,
                        "responder",
                        string.IsNullOrWhiteSpace(secondaryCustomPrompt) ? "You are the responder in this dialogue. Engage constructively with the initiator's points." : secondaryCustomPrompt,
                        model: null),
                ]);

            var sidecarDirectory = string.IsNullOrWhiteSpace(roomDirectoryPath) ? Path.GetTempPath() : roomDirectoryPath;
            Directory.CreateDirectory(sidecarDirectory);
            var sidecarPath = Path.Combine(sidecarDirectory, "dialogue-config.json");
            File.WriteAllText(sidecarPath, JsonSerializer.Serialize(dialogueConfig, new JsonSerializerOptions { WriteIndented = true }));

            var definition = new WorkflowDefinition(
                WorkflowTemplateId: new WorkflowTemplateId("two-vendor-dialogue-template"),
                WorkflowTemplateVersion: 1,
                Steps:
                [
                    new WorkflowStepDefinition(
                        StepId: new StepId("dialogue"),
                        Worker: "dialogue-worker",
                        Inputs: [],
                        Outputs: [finalOutputName],
                        DependsOn: [],
                        RetryPolicy: new RetryPolicy(3),
                        PausePoint: null)
                ]);

            var bindings = new Dictionary<string, WorkerBindingConfigEntry>
            {
                ["dialogue-worker"] = new WorkerBindingConfigEntry(
                    Adapter: "dialogue",
                    Contract: new WorkerContract(
                        WorkerName: "dialogue-worker",
                        RequiredInputs: [],
                        ProducedOutputs: [new ProducedOutput(finalOutputName)],
                        OptionalMetadata: []),
                    PromptTemplate: sidecarPath,
                    Timeout: TimeSpan.FromMinutes(20))
            };

            return (definition, bindings);
        }

        if (string.Equals(templateId, SoloRun.Id, StringComparison.OrdinalIgnoreCase))
        {
            var definition = new WorkflowDefinition(
                WorkflowTemplateId: new WorkflowTemplateId("solo-run-template"),
                WorkflowTemplateVersion: 1,
                Steps:
                [
                    new WorkflowStepDefinition(
                        StepId: new StepId("solo-step"),
                        Worker: "solo-worker",
                        Inputs: [],
                        Outputs: ["output.md"],
                        DependsOn: [],
                        RetryPolicy: new RetryPolicy(3),
                        PausePoint: null)
                ]);

            var defaultGrant = new PermissionGrant(ReadFiles: true, WriteFiles: true, RunShellCommands: false, ShellCommandPatterns: [], NetworkAccess: false);

            var bindings = new Dictionary<string, WorkerBindingConfigEntry>
            {
                ["solo-worker"] = new WorkerBindingConfigEntry(
                    Adapter: normalizedPrimary,
                    Contract: new WorkerContract(
                        WorkerName: "solo-worker",
                        RequiredInputs: [],
                        ProducedOutputs: [new ProducedOutput("output.md")],
                        OptionalMetadata: []),
                    PromptTemplate: string.IsNullOrWhiteSpace(customPrompt) ? "Perform the requested solo task and write the output to output.md." : customPrompt,
                    Timeout: TimeSpan.FromMinutes(10),
                    PermissionGrant: defaultGrant)
            };

            return (definition, bindings);
        }

        if (string.Equals(templateId, ReviewRun.Id, StringComparison.OrdinalIgnoreCase))
        {
            // The review-worker binding is sourced from the catalog's review role via RoleDispatch.ToBinding.
            // write_files: false is the role's intent and GrantAuditMode materializes the vendor-conditional
            // realization (agy: audited single-output write) (#901, #1146).
            var defaultGrant = new PermissionGrant(ReadFiles: true, WriteFiles: true, RunShellCommands: false, ShellCommandPatterns: [], NetworkAccess: false);

            var definition = new WorkflowDefinition(
                WorkflowTemplateId: new WorkflowTemplateId("review-run-template"),
                WorkflowTemplateVersion: 1,
                Steps:
                [
                    new WorkflowStepDefinition(
                        StepId: new StepId("draft"),
                        Worker: "draft-worker",
                        Inputs: [],
                        Outputs: ["draft.md"],
                        DependsOn: [],
                        RetryPolicy: new RetryPolicy(3),
                        PausePoint: null),
                    new WorkflowStepDefinition(
                        StepId: new StepId("review"),
                        Worker: "review-worker",
                        Inputs: ["draft.md"],
                        Outputs: ["report.md"],
                        DependsOn: [new StepId("draft")],
                        RetryPolicy: new RetryPolicy(3),
                        PausePoint: new PausePoint([new StepId("draft")]))
                ]);

            var bindings = new Dictionary<string, WorkerBindingConfigEntry>
            {
                ["draft-worker"] = new WorkerBindingConfigEntry(
                    Adapter: normalizedPrimary,
                    Contract: new WorkerContract(
                        WorkerName: "draft-worker",
                        RequiredInputs: [],
                        ProducedOutputs: [new ProducedOutput("draft.md")],
                        OptionalMetadata: []),
                    PromptTemplate: string.IsNullOrWhiteSpace(customPrompt) ? "Draft initial content for the requested topic and write to draft.md." : customPrompt,
                    Timeout: TimeSpan.FromMinutes(10),
                    PermissionGrant: defaultGrant),
                ["review-worker"] = RoleDispatch.ToBinding(
                    WorkerRoleCatalog.For("review"),
                    string.IsNullOrWhiteSpace(secondaryCustomPrompt)
                        ? "Review draft.md carefully, provide feedback and recommendations."
                        : secondaryCustomPrompt,
                    adapterOverride: normalizedSecondary,
                    workerName: "review-worker")
            };

            return (definition, bindings);
        }

        throw new ArgumentException(
            $"Unknown template ID '{templateId}'. Valid template IDs are: {string.Join(", ", Catalog.Select(t => t.Id))}.",
            nameof(templateId));
    }

    /// <summary>
    /// Materializes and persists the template definition (<c>workflow.json</c>) and bindings (<c>bindings.json</c>)
    /// into <paramref name="roomDirectoryPath"/>, along with <c>.aer/workflow-path</c> and <c>.aer/bindings-path</c> metadata.
    /// </summary>
    public static async Task MaterializeToDirectoryAsync(
        string templateId,
        string primaryAdapter,
        string? secondaryAdapter,
        string roomDirectoryPath,
        string? customPrompt = null,
        string? secondaryCustomPrompt = null,
        CancellationToken cancellationToken = default)
    {
        var workflowFilePath = Path.Combine(roomDirectoryPath, "workflow.json");
        if (File.Exists(workflowFilePath))
        {
            throw new RoomDirectoryAlreadyExistsException(
                RoomLifecycle.IsArchived(roomDirectoryPath)
                    ? $"A room already exists at '{roomDirectoryPath}' and is archived. Unarchive or delete it before reusing this name."
                    : $"A room already exists at '{roomDirectoryPath}'. Choose a different room/session name.");
        }

        Directory.CreateDirectory(roomDirectoryPath);
        var (definition, bindings) = Materialize(templateId, primaryAdapter, secondaryAdapter, customPrompt, secondaryCustomPrompt, roomDirectoryPath);

        var bindingsFilePath = Path.Combine(roomDirectoryPath, "bindings.json");

        await WorkflowDefinitionWriter.SaveToFileAsync(definition, workflowFilePath, cancellationToken).ConfigureAwait(false);
        await WorkerBindingConfigWriter.SaveToFileAsync(bindings, bindingsFilePath, cancellationToken).ConfigureAwait(false);

        var aerDir = Path.Combine(roomDirectoryPath, ".aer");
        Directory.CreateDirectory(aerDir);
        await File.WriteAllTextAsync(Path.Combine(aerDir, "workflow-path"), workflowFilePath, cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(aerDir, "bindings-path"), bindingsFilePath, cancellationToken).ConfigureAwait(false);

        // Records this room's kind on disk so it is self-describing rather than inferred from a
        // missing session marker (0013). Defensive: an absent room.json already reads as a workflow
        // room, but writing it keeps ReadRoomKind authoritative for every room the app creates.
        await InteractiveSessionMaterializer.WriteWorkflowRoomMarkerAsync(roomDirectoryPath, cancellationToken).ConfigureAwait(false);
    }
}
