using System.Text.Json;
using Baton.Vendors;
using Baton.Dispatch;
using Baton.Domain;
using Baton.Tests.Shared;
using Xunit;

namespace Baton.Cli.Tests;

/// <summary>
/// #599: a room directory resolving inside an adapter's <see cref="IWorkerAdapter.SensitiveOutputRoot"/>
/// is refused before <c>baton run</c> dispatches anything, rather than discovered as a silent,
/// unclassified contract failure after a full run was paid for.
/// </summary>
public class RunCommandSensitiveOutputRootTests
{
    /// <summary>An adapter whose vendor treats <see cref="SensitiveOutputRoot"/> as sensitive — never spawns anything, so the assertion is reached only if the refusal fails to fire.</summary>
    private sealed class FakeSensitiveAdapter(string sensitiveRoot) : IWorkerAdapter
    {
        public string? SensitiveOutputRoot => sensitiveRoot;

        public CoreDispatchTarget Resolve(WorkerInvocation invocation, WorkerContract contract) =>
            throw new InvalidOperationException("Should never be reached: the room-directory refusal must fire first.");
    }

    [Fact]
    public async Task A_room_directory_inside_the_adapters_sensitive_root_is_refused_before_dispatch()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-599-{Guid.NewGuid():N}");
        var sensitiveRoot = Path.Combine(testRoot, "vendor-config-root");
        var roomDirectory = Path.Combine(sensitiveRoot, "jobs", "room1");
        Directory.CreateDirectory(testRoot);

        try
        {
            var workflowFilePath = await WriteOneStepWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteOneStepBindingsAsync(testRoot);
            var options = new RunOptions(workflowFilePath, bindingsFilePath, roomDirectory);
            var adapters = new Dictionary<string, IWorkerAdapter> { ["fake-sensitive"] = new FakeSensitiveAdapter(sensitiveRoot) };

            var ex = await Assert.ThrowsAsync<SensitiveOutputRootException>(
                () => RunCommand.ExecuteAsync(options, adapters, cancellationToken: TestContext.Current.CancellationToken));

            Assert.Equal("step1", ex.WorkerName);
            Assert.Equal(sensitiveRoot, ex.SensitiveRoot);
            Assert.Contains(sensitiveRoot, ex.Message, StringComparison.Ordinal);

            // Never reached the room's dispatch pump: an execution directory would mean the refusal
            // fired too late, after AER had already paid for (part of) a run.
            Assert.False(Directory.Exists(Path.Combine(roomDirectory, "artifacts")));
        }
        finally
        {
            if (Directory.Exists(testRoot))
            {
                DirectoryCleanup.DeleteRecursively(testRoot);
            }
        }
    }

    [Fact]
    public async Task A_room_directory_outside_the_adapters_sensitive_root_is_not_refused()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-599-control-{Guid.NewGuid():N}");
        var sensitiveRoot = Path.Combine(testRoot, "vendor-config-root");
        var roomDirectory = Path.Combine(testRoot, "elsewhere", "room1");
        Directory.CreateDirectory(testRoot);

        try
        {
            var workflowFilePath = await WriteOneStepWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteOneStepBindingsAsync(testRoot);
            var options = new RunOptions(workflowFilePath, bindingsFilePath, roomDirectory);
            var adapters = new Dictionary<string, IWorkerAdapter> { ["fake-sensitive"] = new FakeSensitiveAdapter(sensitiveRoot) };

            // The control arm: same sensitive-root adapter, a room directory that does not resolve
            // inside it. Reaches Resolve (which throws its own distinguishing exception) rather than
            // the refusal under test -- proving the refusal above discriminates on the path, not on
            // the mere presence of a SensitiveOutputRoot.
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => RunCommand.ExecuteAsync(options, adapters, cancellationToken: TestContext.Current.CancellationToken));
            Assert.Contains("Should never be reached", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(testRoot))
            {
                DirectoryCleanup.DeleteRecursively(testRoot);
            }
        }
    }

    private static async Task<string> WriteOneStepWorkflowAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var definition = new WorkflowDefinition(
            new WorkflowTemplateId("one-step-linear"),
            1,
            [
                new WorkflowStepDefinition(new StepId("step1"), "step1", [], ["output1"], [], new RetryPolicy(1)),
            ]);

        var path = Path.Combine(directory, "workflow.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(definition), TestContext.Current.CancellationToken);
        return path;
    }

    private static async Task<string> WriteOneStepBindingsAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["step1"] = new WorkerBindingConfigEntry(
                "fake-sensitive",
                new WorkerContract("step1", [], [new ProducedOutput("output1")], []),
                "prompt",
                TimeSpan.FromSeconds(30)),
        };

        var path = Path.Combine(directory, "bindings.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(config), TestContext.Current.CancellationToken);
        return path;
    }
}
