using Baton.Domain;

namespace Baton.Vendors;

/// <summary>
/// Materializes a single worker <see cref="WorkerRole"/> from the catalog into the
/// <see cref="WorkflowDefinition"/> + <see cref="WorkerBindingConfigEntry"/> the engine runs — the
/// shared primitive behind <c>baton dispatch &lt;role&gt;</c> (#900, front-door rung 2). It is the one
/// place that turns "what a role produces" (its <see cref="WorkerRole.Outputs"/>) into a
/// <see cref="WorkerContract"/> the engine's <c>ContractValidator</c> enforces, so a role that writes
/// nothing fails loudly without the caller restating the contract.
/// </summary>
/// <remarks>
/// Deliberately surface-agnostic — it takes catalog and domain types only, never a CLI or UI type —
/// so any future built-in template or desktop authoring surface can adopt it in place of hand-rolling
/// its own bindings (#901), rather than growing a second parallel source of truth. Its other consumer
/// today is <see cref="WorkflowTemplateComposer"/>.
/// </remarks>
public static class RoleDispatch
{
    /// <summary>
    /// The reusable core: a resolved role plus a task spec become one worker binding whose contract's
    /// <c>ProducedOutputs</c> are exactly the role's declared outputs, whose grant/timeout/model/effort
    /// come from the role, and whose prompt is the spec with the role's output instructions appended —
    /// single-sourced from the catalog so a spec prompt stays just the task.
    /// </summary>
    /// <param name="role">The resolved catalog role (see <see cref="WorkerRoleCatalog.For"/>).</param>
    /// <param name="spec">The task prompt for this dispatch — what the worker is asked to do.</param>
    /// <param name="adapterOverride">
    /// A vendor adapter to run this role on instead of its tier's default (<see cref="WorkerRole.Adapter"/>) —
    /// the <c>--adapter</c> escape hatch. A role never names a vendor, so this is the only place a
    /// caller picks one, and it does not change the role's capability.
    /// </param>
    /// <param name="workerName">
    /// The binding/contract key for this worker, defaulting to <see cref="WorkerRole.Id"/>. A
    /// multi-phase composer passes a phase-unique name instead — see
    /// <see cref="WorkflowTemplateComposer"/> for why role ids will not do there.
    /// </param>
    /// <param name="workingDirectory">
    /// The directory the worker runs in and may read — set on the binding so a vendor that ignores the
    /// process cwd (agy <c>-p</c>, #491) is still handed the project via <c>--add-dir</c>. Null leaves it
    /// unset, the pre-#1083 behaviour, under which a role dispatched to read the repo was given no path to
    /// it and every repo read was auto-denied.
    /// </param>
    /// <param name="modelOverride">
    /// The model axis, independent of the role ([0017]: vendor, model and effort are three
    /// separate axes over a role's instructions). Null keeps the role's tier model — except when
    /// <paramref name="adapterOverride"/> moves the role to a different vendor, where the tier's
    /// vendor-specific model string is dropped for that vendor's own default (#1082).
    /// </param>
    /// <param name="effortOverride">The effort axis, independent of the role — a behavioural name ([0023]), null keeps the role's tier effort.</param>
    /// <param name="requiredInputs">
    /// The upstream artifacts this worker consumes, in the SAME order as its step definition's
    /// <c>Inputs</c> (#1147): the adapters' prompt builders key the "inputs are available at
    /// <c>BATON_INPUT_&lt;n&gt;</c>" disclosure on the contract's <see cref="WorkerContract.RequiredInputs"/>,
    /// and the variables are positional per the step's list — an input the contract omits is delivered
    /// but never disclosed, so the worker cannot find it. Empty for a role dispatched alone.
    /// </param>
    /// <param name="autoProvisionWorktree">
    /// When an audited grant needs isolation (<see cref="GrantAuditMode.AuditedNotEnforced"/>), declare
    /// a fresh worktree of <paramref name="workingDirectory"/> at <c>HEAD</c> — never handing the
    /// worker that directory as-is, regardless of whether it already happens to be a worktree itself,
    /// because <see cref="WorkerBindingConfigEntry.IsWorktree"/> is the provisioner's own stamp that a
    /// run made the tree (#1354). <see cref="RoleDispatch.Materialize"/> (a direct role dispatch) takes
    /// this path; <see cref="WorkflowTemplateComposer"/> deliberately opts out (R5) — see its own call
    /// site for why.
    /// </param>
    /// <param name="timeoutOverride">
    /// The <c>--timeout</c> escape hatch (#1442), independent of the role like <paramref
    /// name="modelOverride"/>/<paramref name="effortOverride"/> — rationale in spec/baton.md §2.
    /// Null keeps <see cref="WorkerRole.Timeout"/>.
    /// </param>
    /// <param name="attachments">Attached context files supplied by the operator.</param>
    /// <param name="attachmentsDirectory">The directory inside the room artifacts where attached files live.</param>
    /// <param name="tokenBudgetOverride">
    /// The <c>--token-budget</c> escape hatch (#1623), independent of the role like <paramref
    /// name="timeoutOverride"/>. Null keeps <see cref="WorkerRole.TokenBudget"/>.
    /// </param>
    /// <param name="maxToolStepsOverride">
    /// The <c>--max-tool-steps</c> escape hatch (#1686 review F11), mirroring <paramref
    /// name="tokenBudgetOverride"/> end to end. Null keeps <see cref="WorkerRole.MaxToolSteps"/>.
    /// </param>
    /// <param name="billedRateLimitOverride">
    /// The <c>--billed-rate-limit</c> escape hatch (#1691), mirroring <paramref
    /// name="tokenBudgetOverride"/> end to end. Null keeps <see cref="WorkerRole.BilledRateLimit"/> —
    /// which no role sets, so in practice null means no rate trigger at all.
    /// </param>
    /// <param name="verifyCommandOverride">
    /// The <c>--verify</c> escape hatch (#1702), independent of the role like <paramref
    /// name="tokenBudgetOverride"/>. Null keeps the workspace-resolution order
    /// (<c>Baton.Mutation.VerifyCommandResolver.Resolve</c>): a <c>.baton/verify</c> declaration, then
    /// <see cref="WorkerRole.VerifyPixiTask"/>.
    /// </param>
    public static WorkerBindingConfigEntry ToBinding(
        WorkerRole role, string spec, string? adapterOverride = null, string? workerName = null,
        string? workingDirectory = null, string? modelOverride = null, string? effortOverride = null,
        IReadOnlyList<string>? requiredInputs = null, string? outputOverride = null,
        bool autoProvisionWorktree = true, TimeSpan? timeoutOverride = null,
        IReadOnlyList<string>? attachments = null, string? attachmentsDirectory = null,
        long? tokenBudgetOverride = null, int? maxToolStepsOverride = null,
        long? billedRateLimitOverride = null, string? verifyCommandOverride = null)
    {
        ArgumentNullException.ThrowIfNull(role);
        ArgumentNullException.ThrowIfNull(spec);

        var outputs = role.Outputs.ToList();
        if (!string.IsNullOrWhiteSpace(outputOverride) && outputs.Count > 0)
        {
            var customName = Path.GetFileName(outputOverride);
            outputs[0] = new WorkerRoleOutput(customName, outputs[0].Schema, outputs[0].Instruction);
        }

        var contract = new WorkerContract(
            WorkerName: string.IsNullOrWhiteSpace(workerName) ? role.Id : workerName,
            RequiredInputs: requiredInputs ?? [],
            ProducedOutputs: outputs.Select(o => new ProducedOutput(o.Name, Schema: o.Schema)).ToList(),
            OptionalMetadata: []);

        // Normalize whichever adapter wins, not just the CLI override: role.Adapter comes from the
        // operator-editable, rebuild-free WorkerTiers.json, so a tier authored as "Claude" must resolve
        // the same as the override path does — otherwise the binding fails with UnknownWorkerAdapterException
        // for an adapter that plainly exists. Since #1567 this normalized string is also what gets frozen
        // onto ExecutionRequest.Adapter and written into flow.jsonl, so it is now the join key of durable
        // history against WorkerAdapterRegistry/StandardWorkerUsageParsers, not just a same-room round-trip
        // through bindings.json — a future change to this normalization changes what already-recorded lines
        // resolve to.
        var adapter = (string.IsNullOrWhiteSpace(adapterOverride) ? role.Adapter : adapterOverride)
            .Trim().ToLowerInvariant();

        // Vendor, model and effort are three independent axes ([0017]): the role carries a
        // default bundle (its tier), and each axis overrides on its own. An explicit --model/--effort
        // wins; with none, swapping the vendor drops the tier's model AND effort. Both are vendor-specific
        // as the catalog actually pins them: the model string plainly so (the measured #1082 failure, the
        // claude CLI handed 'gemini-3.6-flash-high'), and effort because WorkerTiers.json pins raw vendor
        // flag values ("high"/"low"), not the canonical [0023] vocabulary the adapters would map — so an
        // "xhigh"/"max" tier swapped onto agy (which rejects those) would leak the exact same way. On a
        // swap the new vendor falls back to its own default for both, unless the axis is set explicitly.
        var vendorSwapped = !string.Equals(adapter, role.Adapter.Trim().ToLowerInvariant(), StringComparison.Ordinal);
        var model = !string.IsNullOrWhiteSpace(modelOverride) ? modelOverride
            : vendorSwapped ? null
            : role.Model;
        var effort = !string.IsNullOrWhiteSpace(effortOverride) ? effortOverride
            : vendorSwapped ? null
            : role.Effort;

        var grant = role.Grant;
        var grantAuditMode = GrantAuditMode.Enforced;

        if (!role.Grant.WriteFiles && contract.ProducedOutputs.Count > 0)
        {
            if (WorkerAdapterRegistry.Default.TryGetValue(adapter, out var targetAdapter) && !targetAdapter.WithheldWritesReachTheOutbox)
            {
                grant = role.Grant with { WriteFiles = true };
                grantAuditMode = GrantAuditMode.AuditedNotEnforced;
            }
        }

        WorktreeWorkspace? worktreeSpec = null;
        var effectiveWorkDir = workingDirectory;

        if (autoProvisionWorktree && grantAuditMode == GrantAuditMode.AuditedNotEnforced && !string.IsNullOrWhiteSpace(workingDirectory))
        {
            // R1: always a fresh worktree of the caller's directory at HEAD, whether that directory is
            // a plain checkout or already a worktree itself — never trust the caller's own tree, and
            // never stamp IsWorktree on it (see the parameter doc above). WorktreeWorkspaces.Provision
            // is what actually creates the tree and stamps IsWorktree: true once it has.
            worktreeSpec = new WorktreeWorkspace(workingDirectory, "HEAD");
            effectiveWorkDir = null;
        }

        return new WorkerBindingConfigEntry(
            Adapter: adapter,
            Contract: contract,
            PromptTemplate: BuildPrompt(role, spec, outputs, attachments, attachmentsDirectory),
            Timeout: timeoutOverride ?? role.Timeout,
            Model: model,
            PermissionGrant: grant,
            WorkingDirectory: effectiveWorkDir,
            Effort: effort,
            Worktree: worktreeSpec,
            GrantAuditMode: grantAuditMode,
            IsWorktree: false,
            // #1089, #1540: agy and claude. Streaming puts event-level JSON envelopes on stdout so a running lane's
            // log fills incrementally (feeding the live tail), while agy's terminal `result` event reaches the
            // teardown-hang guard. claude dispatches run plain stream-json --verbose without --include-partial-messages.
            StreamJson: StreamsJson(adapter),
            // #1623: verify is the engine's own step. #1702: the role's own default is now only the
            // lowest-precedence input to VerifyCommandResolver.Resolve, alongside the workspace's own
            // .baton/verify declaration and this verifyCommandOverride -- see VerifyCommandOverride's
            // own remarks on WorkerBindingConfigEntry.
            VerifyPixiTask: role.VerifyPixiTask,
            VerifyCommandOverride: verifyCommandOverride,
            TokenBudget: tokenBudgetOverride ?? role.TokenBudget,
            // #1686 review F11: the --max-tool-steps escape hatch, mirroring --token-budget.
            MaxToolSteps: maxToolStepsOverride ?? role.MaxToolSteps,
            // #1691: the --billed-rate-limit escape hatch, mirroring both of the above.
            BilledRateLimit: billedRateLimitOverride ?? role.BilledRateLimit);
    }

    /// <summary>
    /// Whether <paramref name="adapter"/> is one of the two vendors #1089/#1540 stream JSON on stdout
    /// for (issue #1561 finding 10). The single source for a predicate previously duplicated between
    /// <see cref="ToBinding"/> above and <c>RedispatchCommand.InheritBinding</c> — a third streaming
    /// vendor now only needs adding here, rather than in both places with the redispatch path silently
    /// diverging from dispatch if the second site were missed.
    /// </summary>
    public static bool StreamsJson(string adapter) =>
        string.Equals(adapter, "agy", StringComparison.OrdinalIgnoreCase) || string.Equals(adapter, "claude", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Wraps <see cref="ToBinding"/> in a single-step workflow — the shape <c>baton dispatch</c> hands to
    /// the same pump <c>baton run</c> drives. The step's <see cref="WorkflowStepDefinition.Outputs"/>
    /// mirror the contract's, so the reporter prints each produced file's path on success.
    /// </summary>
    public static (WorkflowDefinition Definition, IReadOnlyDictionary<string, WorkerBindingConfigEntry> Bindings) Materialize(
        WorkerRole role, string spec, string? adapterOverride = null, string? workingDirectory = null,
        string? modelOverride = null, string? effortOverride = null, string? outputOverride = null,
        TimeSpan? timeoutOverride = null, IReadOnlyList<string>? attachments = null,
        string? attachmentsDirectory = null, long? tokenBudgetOverride = null, int? maxToolStepsOverride = null,
        long? billedRateLimitOverride = null, string? verifyCommandOverride = null)
    {
        ArgumentNullException.ThrowIfNull(role);

        var binding = ToBinding(
            role, spec, adapterOverride, workingDirectory: workingDirectory,
            modelOverride: modelOverride, effortOverride: effortOverride, outputOverride: outputOverride,
            timeoutOverride: timeoutOverride, attachments: attachments, attachmentsDirectory: attachmentsDirectory,
            tokenBudgetOverride: tokenBudgetOverride, maxToolStepsOverride: maxToolStepsOverride,
            billedRateLimitOverride: billedRateLimitOverride,
            verifyCommandOverride: verifyCommandOverride);

        var stepOutputs = binding.Contract.ProducedOutputs.Select(o => o.Name).ToList();

        var definition = new WorkflowDefinition(
            WorkflowTemplateId: new WorkflowTemplateId($"dispatch-{role.Id}"),
            WorkflowTemplateVersion: 1,
            Steps:
            [
                new WorkflowStepDefinition(
                    StepId: new StepId(role.Id),
                    Worker: role.Id,
                    Inputs: [],
                    Outputs: stepOutputs,
                    DependsOn: [],
                    RetryPolicy: new RetryPolicy(3),
                    PausePoint: null)
            ]);

        var bindings = new Dictionary<string, WorkerBindingConfigEntry> { [role.Id] = binding };
        return (definition, bindings);
    }

    /// <summary>
    /// The spec, then the role's output instructions verbatim — so the worker is told to produce
    /// exactly the files the contract asserts. A role always declares at least one output (the catalog
    /// enforces it at load), so the header is never emitted without lines under it.
    /// </summary>
    private static string BuildPrompt(
        WorkerRole role, string spec, IReadOnlyList<WorkerRoleOutput>? outputs = null,
        IReadOnlyList<string>? attachments = null, string? attachmentsDirectory = null)
    {
        var activeOutputs = outputs ?? role.Outputs;
        var instructions = string.Join("\n", activeOutputs.Select(o => $"- {o.Instruction}"));
        if (role.Outputs.Count > 0 && activeOutputs.Count > 0 && !string.Equals(role.Outputs[0].Name, activeOutputs[0].Name, StringComparison.Ordinal))
        {
            instructions = instructions.Replace(role.Outputs[0].Name, activeOutputs[0].Name, StringComparison.Ordinal);
        }

        var promptBuilder = new System.Text.StringBuilder();
        promptBuilder.Append(spec.TrimEnd());

        if (attachments is { Count: > 0 } && !string.IsNullOrEmpty(attachmentsDirectory))
        {
            var fileNames = attachments.Select(Path.GetFileName);
            promptBuilder.Append($"\n\nAttached files (in {attachmentsDirectory}): {string.Join(", ", fileNames)}");
        }

        promptBuilder.Append($"\n\nRequired outputs:\n{instructions}\n\n{OneShotContract}");
        return promptBuilder.ToString();
    }

    // #1095: a dispatched worker runs in a one-shot, non-interactive harness — the turn is never
    // resumed. A sonnet implement worker instead scheduled a background test run, ended its turn to
    // wait for the notification, and produced no output; the contract failed and the step retried a
    // worker that would defer identically every time. State the contract in the prompt. Lives here,
    // the dispatch prompt builder, not in the adapter's BuildPrompt (which also runs for the
    // interactive chat turn, where deferring genuinely is fine).
    private const string OneShotContract =
        "This is a single, non-interactive turn: do all of the work to completion now and write the "
        + "required outputs before it ends. Do not schedule background tasks or wait for a "
        + "notification or wake-up — nothing will resume this turn, so any deferred work is lost.";
}
