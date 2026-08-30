using System.Text.Json;
using Baton.Vendors;
using Baton.Domain;
using Baton.Status;
using Baton.Templates;

namespace Baton.Cli;

/// <summary>
/// <c>baton dispatch &lt;name&gt;</c> (#900 role dispatch, widened for rung-3 composed templates, #920):
/// resolves <see cref="DispatchOptions.Name"/> as either a worker role (single-step, via
/// <see cref="RoleDispatch"/>, against a <c>--spec</c>) or a workflow template (a composed multi-phase
/// DAG, via <see cref="WorkflowTemplateComposer"/>) — one namespace, decision 0047 §5. Either way it
/// persists the same <c>workflow.json</c>/<c>bindings.json</c> and hands them to
/// <see cref="RunCommand.ExecuteAsync"/>, so outputs are contract-checked by the very pump <c>baton run</c>
/// drives. A template that declares a capture step (0047 §4) gets its base ref — the workspace HEAD at
/// this moment — captured and injected here, the git-aware entrypoint, before the run begins.
/// </summary>
public static class DispatchCommand
{
    private const string WorkflowFileName = "workflow.json";
    private const string BindingsFileName = "bindings.json";

    /// <exception cref="CliArgumentException">
    /// <paramref name="options"/> names neither a role nor a template (or names both), a role without a
    /// <c>--spec</c> or a template with one, a missing spec file, a non-git workspace behind a capture
    /// step, or a catalog that is itself unreadable — every resolution failure is translated so it exits
    /// cleanly through <c>Program</c>'s typed boundary rather than as a raw stack trace.
    /// </exception>
    /// <param name="workspaceDirectory">
    /// The git workspace a capture step operates in — where its base ref is captured <em>and</em> where
    /// its <c>git diff</c> runs (the injection pins both to this one directory, so they cannot diverge).
    /// The process directory in production; left overridable so a test can point a capture at a repo it
    /// controls rather than racing on the process-global current directory. Null resolves to the cwd.
    /// Note it governs the capture step only — a role phase's own working directory is unchanged.
    /// </param>
    public static async Task<CommandResult> ExecuteAsync(
        DispatchOptions options,
        IReadOnlyDictionary<string, IWorkerAdapter> adapters,
        CancellationToken cancellationToken = default,
        string? workspaceDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(adapters);

        var workspace = options.WorkspaceDirectory ?? workspaceDirectory ?? Directory.GetCurrentDirectory();
        var (definition, bindings) = await MaterializeAsync(options, workspace, cancellationToken).ConfigureAwait(false);

        // R1 (#1354/#1380): disclose the consequence up front, before the run starts, whenever
        // RoleDispatch.ToBinding declared a fresh worktree for an audited role — the worker then never
        // sees uncommitted or staged changes in `workspace`, only what HEAD already had (finding 5).
        string? workspaceFact = null;
        if (bindings.Values.Any(b => b.Worktree is not null))
        {
            var headSha = await WorkspaceHead.CaptureAsync(workspace, cancellationToken).ConfigureAwait(false);
            var shortSha = headSha.Length > 8 ? headSha[..8] : headSha;
            workspaceFact = $"Workspace: worktree of {workspace} at HEAD ({shortSha}) — uncommitted changes are not visible to the worker";
        }

        // #1442: warn-don't-refuse above the caution threshold — rationale in spec/baton.md §2.
        if (options.Timeout is { } timeoutOverride && timeoutOverride > TimeSpan.FromMinutes(DispatchOptionsParser.WarnTimeoutMinutes))
        {
            Console.Error.WriteLine(
                $"Warning: --timeout {(int)timeoutOverride.TotalMinutes} exceeds "
                + $"{DispatchOptionsParser.WarnTimeoutMinutes} minutes (2h) — a typo here can strand a lane for a long time.");
        }

        Directory.CreateDirectory(options.RoomDirectoryPath);

        var primaryOutputName = definition.Steps.FirstOrDefault()?.Outputs.FirstOrDefault() ?? "output";
        Console.Out.WriteLine($"Room directory: {options.RoomDirectoryPath}");
        if (workspaceFact is not null)
        {
            Console.Out.WriteLine(workspaceFact);
        }

        // #1355: the least-privilege grant profile actually in force, so the invoking agent can relay
        // it to its own permission layer honestly. Extends the same printing seam as
        // workspaceFact/output-path above rather than building a second one -- one line per bound
        // worker whose adapter actually consumes a grant, which for a single-role dispatch (the common
        // case) is the one line the issue asks for.
        //
        // F2: a grant is only "what the worker can do" for an adapter that consumes it --
        // WorkerBindingResolver.cs:137-141 already draws this population as `is IPermissionGrantTranslator`
        // (checked against this same `adapters` registry, the one WorkerBindingResolver.Resolve is
        // handed downstream via RunCommand). A binding bound to an adapter outside that population
        // (e.g. a composed template's capture step, which spawns git directly) never had its grant
        // consumed, so its "no-shell"/"no-network" would be false in the only sense an invoking agent's
        // permission layer cares about. Skip it -- no placeholder line either.
        var translatorBindings = bindings
            .Where(pair => adapters.TryGetValue(pair.Value.Adapter, out var boundAdapter) && boundAdapter is IPermissionGrantTranslator)
            .ToList();
        var multipleWorkers = translatorBindings.Count > 1;
        foreach (var (workerName, binding) in translatorBindings)
        {
            var label = multipleWorkers ? $"Grant ({workerName})" : "Grant";
            Console.Out.WriteLine($"{label}: {DescribeGrant(binding)}");
        }

        // R4 (#1354/#1380): the execution-scoped artifact path isn't known until dispatch actually runs,
        // so without --output the only truthful thing to print beforehand is the artifacts directory
        // itself, labeled as a directory — not a fabricated per-execution file path that will not exist
        // (finding 4).
        if (options.OutputPath is not null)
        {
            Console.Out.WriteLine($"Output path: {options.OutputPath}");
        }
        else
        {
            var artifactsDirectory = Path.Combine(options.RoomDirectoryPath, Baton.Artifacts.ArtifactManager.ArtifactsDirectoryName);
            Console.Out.WriteLine($"Artifacts directory: {artifactsDirectory} (each execution's outputs land in its own subdirectory under it)");
        }

        Console.Out.WriteLine($"Completion signal: process exit code or {Path.Combine(options.RoomDirectoryPath, TerminalSentinelWriter.TerminalSentinelFileName)}");

        var workflowFilePath = Path.Combine(options.RoomDirectoryPath, WorkflowFileName);
        var bindingsFilePath = Path.Combine(options.RoomDirectoryPath, BindingsFileName);
        await WorkflowDefinitionWriter.SaveToFileAsync(definition, workflowFilePath, cancellationToken).ConfigureAwait(false);
        await WorkerBindingConfigWriter.SaveToFileAsync(bindings, bindingsFilePath, cancellationToken).ConfigureAwait(false);

        var runOptions = new RunOptions(
            workflowFilePath, bindingsFilePath, options.RoomDirectoryPath, options.WorkflowId,
            ProjectRootDirectory: workspace);
        var result = await RunCommand.ExecuteAsync(runOptions, adapters, cancellationToken: cancellationToken).ConfigureAwait(false);

        if (options.OutputPath is not null && result.State.Status == WorkflowStatus.Terminal)
        {
            CopyPrimaryOutputToOverride(options, result, primaryOutputName);
        }

        return result;
    }

    /// <summary>
    /// R3 (#1354/#1380, finding 3): this copy must never be the thing that kills the process before
    /// <c>Program</c> writes <c>terminal.json</c> (#1374's completion contract) — an existing-directory
    /// destination, a read-only target, or a file another process still holds open all throw
    /// <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/>, neither of which derives
    /// from <see cref="BatonFlowException"/>, so neither of <c>Program</c>'s typed catches would have
    /// handled it. Report on stderr and return, letting the normal exit path run — the workflow has
    /// already reached Terminal and its declared output already exists at <c>srcPath</c> regardless of
    /// whether this copy succeeds.
    /// </summary>
    private static void CopyPrimaryOutputToOverride(DispatchOptions options, CommandResult result, string primaryOutputName)
    {
        var step = result.State.Steps.FirstOrDefault(s => s.Status == StepStatus.Succeeded);
        if (step is null || step.LatestExecutionId is not { } execId)
        {
            return;
        }

        var srcPath = Path.Combine(
            options.RoomDirectoryPath, Baton.Artifacts.ArtifactManager.ArtifactsDirectoryName, $"execution_{execId}", primaryOutputName);
        if (!File.Exists(srcPath))
        {
            return;
        }

        try
        {
            var destPath = Path.GetFullPath(options.OutputPath!);
            var destDir = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(destDir))
            {
                Directory.CreateDirectory(destDir);
            }

            File.Copy(srcPath, destPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(
                $"Could not copy the declared output to '{options.OutputPath}': {ex.Message}. "
                + $"The output still exists at '{srcPath}'.");
        }
    }

    /// <summary>
    /// Same category vocabulary <c>FakeEchoWorkerAdapter</c>'s translator uses in the test suite
    /// (read/write/shell/network, negated with a <c>no-</c> prefix) -- one register for "what a grant
    /// says", not a second one invented for this printed line.
    /// </summary>
    /// <remarks>
    /// #1355 F1: the <see cref="GrantAuditMode.AuditedNotEnforced"/> branch must say only what that
    /// mode's own doc says is true (<see cref="GrantAuditMode"/>'s remarks) -- the grant EXCEEDS the
    /// role's intent because the vendor hook cannot path-scope it, not "scoped to declared outputs".
    /// What actually bounds the write is the hook confining write-family tools to the worktree/outbox
    /// (<c>AgyHookCheckCommand</c>'s write-family check) -- i.e. every file in the provisioned
    /// worktree -- with declared-output confinement checked only AFTER the run, by
    /// <c>OutcomeClassifier</c>'s worktree-cleanliness audit. Do not restate the two mechanisms here
    /// beyond naming them (record-once); the citations above are the source, this line is the gloss.
    /// </remarks>
    private static string DescribeGrant(WorkerBindingConfigEntry binding)
    {
        var grant = binding.PermissionGrant;
        if (grant is null)
        {
            return "unset (falls back to the adapter's raw PermissionScope)";
        }

        var write = grant.WriteFiles
            ? binding.GrantAuditMode == GrantAuditMode.AuditedNotEnforced
                ? "write (workspace-wide inside an isolated worktree; audited against declared outputs after the run)"
                : "write"
            : "no-write";

        // #1456: an unqualified "shell" would understate a pattern-scoped grant (review's) the same
        // way an unqualified "write" would understate an audited one above -- this line exists so the
        // invoking agent can relay the actual grant honestly, and "shell" alone reads as unscoped.
        var shell = grant.RunShellCommands
            ? grant.ShellCommandPatterns is { Count: > 0 } patterns
                ? $"shell (scoped: {string.Join(", ", patterns)})"
                : "shell"
            : "no-shell";

        return string.Join(
            ", ",
            grant.ReadFiles ? "read" : "no-read",
            write,
            shell,
            grant.NetworkAccess ? "network" : "no-network");
    }

    private static async Task<(WorkflowDefinition Definition, IReadOnlyDictionary<string, WorkerBindingConfigEntry> Bindings)>
        MaterializeAsync(DispatchOptions options, string workspaceDirectory, CancellationToken cancellationToken)
    {
        try
        {
            // The catalog reads are the fail-loud set both catalogs share: a missing file (FileNotFound),
            // malformed JSON (JsonException), a structural fault (InvalidOperationException — duplicate id,
            // empty outputs, capture-id collision), or a phase naming a role the catalog lacks
            // (KeyNotFoundException, via WorkerRoleCatalog.For). None derive from BatonFlowException, so
            // without this they escape Program's boundary as a crash rather than the clean exit promised.
            // This wraps the WHOLE materialization, not just the isTemplate/isRole probes: a template
            // dispatch re-reads the catalog fresh during composition (WorkflowTemplateCatalog.For, and
            // WorkerRoleCatalog.For per phase — All => Load() opens the file on every access, it is not
            // cached), and a fault there must surface as a typed CliArgumentException too (#929). The
            // deliberate CliArgumentException throws below (and WorkspaceHead's non-git refusal) are not in
            // the filter, so they pass through unwrapped.
            var isTemplate = WorkflowTemplateCatalog.All.Any(t => string.Equals(t.Id, options.Name, StringComparison.Ordinal));
            var isRole = WorkerRoleCatalog.All.Any(r => string.Equals(r.Id, options.Name, StringComparison.Ordinal));

            if (isTemplate && isRole)
            {
                throw new CliArgumentException(
                    $"'{options.Name}' is both a workflow template and a worker role. Dispatch is one "
                    + "namespace (decision 0047 §5) — rename one so a dispatch is unambiguous.");
            }

            if (isTemplate)
            {
                return await MaterializeTemplateAsync(options, workspaceDirectory, cancellationToken).ConfigureAwait(false);
            }

            if (isRole)
            {
                return await MaterializeRoleAsync(options, workspaceDirectory, cancellationToken).ConfigureAwait(false);
            }

            throw new CliArgumentException(
                $"No worker role or workflow template named '{options.Name}'.",
                "run 'baton templates' to list available built-ins.");
        }
        catch (Exception ex) when (ex is FileNotFoundException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw new CliArgumentException(ex.Message);
        }
    }

    private static async Task<(WorkflowDefinition, IReadOnlyDictionary<string, WorkerBindingConfigEntry>)>
        MaterializeTemplateAsync(DispatchOptions options, string workspaceDirectory, CancellationToken cancellationToken)
    {
        if (options.SpecFilePath is not null)
        {
            throw new CliArgumentException(
                $"'{options.Name}' is a workflow template — its phases carry their own instructions, so "
                + "--spec does not apply. Pass --spec only when dispatching a role.");
        }

        // R5 (#1354/#1380, finding 7): a template's steps each declare their own output — there is no
        // one "primary output" for --output to rename, and the prior behaviour renamed whichever step
        // happened to be first regardless of what kind of step that was (a capture step, say), silently.
        // Refuse up front, the same way --spec already is above.
        if (options.OutputPath is not null)
        {
            throw new CliArgumentException(
                $"'{options.Name}' is a workflow template — its phases each declare their own outputs, so "
                + "--output does not apply. Pass --output only when dispatching a role.",
                "remove the --output flag, or dispatch a single role instead of a template.");
        }

        if (options.Timeout is not null)
        {
            throw new CliArgumentException(
                $"'{options.Name}' is a workflow template — each phase carries its own role's timeout, so "
                + "--timeout does not apply to one of them. Pass --timeout only when dispatching a role.",
                "remove the --timeout flag, or dispatch a single role instead of a template.");
        }

        var template = WorkflowTemplateCatalog.For(options.Name);
        // #1083: hand every phase the workspace too, so a role run as a template phase can read the repo
        // exactly as a directly-dispatched role now can.
        var (definition, bindings) = WorkflowTemplateComposer.Materialize(
            template, options.Adapter, workingDirectory: workspaceDirectory);
        bindings = await InjectCaptureBaseRefAsync(bindings, workspaceDirectory, cancellationToken).ConfigureAwait(false);
        return (definition, bindings);
    }

    private static async Task<(WorkflowDefinition, IReadOnlyDictionary<string, WorkerBindingConfigEntry>)>
        MaterializeRoleAsync(DispatchOptions options, string workspaceDirectory, CancellationToken cancellationToken)
    {
        if (options.SpecFilePath is null)
        {
            throw new CliArgumentException(
                $"'{options.Name}' is a worker role, which runs against a task spec. Pass --spec <spec-file>.",
                $"baton dispatch {options.Name} --spec <spec-file>");
        }

        if (!File.Exists(options.SpecFilePath))
        {
            throw new CliArgumentException($"Spec file '{options.SpecFilePath}' does not exist.");
        }

        var role = WorkerRoleCatalog.For(options.Name);

        if (options.OutputPath is not null)
        {
            ValidateOutputOverride(options, role);
        }

        var spec = await File.ReadAllTextAsync(options.SpecFilePath, cancellationToken).ConfigureAwait(false);

        // #1083: pin the workspace onto the binding so the worker can actually read the project it was
        // dispatched to study — the process cwd alone does not reach agy (`-p` ignores it, #491).
        // #1082: vendor/model/effort are three independent axes over the role's instructions ([0017]).
        return RoleDispatch.Materialize(
            role, spec, options.Adapter, workingDirectory: workspaceDirectory,
            modelOverride: options.Model, effortOverride: options.Effort, outputOverride: options.OutputPath,
            timeoutOverride: options.Timeout);
    }

    /// <summary>
    /// R6 (#1354/#1380, finding 8): validated before anything is printed or written — the
    /// materialization that calls this runs before the room directory is even created (finding 6's
    /// three checks). <see cref="Path.GetFileName"/> on a trailing-separator path (<c>--output
    /// reports/</c>) returns an empty string, which would otherwise declare an anonymous
    /// <see cref="ProducedOutput"/> that pays for a full run before failing "contract not satisfied"
    /// with nothing naming the cause. The other two checks catch a rename that collides with something
    /// already writing to the same execution output directory: the engine's own reserved namespace
    /// (<see cref="ReservedOutputNames"/>), its durable prompt capture
    /// (<see cref="Baton.Artifacts.ArtifactManager.PromptFileName"/>), or another output the same
    /// role already declares.
    /// </summary>
    private static void ValidateOutputOverride(DispatchOptions options, WorkerRole role)
    {
        var outputPath = options.OutputPath!;
        var customName = Path.GetFileName(outputPath);
        if (string.IsNullOrEmpty(customName))
        {
            throw new CliArgumentException(
                $"'--output {outputPath}' names no file — a path ending in a directory separator has no "
                + "filename. Pass a file path, e.g. --output report.md.",
                "pass a file path instead of a directory, e.g. --output report.md");
        }

        // #1382 F6: "choose a different file name for --output" restated the message with no
        // invocation in it. The rest of the corrected command is already in scope here -- only the
        // replacement file name is genuinely unknowable, so that alone stays a placeholder.
        var retryInvocation = $"baton dispatch {options.Name} --spec {options.SpecFilePath} --output <different-file-name>";

        if (ReservedOutputNames.IsReserved(customName))
        {
            throw new CliArgumentException(
                $"'--output {customName}' is invalid: {ReservedOutputNames.RejectionClause}.",
                retryInvocation);
        }

        if (string.Equals(customName, Baton.Artifacts.ArtifactManager.PromptFileName, StringComparison.Ordinal))
        {
            throw new CliArgumentException(
                $"'--output {customName}' collides with '{Baton.Artifacts.ArtifactManager.PromptFileName}', "
                + "the durable prompt capture the engine writes into every execution's own output directory. "
                + "Choose a different name.",
                retryInvocation);
        }

        if (role.Outputs.Skip(1).Any(o => string.Equals(o.Name, customName, StringComparison.Ordinal)))
        {
            throw new CliArgumentException(
                $"'--output {customName}' collides with role '{role.Id}''s own declared output of the same name.",
                retryInvocation);
        }
    }

    /// <summary>
    /// When a composed template declares a capture step (0047 §4), captures <paramref name="workspaceDirectory"/>'s
    /// HEAD-at-start once and injects it into every capture binding's
    /// <see cref="WorkerBindingConfigEntry.PromptTemplate"/> — the base ref
    /// <see cref="CaptureWorkerAdapter"/> diffs the working tree against — <em>and</em> pins that binding's
    /// <see cref="WorkerBindingConfigEntry.WorkingDirectory"/> to the same workspace. Pinning both is the
    /// point: the base and the <c>git diff</c> that consumes it are then taken in one directory, so they
    /// cannot silently diverge if the process cwd differs from the workspace (a null binding working
    /// directory would fall through to the ambient cwd, diffing a captured SHA against the wrong tree).
    /// A non-git workspace fails loudly here, before the run, rather than opaquely inside the capture step.
    /// </summary>
    private static async Task<IReadOnlyDictionary<string, WorkerBindingConfigEntry>> InjectCaptureBaseRefAsync(
        IReadOnlyDictionary<string, WorkerBindingConfigEntry> bindings, string workspaceDirectory, CancellationToken cancellationToken)
    {
        var hasCapture = bindings.Values.Any(
            b => string.Equals(b.Adapter, WorkflowTemplateComposer.CaptureAdapter, StringComparison.Ordinal));
        if (!hasCapture)
        {
            return bindings;
        }

        var baseRef = await WorkspaceHead.CaptureAsync(workspaceDirectory, cancellationToken).ConfigureAwait(false);

        return bindings.ToDictionary(
            pair => pair.Key,
            pair => string.Equals(pair.Value.Adapter, WorkflowTemplateComposer.CaptureAdapter, StringComparison.Ordinal)
                ? pair.Value with { PromptTemplate = baseRef, WorkingDirectory = workspaceDirectory }
                : pair.Value,
            StringComparer.Ordinal);
    }
}
