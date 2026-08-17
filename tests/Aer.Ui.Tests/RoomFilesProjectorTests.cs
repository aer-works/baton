using Aer.Ui.Tests.TestSupport;
using Aer.Flow.Domain;

namespace Aer.Ui.Tests;

/// <summary>
/// Unit-level coverage for <see cref="RoomFilesProjector"/> (#1340, 0021 §2), mirroring
/// <see cref="ArtifactLineageProjectorTests"/>' style of building <see cref="FlowEvent"/> lists (and,
/// for the terminal events this projector reads timestamps off, <see cref="LogEntry"/> envelopes) by
/// hand, plus a real temp directory standing in for <c>artifacts/</c> — this projector, like
/// <see cref="ArtifactLineageProjector"/>, reads real output-file names off <see cref="ArtifactLineage"/>,
/// which itself reads the filesystem.
/// </summary>
public class RoomFilesProjectorTests
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

    private static ExecutionRequest MakeRequest(ExecutionId executionId, StepId? stepId, string worker = "claude")
        => new(
            executionId,
            new WorkflowId("wf-1"),
            stepId,
            worker,
            Inputs: [],
            Outputs: [],
            Timeout: TimeSpan.FromMinutes(10),
            Environment: [],
            UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>());

    private static string NewArtifactsRoot() => Path.Combine(Path.GetTempPath(), $"ui-room-files-{Guid.NewGuid():N}");

    private static void WriteOutputFile(string artifactsRoot, ExecutionId executionId, string fileName, string content = "content")
    {
        var directory = Path.Combine(artifactsRoot, $"execution_{executionId}");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, fileName), content);
    }

    /// <summary>Wraps a bare <see cref="FlowEvent"/> the way the journal reader hands entries to <see cref="RoomProjectionLoader"/> — with an optional writer stamp.</summary>
    private static LogEntry.FlowLogEntry Entry(FlowEvent flowEvent, DateTime? writerUtcTimestamp = null) =>
        new(flowEvent, writerUtcTimestamp);

    [Fact]
    public void A_retried_step_yields_two_versions_of_one_file()
    {
        var artifactsRoot = NewArtifactsRoot();
        var firstAttempt = new ExecutionId("a-1");
        var secondAttempt = new ExecutionId("a-2");
        try
        {
            WriteOutputFile(artifactsRoot, firstAttempt, "plan.md");
            WriteOutputFile(artifactsRoot, secondAttempt, "plan.md");

            var events = new FlowEvent[]
            {
                new FlowEvent.ExecutionRequestAccepted(MakeRequest(firstAttempt, Architect)),
                new FlowEvent.ExecutionFailed(firstAttempt, FailureClassification.Retryable),
                new FlowEvent.ExecutionRequestAccepted(MakeRequest(secondAttempt, Architect)),
                new FlowEvent.ExecutionSucceeded(secondAttempt),
            };
            var entries = new LogEntry[]
            {
                Entry(events[0]),
                Entry(events[1], new DateTime(2026, 8, 17, 10, 0, 0, DateTimeKind.Utc)),
                Entry(events[2]),
                Entry(events[3], new DateTime(2026, 8, 17, 10, 5, 0, DateTimeKind.Utc)),
            };

            var lineage = ArtifactLineageProjector.Project(events, TwoStepSnapshot(), artifactsRoot);
            var roomFiles = RoomFilesProjector.Project(lineage, entries, artifactsRoot);

            var file = Assert.Single(roomFiles.Files);
            Assert.Equal("plan.md", file.Name);
            Assert.Equal(2, file.Versions.Count);
            Assert.Equal(firstAttempt, file.Versions[0].Origin);
            Assert.Equal(secondAttempt, file.Versions[1].Origin);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(artifactsRoot);
        }
    }

    [Fact]
    public void A_retried_step_producing_different_names_yields_two_separate_files_not_one()
    {
        var artifactsRoot = NewArtifactsRoot();
        var firstAttempt = new ExecutionId("a-1");
        var secondAttempt = new ExecutionId("a-2");
        try
        {
            WriteOutputFile(artifactsRoot, firstAttempt, "draft.md");
            WriteOutputFile(artifactsRoot, secondAttempt, "final.md");

            var events = new FlowEvent[]
            {
                new FlowEvent.ExecutionRequestAccepted(MakeRequest(firstAttempt, Architect)),
                new FlowEvent.ExecutionFailed(firstAttempt, FailureClassification.Retryable),
                new FlowEvent.ExecutionRequestAccepted(MakeRequest(secondAttempt, Architect)),
                new FlowEvent.ExecutionSucceeded(secondAttempt),
            };
            var entries = events.Select(e => (LogEntry)Entry(e)).ToList();

            var lineage = ArtifactLineageProjector.Project(events, TwoStepSnapshot(), artifactsRoot);
            var roomFiles = RoomFilesProjector.Project(lineage, entries, artifactsRoot);

            Assert.Equal(2, roomFiles.Files.Count);
            Assert.All(roomFiles.Files, file => Assert.Single(file.Versions));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(artifactsRoot);
        }
    }

    [Fact]
    public void The_same_name_produced_by_two_different_steps_chains_into_one_file()
    {
        var artifactsRoot = NewArtifactsRoot();
        var architectExecutionId = new ExecutionId("a-1");
        var criticExecutionId = new ExecutionId("c-1");
        try
        {
            WriteOutputFile(artifactsRoot, architectExecutionId, "handoff.md");
            WriteOutputFile(artifactsRoot, criticExecutionId, "handoff.md");

            var events = new FlowEvent[]
            {
                new FlowEvent.ExecutionRequestAccepted(MakeRequest(architectExecutionId, Architect, worker: "claude")),
                new FlowEvent.ExecutionSucceeded(architectExecutionId),
                new FlowEvent.ExecutionRequestAccepted(MakeRequest(criticExecutionId, Critic, worker: "gemini")),
                new FlowEvent.ExecutionSucceeded(criticExecutionId),
            };
            var entries = events.Select(e => (LogEntry)Entry(e)).ToList();

            var lineage = ArtifactLineageProjector.Project(events, TwoStepSnapshot(), artifactsRoot);
            var roomFiles = RoomFilesProjector.Project(lineage, entries, artifactsRoot);

            var file = Assert.Single(roomFiles.Files);
            Assert.Equal("handoff.md", file.Name);
            Assert.Equal(2, file.Versions.Count);
            Assert.Equal("claude", file.Versions[0].Worker);
            Assert.Equal("gemini", file.Versions[1].Worker);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(artifactsRoot);
        }
    }

    [Fact]
    public void Different_names_produced_by_two_different_steps_stay_two_separate_files()
    {
        var artifactsRoot = NewArtifactsRoot();
        var architectExecutionId = new ExecutionId("a-1");
        var criticExecutionId = new ExecutionId("c-1");
        try
        {
            WriteOutputFile(artifactsRoot, architectExecutionId, "plan.md");
            WriteOutputFile(artifactsRoot, criticExecutionId, "review.md");

            var events = new FlowEvent[]
            {
                new FlowEvent.ExecutionRequestAccepted(MakeRequest(architectExecutionId, Architect)),
                new FlowEvent.ExecutionSucceeded(architectExecutionId),
                new FlowEvent.ExecutionRequestAccepted(MakeRequest(criticExecutionId, Critic)),
                new FlowEvent.ExecutionSucceeded(criticExecutionId),
            };
            var entries = events.Select(e => (LogEntry)Entry(e)).ToList();

            var lineage = ArtifactLineageProjector.Project(events, TwoStepSnapshot(), artifactsRoot);
            var roomFiles = RoomFilesProjector.Project(lineage, entries, artifactsRoot);

            Assert.Equal(["plan.md", "review.md"], roomFiles.Files.Select(f => f.Name));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(artifactsRoot);
        }
    }

    [Fact]
    public void An_execution_whose_terminal_event_predates_the_writer_stamp_renders_absent_not_fabricated()
    {
        var artifactsRoot = NewArtifactsRoot();
        var executionId = new ExecutionId("a-1");
        try
        {
            WriteOutputFile(artifactsRoot, executionId, "plan.md");

            var events = new FlowEvent[]
            {
                new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Architect)),
                new FlowEvent.ExecutionSucceeded(executionId),
            };
            // The terminal event's own envelope carries no writer stamp (an older room, #1197's
            // precedent) — an honest gap, never a fabricated instant.
            var entries = new LogEntry[] { Entry(events[0]), Entry(events[1], writerUtcTimestamp: null) };

            var lineage = ArtifactLineageProjector.Project(events, TwoStepSnapshot(), artifactsRoot);
            var roomFiles = RoomFilesProjector.Project(lineage, entries, artifactsRoot);

            var version = Assert.Single(Assert.Single(roomFiles.Files).Versions);
            Assert.Null(version.ProducedAt);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(artifactsRoot);
        }
    }

    [Fact]
    public void An_execution_whose_terminal_event_carries_a_writer_stamp_renders_it_exactly()
    {
        var artifactsRoot = NewArtifactsRoot();
        var executionId = new ExecutionId("a-1");
        var stamp = new DateTime(2026, 8, 17, 14, 2, 0, DateTimeKind.Utc);
        try
        {
            WriteOutputFile(artifactsRoot, executionId, "plan.md");

            var events = new FlowEvent[]
            {
                new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Architect)),
                new FlowEvent.ExecutionSucceeded(executionId),
            };
            var entries = new LogEntry[] { Entry(events[0]), Entry(events[1], stamp) };

            var lineage = ArtifactLineageProjector.Project(events, TwoStepSnapshot(), artifactsRoot);
            var roomFiles = RoomFilesProjector.Project(lineage, entries, artifactsRoot);

            var version = Assert.Single(Assert.Single(roomFiles.Files).Versions);
            Assert.Equal(new DateTimeOffset(stamp), version.ProducedAt);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(artifactsRoot);
        }
    }

    [Fact]
    public void Prompt_txt_is_excluded_even_when_produced_alongside_a_real_output_file()
    {
        var artifactsRoot = NewArtifactsRoot();
        var executionId = new ExecutionId("a-1");
        try
        {
            WriteOutputFile(artifactsRoot, executionId, "plan.md");
            WriteOutputFile(artifactsRoot, executionId, "prompt.txt");

            var events = new FlowEvent[]
            {
                new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Architect)),
                new FlowEvent.ExecutionSucceeded(executionId),
            };
            var entries = events.Select(e => (LogEntry)Entry(e)).ToList();

            var lineage = ArtifactLineageProjector.Project(events, TwoStepSnapshot(), artifactsRoot);
            var roomFiles = RoomFilesProjector.Project(lineage, entries, artifactsRoot);

            var file = Assert.Single(roomFiles.Files);
            Assert.Equal("plan.md", file.Name);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(artifactsRoot);
        }
    }

    [Fact]
    public void A_file_that_is_not_named_exactly_prompt_txt_is_never_excluded()
    {
        var artifactsRoot = NewArtifactsRoot();
        var executionId = new ExecutionId("a-1");
        try
        {
            // Ordinal, exact-name matching only — a near-miss on the constant is a real file, not a
            // second, accidentally broader exclusion rule.
            WriteOutputFile(artifactsRoot, executionId, "prompt2.txt");
            WriteOutputFile(artifactsRoot, executionId, "PROMPT.TXT");

            var events = new FlowEvent[]
            {
                new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Architect)),
                new FlowEvent.ExecutionSucceeded(executionId),
            };
            var entries = events.Select(e => (LogEntry)Entry(e)).ToList();

            var lineage = ArtifactLineageProjector.Project(events, TwoStepSnapshot(), artifactsRoot);
            var roomFiles = RoomFilesProjector.Project(lineage, entries, artifactsRoot);

            Assert.Equal(["PROMPT.TXT", "prompt2.txt"], roomFiles.Files.Select(f => f.Name));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(artifactsRoot);
        }
    }
}
