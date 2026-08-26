using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Text.Json;
using Aer.Adapters;
using Aer.Flow.Concurrency;
using Aer.Workers.Dialogue;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Aer.Ui.Core;

/// <summary>
/// The worker-bindings editor's state (M16 Phase 4, issue #153) — the second authoring surface,
/// riding the same MVVM shape <see cref="TemplateEditorViewModel"/> established (M16 Phase 1):
/// in-memory editing against an explicit baseline with dirty tracking; <c>MainWindow</c>'s
/// New/Open-in-editor/Save bindings actions own all file I/O (the same state-in-VM/IO-in-window
/// split). This editor only ever touches bindings *files* (UI spec §4, §9) — bindings are
/// deliberately not template data; a room carries its own copy as its register (0056), and saving
/// here refuses to save over a live room's register (0057 rule 4).
/// <para>
/// <b>Dirty-tracking decision of record:</b> unlike <see cref="TemplateEditorViewModel"/>, this
/// cannot compare baseline-vs-candidate by record <c>==</c>, because a template save's candidate is
/// built with <c>baseline with { ... }</c> — reusing the same <c>Steps</c> list reference when
/// unchanged, so reference-equal collections make <c>==</c> already correct. A bindings save always
/// rebuilds a fresh <c>Dictionary</c> from the editable rows, so two structurally-identical configs
/// are never reference-equal. <see cref="ConfigEquals"/> is the deliberate manual deep-equality
/// check that replaces the <c>==</c> trick for this editor.
/// </para>
/// </summary>
public sealed partial class BindingsEditorViewModel : ObservableObject
{
    /// <summary>The state the current <see cref="Entries"/> are compared against for dirty tracking — what was last loaded from or saved to disk, or the empty config <see cref="StartNew"/> minted. <see langword="null"/> until an editing session starts.</summary>
    internal IReadOnlyDictionary<string, WorkerBindingConfigEntry>? Baseline { get; private set; }

    /// <summary>
    /// The dialogue sidecar content <see cref="Entries"/>' structured dialogue fields are compared
    /// against for dirty tracking (M23 Phase 1, #270) — keyed by worker name, populated alongside
    /// <see cref="Baseline"/> by <see cref="LoadFrom"/>. A <c>"dialogue"</c> entry's own
    /// <see cref="WorkerBindingConfigEntry"/> never changes when only its sidecar's *content*
    /// changes (the entry only carries the sidecar's *path*, in <see cref="WorkerBindingConfigEntry.PromptTemplate"/>),
    /// so <see cref="Baseline"/> comparison alone would miss that edit — this sits alongside it as a
    /// second, sibling baseline for the second, sibling artifact <see cref="SaveToFileAsync"/> writes.
    /// </summary>
    private IReadOnlyDictionary<string, DialogueWorkerConfig?> _dialogueBaseline = new Dictionary<string, DialogueWorkerConfig?>();

    /// <summary>Whether <see cref="Baseline"/> reflects a state that exists on disk — mirrors <see cref="TemplateEditorViewModel.BaselineIsPersisted"/>, though bindings have no version field to distinguish a first save on.</summary>
    internal bool BaselineIsPersisted { get; private set; }

    public ObservableCollection<WorkerBindingEntryViewModel> Entries { get; } = [];

    /// <summary>
    /// Advisory-only cross-check display (UI spec §9's worker-bindings grant, never a save gate):
    /// which <c>Worker</c> names the currently-open template declares that have no entry here.
    /// Populated only by <c>MainWindow.RefreshBindingsTemplateCrossCheck</c>, on demand — never
    /// automatically, since keeping it live would mean reaching into the template editor's own
    /// change notifications, which this phase deliberately does not touch (Phases 1-3 own template
    /// editing). Bindings are deliberately not template data (§9), so this can never gate <see cref="Entries"/> or Save.
    /// </summary>
    public ObservableCollection<string> MissingTemplateWorkerNames { get; } = [];

    /// <summary>The candidate adapter names offered to every row (M15 Phase 1's registry, reflected — never invented).</summary>
    private IReadOnlyList<string> _adapterCandidates = [];

    /// <summary>
    /// The adapter registry itself (M21 Phase 1) — threaded down to each row so its permission-grant
    /// builder can check <see cref="IPermissionGrantTranslator"/> support and live-validate a grant
    /// against the row's currently-selected <see cref="WorkerBindingEntryViewModel.Adapter"/>, the
    /// same "reflect, don't invent" seam <see cref="_adapterCandidates"/> already established for
    /// names alone.
    /// </summary>
    private IReadOnlyDictionary<string, IWorkerAdapter> _adapterRegistry = new Dictionary<string, IWorkerAdapter>();

    [ObservableProperty]
    private bool isOpen;

    [ObservableProperty]
    private bool isDirty;

    [ObservableProperty]
    private string statusText = string.Empty;

    public BindingsEditorViewModel()
    {
        Entries.CollectionChanged += OnEntriesCollectionChanged;
    }

    /// <summary>
    /// Wires the adapter registry every row is constructed with — called once by <c>MainWindow</c>'s
    /// constructor, the same "registry is a constructor argument" seam M15 Phase 1 established for
    /// adapter names. Derives <see cref="_adapterCandidates"/> from the same registry rather than
    /// taking both separately, so there is exactly one source of truth for "what adapters exist."
    /// </summary>
    internal void SetAdapterRegistry(IReadOnlyDictionary<string, IWorkerAdapter> adapterRegistry)
    {
        _adapterRegistry = adapterRegistry;
        _adapterCandidates = adapterRegistry.Keys.OrderBy(name => name, StringComparer.Ordinal).ToList();
    }

    /// <summary>Starts a fresh editing session over an empty config — nothing touches disk until Save.</summary>
    public void StartNew()
    {
        UnsubscribeAll();
        Baseline = new Dictionary<string, WorkerBindingConfigEntry>();
        _dialogueBaseline = new Dictionary<string, DialogueWorkerConfig?>();
        BaselineIsPersisted = false;
        Entries.Clear();
        IsOpen = true;
        StatusText = string.Empty;
        RecomputeIsDirty();
    }

    /// <summary>
    /// (Re)anchors the session to <paramref name="config"/> as the on-disk state — called by
    /// Open-in-editor after a parse, and by Save after a successful write. <paramref name="bindingsFilePath"/>
    /// resolves a <c>"dialogue"</c> entry's <see cref="WorkerBindingConfigEntry.PromptTemplate"/>
    /// sidecar path (relative paths resolve against the bindings file's own directory, the natural
    /// reading of "relative to this file") so its content can be loaded into
    /// <see cref="WorkerBindingEntryViewModel.DialogueParticipants"/> et al. for a true structured
    /// round trip (M23 Phase 1, #270); null when there is no bindings file yet (<see cref="StartNew"/>
    /// never calls this overload) or the caller has no path to resolve against. A sidecar that fails
    /// to load (missing file, malformed JSON) is not a load failure for the whole session — the
    /// affected row just falls back to <see cref="WorkerBindingEntryViewModel.FromEntry"/>'s default
    /// two-party seed, the same as a freshly-switched-to-dialogue row.
    /// </summary>
    public void LoadFrom(IReadOnlyDictionary<string, WorkerBindingConfigEntry> config, string? bindingsFilePath = null)
    {
        UnsubscribeAll();
        Baseline = config;
        BaselineIsPersisted = true;
        Entries.Clear();

        var dialogueBaseline = new Dictionary<string, DialogueWorkerConfig?>();
        foreach (var (workerName, entry) in config)
        {
            DialogueWorkerConfig? dialogueConfig = null;
            if (entry.Adapter == "dialogue")
            {
                dialogueConfig = TryLoadDialogueSidecar(entry.PromptTemplate, bindingsFilePath);
                dialogueBaseline[workerName] = dialogueConfig;
            }

            Entries.Add(WorkerBindingEntryViewModel.FromEntry(workerName, entry, _adapterCandidates, _adapterRegistry, RemoveEntry, dialogueConfig));
        }

        _dialogueBaseline = dialogueBaseline;
        IsOpen = true;
        RecomputeIsDirty();
    }

    /// <summary>Resolves <paramref name="sidecarPath"/> against <paramref name="bindingsFilePath"/>'s directory when relative — the shared path convention <see cref="LoadFrom"/>/<see cref="SaveToFileAsync"/> both use for a dialogue entry's sidecar.</summary>
    private static string ResolveSidecarPath(string sidecarPath, string? bindingsFilePath) =>
        Path.IsPathRooted(sidecarPath) || string.IsNullOrEmpty(bindingsFilePath)
            ? sidecarPath
            : Path.Combine(Path.GetDirectoryName(bindingsFilePath) ?? string.Empty, sidecarPath);

    private static DialogueWorkerConfig? TryLoadDialogueSidecar(string sidecarPath, string? bindingsFilePath)
    {
        if (string.IsNullOrWhiteSpace(sidecarPath))
        {
            return null;
        }

        try
        {
            var resolvedPath = ResolveSidecarPath(sidecarPath, bindingsFilePath);
            return DialogueWorkerConfigParser.Parse(File.ReadAllText(resolvedPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DialogueWorkerConfigException)
        {
            return null;
        }
    }

    /// <summary>Starts a fresh session with its user-facing status line — the New-bindings action's whole behavior, moved here from <c>MainWindow</c> when the file half of authoring moved into this type (M19 Phase 2, #187).</summary>
    public void StartNewFile()
    {
        StartNew();
        StatusText = "New worker-bindings file — add entries, then Save."; // vocabulary-ok: technical file concept
    }

    /// <summary>
    /// Opens <paramref name="bindingsFilePath"/> into the editor (M16 Phase 4, issue #153) via
    /// <see cref="BindingsProjectionLoader"/> — never a second parser. Moved here from
    /// <c>MainWindow</c> code-behind (M19 Phase 2, #187), same failure behavior as
    /// <see cref="TemplateEditorViewModel.OpenFromFileAsync"/>: a failed open must not leave a
    /// previous session's rows behind.
    /// </summary>
    public async Task OpenFromFileAsync(string bindingsFilePath, CancellationToken cancellationToken = default)
    {
        try
        {
            var config = await BindingsProjectionLoader.LoadAsync(bindingsFilePath, cancellationToken).ConfigureAwait(true);
            LoadFrom(config, bindingsFilePath);
            StatusText = string.Empty;
        }
        catch (Aer.Flow.AerFlowException ex)
        {
            Close();
            StatusText = ex.Message;
        }
    }

    /// <summary>
    /// Saves the editor's current rows to <paramref name="bindingsFilePath"/> through
    /// <c>WorkerBindingConfigWriter</c>, so the saved file round-trips through the exact
    /// <c>WorkerBindingConfigParser.Parse</c> every other consumer uses (M16 Phase 4, issue #153).
    /// Unlike the template editor's save, there is no version field to increment — a bindings file
    /// has no §11.1 counterpart.
    /// </summary>
    public async Task SaveToFileAsync(string bindingsFilePath, CancellationToken cancellationToken = default)
    {
        if (!IsOpen)
        {
            StatusText = "Nothing to save — create a new bindings file or open one in the editor first."; // vocabulary-ok: technical file concept
            return;
        }

        if (string.IsNullOrWhiteSpace(bindingsFilePath))
        {
            StatusText = "Enter a bindings file path to save to."; // vocabulary-ok: technical file concept
            return;
        }

        if (IsLiveRoomRegister(bindingsFilePath))
        {
            // Every remedy named here was checked to exist. An earlier draft offered "withdraw a
            // permission ... in the room's chat", and no client calls the revoke route at all
            // (#1272) — the daemon's own unbound-room message was corrected twice for exactly that,
            // and this message reached review carrying the same defect.
            StatusText = "Baton won't save over the worker setup of a room that is already using it. "
                + "To change this room's workers, edit your own copy of the file and Run the room again. "
                + "To grant a permission, answer the request where the room asks for it.";
            return;
        }

        // A dialogue row with no PromptTemplate yet gets an owned default sidecar file name (the
        // same "dialogue-<step>.json" convention the guided wizard already uses) — so authoring a
        // dialogue step through the structured fields never requires typing a path by hand either.
        foreach (var entryVm in Entries)
        {
            if (entryVm.IsDialogueAdapter && string.IsNullOrWhiteSpace(entryVm.PromptTemplate) && !string.IsNullOrWhiteSpace(entryVm.WorkerName))
            {
                entryVm.PromptTemplate = $"dialogue-{entryVm.WorkerName}.json";
            }
        }

        if (!TryBuildConfig(out var config, out var buildError))
        {
            StatusText = buildError!;
            return;
        }

        if (BaselineIsPersisted && !IsDirty)
        {
            // Same no-write, no-op-save precedent as the template editor's save (M16 Phase 1): the
            // file already contains exactly this state.
            StatusText = "No changes to save.";
            return;
        }

        if (!TryBuildDialogueConfigs(out var dialogueConfigs, out var dialogueError))
        {
            StatusText = dialogueError!;
            return;
        }

        try
        {
            foreach (var (workerName, dialogueConfig) in dialogueConfigs!)
            {
                var sidecarPath = ResolveSidecarPath(config![workerName].PromptTemplate, bindingsFilePath);
                var sidecarDirectory = Path.GetDirectoryName(sidecarPath);
                if (!string.IsNullOrEmpty(sidecarDirectory))
                {
                    Directory.CreateDirectory(sidecarDirectory);
                }

                await File.WriteAllTextAsync(
                    sidecarPath,
                    JsonSerializer.Serialize(dialogueConfig, new JsonSerializerOptions { WriteIndented = true }),
                    cancellationToken).ConfigureAwait(true);
            }

            await WorkerBindingConfigWriter.SaveToFileAsync(config!, bindingsFilePath, cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is Aer.Flow.AerFlowException or IOException or UnauthorizedAccessException)
        {
            // A config that fails to round-trip through the parser (blank Adapter/PromptTemplate) or
            // an unwritable path renders as the editor's status message.
            StatusText = ex.Message;
            return;
        }

        // Re-anchor dirty tracking to what the file (and every dialogue sidecar) now actually
        // contains.
        LoadFrom(config!, bindingsFilePath);
        StatusText = $"Saved '{bindingsFilePath}' ({config!.Count} entr{(config.Count == 1 ? "y" : "ies")}).";
    }

    /// <summary>
    /// Builds every <c>"dialogue"</c> entry's sidecar <see cref="DialogueWorkerConfig"/> from its
    /// structured fields (M23 Phase 1, #270) — <see cref="SaveToFileAsync"/>'s pre-write validation
    /// pass for the second, sibling artifact alongside <see cref="TryBuildConfig"/>'s own.
    /// </summary>
    private bool TryBuildDialogueConfigs(out Dictionary<string, DialogueWorkerConfig>? configs, out string? error)
    {
        var built = new Dictionary<string, DialogueWorkerConfig>();
        foreach (var entryVm in Entries)
        {
            if (!entryVm.IsDialogueAdapter)
            {
                continue;
            }

            if (!entryVm.TryBuildDialogueConfig(out var dialogueConfig, out var dialogueError))
            {
                configs = null;
                error = dialogueError;
                return false;
            }

            built[entryVm.WorkerName] = dialogueConfig!;
        }

        configs = built;
        error = null;
        return true;
    }

    /// <summary>
    /// Recomputes <see cref="MissingTemplateWorkerNames"/> (UI spec §9's advisory cross-check, M16
    /// Phase 4's named open question) against <paramref name="templateBaseline"/> — the template
    /// editor's own in-memory state, passed in by the caller rather than reached for, so this stays
    /// a read-only consultation of already-computed state. Advisory display only, never a save gate
    /// (§9): <see cref="SaveToFileAsync"/> never consults this.
    /// </summary>
    public void RefreshTemplateCrossCheck(Aer.Flow.Domain.WorkflowDefinition? templateBaseline)
    {
        MissingTemplateWorkerNames.Clear();

        if (!IsOpen || templateBaseline is null)
        {
            return;
        }

        var boundWorkerNames = Entries.Select(entry => entry.WorkerName).ToHashSet();
        foreach (var workerName in templateBaseline.Steps.Select(step => step.Worker).Distinct())
        {
            if (!boundWorkerNames.Contains(workerName))
            {
                MissingTemplateWorkerNames.Add(workerName);
            }
        }
    }

    /// <summary>Ends the editing session (a failed Open-in-editor must not leave a stale one behind).</summary>
    public void Close()
    {
        UnsubscribeAll();
        Baseline = null;
        _dialogueBaseline = new Dictionary<string, DialogueWorkerConfig?>();
        BaselineIsPersisted = false;
        Entries.Clear();
        IsOpen = false;
        MissingTemplateWorkerNames.Clear();
        RecomputeIsDirty();
    }

    /// <summary>Adds one blank row, ready for editing.</summary>
    public void AddEntry() => Entries.Add(WorkerBindingEntryViewModel.Blank(_adapterCandidates, _adapterRegistry, RemoveEntry));

    private void RemoveEntry(WorkerBindingEntryViewModel entry) => Entries.Remove(entry);

    /// <summary>
    /// Builds a <see cref="WorkerBindingConfigEntry"/> dictionary from <see cref="Entries"/>'
    /// current field state — the row-level counterpart of a save attempt, also used by
    /// <see cref="RecomputeIsDirty"/> so dirty tracking and Save agree on what "current" means.
    /// </summary>
    internal bool TryBuildConfig(out Dictionary<string, WorkerBindingConfigEntry>? config, out string? error)
    {
        var built = new Dictionary<string, WorkerBindingConfigEntry>();

        foreach (var entryVm in Entries)
        {
            if (string.IsNullOrWhiteSpace(entryVm.WorkerName))
            {
                config = null;
                error = "Every entry needs a worker role name.";
                return false;
            }

            if (built.ContainsKey(entryVm.WorkerName))
            {
                config = null;
                error = $"Duplicate worker role name '{entryVm.WorkerName}'.";
                return false;
            }

            if (!entryVm.TryBuildEntry(out var entry, out var entryError))
            {
                config = null;
                error = entryError;
                return false;
            }

            built[entryVm.WorkerName] = entry!;
        }

        config = built;
        error = null;
        return true;
    }

    private void OnEntriesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
        {
            foreach (WorkerBindingEntryViewModel entry in e.NewItems)
            {
                entry.PropertyChanged += OnEntryPropertyChanged;
            }
        }

        if (e.OldItems is not null)
        {
            foreach (WorkerBindingEntryViewModel entry in e.OldItems)
            {
                entry.PropertyChanged -= OnEntryPropertyChanged;
            }
        }

        RecomputeIsDirty();
    }

    private void OnEntryPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => RecomputeIsDirty();

    private void UnsubscribeAll()
    {
        foreach (var entry in Entries)
        {
            entry.PropertyChanged -= OnEntryPropertyChanged;
        }
    }

    internal void RecomputeIsDirty()
    {
        if (!IsOpen || Baseline is not { } baseline)
        {
            IsDirty = false;
            return;
        }

        if (!BaselineIsPersisted)
        {
            IsDirty = true;
            return;
        }

        IsDirty = !TryBuildConfig(out var current, out _)
            || !ConfigEquals(current!, baseline)
            || !DialogueBaselineEquals();
    }

    /// <summary>
    /// Whether every current dialogue row's structured content still matches
    /// <see cref="_dialogueBaseline"/> (M23 Phase 1, #270) — the dirty check
    /// <see cref="ConfigEquals"/> alone can't make, since a dialogue sidecar's content is invisible
    /// to <see cref="WorkerBindingConfigEntry"/> equality (see <see cref="_dialogueBaseline"/>'s own
    /// remarks). A row whose structured fields don't yet build a valid <see cref="DialogueWorkerConfig"/>
    /// counts as dirty rather than throwing here — <see cref="SaveToFileAsync"/> is where an
    /// in-progress edit's validation error actually surfaces.
    /// </summary>
    private bool DialogueBaselineEquals()
    {
        foreach (var entryVm in Entries)
        {
            if (!entryVm.IsDialogueAdapter)
            {
                continue;
            }

            _dialogueBaseline.TryGetValue(entryVm.WorkerName, out var baselineConfig);

            if (!entryVm.TryBuildDialogueConfig(out var currentConfig, out _))
            {
                return false;
            }

            if (!DialogueConfigEquals(currentConfig, baselineConfig))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Manual field-by-field comparison, not <see cref="DialogueWorkerConfig"/>'s own record
    /// <c>==</c> — the same reason <see cref="EntryEquals"/> hand-compares <see cref="WorkerContract"/>'s
    /// list-typed members instead of trusting record equality: <see cref="DialogueWorkerConfig.Participants"/>
    /// (and each entry's own <see cref="DialogueParticipant.Args"/>) breaks structural equality the
    /// identical way.
    /// </summary>
    private static bool DialogueConfigEquals(DialogueWorkerConfig? a, DialogueWorkerConfig? b)
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }

        return a.SeedPrompt == b.SeedPrompt
            && a.TurnBudget == b.TurnBudget
            && a.FinalOutputName == b.FinalOutputName
            && a.TurnTimeout == b.TurnTimeout
            && a.FinalOutputMode == b.FinalOutputMode
            && a.Participants.Count == b.Participants.Count
            && a.Participants.Zip(b.Participants, DialogueParticipantEquals).All(equal => equal);
    }

    private static bool DialogueParticipantEquals(DialogueParticipant a, DialogueParticipant b) =>
        a.Role == b.Role
        && a.Vendor == b.Vendor
        && a.Model == b.Model
        && a.Preamble == b.Preamble
        && a.Command == b.Command
        && a.Args.SequenceEqual(b.Args);

    private static bool ConfigEquals(
        IReadOnlyDictionary<string, WorkerBindingConfigEntry> a,
        IReadOnlyDictionary<string, WorkerBindingConfigEntry> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        foreach (var (key, entry) in a)
        {
            if (!b.TryGetValue(key, out var other) || !EntryEquals(entry, other))
            {
                return false;
            }
        }

        return true;
    }

    private static bool EntryEquals(WorkerBindingConfigEntry a, WorkerBindingConfigEntry b) =>
        a.Adapter == b.Adapter
        && a.Contract.WorkerName == b.Contract.WorkerName
        && a.Contract.RequiredInputs.SequenceEqual(b.Contract.RequiredInputs)
        && a.Contract.ProducedOutputs.SequenceEqual(b.Contract.ProducedOutputs)
        && a.Contract.OptionalMetadata.SequenceEqual(b.Contract.OptionalMetadata)
        && a.PromptTemplate == b.PromptTemplate
        && a.Timeout == b.Timeout
        && a.Model == b.Model
        && a.PermissionScope == b.PermissionScope
        && a.WorkingDirectory == b.WorkingDirectory
        && PermissionGrantEquals(a.PermissionGrant, b.PermissionGrant);

    /// <summary>
    /// Manual field-by-field comparison, not <see cref="PermissionGrant"/>'s own record
    /// <c>==</c> — the same reason <see cref="EntryEquals"/> already hand-compares
    /// <c>RequiredInputs</c>/<c>ProducedOutputs</c>/<c>OptionalMetadata</c> instead of trusting
    /// <see cref="WorkerContract"/>'s equality: a list-typed member breaks record structural
    /// equality (list/array equality is reference-based, not element-wise). A null
    /// <see cref="PermissionGrant.ShellCommandPatterns"/> and an empty one are treated as equal so a
    /// round trip through JSON (which deserializes an absent array as <see langword="null"/> but a
    /// serialized empty one as <c>[]</c>) never reads as a spurious dirty state.
    /// </summary>
    private static bool PermissionGrantEquals(PermissionGrant? a, PermissionGrant? b)
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }

        return a.ReadFiles == b.ReadFiles
            && a.WriteFiles == b.WriteFiles
            && a.RunShellCommands == b.RunShellCommands
            && a.NetworkAccess == b.NetworkAccess
            && (a.ShellCommandPatterns ?? []).SequenceEqual(b.ShellCommandPatterns ?? []);
    }

    /// <summary>
    /// 0057 Rule 4: detection of a live room's register is local evidence in its directory
    /// (<c>room.jsonl</c>, <c>flow.jsonl</c>, <c>snapshot.json</c>, <c>flow.lock</c>), never a path
    /// prefix like under AerPaths.Rooms.
    /// </summary>
    internal static bool IsLiveRoomRegister(string bindingsFilePath)
    {
        if (string.IsNullOrWhiteSpace(bindingsFilePath))
        {
            return false;
        }

        var fileName = Path.GetFileName(bindingsFilePath);
        if (!string.Equals(fileName, AerPaths.RoomBindingsFileName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string? directoryPath;
        try
        {
            directoryPath = Path.GetDirectoryName(Path.GetFullPath(bindingsFilePath));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        if (string.IsNullOrEmpty(directoryPath) || !Directory.Exists(directoryPath))
        {
            return false;
        }

        // `flow.lock` comes from the constant that owns it; the other three have no canonical home
        // anywhere in the repo yet and are literals until #1271 gives them one. `terminal.json`
        // (#1356) is a fifth room-identifying file, added straight from the constant that owns it
        // rather than repeating the same drift for a file introduced today.
        string[] roomEvidenceFiles =
        [
            "room.jsonl", "flow.jsonl", "snapshot.json", ConcurrencyGuard.FlowLockFileName,
            Aer.Cli.TerminalSentinelWriter.TerminalSentinelFileName,
        ];
        foreach (var evidenceFile in roomEvidenceFiles)
        {
            if (File.Exists(Path.Combine(directoryPath, evidenceFile)))
            {
                return true;
            }
        }

        return false;
    }
}
