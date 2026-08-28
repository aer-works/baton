using System.Collections.ObjectModel;
using Aer.Adapters;
using Aer.Flow.Domain;
using Aer.Flow.Templates;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Aer.Ui.Core;

/// <summary>The step kinds the guided flow authors — the two vendor runners (M17; UI spec §18.2).</summary>
public enum GuidedStepKind
{
    Claude,
    Agy,
}

/// <summary>
/// The guided New Workflow flow (M19 Phase 4, issue #189): author a workflow and who runs each
/// step form-first — no typed paths, no raw JSON — then run it without leaving the flow. Saving
/// writes the same durable files everything else already consumes (workflow definition and
/// bindings): the files remain the durable format; the user just never opens them.
/// <para>
/// <b>Workspace decision of record (the phase's named open question):</b> authored files live in
/// a UI-managed default workspace (<c>Documents/Baton/&lt;workflow-name&gt;</c>) — visible in
/// the flow and swappable via the folder picker, never required. Explicit-everywhere would put a
/// path decision back at the start of the non-expert's very first action, which is the exact
/// failure the audit's walkthrough recorded.
/// </para>
/// </summary>
public sealed partial class NewWorkflowViewModel : ObservableObject
{
    private IReadOnlyDictionary<string, IWorkerAdapter> _adapterRegistry = WorkerAdapterRegistry.Default;

    public ObservableCollection<GuidedStepViewModel> Steps { get; } = [];
    public ObservableCollection<string> GuidanceMessages { get; } = [];
    public ObservableCollection<string> VendorReadinessLines { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveWorkspacePath))]
    private string workflowName = string.Empty;

    /// <summary>Empty means "use the default workspace"; the effective path stays visible either way (visible, swappable, never required).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveWorkspacePath))]
    private string workspaceOverridePath = string.Empty;

    [ObservableProperty]
    private string statusText = string.Empty;

    // M21 Phase 1 follow-up: permissions live once per workflow, not once per step — a builder on
    // every step card doesn't scale (most workflows want one policy, not N to fiddle with), and
    // per-step controls only ever lived in the Advanced bindings editor anyway, invisible to anyone
    // on the guided (primary) path. This shared grant is translated per adapter and applied to every
    // step's binding entry at Save (see GuidedStepViewModel.BuildBindingEntryAsync).
    [ObservableProperty]
    private bool grantReadFiles;

    [ObservableProperty]
    private bool grantWriteFiles;

    [ObservableProperty]
    private bool grantRunShellCommands;

    [ObservableProperty]
    private string shellCommandPatternsText = string.Empty;

    [ObservableProperty]
    private bool grantNetworkAccess;

    public bool ShowShellCommandPatterns => GrantRunShellCommands;

    public void SetAdapterRegistry(IReadOnlyDictionary<string, IWorkerAdapter> adapterRegistry)
    {
        _adapterRegistry = adapterRegistry;
        RefreshStructure();
    }

    internal PermissionGrant BuildPermissionGrant() => new(
        GrantReadFiles,
        GrantWriteFiles,
        GrantRunShellCommands,
        SplitList(ShellCommandPatternsText),
        GrantNetworkAccess);

    partial void OnGrantReadFilesChanged(bool value) => RefreshStructure();

    partial void OnGrantWriteFilesChanged(bool value) => RefreshStructure();

    partial void OnGrantRunShellCommandsChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowShellCommandPatterns));
        RefreshStructure();
    }

    partial void OnShellCommandPatternsTextChanged(string value) => RefreshStructure();

    partial void OnGrantNetworkAccessChanged(bool value) => RefreshStructure();

    private static IReadOnlyList<string> SplitList(string text) =>
        text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public string EffectiveWorkspacePath => WorkspaceOverridePath.Length > 0
        ? WorkspaceOverridePath
        : Path.Combine(DefaultWorkspace.RootPath, WorkflowName.Length > 0 ? WorkflowName : "new-workflow");

    /// <summary>Raised by Save &amp; Run with the just-written file paths — the skin starts the run and navigates; this VM never executes anything (§6).</summary>
    public event Func<string, string, Task>? RunRequested;

    [RelayCommand]
    private void AddStep()
    {
        var step = new GuidedStepViewModel(this);
        Steps.Add(step);
        RefreshStructure();
    }

    internal void RemoveStep(GuidedStepViewModel step)
    {
        Steps.Remove(step);
        RefreshStructure();
    }

    /// <summary>Re-derives each step's depends-on options and the inline guidance — called on every structural edit (the same live-validation posture as M16's editors).</summary>
    public void RefreshStructure()
    {
        foreach (var step in Steps)
        {
            step.RefreshDependsOnOptions();
        }

        GuidanceMessages.Clear();
        foreach (var message in Validate())
        {
            GuidanceMessages.Add(message);
        }
    }

    /// <summary>The read-only vendor presence line (the audit's vendor-readiness surface) — "available / not found", never credentials, never a gate.</summary>
    public void RefreshVendorReadiness(Func<string, bool>? isOnPath = null)
    {
        VendorReadinessLines.Clear();
        foreach (var status in VendorCliPresence.Probe(isOnPath))
        {
            var vendorLabel = char.ToUpperInvariant(status.BinaryName[0]) + status.BinaryName[1..];
            VendorReadinessLines.Add(status.IsAvailable
                ? $"{vendorLabel}: available"
                : $"{vendorLabel}: not found — install and sign in to the {status.BinaryName} CLI to run steps with it");
        }
    }

    private IEnumerable<string> Validate()
    {
        if (WorkflowName.Length == 0)
        {
            yield return "Give the workflow a name — it names the plan and its folder.";
        }

        if (Steps.Count == 0)
        {
            yield return "Add at least one step.";
        }

        var duplicateNames = Steps
            .Where(step => step.Name.Length > 0)
            .GroupBy(step => step.Name)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key);
        foreach (var duplicate in duplicateNames)
        {
            yield return $"Two steps are named '{duplicate}' — step names must be unique.";
        }

        foreach (var step in Steps)
        {
            foreach (var message in step.Validate())
            {
                yield return message;
            }
        }

        var grant = BuildPermissionGrant();
        if (!grant.IsEmpty)
        {
            // #645, asked before the loop and before anything vendor-specific. Why that order, and
            // why the rule lives on PermissionGrant, are recorded once -- on
            // PermissionGrant.CategoriesDefeatedByTheShell and at the bindings editor's own call
            // site. What is particular to THIS surface: Save & Run dispatches immediately, so a
            // grant that slips through is discovered one click later, at bind time.
            if (grant.CategoriesDefeatedByTheShell is { Count: > 0 } defeated)
            {
                yield return "The permissions above can't be saved: "
                    + PermissionGrantWording.ShellDefeats(defeated);
            }

            var ignoredSteps = new List<(string AdapterName, string Label)>();
            var refusedSteps = new List<(string AdapterName, string? Reason, string Label)>();
            foreach (var step in Steps)
            {
                var adapterName = step.Kind == GuidedStepKind.Claude ? "claude" : "agy"; // vocabulary-ok: technical adapter key
                if (!_adapterRegistry.TryGetValue(adapterName, out var adapter))
                {
                    continue;
                }

                var label = step.Name.Length > 0 ? $"'{step.Name}'" : "an unnamed step";
                if (adapter is not IPermissionGrantTranslator translator)
                {
                    ignoredSteps.Add((adapterName, label));
                    continue;
                }

                if (!translator.TryTranslatePermissionGrant(grant, out _, out var gapReason))
                {
                    refusedSteps.Add((adapterName, gapReason, label));
                }
            }

            // One message per adapter+reason, naming every affected step — five gemini steps
            // sharing one grant problem are one problem, not five.
            foreach (var group in ignoredSteps.GroupBy(s => s.AdapterName))
            {
                var labels = string.Join(", ", group.Select(s => s.Label));
                yield return $"'{group.Key}' does not enforce permission grants — the permissions "
                    + $"above would be ignored at dispatch for {labels}, not applied.";
            }

            foreach (var group in refusedSteps.GroupBy(s => (s.AdapterName, s.Reason)))
            {
                var labels = string.Join(", ", group.Select(s => s.Label));
                yield return $"The permissions above can't be granted to {labels} ({group.Key.AdapterName}): {group.Key.Reason}";
            }
        }
    }

    // A projection of Validate() alone (#615): the empty-name and no-steps refusals live there with
    // the other validity rules, so restating them here was a second copy of the same opinion.
    public bool CanSave => !Validate().Any();

    /// <summary>
    /// Writes the workspace: <c>workflow.json</c> and <c>bindings.json</c> — through the same
    /// writers the file editors use, so guided output and hand-authored files can never diverge in
    /// format. Returns the two paths a run needs, or null when guidance is outstanding.
    /// </summary>
    public async Task<(string WorkflowFilePath, string BindingsFilePath)?> SaveAsync(CancellationToken cancellationToken = default)
    {
        RefreshStructure();
        if (!CanSave)
        {
            StatusText = "Not saved — finish the guidance items above.";
            return null;
        }

        var workspacePath = EffectiveWorkspacePath;
        Directory.CreateDirectory(workspacePath);

        var definition = new WorkflowDefinition(
            new WorkflowTemplateId(WorkflowName),
            WorkflowTemplateVersion: 1,
            [.. Steps.Select(step => step.BuildStepDefinition())]);
        var workflowFilePath = Path.Combine(workspacePath, "workflow.json");
        await WorkflowDefinitionWriter.SaveToFileAsync(definition, workflowFilePath, cancellationToken).ConfigureAwait(true);

        var bindings = new Dictionary<string, WorkerBindingConfigEntry>();
        foreach (var step in Steps)
        {
            bindings[step.Name] = await step.BuildBindingEntryAsync(workspacePath, cancellationToken).ConfigureAwait(true);
        }

        var bindingsFilePath = Path.Combine(workspacePath, "bindings.json"); // vocabulary-ok: technical file path
        await WorkerBindingConfigWriter.SaveToFileAsync(bindings, bindingsFilePath, cancellationToken).ConfigureAwait(true);

        StatusText = $"Saved to {workspacePath}.";
        return (workflowFilePath, bindingsFilePath);
    }

    [RelayCommand]
    private Task Save() => SaveAsync();

    [RelayCommand]
    private async Task SaveAndRun()
    {
        if (await SaveAsync().ConfigureAwait(true) is not { } paths)
        {
            return;
        }

        if (RunRequested is { } handler)
        {
            await handler(paths.WorkflowFilePath, paths.BindingsFilePath).ConfigureAwait(true);
        }
    }
}

/// <summary>
/// One authored step in the guided flow. Field names speak the vocabulary map ("what it
/// produces", "review gate", "who runs it"); <see cref="BuildStepDefinition"/>/
/// <see cref="BuildBindingEntryAsync"/> translate to the durable spec shapes. Vendor presets fill
/// the invocation side (timeout) from the layer that owns that knowledge —
/// <see cref="VendorCliPresence"/>'s adapters — never re-encoded here. Permissions are the one
/// exception: they're set once per workflow, not per
/// step (<see cref="NewWorkflowViewModel.BuildPermissionGrant"/>), and applied here at
/// <see cref="BuildBindingEntryAsync"/> time.
/// </summary>
public sealed partial class GuidedStepViewModel : ObservableObject
{
    /// <summary>The presets' default step timeout — generous for an interactive vendor CLI turn, still finite (§7's timeout discipline).</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(10);

    private readonly NewWorkflowViewModel _owner;

    public GuidedStepViewModel(NewWorkflowViewModel owner)
    {
        _owner = owner;
    }

    [ObservableProperty]
    private string name = string.Empty;

    [ObservableProperty]
    private GuidedStepKind kind = GuidedStepKind.Claude;

    /// <summary>The instruction for a single-vendor step — prose, so a text box is the right control (the "no raw JSON textareas" rule is about structure, not words).</summary>
    [ObservableProperty]
    private string prompt = string.Empty;

    [ObservableProperty]
    private string model = string.Empty;

    /// <summary>What this step produces — one file name, the contract's produced output.</summary>
    [ObservableProperty]
    private string producesFileName = string.Empty;

    [ObservableProperty]
    private bool hasReviewGate;

    public IReadOnlyList<GuidedStepKind> KindOptions { get; } =
        [GuidedStepKind.Claude, GuidedStepKind.Agy];

    public ObservableCollection<GuidedDependsOnOptionViewModel> DependsOnOptions { get; } = [];

    [RelayCommand]
    private void Remove() => _owner.RemoveStep(this);

    partial void OnNameChanged(string value) => _owner.RefreshStructure();

    partial void OnKindChanged(GuidedStepKind value) => _owner.RefreshStructure();

    internal void RefreshDependsOnOptions()
    {
        var selected = DependsOnOptions.Where(option => option.IsSelected).Select(option => option.StepName).ToHashSet();
        DependsOnOptions.Clear();
        foreach (var other in _owner.Steps)
        {
            if (!ReferenceEquals(other, this) && other.Name.Length > 0)
            {
                DependsOnOptions.Add(new GuidedDependsOnOptionViewModel(other.Name)
                {
                    IsSelected = selected.Contains(other.Name),
                });
            }
        }
    }

    internal IEnumerable<string> Validate()
    {
        var label = Name.Length > 0 ? $"'{Name}'" : "an unnamed step";

        if (Name.Length == 0)
        {
            yield return "Name every step — the name is how the plan and the graph refer to it.";
        }

        if (ProducesFileName.Length == 0)
        {
            yield return $"Say what {label} produces — the file name later steps and your review read.";
        }

        if (Prompt.Length == 0)
        {
            yield return $"Tell {label}'s runner what to do — the prompt is the step's instruction.";
        }
    }

    internal WorkflowStepDefinition BuildStepDefinition()
    {
        var dependsOn = DependsOnOptions
            .Where(option => option.IsSelected)
            .Select(option => new StepId(option.StepName))
            .ToList();

        // A review gate's send-back targets are the step's direct dependencies — the steps whose
        // work the reviewer is looking at. (No dependencies: the gate still pauses; there is
        // simply nowhere to send back to, matching §17's optional-targets shape.)
        return new WorkflowStepDefinition(
            new StepId(Name),
            Worker: Name,
            Inputs: [.. DependsOnOptions.Where(option => option.IsSelected).Select(option => option.StepName)
                .SelectMany(dependencyName => _owner.Steps
                    .Where(step => step.Name == dependencyName && step.ProducesFileName.Length > 0)
                    .Select(step => step.ProducesFileName))],
            Outputs: [ProducesFileName],
            DependsOn: dependsOn,
            RetryPolicy: new RetryPolicy(3),
            PausePoint: HasReviewGate ? new PausePoint(SupersedeTargets: dependsOn) : null);
    }

    internal Task<WorkerBindingConfigEntry> BuildBindingEntryAsync(string workspacePath, CancellationToken cancellationToken)
    {
        var requiredInputs = DependsOnOptions
            .Where(option => option.IsSelected)
            .SelectMany(option => _owner.Steps
                .Where(step => step.Name == option.StepName && step.ProducesFileName.Length > 0)
                .Select(step => step.ProducesFileName))
            .ToList();
        var contract = new WorkerContract(Name, requiredInputs, [new ProducedOutput(ProducesFileName)], []);

        var grant = _owner.BuildPermissionGrant();
        return Task.FromResult(new WorkerBindingConfigEntry(
            Kind == GuidedStepKind.Claude ? "claude" : "agy", // vocabulary-ok: technical adapter key
            contract,
            Prompt,
            DefaultTimeout,
            Model.Length > 0 ? Model : null,
            PermissionScope: null,
            PermissionGrant: grant.IsEmpty ? null : grant));
    }
}

/// <summary>One selectable "runs after" choice — the guided counterpart of M16's depends-on checkboxes.</summary>
public sealed partial class GuidedDependsOnOptionViewModel(string stepName) : ObservableObject
{
    public string StepName { get; } = stepName;

    [ObservableProperty]
    private bool isSelected;
}
