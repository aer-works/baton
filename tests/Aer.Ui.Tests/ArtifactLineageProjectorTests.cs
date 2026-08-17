using Aer.Ui.Tests.TestSupport;
using Aer.Flow.Dispatch;
using Aer.Flow.Domain;

namespace Aer.Ui.Tests;

/// <summary>
/// Unit-level coverage for <see cref="ArtifactLineageProjector"/> (M14 Phase 4, issue #121),
/// mirroring <see cref="ExecutionHistoryProjectorTests"/>' style of building <see cref="FlowEvent"/>
/// lists by hand — plus a real temp directory standing in for <c>artifacts/</c>, since this
/// projector (unlike <c>ExecutionHistoryProjector</c>/<c>StateProjector</c>) also reads the
/// filesystem, per UI spec §12's transparency rule naming artifact directories a legitimate
/// projection input.
/// </summary>
public class ArtifactLineageProjectorTests
{
    private static readonly StepId Architect = new("architect");
    private static readonly StepId Critic = new("critic");

    private static WorkflowDefinitionSnapshot TwoStepSnapshot() => new(
        new WorkflowDefinitionSnapshotId("snapshot-1"),
        new WorkflowTemplateId("architect-critic"),
        WorkflowTemplateVersion: 1,
        Steps:
        [
            new WorkflowStepDefinition(Architect, "architect", ["goal"], ["plan"], DependsOn: [], RetryPolicy: new RetryPolicy(3)),
            new WorkflowStepDefinition(Critic, "critic", ["plan"], ["review"], DependsOn: [Architect], RetryPolicy: new RetryPolicy(1)),
        ]);

    private static ExecutionRequest MakeRequest(
        ExecutionId executionId,
        StepId? stepId,
        IReadOnlyList<string>? inputs = null,
        IReadOnlyDictionary<StepId, ExecutionId>? upstreamExecutionIds = null,
        string worker = "worker")
        => new(
            executionId,
            new WorkflowId("wf-1"),
            stepId,
            worker,
            Inputs: inputs ?? [],
            Outputs: [],
            Timeout: TimeSpan.FromMinutes(10),
            Environment: [],
            UpstreamExecutionIds: upstreamExecutionIds ?? new Dictionary<StepId, ExecutionId>());

    private static string NewArtifactsRoot() => Path.Combine(Path.GetTempPath(), $"ui-lineage-{Guid.NewGuid():N}");

    private static void WriteOutputFile(string artifactsRoot, ExecutionId executionId, string fileName, string content = "content")
    {
        var directory = Path.Combine(artifactsRoot, $"execution_{executionId}");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, fileName), content);
    }

    [Fact]
    public void An_execution_with_no_output_directory_projects_an_empty_file_list_not_an_error()
    {
        var artifactsRoot = NewArtifactsRoot();
        var executionId = new ExecutionId("a-1");
        var events = new FlowEvent[] { new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Architect)) };

        var lineage = ArtifactLineageProjector.Project(events, TwoStepSnapshot(), artifactsRoot);

        var execution = Assert.Single(lineage.Executions);
        Assert.Empty(execution.OutputFiles);
    }

    // #1345 (0021 §2): the polarity pair for the stream-log filter. AER's own capture of a run is
    // plumbing and never a room file; a dot-prefixed file a WORKER wrote is a deliverable like any
    // other. Both arms matter — the first alone would pass for a naive dot-prefix rule, which would
    // silently eat the second.
    [Fact]
    public void The_engines_own_stream_logs_are_not_projected_as_output_files()
    {
        var artifactsRoot = NewArtifactsRoot();
        var executionId = new ExecutionId("a-1");
        try
        {
            WriteOutputFile(artifactsRoot, executionId, "report.md");
            WriteOutputFile(artifactsRoot, executionId, ExecutionStreamLogger.StdoutLogFileName);
            WriteOutputFile(artifactsRoot, executionId, ExecutionStreamLogger.StdoutRolloverFileName);
            WriteOutputFile(artifactsRoot, executionId, ExecutionStreamLogger.StderrLogFileName);
            WriteOutputFile(artifactsRoot, executionId, ExecutionStreamLogger.StderrRolloverFileName);

            var events = new FlowEvent[] { new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Architect)) };
            var lineage = ArtifactLineageProjector.Project(events, TwoStepSnapshot(), artifactsRoot);

            var execution = Assert.Single(lineage.Executions);
            Assert.Equal(["report.md"], execution.OutputFiles);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(artifactsRoot);
        }
    }

    [Fact]
    public void A_dot_prefixed_file_the_worker_produced_is_still_an_output_file()
    {
        var artifactsRoot = NewArtifactsRoot();
        var executionId = new ExecutionId("a-1");
        try
        {
            WriteOutputFile(artifactsRoot, executionId, ".gitignore");
            WriteOutputFile(artifactsRoot, executionId, ".editorconfig");
            WriteOutputFile(artifactsRoot, executionId, ExecutionStreamLogger.StdoutLogFileName);

            var events = new FlowEvent[] { new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Architect)) };
            var lineage = ArtifactLineageProjector.Project(events, TwoStepSnapshot(), artifactsRoot);

            var execution = Assert.Single(lineage.Executions);
            Assert.Equal([".editorconfig", ".gitignore"], execution.OutputFiles);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(artifactsRoot);
        }
    }

    [Fact]
    public void Output_files_on_disk_are_listed_sorted_ordinally_regardless_of_creation_order()
    {
        var artifactsRoot = NewArtifactsRoot();
        var executionId = new ExecutionId("a-1");
        try
        {
            WriteOutputFile(artifactsRoot, executionId, "zeta.txt");
            WriteOutputFile(artifactsRoot, executionId, "alpha.txt");
            WriteOutputFile(artifactsRoot, executionId, "Middle.txt");

            var events = new FlowEvent[] { new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Architect)) };
            var lineage = ArtifactLineageProjector.Project(events, TwoStepSnapshot(), artifactsRoot);

            var execution = Assert.Single(lineage.Executions);
            Assert.Equal(["Middle.txt", "alpha.txt", "zeta.txt"], execution.OutputFiles);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(artifactsRoot);
        }
    }

    [Fact]
    public void An_input_resolves_to_the_exact_upstream_execution_this_request_recorded_not_the_latest_one()
    {
        var artifactsRoot = NewArtifactsRoot();
        var firstArchitectAttempt = new ExecutionId("a-1");
        var secondArchitectAttempt = new ExecutionId("a-2");
        var criticExecutionId = new ExecutionId("c-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(firstArchitectAttempt, Architect)),
            new FlowEvent.ExecutionFailed(firstArchitectAttempt, FailureClassification.Retryable),
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(secondArchitectAttempt, Architect)),
            new FlowEvent.ExecutionSucceeded(secondArchitectAttempt),
            // Recorded fact: this request's input came from the *first* attempt, even though the
            // second later succeeded — exactly the case a "latest execution" re-derivation would get
            // wrong.
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(
                criticExecutionId, Critic, inputs: ["plan"],
                upstreamExecutionIds: new Dictionary<StepId, ExecutionId> { [Architect] = firstArchitectAttempt })),
        };

        var lineage = ArtifactLineageProjector.Project(events, TwoStepSnapshot(), artifactsRoot);

        var criticExecution = lineage.Executions.Single(e => e.ExecutionId == criticExecutionId);
        var link = Assert.Single(criticExecution.Inputs);
        Assert.Equal("plan", link.InputName);
        Assert.Equal(Architect, link.ProducerStepId);
        Assert.Equal(firstArchitectAttempt, link.ProducerExecutionId);
    }

    [Fact]
    public void A_step_less_execution_has_no_input_links_and_never_throws()
    {
        var artifactsRoot = NewArtifactsRoot();
        var executionId = new ExecutionId("supplement-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, stepId: null, worker: "human")),
        };

        var lineage = ArtifactLineageProjector.Project(events, TwoStepSnapshot(), artifactsRoot);

        var execution = Assert.Single(lineage.Executions);
        Assert.Null(execution.StepId);
        Assert.Empty(execution.Inputs);
    }

    [Fact]
    public void Executions_are_projected_in_recorded_order()
    {
        var artifactsRoot = NewArtifactsRoot();
        var first = new ExecutionId("a-1");
        var second = new ExecutionId("c-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(first, Architect)),
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(second, Critic)),
        };

        var lineage = ArtifactLineageProjector.Project(events, TwoStepSnapshot(), artifactsRoot);

        Assert.Equal([first, second], lineage.Executions.Select(e => e.ExecutionId));
    }
}
