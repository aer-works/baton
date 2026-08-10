using System.Text.Json;
using Aer.Adapters;
using Aer.Flow.Domain;
using Aer.Flow.Templates;

namespace Aer.Cli;

/// <summary>
/// <c>aer dispatch &lt;name&gt;</c> (#900 role dispatch, widened for rung-3 composed templates, #920):
/// resolves <see cref="DispatchOptions.Name"/> as either a worker role (single-step, via
/// <see cref="RoleDispatch"/>, against a <c>--spec</c>) or a workflow template (a composed multi-phase
/// DAG, via <see cref="WorkflowTemplateComposer"/>) — one namespace, decision 0047 §5. Either way it
/// persists the same <c>workflow.json</c>/<c>bindings.json</c> and hands them to
/// <see cref="RunCommand.ExecuteAsync"/>, so outputs are contract-checked by the very pump <c>aer run</c>
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

        Directory.CreateDirectory(options.RoomDirectoryPath);
        var workflowFilePath = Path.Combine(options.RoomDirectoryPath, WorkflowFileName);
        var bindingsFilePath = Path.Combine(options.RoomDirectoryPath, BindingsFileName);
        await WorkflowDefinitionWriter.SaveToFileAsync(definition, workflowFilePath, cancellationToken).ConfigureAwait(false);
        await WorkerBindingConfigWriter.SaveToFileAsync(bindings, bindingsFilePath, cancellationToken).ConfigureAwait(false);

        var runOptions = new RunOptions(workflowFilePath, bindingsFilePath, options.RoomDirectoryPath, options.WorkflowId);
        return await RunCommand.ExecuteAsync(runOptions, adapters, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static async Task<(WorkflowDefinition Definition, IReadOnlyDictionary<string, WorkerBindingConfigEntry> Bindings)>
        MaterializeAsync(DispatchOptions options, string workspaceDirectory, CancellationToken cancellationToken)
    {
        try
        {
            // The catalog reads are the fail-loud set both catalogs share: a missing file (FileNotFound),
            // malformed JSON (JsonException), a structural fault (InvalidOperationException — duplicate id,
            // empty outputs, capture-id collision), or a phase naming a role the catalog lacks
            // (KeyNotFoundException, via WorkerRoleCatalog.For). None derive from AerFlowException, so
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
                $"No worker role or workflow template named '{options.Name}'.");
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
                $"'{options.Name}' is a worker role, which runs against a task spec. Pass --spec <spec-file>.");
        }

        if (!File.Exists(options.SpecFilePath))
        {
            throw new CliArgumentException($"Spec file '{options.SpecFilePath}' does not exist.");
        }

        var role = WorkerRoleCatalog.For(options.Name);
        var spec = await File.ReadAllTextAsync(options.SpecFilePath, cancellationToken).ConfigureAwait(false);

        // #1083: pin the workspace onto the binding so the worker can actually read the project it was
        // dispatched to study — the process cwd alone does not reach agy (`-p` ignores it, #491).
        // #1082: vendor/model/effort are three independent axes over the role's instructions ([0017]).
        return RoleDispatch.Materialize(
            role, spec, options.Adapter, workingDirectory: workspaceDirectory,
            modelOverride: options.Model, effortOverride: options.Effort);
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
