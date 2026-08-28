using System.Text.Json;
using Aer.Adapters;
using Aer.Flow.Domain;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Aer.Ui.Core;

/// <summary>
/// One worker-role row in the bindings editor (M16 Phase 4, issue #153) — the two-way-bound
/// per-entry state <see cref="BindingsEditorViewModel"/>'s <c>Entries</c> collection holds, the same
/// per-item child-ViewModel shape <see cref="PausedStepViewModel"/>/<c>SendBackTargetViewModel</c>
/// established (M15 Phase 3).
/// <para>
/// <b>Structured-vs-opaque editing decision of record (the phase's other named open question):</b>
/// <see cref="Adapter"/>, <see cref="PromptTemplate"/>, <see cref="TimeoutText"/>, <see cref="Model"/>,
/// <see cref="PermissionScope"/> — the entry's scalar fields — and
/// <see cref="WorkerContract.RequiredInputs"/>/<see cref="WorkerContract.OptionalMetadata"/> — its
/// two plain-string lists — all get real structured editing (a text box per scalar, a
/// comma-separated text box per string list: <see cref="RequiredInputsText"/>/
/// <see cref="OptionalMetadataText"/>). <see cref="WorkerContract.ProducedOutputs"/> does not: each
/// entry is itself a small record (<c>Name</c> plus an optional <c>OutputCondition</c> carrying a
/// <c>JsonScalar</c> sum type — string/number/bool/null), and a safe small editable surface for that
/// shape (per-item add/remove, a scalar-type picker) is new list-editing machinery this phase's
/// scope doesn't call for. <see cref="ProducedOutputsJson"/> round-trips it opaquely instead — a raw
/// JSON text box using the exact same <see cref="JsonSerializer"/> converters
/// <see cref="WorkerBindingConfigParser"/>/<see cref="WorkerBindingConfigWriter"/> use, so fidelity
/// (including <c>OutputCondition</c>) is guaranteed by construction rather than by a hand-written
/// mapping this phase would otherwise have to get right.
/// </para>
/// <para>
/// <b>M21 Phase 1's builder-vs-Advanced toggle:</b> <see cref="IsAdvancedPermissionScope"/> picks
/// between the pre-existing free-text <see cref="PermissionScope"/> box and the four
/// <c>Grant*</c>/<see cref="ShellCommandPatternsText"/> checkbox-and-list fields
/// <see cref="TryBuildEntry"/> composes into a <see cref="Adapters.PermissionGrant"/>. Exactly one
/// of the two is ever persisted on <see cref="TryBuildEntry"/>'s built entry — never both — mirroring
/// <see cref="WorkerInvocation"/>'s own documented precedence, so a saved file never carries an
/// ambiguous "which one wins" state. <see cref="Blank"/> defaults new rows to the builder (the
/// promoted, primary path per the phase plan); <see cref="FromEntry"/> instead derives the mode from
/// which field the loaded entry actually has set, so opening an existing hand-typed
/// <see cref="PermissionScope"/> round-trips into Advanced mode unchanged. If a hand-edited file
/// somehow has both set, loading lands in Builder mode (structured wins, matching the resolver) and
/// a subsequent Save normalizes the entry to that interpretation.
/// </para>
/// </summary>
public sealed partial class WorkerBindingEntryViewModel : ObservableObject
{
    private static readonly JsonSerializerOptions IndentedOptions = new() { WriteIndented = true };

    private readonly Action<WorkerBindingEntryViewModel> _onRemove;
    private readonly IReadOnlyDictionary<string, IWorkerAdapter> _adapterRegistry;

    /// <summary>
    /// Candidate adapter names offered from the registry <c>MainWindow</c> was constructed with
    /// (M15 Phase 1's decision of record) — reflect, don't invent (this phase's named open
    /// question). Carried per-entry rather than read from a shared/ancestor binding: inside an
    /// <c>ItemsControl.ItemTemplate</c> the bound <c>DataContext</c> is this entry, so an
    /// item-local <c>ItemsSource</c> binding is what actually resolves in Avalonia. Not a hard
    /// gate — <see cref="Adapter"/> stays a free-editable box seeded with these candidates, since
    /// nothing in <see cref="WorkerBindingConfigParser.Parse"/> validates an entry's <c>Adapter</c>
    /// against any registry either.
    /// </summary>
    public IReadOnlyList<string> AdapterCandidates { get; }

    [ObservableProperty]
    private string workerName = string.Empty;

    [ObservableProperty]
    private string adapter = string.Empty;

    [ObservableProperty]
    private string contractWorkerName = string.Empty;

    [ObservableProperty]
    private string requiredInputsText = string.Empty;

    [ObservableProperty]
    private string optionalMetadataText = string.Empty;

    [ObservableProperty]
    private string producedOutputsJson = "[]";

    [ObservableProperty]
    private string promptTemplate = string.Empty;

    [ObservableProperty]
    private string timeoutText = string.Empty;

    [ObservableProperty]
    private string model = string.Empty;

    /// <summary>
    /// Where this worker role's process should run (M23 Phase 3, #272) — a stopgap text field plus
    /// a folder-browse button (issue TBD, requested directly ahead of M24 Phase 3's fuller Known
    /// Projects picker) so a rooted absolute path can be set from the app instead of by hand-editing
    /// the bindings file. A bare profile name (resolved via <c>AerProfileStore</c> at run time) is
    /// still typeable here too — the browse button only ever writes a rooted path, it never
    /// interprets or validates a typed profile name.
    /// </summary>
    [ObservableProperty]
    private string workingDirectoryText = string.Empty;

    [ObservableProperty]
    private string permissionScope = string.Empty;

    [ObservableProperty]
    private bool isAdvancedPermissionScope;

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

    [ObservableProperty]
    private string permissionGrantGapWarning = string.Empty;

    /// <summary>The AuthorView visibility switch for the gap-warning line — matches this file's own established "expose a bool, don't bind on string length" convention (e.g. <see cref="IsAdvancedPermissionScope"/> elsewhere in AuthorView.axaml).</summary>
    public bool HasPermissionGrantGapWarning => !string.IsNullOrEmpty(PermissionGrantGapWarning);

    /// <summary>
    /// The shell-command-patterns box's own visibility needs both "builder mode" and "shell grant
    /// checked" — a compound condition Avalonia's single-property binding syntax can't express
    /// without a MultiBinding/converter this file's other bindings don't use, so it's a computed
    /// bool here instead (same reasoning as <see cref="HasPermissionGrantGapWarning"/>).
    /// </summary>
    public bool ShowShellCommandPatterns => !IsAdvancedPermissionScope && GrantRunShellCommands;

    private WorkerBindingEntryViewModel(
        IReadOnlyList<string> adapterCandidates,
        IReadOnlyDictionary<string, IWorkerAdapter> adapterRegistry,
        Action<WorkerBindingEntryViewModel> onRemove)
    {
        AdapterCandidates = adapterCandidates;
        _adapterRegistry = adapterRegistry;
        _onRemove = onRemove;
    }

    /// <summary>A freshly-added blank row (<c>BindingsEditorViewModel.AddEntry</c>) — nothing loaded from any file yet.</summary>
    public static WorkerBindingEntryViewModel Blank(
        IReadOnlyList<string> adapterCandidates,
        IReadOnlyDictionary<string, IWorkerAdapter> adapterRegistry,
        Action<WorkerBindingEntryViewModel> onRemove) =>
        new(adapterCandidates, adapterRegistry, onRemove)
        {
            TimeoutText = "00:05:00",
            ProducedOutputsJson = "[]",
            IsAdvancedPermissionScope = false,
        };

    /// <summary>
    /// Reconstructs one row from an already-parsed <see cref="WorkerBindingConfigEntry"/> —
    /// <c>BindingsEditorViewModel.LoadFrom</c>'s per-entry step.
    /// </summary>
    public static WorkerBindingEntryViewModel FromEntry(
        string workerName,
        WorkerBindingConfigEntry entry,
        IReadOnlyList<string> adapterCandidates,
        IReadOnlyDictionary<string, IWorkerAdapter> adapterRegistry,
        Action<WorkerBindingEntryViewModel> onRemove)
    {
        var vm = new WorkerBindingEntryViewModel(adapterCandidates, adapterRegistry, onRemove)
        {
            WorkerName = workerName,
            Adapter = entry.Adapter,
            ContractWorkerName = entry.Contract.WorkerName,
            RequiredInputsText = string.Join(", ", entry.Contract.RequiredInputs),
            OptionalMetadataText = string.Join(", ", entry.Contract.OptionalMetadata),
            ProducedOutputsJson = JsonSerializer.Serialize(entry.Contract.ProducedOutputs, IndentedOptions),
            PromptTemplate = entry.PromptTemplate,
            TimeoutText = entry.Timeout.ToString(),
            Model = entry.Model ?? string.Empty,
            WorkingDirectoryText = entry.WorkingDirectory ?? string.Empty,
            PermissionScope = entry.PermissionScope ?? string.Empty,
            IsAdvancedPermissionScope = entry.PermissionGrant is null,
            GrantReadFiles = entry.PermissionGrant?.ReadFiles ?? false,
            GrantWriteFiles = entry.PermissionGrant?.WriteFiles ?? false,
            GrantRunShellCommands = entry.PermissionGrant?.RunShellCommands ?? false,
            ShellCommandPatternsText = entry.PermissionGrant?.ShellCommandPatterns is { Count: > 0 } patterns
                ? string.Join(", ", patterns)
                : string.Empty,
            GrantNetworkAccess = entry.PermissionGrant?.NetworkAccess ?? false,
        };

        return vm;
    }

    [RelayCommand]
    private void Remove() => _onRemove(this);

    partial void OnAdapterChanged(string value) => RecomputePermissionGrantGapWarning();

    partial void OnIsAdvancedPermissionScopeChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowShellCommandPatterns));
        RecomputePermissionGrantGapWarning();
    }

    partial void OnGrantReadFilesChanged(bool value) => RecomputePermissionGrantGapWarning();

    partial void OnGrantWriteFilesChanged(bool value) => RecomputePermissionGrantGapWarning();

    partial void OnGrantRunShellCommandsChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowShellCommandPatterns));
        RecomputePermissionGrantGapWarning();
    }

    partial void OnShellCommandPatternsTextChanged(string value) => RecomputePermissionGrantGapWarning();

    partial void OnGrantNetworkAccessChanged(bool value) => RecomputePermissionGrantGapWarning();

    partial void OnPermissionGrantGapWarningChanged(string value) => OnPropertyChanged(nameof(HasPermissionGrantGapWarning));

    /// <summary>
    /// Live validation the builder UI shows inline (M21 Phase 1's "surface that gap in the UI
    /// explicitly rather than silently dropping or downgrading it") — re-run on every field that
    /// feeds a <see cref="Adapters.PermissionGrant"/>, plus <see cref="Adapter"/> itself since one of
    /// the refusals is adapter-specific.
    /// <para>
    /// Three things it can say, in the order they are asked. The adapter <b>does not enforce grants
    /// at all</b> — nothing ticked would reach dispatch (#657). The grant is <b>incoherent</b> —
    /// its shell defeats a withheld category, which is true on every adapter and so is asked before
    /// anything vendor-specific (#645). The adapter <b>cannot express</b> this grant — the vendor gap
    /// its own translator reports. Empty otherwise, including in Advanced mode and for an adapter
    /// name not in the registry, where there is nothing to check yet.
    /// </para>
    /// </summary>
    private void RecomputePermissionGrantGapWarning()
    {
        if (IsAdvancedPermissionScope)
        {
            PermissionGrantGapWarning = string.Empty;
            return;
        }

        if (!_adapterRegistry.TryGetValue(Adapter, out var adapter))
        {
            PermissionGrantGapWarning = string.Empty;
            return;
        }

        if (adapter is not IPermissionGrantTranslator translator)
        {
            // #657: the old wording — "no structured permission builder support, use Advanced
            // instead" — read as a note about the EDITOR, so an operator who ticked four boxes on a
            // noop worker had been told nothing that reads as "these will not apply".
            // They do not: neither adapter reads WorkerInvocation.PermissionGrant at all, and
            // WorkerAdapterRegistryTests (#651) holds that population to IPermissionGrantTranslator.
            PermissionGrantGapWarning =
                $"'{Adapter}' does not enforce permission grants — anything ticked here would be " // vocabulary-ok: technical setting name
                + "ignored at dispatch, not applied. Use Advanced to set a vendor's own flags.";
            return;
        }

        var grant = BuildPermissionGrant();
        if (grant.IsEmpty)
        {
            PermissionGrantGapWarning = string.Empty;
            return;
        }

        // #645: adapter-independent, and checked BEFORE translation because an incoherent grant is
        // wrong on every adapter -- reporting a vendor's expressiveness gap first would send an
        // operator to fix the wrong thing. The rule itself lives on PermissionGrant; restating its
        // conditions here is what would let this surface and the engine drift apart.
        if (grant.CategoriesDefeatedByTheShell is { Count: > 0 } defeated)
        {
            PermissionGrantGapWarning =
                $"These permissions can't be saved: {PermissionGrantWording.ShellDefeats(defeated)} "
                + "Tick them, or untick the shell.";
            return;
        }

        PermissionGrantGapWarning = translator.TryTranslatePermissionGrant(grant, out _, out var gapReason)
            ? string.Empty
            : gapReason ?? "This adapter cannot express the selected permissions."; // vocabulary-ok: technical setting name
    }

    private PermissionGrant BuildPermissionGrant() => new(
        GrantReadFiles,
        GrantWriteFiles,
        GrantRunShellCommands,
        SplitList(ShellCommandPatternsText),
        GrantNetworkAccess);

    /// <summary>
    /// Builds this row's <see cref="WorkerBindingConfigEntry"/> — the only place a malformed
    /// <see cref="TimeoutText"/> or <see cref="ProducedOutputsJson"/> is caught, since neither can
    /// be represented as the strongly-typed field until it parses (the same half-typed-value
    /// discipline <c>TemplateEditorViewModel.TemplateVersionText</c> established: kept as text while
    /// editing, parsed only at build/Save time). <see cref="WorkerBindingConfigWriter.Serialize"/>
    /// still re-validates <see cref="Adapter"/>/<see cref="PromptTemplate"/> blankness at save time —
    /// this only catches the two fields that would throw before a <see cref="WorkerBindingConfigEntry"/>
    /// could even be constructed.
    /// </summary>
    internal bool TryBuildEntry(out WorkerBindingConfigEntry? entry, out string? error)
    {
        if (!TimeSpan.TryParse(TimeoutText, out var timeout))
        {
            entry = null;
            error = $"Timeout '{TimeoutText}' for '{WorkerName}' is not a valid duration (e.g. 00:05:00).";
            return false;
        }

        List<ProducedOutput>? producedOutputs;
        try
        {
            producedOutputs = JsonSerializer.Deserialize<List<ProducedOutput>>(
                string.IsNullOrWhiteSpace(ProducedOutputsJson) ? "[]" : ProducedOutputsJson);
        }
        catch (JsonException ex)
        {
            entry = null;
            error = $"Produced-outputs JSON for '{WorkerName}' is invalid: {ex.Message}";
            return false;
        }

        if (producedOutputs is null)
        {
            entry = null;
            error = $"Produced-outputs JSON for '{WorkerName}' must be a JSON array.";
            return false;
        }

        string? permissionScope = null;
        PermissionGrant? permissionGrant = null;
        if (IsAdvancedPermissionScope)
        {
            permissionScope = string.IsNullOrWhiteSpace(PermissionScope) ? null : PermissionScope;
        }
        else
        {
            var grant = BuildPermissionGrant();
            if (!grant.IsEmpty)
            {
                // Mirrors PermissionGrantUnsupportedException's precedent (defense in depth at
                // dispatch time) one layer earlier, at Save time, so the gaps RecomputePermissionGrantGapWarning
                // already shows inline also block persisting a grant that is not what it looks like.
                if (_adapterRegistry.TryGetValue(Adapter, out var adapter))
                {
                    // #657: the ORDER of these two conjuncts was the defect. `adapter is
                    // IPermissionGrantTranslator translator && !translator.TryTranslate…` short-circuits
                    // for an adapter that implements nothing, so the guard never ran and the grant was
                    // persisted -- inert, but indistinguishable in the file from an enforced one.
                    if (adapter is not IPermissionGrantTranslator translator)
                    {
                        entry = null;
                        error = $"Permission grant for '{WorkerName}' can't be saved: '{Adapter}' does not " // vocabulary-ok: technical setting name
                            + "enforce permission grants, so these values would be stored as though they "
                            + "constrained the worker while being ignored at dispatch.";
                        return false;
                    }

                    // #645: same rule as the engine's, asked of the same object one layer earlier.
                    // Why it lives on PermissionGrant rather than in the resolver is recorded once,
                    // on PermissionGrant.CategoriesDefeatedByTheShell.
                    if (grant.CategoriesDefeatedByTheShell is { Count: > 0 } defeated)
                    {
                        entry = null;
                        error = $"Permission grant for '{WorkerName}' can't be saved: "
                            + PermissionGrantWording.ShellDefeats(defeated);
                        return false;
                    }

                    if (!translator.TryTranslatePermissionGrant(grant, out _, out var gapReason))
                    {
                        entry = null;
                        error = $"Permission grant for '{WorkerName}' can't be saved: {gapReason}";
                        return false;
                    }
                }

                permissionGrant = grant;
            }
        }

        var contract = new WorkerContract(
            string.IsNullOrWhiteSpace(ContractWorkerName) ? WorkerName : ContractWorkerName,
            SplitList(RequiredInputsText),
            producedOutputs,
            SplitList(OptionalMetadataText));

        entry = new WorkerBindingConfigEntry(
            Adapter,
            contract,
            PromptTemplate,
            timeout,
            string.IsNullOrWhiteSpace(Model) ? null : Model,
            permissionScope,
            permissionGrant,
            string.IsNullOrWhiteSpace(WorkingDirectoryText) ? null : WorkingDirectoryText);
        error = null;
        return true;
    }

    private static IReadOnlyList<string> SplitList(string text) =>
        text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
