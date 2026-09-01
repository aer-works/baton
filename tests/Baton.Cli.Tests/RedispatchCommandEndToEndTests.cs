using System.Text.Json;
using Baton.Vendors;
using Baton.Cli.Tests.TestSupport;
using Baton.Domain;
using Baton.Status;
using Baton.Templates;

namespace Baton.Cli.Tests;

/// <summary>
/// <c>baton redispatch &lt;room-dir&gt;</c> end to end (#1441): a TERMINAL room produced by a real
/// <c>baton dispatch</c> is redispatched into a fresh room, driven through the exact pump <c>baton
/// dispatch</c>/<c>baton run</c> share, so the inherited binding is exercised for real rather than just
/// asserted in isolation (that half is <see cref="RedispatchBindingTests"/>). Mirrors
/// <see cref="DispatchCommandEndToEndTests"/>'s catalog-pinning and fake-adapter setup.
/// </summary>
[Collection(SerializedEnvironmentCollection.Name)]
public sealed class RedispatchCommandEndToEndTests : IDisposable
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

    public RedispatchCommandEndToEndTests()
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
    public async Task Redispatching_without_a_spec_reuses_the_parents_prompt_verbatim()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"redispatch-e2e-{Guid.NewGuid():N}");
        try
        {
            var parentRoom = await DispatchTerminalParentAsync(testRoot, "Weigh the options for X.");
            var parentBindings = await WorkerBindingConfigParser.LoadFromFileAsync(
                Path.Combine(parentRoom, "bindings.json"), TestContext.Current.CancellationToken);

            var childRoom = Path.Combine(testRoot, "child");
            var options = new RedispatchOptions(parentRoom, childRoom);

            var result = await RedispatchCommand.ExecuteAsync(options, Adapters, TestContext.Current.CancellationToken);

            Assert.Equal(WorkflowStatus.Terminal, result.State.Status);
            var childBindings = await WorkerBindingConfigParser.LoadFromFileAsync(
                Path.Combine(childRoom, "bindings.json"), TestContext.Current.CancellationToken);
            Assert.Equal(parentBindings["advise"].PromptTemplate, childBindings["advise"].PromptTemplate);
            Assert.Equal(parentBindings["advise"].Adapter, childBindings["advise"].Adapter);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Redispatching_with_an_amended_spec_replaces_the_prompt_without_duplicating_output_instructions()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"redispatch-e2e-{Guid.NewGuid():N}");
        try
        {
            var parentRoom = await DispatchTerminalParentAsync(testRoot, "Weigh the options for X.");

            var amendedSpecPath = Path.Combine(testRoot, "amended.md");
            await File.WriteAllTextAsync(amendedSpecPath, "Weigh the options for Y instead.", TestContext.Current.CancellationToken);

            var childRoom = Path.Combine(testRoot, "child");
            var options = new RedispatchOptions(parentRoom, childRoom, SpecFilePath: amendedSpecPath, Adapter: "fake");

            var result = await RedispatchCommand.ExecuteAsync(options, Adapters, TestContext.Current.CancellationToken);

            Assert.Equal(WorkflowStatus.Terminal, result.State.Status);
            var childBindings = await WorkerBindingConfigParser.LoadFromFileAsync(
                Path.Combine(childRoom, "bindings.json"), TestContext.Current.CancellationToken);
            var prompt = childBindings["advise"].PromptTemplate;
            Assert.StartsWith("Weigh the options for Y instead.", prompt);
            Assert.DoesNotContain("Weigh the options for X.", prompt);
            // The role's output instructions must appear exactly once, not once from the role catalog
            // and again from a stale copy carried over in the parent's already-built prompt.
            var instructionCount = prompt.Split("Required outputs:").Length - 1;
            Assert.Equal(1, instructionCount);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    /// <summary>
    /// #1499: the amended-spec path rebuilds through <c>RoleDispatch.Materialize</c>, which knows
    /// nothing of the parent's label -- <c>RedispatchCommand.ExecuteAsync</c> stamps the
    /// inherit-unless-overridden rule on afterward. This is the one inheritance path
    /// <see cref="RedispatchBindingTests"/> cannot reach, since that suite only exercises
    /// <see cref="RedispatchCommand.InheritBinding"/> directly (the no-spec path).
    /// </summary>
    [Fact]
    public async Task A_label_survives_an_amended_spec_redispatch_unless_overridden()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"redispatch-e2e-{Guid.NewGuid():N}");
        try
        {
            var parentRoom = await DispatchTerminalParentAsync(testRoot, "Weigh the options for X.", label: "env-snapshot lane");

            var amendedSpecPath = Path.Combine(testRoot, "amended.md");
            await File.WriteAllTextAsync(amendedSpecPath, "Weigh the options for Y instead.", TestContext.Current.CancellationToken);

            var inheritedChildRoom = Path.Combine(testRoot, "child-inherited");
            var inheritedResult = await RedispatchCommand.ExecuteAsync(
                new RedispatchOptions(parentRoom, inheritedChildRoom, SpecFilePath: amendedSpecPath, Adapter: "fake"),
                Adapters, TestContext.Current.CancellationToken);
            Assert.Equal(WorkflowStatus.Terminal, inheritedResult.State.Status);
            var inheritedBindings = await WorkerBindingConfigParser.LoadFromFileAsync(
                Path.Combine(inheritedChildRoom, "bindings.json"), TestContext.Current.CancellationToken);
            Assert.Equal("env-snapshot lane", inheritedBindings["advise"].Label);

            var overriddenChildRoom = Path.Combine(testRoot, "child-overridden");
            var overriddenResult = await RedispatchCommand.ExecuteAsync(
                new RedispatchOptions(parentRoom, overriddenChildRoom, SpecFilePath: amendedSpecPath, Adapter: "fake", Label: "different lane"),
                Adapters, TestContext.Current.CancellationToken);
            Assert.Equal(WorkflowStatus.Terminal, overriddenResult.State.Status);
            var overriddenBindings = await WorkerBindingConfigParser.LoadFromFileAsync(
                Path.Combine(overriddenChildRoom, "bindings.json"), TestContext.Current.CancellationToken);
            Assert.Equal("different lane", overriddenBindings["advise"].Label);

            var clearedChildRoom = Path.Combine(testRoot, "child-cleared");
            var clearedResult = await RedispatchCommand.ExecuteAsync(
                new RedispatchOptions(parentRoom, clearedChildRoom, SpecFilePath: amendedSpecPath, Adapter: "fake", Label: null, LabelSpecified: true),
                Adapters, TestContext.Current.CancellationToken);
            Assert.Equal(WorkflowStatus.Terminal, clearedResult.State.Status);
            var clearedBindings = await WorkerBindingConfigParser.LoadFromFileAsync(
                Path.Combine(clearedChildRoom, "bindings.json"), TestContext.Current.CancellationToken);
            Assert.Null(clearedBindings["advise"].Label);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task A_blank_label_clears_the_inherited_label_on_an_unchanged_spec_redispatch()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"redispatch-e2e-{Guid.NewGuid():N}");
        try
        {
            var parentRoom = await DispatchTerminalParentAsync(testRoot, "Weigh the options for X.", label: "env-snapshot lane");

            var childRoom = Path.Combine(testRoot, "child-cleared");
            var options = new RedispatchOptions(parentRoom, childRoom, Label: null, LabelSpecified: true);

            var result = await RedispatchCommand.ExecuteAsync(options, Adapters, TestContext.Current.CancellationToken);

            Assert.Equal(WorkflowStatus.Terminal, result.State.Status);
            var childBindings = await WorkerBindingConfigParser.LoadFromFileAsync(
                Path.Combine(childRoom, "bindings.json"), TestContext.Current.CancellationToken);
            Assert.Null(childBindings["advise"].Label);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task An_explicit_override_wins_over_the_inherited_binding()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"redispatch-e2e-{Guid.NewGuid():N}");
        try
        {
            var parentRoom = await DispatchTerminalParentAsync(testRoot, "Weigh the options for X.", timeout: TimeSpan.FromMinutes(30));

            var childRoom = Path.Combine(testRoot, "child");
            var options = new RedispatchOptions(parentRoom, childRoom, Timeout: TimeSpan.FromMinutes(99));

            await RedispatchCommand.ExecuteAsync(options, Adapters, TestContext.Current.CancellationToken);

            var childBindings = await WorkerBindingConfigParser.LoadFromFileAsync(
                Path.Combine(childRoom, "bindings.json"), TestContext.Current.CancellationToken);
            Assert.Equal(TimeSpan.FromMinutes(99), childBindings["advise"].Timeout);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Lineage_is_recorded_naming_the_parent_room_and_its_execution_id()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"redispatch-e2e-{Guid.NewGuid():N}");
        try
        {
            var parentRoom = await DispatchTerminalParentAsync(testRoot, "Weigh the options for X.");
            var childRoom = Path.Combine(testRoot, "child");

            await RedispatchCommand.ExecuteAsync(new RedispatchOptions(parentRoom, childRoom), Adapters, TestContext.Current.CancellationToken);

            var markerPath = Path.Combine(childRoom, ".baton", BatonPaths.RoomMetadataFileName);
            Assert.True(File.Exists(markerPath));
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(markerPath, TestContext.Current.CancellationToken));
            Assert.Equal(parentRoom, doc.RootElement.GetProperty("ParentRoomDirectoryPath").GetString());
            Assert.False(string.IsNullOrEmpty(doc.RootElement.GetProperty("ParentExecutionId").GetString()));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Redispatching_a_non_terminal_parent_is_refused()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"redispatch-e2e-{Guid.NewGuid():N}");
        try
        {
            // Dispatched but no terminal.json written -- DispatchCommand.ExecuteAsync alone never
            // writes one; that is Program.cs's own post-processing (#1356), which this deliberately
            // skips to leave the room looking mid-flight.
            var specPath = await WriteSpecAsync(testRoot, "Weigh the options for X.");
            var parentRoom = Path.Combine(testRoot, "parent");
            await DispatchCommand.ExecuteAsync(
                new DispatchOptions("advise", specPath, parentRoom, Adapter: "fake"), Adapters, TestContext.Current.CancellationToken);

            var childRoom = Path.Combine(testRoot, "child");
            var ex = await Assert.ThrowsAsync<CliArgumentException>(() => RedispatchCommand.ExecuteAsync(
                new RedispatchOptions(parentRoom, childRoom), Adapters, TestContext.Current.CancellationToken));

            Assert.Contains("terminal", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(Directory.Exists(childRoom));

            // #1586: the refusal must diagnose WHY (no terminal sentinel means the room never
            // settled -- genuinely still running, or its engine died mid-wait) and point at the one
            // verb that actually recovers a dead-engine room, rather than only explaining its own
            // refusal (spec/baton.md §3's `baton run --room-dir` recovery, first said by
            // StatusCommand's parked-status line for #1582).
            Assert.Contains("engine died", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.NotNull(ex.TryInvocation);
            Assert.Contains("baton run", ex.TryInvocation, StringComparison.Ordinal);
            Assert.Contains("--room-dir", ex.TryInvocation, StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Redispatching_a_missing_parent_room_is_a_typed_argument_error()
    {
        var missingParent = Path.Combine(Path.GetTempPath(), $"redispatch-missing-{Guid.NewGuid():N}");
        var childRoom = Path.Combine(Path.GetTempPath(), $"redispatch-child-{Guid.NewGuid():N}");

        await Assert.ThrowsAsync<CliArgumentException>(() => RedispatchCommand.ExecuteAsync(
            new RedispatchOptions(missingParent, childRoom), Adapters, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_terminal_but_not_Succeeded_parent_is_redispatched_with_a_warning_not_a_refusal()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"redispatch-e2e-{Guid.NewGuid():N}");
        var originalError = Console.Error;
        try
        {
            // fake-noop satisfies no declared output, so advise's step -- and the workflow -- lands
            // Failed, not Succeeded, once terminal.json is written for it below.
            var parentRoom = await DispatchTerminalParentAsync(testRoot, "Weigh the options for X.", adapter: "fake-noop");

            using var stderr = new StringWriter();
            Console.SetError(stderr);

            var childRoom = Path.Combine(testRoot, "child");
            var options = new RedispatchOptions(parentRoom, childRoom, Adapter: "fake");
            var result = await RedispatchCommand.ExecuteAsync(options, Adapters, TestContext.Current.CancellationToken);

            Assert.Equal(WorkflowStatus.Terminal, result.State.Status);
            Assert.Contains("did not succeed", stderr.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetError(originalError);
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Redispatching_a_composed_template_room_is_refused()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"redispatch-e2e-{Guid.NewGuid():N}");
        try
        {
            var parentRoom = Path.Combine(testRoot, "parent");
            Directory.CreateDirectory(parentRoom);

            // A two-worker bindings.json is enough to look template-shaped without materializing a
            // real composed template -- redispatch's refusal keys only on bindings.json's own arity.
            var multiWorkerBindings = new Dictionary<string, WorkerBindingConfigEntry>
            {
                ["capture"] = new(
                    "git", new WorkerContract("capture", [], [new ProducedOutput("base.txt")], []), "prompt", TimeSpan.FromMinutes(5)),
                ["advise"] = new(
                    "fake", new WorkerContract("advise", [], [new ProducedOutput("advice.md")], []), "prompt", TimeSpan.FromMinutes(30)),
            };
            await WorkerBindingConfigWriter.SaveToFileAsync(
                multiWorkerBindings, BatonPaths.RoomBindingsFile(parentRoom), TestContext.Current.CancellationToken);
            await TerminalSentinelWriter.WriteAsync(
                parentRoom, new WorkflowStatusView(WorkflowOutcome.Succeeded, [], [], null), TestContext.Current.CancellationToken);

            var childRoom = Path.Combine(testRoot, "child");
            var ex = await Assert.ThrowsAsync<CliArgumentException>(() => RedispatchCommand.ExecuteAsync(
                new RedispatchOptions(parentRoom, childRoom), Adapters, TestContext.Current.CancellationToken));

            Assert.Contains("2 workers", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task An_Indeterminate_parent_refuses_bare_redispatch_with_a_diagnosis()
    {
        // #1586 S1: no producer in this slice writes "Indeterminate" to a real terminal.json (see
        // WorkflowOutcome.Indeterminate's own remarks) -- this fixture writes the sentinel by hand,
        // which the slice's scope note permits, so the CONSUMER side of the vocabulary gets proven
        // ahead of any producer existing.
        var testRoot = Path.Combine(Path.GetTempPath(), $"redispatch-e2e-{Guid.NewGuid():N}");
        try
        {
            var parentRoom = Path.Combine(testRoot, "parent");
            Directory.CreateDirectory(parentRoom);
            await TerminalSentinelWriter.WriteAsync(
                parentRoom, new WorkflowStatusView(WorkflowOutcome.Indeterminate, [], [], null), TestContext.Current.CancellationToken);

            var childRoom = Path.Combine(testRoot, "child");
            var ex = await Assert.ThrowsAsync<CliArgumentException>(() => RedispatchCommand.ExecuteAsync(
                new RedispatchOptions(parentRoom, childRoom), Adapters, TestContext.Current.CancellationToken));

            Assert.Contains("Indeterminate", ex.Message, StringComparison.Ordinal);
            Assert.False(Directory.Exists(childRoom));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task The_same_room_Failed_instead_of_Indeterminate_is_redispatched_with_a_warning_not_a_refusal()
    {
        // Polarity partner: identical fixture, one state string apart, proving the refusal above is
        // about Indeterminate specifically and not incidentally about "any non-Succeeded terminal
        // parent" (that's the existing warn-and-proceed test above).
        var testRoot = Path.Combine(Path.GetTempPath(), $"redispatch-e2e-{Guid.NewGuid():N}");
        var originalError = Console.Error;
        try
        {
            var parentRoom = Path.Combine(testRoot, "parent");
            Directory.CreateDirectory(parentRoom);
            var bindings = new Dictionary<string, WorkerBindingConfigEntry>
            {
                ["advise"] = new("fake", new WorkerContract("advise", [], [new ProducedOutput("advice.md")], []), "prompt", TimeSpan.FromMinutes(30)),
            };
            await WorkerBindingConfigWriter.SaveToFileAsync(
                bindings, BatonPaths.RoomBindingsFile(parentRoom), TestContext.Current.CancellationToken);
            await WorkflowDefinitionWriter.SaveToFileAsync(
                new WorkflowDefinition(
                    new WorkflowTemplateId("wf-1"), WorkflowTemplateVersion: 1,
                    Steps: [new WorkflowStepDefinition(new StepId("advise"), "advise", [], ["advice.md"], DependsOn: [], RetryPolicy: new RetryPolicy(1))]),
                Path.Combine(parentRoom, "workflow.json"), TestContext.Current.CancellationToken);
            await TerminalSentinelWriter.WriteAsync(
                parentRoom, new WorkflowStatusView(WorkflowOutcome.Failed, [], [], "some reason"), TestContext.Current.CancellationToken);

            using var stderr = new StringWriter();
            Console.SetError(stderr);

            var childRoom = Path.Combine(testRoot, "child");
            var options = new RedispatchOptions(parentRoom, childRoom, Adapter: "fake");
            var result = await RedispatchCommand.ExecuteAsync(options, Adapters, TestContext.Current.CancellationToken);

            Assert.Equal(WorkflowStatus.Terminal, result.State.Status);
            Assert.Contains("did not succeed", stderr.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetError(originalError);
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    private static async Task<string> DispatchTerminalParentAsync(
        string testRoot, string spec, string adapter = "fake", TimeSpan? timeout = null, string? label = null)
    {
        var specPath = await WriteSpecAsync(testRoot, spec);
        var roomDirectory = Path.Combine(testRoot, "parent");
        var options = new DispatchOptions("advise", specPath, roomDirectory, Adapter: adapter, Timeout: timeout, Label: label);

        var result = await DispatchCommand.ExecuteAsync(options, Adapters, TestContext.Current.CancellationToken);

        // DispatchCommand.ExecuteAsync alone never writes terminal.json -- that is Program.cs's own
        // post-processing (#1356) -- so a test driving the command directly reproduces it here to set
        // up a genuinely terminal parent room.
        var view = WorkflowStatusProjector.Project(result.State, result.Snapshot, roomDirectory);
        await TerminalSentinelWriter.WriteAsync(roomDirectory, view, TestContext.Current.CancellationToken);

        return roomDirectory;
    }

    private static async Task<string> WriteSpecAsync(string directory, string content)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "spec.md");
        await File.WriteAllTextAsync(path, content, TestContext.Current.CancellationToken);
        return path;
    }
}
