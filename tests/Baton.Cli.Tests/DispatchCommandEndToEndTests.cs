using Baton.Vendors;
using Baton.Cli.Tests.TestSupport;
using Baton.Flow.Domain;
using Baton.Flow.Status;

namespace Baton.Cli.Tests;

/// <summary>
/// <c>baton dispatch &lt;role&gt;</c> end to end (#900): a real shipped catalog role is materialized into
/// a single-step workflow and driven through the exact pump <c>baton run</c> uses, so the outputs the
/// role declares become a contract the engine enforces — satisfied means Succeeded, a silent no-op
/// means Failed. The fake adapter (<see cref="ContractOutputWorkerAdapter"/>) stands in for the worker
/// so no live LLM is needed; the role, its outputs, and the contract are the real ones.
/// </summary>
[Collection(WorkerCatalogEnvCollection.Name)]
public sealed class DispatchCommandEndToEndTests : IDisposable
{
    private static readonly IReadOnlyDictionary<string, IWorkerAdapter> Adapters =
        new Dictionary<string, IWorkerAdapter>
        {
            ["fake"] = new ContractOutputWorkerAdapter(satisfyOutputs: true),
            ["fake-noop"] = new ContractOutputWorkerAdapter(satisfyOutputs: false),
        };

    private readonly string? _priorRoles = Environment.GetEnvironmentVariable(WorkerRoleCatalog.RolesPathEnvironmentVariable);
    private readonly string? _priorTiers = Environment.GetEnvironmentVariable(WorkerRoleCatalog.TiersPathEnvironmentVariable);
    private readonly string? _priorTemplates = Environment.GetEnvironmentVariable(WorkflowTemplateCatalog.TemplatesPathEnvironmentVariable);

    // Pin the shipped catalog. Without this these tests resolve through ResolvePath's middle rung
    // ({BatonPaths.Root}/worker-roles.json) and would silently read an operator's local override on a
    // machine that has one -- the exact hazard WorkerRoleCatalogTests.ShippedDefault documents and
    // guards. The env edit is process-global, and one test below sets a deliberately-broken roles path --
    // so this class is not the only catalog reader that matters. It shares
    // [Collection(WorkerCatalogEnvCollection.Name)] with DispatchTemplateEndToEndTests (see that
    // collection for the bleed it prevents); the ctor/Dispose set-and-restore keeps it clean within the
    // serialized group. Templates are pinned too (#1380, finding 7's test): DispatchCommand.MaterializeAsync
    // probes WorkflowTemplateCatalog.All to decide role-vs-template even for a role dispatch.
    public DispatchCommandEndToEndTests()
    {
        Environment.SetEnvironmentVariable(
            WorkerRoleCatalog.RolesPathEnvironmentVariable, Path.Combine(AppContext.BaseDirectory, "WorkerRoles.json"));
        Environment.SetEnvironmentVariable(
            WorkerRoleCatalog.TiersPathEnvironmentVariable, Path.Combine(AppContext.BaseDirectory, "WorkerTiers.json"));
        Environment.SetEnvironmentVariable(
            WorkflowTemplateCatalog.TemplatesPathEnvironmentVariable, Path.Combine(AppContext.BaseDirectory, "WorkflowTemplates.json"));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(WorkerRoleCatalog.RolesPathEnvironmentVariable, _priorRoles);
        Environment.SetEnvironmentVariable(WorkerRoleCatalog.TiersPathEnvironmentVariable, _priorTiers);
        Environment.SetEnvironmentVariable(WorkflowTemplateCatalog.TemplatesPathEnvironmentVariable, _priorTemplates);
    }

    [Fact]
    public async Task Dispatching_a_role_whose_worker_writes_its_declared_output_succeeds()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-e2e-{Guid.NewGuid():N}");
        try
        {
            var specPath = await WriteSpecAsync(testRoot, "Weigh the options for X.");
            var roomDirectory = Path.Combine(testRoot, "task");
            var options = new DispatchOptions("advise", specPath, roomDirectory, Adapter: "fake");

            var state = (await DispatchCommand.ExecuteAsync(options, Adapters, TestContext.Current.CancellationToken)).State;

            Assert.Equal(WorkflowStatus.Terminal, state.Status);
            var step = Assert.Single(state.Steps);
            Assert.Equal("advise", step.StepId.Value);
            Assert.Equal(StepStatus.Succeeded, step.Status);

            // advise declares advice.md; the contract the engine enforced is the role's own.
            var advicePath = Path.Combine(
                roomDirectory, "artifacts", $"execution_{step.LatestExecutionId}", "advice.md");
            Assert.True(File.Exists(advicePath));

            // The dispatch persisted the same files a template run would, so the task is resumable.
            Assert.True(File.Exists(Path.Combine(roomDirectory, "workflow.json")));
            Assert.True(File.Exists(Path.Combine(roomDirectory, "bindings.json")));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    /// <summary>
    /// spec/baton.md §8's writer: <c>baton dispatch</c> registers the room into
    /// <see cref="RoomRegistryStore"/> keyed on its resolved workspace, not the process cwd -- the two
    /// can differ (<c>--workspace</c>), and it is exactly that difference the registry exists to close
    /// (<see cref="FleetStatusToolTests.RegistryEntry_OutsideEveryScannedRoot_IsStillFoundByFleetStatus"/>
    /// is the reader-side half of the same invariant).
    /// </summary>
    [Fact]
    public async Task Dispatching_registers_the_room_under_its_workspace_as_the_project_root()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-e2e-{Guid.NewGuid():N}");
        var workspace = Path.Combine(Path.GetTempPath(), $"dispatch-e2e-workspace-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(workspace);
            var specPath = await WriteSpecAsync(testRoot, "Weigh the options for Y.");
            var roomDirectory = Path.Combine(testRoot, "task");
            var options = new DispatchOptions("advise", specPath, roomDirectory, Adapter: "fake", WorkspaceDirectory: workspace);

            await DispatchCommand.ExecuteAsync(options, Adapters, TestContext.Current.CancellationToken);

            var entries = await RoomRegistryStore.ReadDistinctByRoomAsync(
                BatonPaths.RoomRegistryFile, TestContext.Current.CancellationToken);
            var entry = Assert.Single(entries, e => e.RoomPath == BatonPaths.RecordKey(roomDirectory));
            Assert.Equal(BatonPaths.RecordKey(workspace), entry.ProjectRoot);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
            DirectoryCleanup.DeleteRecursively(workspace);
        }
    }

    [Fact]
    public async Task Dispatching_a_role_whose_worker_writes_nothing_fails_the_contract()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-e2e-{Guid.NewGuid():N}");
        try
        {
            var specPath = await WriteSpecAsync(testRoot, "Weigh the options for X.");
            var roomDirectory = Path.Combine(testRoot, "task");
            // Exits 0 but produces no advice.md — the floor a per-role output exists to catch.
            var options = new DispatchOptions("advise", specPath, roomDirectory, Adapter: "fake-noop");

            var state = (await DispatchCommand.ExecuteAsync(options, Adapters, TestContext.Current.CancellationToken)).State;

            var step = Assert.Single(state.Steps);
            Assert.Equal(StepStatus.Failed, step.Status);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Dispatching_an_unknown_role_is_a_typed_argument_error()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-e2e-{Guid.NewGuid():N}");
        try
        {
            var specPath = await WriteSpecAsync(testRoot, "spec");
            var options = new DispatchOptions("no-such-role", specPath, Path.Combine(testRoot, "task"));

            var ex = await Assert.ThrowsAsync<CliArgumentException>(
                () => DispatchCommand.ExecuteAsync(options, Adapters, TestContext.Current.CancellationToken));
            Assert.Contains("no-such-role", ex.Message);
            Assert.Contains("run 'baton templates'", ex.TryInvocation, StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Dispatching_a_role_without_a_spec_is_a_typed_argument_error_naming_the_fix()
    {
        // #1382 F2: the highest-traffic dispatch rejection -- 'baton dispatch <role>' with no --spec --
        // must carry a Try line an invoking agent can follow literally.
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-e2e-{Guid.NewGuid():N}");
        try
        {
            var options = new DispatchOptions("advise", SpecFilePath: null, Path.Combine(testRoot, "task"));

            var ex = await Assert.ThrowsAsync<CliArgumentException>(
                () => DispatchCommand.ExecuteAsync(options, Adapters, TestContext.Current.CancellationToken));
            Assert.Equal("baton dispatch advise --spec <spec-file>", ex.TryInvocation);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task An_unreadable_catalog_is_a_typed_argument_error_not_a_crash()
    {
        // A typo'd env override or a hand-broken worker-roles.json must exit cleanly, not dump an
        // unhandled JsonException; before the broadened catch (see DispatchCommand) this escaped
        // Program's boundary as exit 127.
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-e2e-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(testRoot);
            var badCatalog = Path.Combine(testRoot, "worker-roles.json");
            await File.WriteAllTextAsync(badCatalog, "{ not valid json", TestContext.Current.CancellationToken);
            Environment.SetEnvironmentVariable(WorkerRoleCatalog.RolesPathEnvironmentVariable, badCatalog);

            var specPath = await WriteSpecAsync(testRoot, "spec");
            var options = new DispatchOptions("advise", specPath, Path.Combine(testRoot, "task"));

            await Assert.ThrowsAsync<CliArgumentException>(
                () => DispatchCommand.ExecuteAsync(options, Adapters, TestContext.Current.CancellationToken));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Dispatching_with_a_missing_spec_file_is_a_typed_argument_error()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-e2e-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(testRoot);
            var options = new DispatchOptions(
                "advise", Path.Combine(testRoot, "does-not-exist.md"), Path.Combine(testRoot, "task"));

            await Assert.ThrowsAsync<CliArgumentException>(
                () => DispatchCommand.ExecuteAsync(options, Adapters, TestContext.Current.CancellationToken));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Dispatching_a_role_with_output_override_copies_artifact_to_output_path()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-e2e-{Guid.NewGuid():N}");
        try
        {
            var specPath = await WriteSpecAsync(testRoot, "Weigh the options for X.");
            var roomDirectory = Path.Combine(testRoot, "task");
            var customOutputPath = Path.Combine(testRoot, "custom-output.md");
            var options = new DispatchOptions("advise", specPath, roomDirectory, Adapter: "fake", OutputPath: customOutputPath);

            var state = (await DispatchCommand.ExecuteAsync(options, Adapters, TestContext.Current.CancellationToken)).State;

            Assert.Equal(WorkflowStatus.Terminal, state.Status);
            Assert.True(File.Exists(customOutputPath));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Dispatching_a_role_with_a_timeout_override_records_it_in_bindings_json()
    {
        // #1442: the override must land in the RECORDED bindings.json, not just influence the live
        // run in memory -- WorkerBindingConfigEntry.Timeout (not workflow.json's WorkflowDefinition,
        // which deliberately keeps a worker's timeout off the frozen step, see RoleDispatch's own doc)
        // is what the engine actually resolves the per-execution timeout from.
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-e2e-{Guid.NewGuid():N}");
        try
        {
            var specPath = await WriteSpecAsync(testRoot, "Weigh the options for X.");
            var roomDirectory = Path.Combine(testRoot, "task");
            var options = new DispatchOptions(
                "advise", specPath, roomDirectory, Adapter: "fake", Timeout: TimeSpan.FromMinutes(99));

            var state = (await DispatchCommand.ExecuteAsync(options, Adapters, TestContext.Current.CancellationToken)).State;

            Assert.Equal(WorkflowStatus.Terminal, state.Status);
            var bindings = await WorkerBindingConfigParser.LoadFromFileAsync(
                Path.Combine(roomDirectory, "bindings.json"), TestContext.Current.CancellationToken);
            Assert.Equal(TimeSpan.FromMinutes(99), bindings["advise"].Timeout);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Omitting_the_timeout_flag_keeps_the_role_s_own_catalog_timeout()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-e2e-{Guid.NewGuid():N}");
        try
        {
            var specPath = await WriteSpecAsync(testRoot, "Weigh the options for X.");
            var roomDirectory = Path.Combine(testRoot, "task");
            var options = new DispatchOptions("advise", specPath, roomDirectory, Adapter: "fake");

            await DispatchCommand.ExecuteAsync(options, Adapters, TestContext.Current.CancellationToken);

            var bindings = await WorkerBindingConfigParser.LoadFromFileAsync(
                Path.Combine(roomDirectory, "bindings.json"), TestContext.Current.CancellationToken);
            Assert.Equal(WorkerRoleCatalog.For("advise").Timeout, bindings["advise"].Timeout);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task A_timeout_override_above_the_2h_caution_threshold_is_a_stderr_warning_not_a_refusal()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-e2e-{Guid.NewGuid():N}");
        var originalError = Console.Error;
        try
        {
            var specPath = await WriteSpecAsync(testRoot, "Weigh the options for X.");
            var roomDirectory = Path.Combine(testRoot, "task");
            var options = new DispatchOptions(
                "advise", specPath, roomDirectory, Adapter: "fake", Timeout: TimeSpan.FromMinutes(180));

            using var stderrCapture = new StringWriter();
            Console.SetError(stderrCapture);
            var state = (await DispatchCommand.ExecuteAsync(options, Adapters, TestContext.Current.CancellationToken)).State;
            Console.SetError(originalError);

            Assert.Equal(WorkflowStatus.Terminal, state.Status);
            Assert.Contains("Warning", stderrCapture.ToString(), StringComparison.Ordinal);
            Assert.Contains("--timeout", stderrCapture.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetError(originalError);
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Dispatching_a_template_with_a_timeout_override_is_a_typed_argument_error()
    {
        // Mirrors --output's template refusal — why in spec/baton.md §2.
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-e2e-{Guid.NewGuid():N}");
        try
        {
            var options = new DispatchOptions(
                "implement-review", SpecFilePath: null, Path.Combine(testRoot, "task"), Timeout: TimeSpan.FromMinutes(30));

            var ex = await Assert.ThrowsAsync<CliArgumentException>(
                () => DispatchCommand.ExecuteAsync(options, Adapters, TestContext.Current.CancellationToken));
            Assert.Contains("--timeout", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Output_pointing_at_an_existing_directory_survives_and_still_reaches_terminal()
    {
        // R3 (#1354/#1380, finding 3): the red test for the copy crashing before Program's
        // terminal-sentinel write. File.Copy(src, existingDirectoryPath, overwrite: true) throws
        // (UnauthorizedAccessException on Windows, IOException on Linux/macOS) -- neither derives from
        // BatonFlowException, so before the fix this escaped ExecuteAsync raw. The run must still reach
        // Terminal and ExecuteAsync must still return normally, since that return is what lets Program
        // go on to write terminal.json.
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-e2e-{Guid.NewGuid():N}");
        var originalError = Console.Error;
        try
        {
            var specPath = await WriteSpecAsync(testRoot, "Weigh the options for X.");
            var roomDirectory = Path.Combine(testRoot, "task");
            var existingDirectoryAsDestination = Path.Combine(testRoot, "already-a-directory");
            Directory.CreateDirectory(existingDirectoryAsDestination);
            var options = new DispatchOptions(
                "advise", specPath, roomDirectory, Adapter: "fake", OutputPath: existingDirectoryAsDestination);

            using var stderrCapture = new StringWriter();
            Console.SetError(stderrCapture);
            var result = await DispatchCommand.ExecuteAsync(options, Adapters, TestContext.Current.CancellationToken);
            Console.SetError(originalError);

            Assert.Equal(WorkflowStatus.Terminal, result.State.Status);
            var step = Assert.Single(result.State.Steps);
            Assert.Equal(StepStatus.Succeeded, step.Status);
            Assert.Contains("Could not copy", stderrCapture.ToString());

            // The declared output the copy tried to move still exists where the engine wrote it,
            // regardless of the copy's own failure. --output renames the role's primary declared output
            // to its own filename (RoleDispatch's outputOverride), so the real artifact is named after
            // the destination, not "advice.md".
            var declaredOutputName = Path.GetFileName(existingDirectoryAsDestination);
            var realOutputPath = Path.Combine(
                roomDirectory, "artifacts", $"execution_{step.LatestExecutionId}", declaredOutputName);
            Assert.True(File.Exists(realOutputPath));
        }
        finally
        {
            Console.SetError(originalError);
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Without_output_the_printed_fact_names_the_artifacts_directory_not_a_fabricated_file_path()
    {
        // R4 (#1354/#1380, finding 4): the prior line printed a per-execution file path
        // (room/artifacts/advice.md) that never exists -- real outputs land one level deeper, under
        // room/artifacts/execution_<id>/advice.md, a path not known until the run actually happens.
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-e2e-{Guid.NewGuid():N}");
        var originalOut = Console.Out;
        try
        {
            var specPath = await WriteSpecAsync(testRoot, "Weigh the options for X.");
            var roomDirectory = Path.Combine(testRoot, "task");
            var options = new DispatchOptions("advise", specPath, roomDirectory, Adapter: "fake");

            using var consoleOutput = new StringWriter();
            Console.SetOut(consoleOutput);
            var result = await DispatchCommand.ExecuteAsync(options, Adapters, TestContext.Current.CancellationToken);
            Console.SetOut(originalOut);

            var printed = consoleOutput.ToString();
            var artifactsDirectory = Path.Combine(roomDirectory, "artifacts");
            Assert.Contains($"Artifacts directory: {artifactsDirectory}", printed);
            Assert.DoesNotContain("Output path:", printed);

            // The printed directory is genuinely the parent of where the real output landed.
            var step = Assert.Single(result.State.Steps);
            var realOutputPath = Path.Combine(artifactsDirectory, $"execution_{step.LatestExecutionId}", "advice.md");
            Assert.True(File.Exists(realOutputPath));
        }
        finally
        {
            Console.SetOut(originalOut);
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Dispatching_a_role_prints_its_least_privilege_grant_profile_before_the_run_starts()
    {
        // #1355: the invoking agent needs this line to relay the actual grant to its own permission
        // layer honestly -- review is read-shaped (write_files: false) on the "fake" adapter, which
        // WorkerAdapterRegistry.Default does not know, so ToBinding never flips the audited branch and
        // the printed line reflects the role's plain catalog grant.
        //
        // F2 needs the bound adapter to actually consume a grant (IPermissionGrantTranslator) for the
        // line to print at all, so this test registers GrantConsumingContractOutputWorkerAdapter under
        // "fake" rather than the class-level Adapters -- ContractOutputWorkerAdapter deliberately sits
        // outside that population so the many other dispatch tests never pay for grant refusal checks.
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-e2e-{Guid.NewGuid():N}");
        var originalOut = Console.Out;
        try
        {
            var specPath = await WriteSpecAsync(testRoot, "Review the change.");
            var roomDirectory = Path.Combine(testRoot, "task");
            var options = new DispatchOptions("review", specPath, roomDirectory, Adapter: "fake");
            var adapters = new Dictionary<string, IWorkerAdapter>
            {
                ["fake"] = new GrantConsumingContractOutputWorkerAdapter(satisfyOutputs: true),
            };

            using var consoleOutput = new StringWriter();
            Console.SetOut(consoleOutput);
            await DispatchCommand.ExecuteAsync(options, adapters, TestContext.Current.CancellationToken);
            Console.SetOut(originalOut);

            // #1456: review now carries a read-only-scoped shell grant (spec/baton.md §9), so the
            // negated-arms baseline this test used to assert no longer describes it -- DescribeGrant
            // spells out the scope rather than printing a bare "shell" that would understate it.
            Assert.Contains(
                "Grant: read, no-write, shell (scoped: git diff*, git log*, git show*, git blame*, "
                + "git status*, git grep*, git rev-parse*, git merge-base*, git ls-files*, "
                + "git branch --list*, gh pr view*, gh pr diff*, gh pr checks*, gh issue view*), no-network",
                consoleOutput.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Dispatching_a_shell_and_network_granting_role_prints_shell_and_network_not_the_negations()
    {
        // F3: the two tests above only ever assert the negated arms (no-write / no-shell / no-network)
        // for DescribeGrant's shell/network categories -- someone could flip those two ternaries to
        // always emit the negation and every existing test would stay green. "implement" is the
        // catalog's one role that grants both (WorkerRoles.json), so dispatching it is the control that
        // discriminates in the dangerous direction.
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-e2e-{Guid.NewGuid():N}");
        var originalOut = Console.Out;
        try
        {
            var specPath = await WriteSpecAsync(testRoot, "Make the bounded change.");
            var roomDirectory = Path.Combine(testRoot, "task");
            var options = new DispatchOptions("implement", specPath, roomDirectory, Adapter: "fake");
            var adapters = new Dictionary<string, IWorkerAdapter>
            {
                ["fake"] = new GrantConsumingContractOutputWorkerAdapter(satisfyOutputs: true),
            };

            using var consoleOutput = new StringWriter();
            Console.SetOut(consoleOutput);
            await DispatchCommand.ExecuteAsync(options, adapters, TestContext.Current.CancellationToken);
            Console.SetOut(originalOut);

            Assert.Contains("Grant: read, write, shell, network", consoleOutput.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Output_ending_in_a_directory_separator_is_refused_before_any_fact_is_printed()
    {
        // R6 (#1354/#1380, finding 8) -- see ValidateOutputOverride's own doc for what a trailing
        // separator would otherwise cost. Must be refused before the room directory even exists.
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-e2e-{Guid.NewGuid():N}");
        var originalOut = Console.Out;
        try
        {
            var specPath = await WriteSpecAsync(testRoot, "Weigh the options for X.");
            var roomDirectory = Path.Combine(testRoot, "task");
            var options = new DispatchOptions(
                "advise", specPath, roomDirectory, Adapter: "fake",
                OutputPath: Path.Combine(testRoot, "reports") + Path.DirectorySeparatorChar);

            using var consoleOutput = new StringWriter();
            Console.SetOut(consoleOutput);
            var ex = await Assert.ThrowsAsync<CliArgumentException>(
                () => DispatchCommand.ExecuteAsync(options, Adapters, TestContext.Current.CancellationToken));
            Console.SetOut(originalOut);

            Assert.Contains("names no file", ex.Message);
            Assert.Contains("pass a file path instead of a directory", ex.TryInvocation, StringComparison.Ordinal);
            Assert.Empty(consoleOutput.ToString());
            Assert.False(Directory.Exists(roomDirectory), "a refused dispatch must not have created the room directory");
        }
        finally
        {
            Console.SetOut(originalOut);
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Output_on_a_template_dispatch_is_refused_up_front_like_spec_already_is()
    {
        // R5 (#1354/#1380, finding 7) -- see MaterializeTemplateAsync's own comment for why. Mirrors
        // the existing --spec-on-a-template refusal exercised elsewhere in this class.
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-e2e-{Guid.NewGuid():N}");
        try
        {
            var roomDirectory = Path.Combine(testRoot, "task");
            var options = new DispatchOptions(
                "implement-review", SpecFilePath: null, roomDirectory, Adapter: "fake",
                OutputPath: Path.Combine(testRoot, "out.md"));

            var ex = await Assert.ThrowsAsync<CliArgumentException>(
                () => DispatchCommand.ExecuteAsync(options, Adapters, TestContext.Current.CancellationToken));

            Assert.Contains("--output", ex.Message);
            Assert.Contains("remove the --output flag", ex.TryInvocation, StringComparison.Ordinal);
            Assert.False(Directory.Exists(roomDirectory), "a refused dispatch must not have created the room directory");
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task The_suggested_output_rename_actually_clears_the_collision_it_follows_from()
    {
        // #1382 F10.1 (see DispatchOptionsParserTests for what this class of test guards): the
        // --output-collides-with-declared-output refusal's suggested
        // "baton dispatch <role> --spec <spec-file> --output <different-file-name>" shape, proven to
        // actually clear the check it follows from.
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-e2e-{Guid.NewGuid():N}");
        try
        {
            var specPath = await WriteSpecAsync(testRoot, "Review the diff.");
            var roomDirectory = Path.Combine(testRoot, "task");
            // "review" declares report.md (primary, --output's target) and verdict.json (secondary) --
            // renaming the primary onto the secondary's own name is the Skip(1) collision this refusal
            // guards.
            var collidingOptions = new DispatchOptions(
                "review", specPath, roomDirectory, Adapter: "fake", OutputPath: Path.Combine(testRoot, "verdict.json"));

            var ex = await Assert.ThrowsAsync<CliArgumentException>(
                () => DispatchCommand.ExecuteAsync(collidingOptions, Adapters, TestContext.Current.CancellationToken));
            Assert.Contains("collides with role 'review'", ex.Message, StringComparison.Ordinal);
            Assert.Equal($"baton dispatch review --spec {specPath} --output <different-file-name>", ex.TryInvocation);

            var correctedRoomDirectory = Path.Combine(testRoot, "task-corrected");
            var correctedOptions = new DispatchOptions(
                "review", specPath, correctedRoomDirectory, Adapter: "fake", OutputPath: Path.Combine(testRoot, "renamed-report.md"));

            var state = (await DispatchCommand.ExecuteAsync(correctedOptions, Adapters, TestContext.Current.CancellationToken)).State;
            Assert.Equal(WorkflowStatus.Terminal, state.Status);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    private static async Task<string> WriteSpecAsync(string directory, string content)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "spec.md");
        await File.WriteAllTextAsync(path, content, TestContext.Current.CancellationToken);
        return path;
    }
}
