using System.Text.Json;
using Aer.Adapters;
using Aer.Flow.Domain;
using Aer.Flow.Status;
using Aer.Flow.Templates;

namespace Aer.Cli;

/// <summary>
/// <c>aer redispatch &lt;room-dir&gt;</c> (#1441) — the implementation of the contract spec/baton.md §2
/// states in full (what inherits vs. overrides, the two refusals, the `--output` exception, where
/// lineage lands); this type doc does not restate it. <see cref="DispatchCommand.CopyPrimaryOutputToOverride"/>
/// is the code reference for why a parent's <c>--output</c> destination cannot be recovered here — it
/// is a process-local copy target, never persisted to any room file.
/// </summary>
public static class RedispatchCommand
{
    private const string WorkflowFileName = "workflow.json";
    private const string BindingsFileName = "bindings.json";

    /// <exception cref="CliArgumentException">
    /// The parent room does not exist, has not reached a terminal state (still running, or never
    /// dispatched), bound more than one worker (a composed template), names a role the catalog no
    /// longer has (only reachable when <c>--spec</c> is given, since only then is the catalog
    /// consulted), or a given <c>--spec</c> file does not exist.
    /// </exception>
    public static async Task<CommandResult> ExecuteAsync(
        RedispatchOptions options,
        IReadOnlyDictionary<string, IWorkerAdapter> adapters,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(adapters);

        if (!Directory.Exists(options.ParentRoomDirectoryPath))
        {
            throw new CliArgumentException($"Parent room '{options.ParentRoomDirectoryPath}' does not exist.");
        }

        // Refuse a non-terminal parent (still running, or never dispatched) outright -- a
        // non-interactive CLI has no prompt to gate a "are you sure" behind, the same doctrine
        // DispatchOptionsParser's --timeout ceiling already rests on (spec/baton.md §2).
        var terminalSentinelPath = Path.Combine(options.ParentRoomDirectoryPath, TerminalSentinelWriter.TerminalSentinelFileName);
        if (!File.Exists(terminalSentinelPath))
        {
            throw new CliArgumentException(
                $"Parent room '{options.ParentRoomDirectoryPath}' has not reached a terminal state — "
                + "redispatch only reruns a room that has already finished.",
                $"wait for it to finish, or cancel it first, then retry: aer status {options.ParentRoomDirectoryPath}");
        }

        // A Succeeded parent needs no confirmation (there is none to ask, non-interactively); a
        // terminal-but-not-Succeeded parent is still allowed, but gets a stderr note rather than a
        // silent redispatch of a failed/cancelled lane.
        var parentTerminal = await TerminalSentinelWriter.TryReadAsync(options.ParentRoomDirectoryPath, cancellationToken)
            .ConfigureAwait(false);
        if (parentTerminal is not null && !string.Equals(parentTerminal.State, WorkflowOutcome.Succeeded, StringComparison.Ordinal))
        {
            Console.Error.WriteLine(
                $"Warning: parent room '{options.ParentRoomDirectoryPath}' did not succeed "
                + $"(state: {parentTerminal.State}) — redispatching it anyway.");
        }

        var parentBindingsPath = AerPaths.RoomBindingsFile(options.ParentRoomDirectoryPath);
        var parentBindings = await WorkerBindingConfigParser.LoadFromFileAsync(parentBindingsPath, cancellationToken)
            .ConfigureAwait(false);

        if (parentBindings.Count != 1)
        {
            throw new CliArgumentException(
                $"Parent room '{options.ParentRoomDirectoryPath}' dispatched {parentBindings.Count} workers — "
                + "redispatch only supports a single-role dispatch (aer dispatch <role> --spec ...), not a "
                + "composed template.");
        }

        var (workerName, parentEntry) = parentBindings.Single();

        WorkflowDefinition definition;
        WorkerBindingConfigEntry entry;
        if (options.SpecFilePath is null)
        {
            // No amended brief: reuse the parent's already-built prompt and step shape verbatim,
            // overriding only the axes the operator actually passed.
            var parentWorkflowPath = Path.Combine(options.ParentRoomDirectoryPath, WorkflowFileName);
            definition = await WorkflowDefinitionParser.LoadFromFileAsync(parentWorkflowPath, cancellationToken).ConfigureAwait(false);
            entry = InheritBinding(parentEntry, options);
        }
        else
        {
            (definition, entry) = await RebuildFromAmendedSpecAsync(workerName, parentEntry, options, cancellationToken)
                .ConfigureAwait(false);
        }

        if (options.Timeout is { } timeoutOverride && timeoutOverride > TimeSpan.FromMinutes(DispatchOptionsParser.WarnTimeoutMinutes))
        {
            Console.Error.WriteLine(
                $"Warning: --timeout {(int)timeoutOverride.TotalMinutes} exceeds "
                + $"{DispatchOptionsParser.WarnTimeoutMinutes} minutes (2h) — a typo here can strand a lane for a long time.");
        }

        Directory.CreateDirectory(options.RoomDirectoryPath);

        Console.Out.WriteLine($"Room directory: {options.RoomDirectoryPath}");
        Console.Out.WriteLine($"Redispatched from: {options.ParentRoomDirectoryPath}");

        // Lineage (#1441): recorded on the room marker, the room-metadata home spec/baton.md §2 already
        // names -- not a new parallel file. The parent's own execution id is cheap here: it is already
        // on parentTerminal, read above for the Succeeded/warning check.
        await InteractiveSessionMaterializer.WriteWorkflowRoomMarkerAsync(
            options.RoomDirectoryPath,
            parentRoomDirectoryPath: options.ParentRoomDirectoryPath,
            parentExecutionId: parentTerminal?.Steps.FirstOrDefault()?.Execution,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var workflowFilePath = Path.Combine(options.RoomDirectoryPath, WorkflowFileName);
        var bindingsFilePath = Path.Combine(options.RoomDirectoryPath, BindingsFileName);
        await WorkflowDefinitionWriter.SaveToFileAsync(definition, workflowFilePath, cancellationToken).ConfigureAwait(false);
        await WorkerBindingConfigWriter.SaveToFileAsync(
            new Dictionary<string, WorkerBindingConfigEntry> { [workerName] = entry }, bindingsFilePath, cancellationToken)
            .ConfigureAwait(false);

        var workspace = entry.WorkingDirectory ?? entry.Worktree?.Repository ?? Directory.GetCurrentDirectory();
        var runOptions = new RunOptions(workflowFilePath, bindingsFilePath, options.RoomDirectoryPath, ProjectRootDirectory: workspace);
        return await RunCommand.ExecuteAsync(runOptions, adapters, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The binding-inheritance rule for an unchanged spec: start from the parent's exact entry (grant,
    /// worktree intent, contract, already-built prompt included) and apply only the axes
    /// <paramref name="options"/> actually set, falling back to the parent's own recorded value for
    /// every axis left null -- adapter, model, effort, workspace, timeout. Public so it is unit-testable
    /// against a hand-built <see cref="WorkerBindingConfigEntry"/> without a room on disk, the same
    /// reusability <see cref="RoleDispatch.ToBinding"/> is public for.
    /// </summary>
    public static WorkerBindingConfigEntry InheritBinding(WorkerBindingConfigEntry parentEntry, RedispatchOptions options)
    {
        ArgumentNullException.ThrowIfNull(parentEntry);
        ArgumentNullException.ThrowIfNull(options);

        var adapter = options.Adapter ?? parentEntry.Adapter;

        // RoleDispatch.ToBinding's own vendor-swap axis rule, applied here too (#1082, spec/baton.md §2).
        var vendorSwapped = !string.Equals(adapter, parentEntry.Adapter, StringComparison.OrdinalIgnoreCase);
        var model = options.Model ?? (vendorSwapped ? null : parentEntry.Model);
        var effort = options.Effort ?? (vendorSwapped ? null : parentEntry.Effort);

        var workingDirectory = parentEntry.WorkingDirectory;
        var worktree = parentEntry.Worktree;
        if (options.WorkspaceDirectory is { } newWorkspace)
        {
            // The parent recorded its workspace in exactly one of these two fields -- ToBinding's
            // grant-audit branch decides which -- so override whichever is actually populated.
            if (parentEntry.WorkingDirectory is not null)
            {
                workingDirectory = newWorkspace;
            }

            if (parentEntry.Worktree is { } parentWorktree)
            {
                worktree = parentWorktree with { Repository = newWorkspace };
            }
        }

        return parentEntry with
        {
            Adapter = adapter,
            Model = model,
            Effort = effort,
            WorkingDirectory = workingDirectory,
            Worktree = worktree,
            Timeout = options.Timeout ?? parentEntry.Timeout,
            // A redispatch is a fresh worker turn, never a continuation of the parent's own session.
            SessionId = null,
            ResumeSession = false,
        };
    }

    /// <summary>
    /// The <c>--spec</c>-given path: an amended brief means the prompt must be rebuilt, so this goes
    /// back through <see cref="RoleDispatch.Materialize"/> -- the same primitive a fresh <c>aer
    /// dispatch</c> uses -- with the parent's recorded axes as the defaults <see cref="RedispatchOptions"/>'s
    /// own overrides win against.
    /// </summary>
    private static async Task<(WorkflowDefinition Definition, WorkerBindingConfigEntry Entry)> RebuildFromAmendedSpecAsync(
        string workerName, WorkerBindingConfigEntry parentEntry, RedispatchOptions options, CancellationToken cancellationToken)
    {
        if (!File.Exists(options.SpecFilePath))
        {
            throw new CliArgumentException($"Spec file '{options.SpecFilePath}' does not exist.");
        }

        try
        {
            var role = WorkerRoleCatalog.For(workerName);
            var spec = await File.ReadAllTextAsync(options.SpecFilePath!, cancellationToken).ConfigureAwait(false);
            var workspace = options.WorkspaceDirectory ?? parentEntry.WorkingDirectory ?? parentEntry.Worktree?.Repository;

            var (definition, bindings) = RoleDispatch.Materialize(
                role, spec,
                adapterOverride: options.Adapter ?? parentEntry.Adapter,
                workingDirectory: workspace,
                modelOverride: options.Model ?? parentEntry.Model,
                effortOverride: options.Effort ?? parentEntry.Effort,
                outputOverride: options.OutputPath,
                timeoutOverride: options.Timeout ?? parentEntry.Timeout);

            return (definition, bindings[role.Id]);
        }
        catch (Exception ex) when (ex is FileNotFoundException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            // Same translation DispatchCommand.MaterializeAsync applies: a catalog fault must reach
            // Program's typed boundary as a CliArgumentException, never a raw crash.
            throw new CliArgumentException(ex.Message);
        }
    }
}
