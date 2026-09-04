using System.Text.Json;
using Baton.Vendors;
using Baton.Dispatch;
using Baton.Domain;
using Baton.Tests.Shared;
using Xunit;

namespace Baton.Cli.Tests;

/// <summary>
/// #599, corrected to a component match by #1834: a room directory whose path contains a component an
/// adapter's <see cref="IWorkerAdapter.HasSensitiveOutputPathComponent"/> treats as sensitive is refused
/// before <c>baton run</c> dispatches anything, rather than discovered as a silent, unclassified
/// contract failure after a full run was paid for.
/// </summary>
public class RunCommandSensitiveOutputRootTests
{
    /// <summary>
    /// An adapter whose <see cref="HasSensitiveOutputPathComponent"/> answer is fixed by the test rather
    /// than actually inspecting <c>roomDirectoryPath</c> — <see cref="Resolve"/> never spawns anything,
    /// so the assertion is reached only if the refusal fails to fire.
    /// </summary>
    private sealed class FakeSensitiveAdapter(bool matches, string? offendingComponent) : IWorkerAdapter
    {
        public bool HasSensitiveOutputPathComponent(string roomDirectoryPath, out string? offendingComponentOut)
        {
            offendingComponentOut = matches ? offendingComponent : null;
            return matches;
        }

        public CoreDispatchTarget Resolve(WorkerInvocation invocation, WorkerContract contract) =>
            throw new InvalidOperationException("Should never be reached: the room-directory refusal must fire first.");
    }

    [Fact]
    public async Task A_room_directory_with_the_adapters_sensitive_component_is_refused_before_dispatch()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-599-{Guid.NewGuid():N}");
        var offendingComponent = ".claude";
        var roomDirectory = Path.Combine(testRoot, offendingComponent, "jobs", "room1");
        Directory.CreateDirectory(testRoot);

        try
        {
            var workflowFilePath = await WriteOneStepWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteOneStepBindingsAsync(testRoot);
            var options = new RunOptions(workflowFilePath, bindingsFilePath, roomDirectory);
            var adapters = new Dictionary<string, IWorkerAdapter> { ["fake-sensitive"] = new FakeSensitiveAdapter(matches: true, offendingComponent) };

            var ex = await Assert.ThrowsAsync<SensitiveOutputRootException>(
                () => RunCommand.ExecuteAsync(options, adapters, cancellationToken: TestContext.Current.CancellationToken));

            Assert.Equal("step1", ex.WorkerName);
            Assert.Equal(offendingComponent, ex.OffendingComponent);
            Assert.Contains(offendingComponent, ex.Message, StringComparison.Ordinal);

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
    public async Task A_room_directory_without_the_adapters_sensitive_component_is_not_refused()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-599-control-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "elsewhere", "room1");
        Directory.CreateDirectory(testRoot);

        try
        {
            var workflowFilePath = await WriteOneStepWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteOneStepBindingsAsync(testRoot);
            var options = new RunOptions(workflowFilePath, bindingsFilePath, roomDirectory);
            var adapters = new Dictionary<string, IWorkerAdapter> { ["fake-sensitive"] = new FakeSensitiveAdapter(matches: false, offendingComponent: null) };

            // The control arm: same adapter type, told this room directory does not match. Reaches
            // Resolve (which throws its own distinguishing exception) rather than the refusal under
            // test -- proving the refusal above discriminates on the adapter's answer, not on the mere
            // presence of a FakeSensitiveAdapter.
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
