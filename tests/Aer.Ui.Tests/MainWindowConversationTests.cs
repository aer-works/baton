using Aer.Flow.Dispatch;
using Aer.Flow.Domain;
using Aer.Flow.Mutation;
using Aer.Flow.Store;
using Aer.Flow.Templates;
using Aer.Ui.Tests.TestSupport;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;

namespace Aer.Ui.Tests;

/// <summary>
/// Drives the real <see cref="MainWindow"/>'s conversation view (M18 Phase 2, issue #178) through
/// its actual rendered controls, the same headless-Avalonia approach every other
/// <c>MainWindow*Tests</c> class uses, on the same real-<c>MutationInterface</c>-pump fixture as
/// <see cref="MainWindowArtifactLineageAndDiffTests"/>. Transcripts are written by hand to UI spec
/// §10.1's reader contract — which worker produced them is irrelevant by design (discovery is by
/// durable content alone), and the full dialogue-worker round trip is Phase 3's gate (#179).
/// </summary>
public class MainWindowConversationTests
{
    private static readonly StepId Architect = new("architect");
    private static readonly StepId Critic = new("critic");

    private static string NewConfigFilePath() =>
        Path.Combine(Path.GetTempPath(), $"aer-ui-conversation-config-{Guid.NewGuid():N}", "recent-room-directories.json");

    private static string TurnLine(int sequence, string role, string vendor, string prompt, string text)
        => $"{{\"Sequence\":{sequence},\"Role\":\"{role}\",\"Vendor\":\"{vendor}\",\"Prompt\":\"{prompt}\",\"Text\":\"{text}\"}}";

    private static async Task<string> CreatePumpedRoomDirectoryAsync(CancellationToken cancellationToken)
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "three-step-linear-workflow.json");
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"ui-conversation-window-{Guid.NewGuid():N}");

        var definition = await WorkflowDefinitionParser.LoadFromFileAsync(fixturePath, cancellationToken);
        var snapshot = SnapshotBinder.Bind(definition);
        await SnapshotBinder.PersistAsync(snapshot, Path.Combine(roomDirectory, "snapshot.json"), cancellationToken);

        var bindings = new Dictionary<string, WorkerBinding>
        {
            ["architect"] = new WorkerBinding.Process(
                new WorkerContract("architect", [], [new ProducedOutput("plan")], []),
                ShellWorkerCommands.WriteFile("plan", "the-plan"),
                TimeSpan.FromSeconds(30)),
            ["critic"] = new WorkerBinding.Process(
                new WorkerContract("critic", ["plan"], [new ProducedOutput("review")], []),
                ShellWorkerCommands.CopyFirstInputTo("review"),
                TimeSpan.FromSeconds(30)),
            ["publisher"] = new WorkerBinding.Process(
                new WorkerContract("publisher", ["review"], [new ProducedOutput("summary")], []),
                ShellWorkerCommands.CopyFirstInputTo("summary"),
                TimeSpan.FromSeconds(30)),
        };

        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        await using (var writer = new FlowEventLogWriter(logPath))
        {
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer);

            await MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf-ui-conversation-window-e2e"),
                roomDirectory,
                snapshot,
                bindings,
                Path.Combine(roomDirectory, "artifacts"),
                reader,
                writer,
                dispatcher,
                cancellationToken: cancellationToken);
        }

        return roomDirectory;
    }

    private static async Task<(MainWindow Window, string TranscriptDirectory)> LoadWithTranscriptOnStepAsync(
        string roomDirectory, StepId stepId, string transcriptContent, CancellationToken cancellationToken)
    {
        var projection = await RoomProjectionLoader.LoadAsync(roomDirectory, cancellationToken);
        var executionId = projection.Lineage.Executions.Single(e => e.StepId == stepId).ExecutionId;
        var outputDirectory = Path.Combine(roomDirectory, "artifacts", $"execution_{executionId}");
        await File.WriteAllTextAsync(
            Path.Combine(outputDirectory, TranscriptProjectionLoader.TranscriptFileName),
            transcriptContent,
            cancellationToken);

        var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));
        await window.LoadAsync(roomDirectory, cancellationToken);
        return (window, outputDirectory);
    }

    private static List<TextBlock> TextBlocksOf(Panel panel)
        => panel.Children.OfType<TextBlock>().ToList();

    [AvaloniaFact]
    public async Task Only_executions_whose_output_directory_has_a_transcript_get_a_conversation_row()
    {
        var roomDirectory = await CreatePumpedRoomDirectoryAsync(TestContext.Current.CancellationToken);
        try
        {
            var (window, _) = await LoadWithTranscriptOnStepAsync(
                roomDirectory, Architect,
                TurnLine(1, "initiator", "claude", "seed", "opening") + "\n",
                TestContext.Current.CancellationToken);

            var entriesPanel = window.FindViewControl<StackPanel>("ConversationExecutionsPanel")!;
            var row = Assert.Single(entriesPanel.Children.OfType<StackPanel>());
            var label = row.Children.OfType<TextBlock>().Single().Text!;
            Assert.StartsWith("architect —", label);
            Assert.Equal("View conversation", row.Children.OfType<Button>().Single().Content);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [AvaloniaFact]
    public async Task ShowConversation_renders_turns_in_file_order_with_prompt_collapsed()
    {
        var roomDirectory = await CreatePumpedRoomDirectoryAsync(TestContext.Current.CancellationToken);
        try
        {
            var (window, transcriptDirectory) = await LoadWithTranscriptOnStepAsync(
                roomDirectory, Architect,
                TurnLine(1, "initiator", "claude", "the-seed-prompt", "opening argument") + "\n" +
                TurnLine(2, "responder", "agy", "threaded-context", "counter argument") + "\n",
                TestContext.Current.CancellationToken);

            window.ShowConversation(transcriptDirectory, "architect — conversation");

            var conversationPanel = window.FindViewControl<StackPanel>("ConversationPanel")!;
            var turnBorders = conversationPanel.Children.OfType<Border>().ToList();
            Assert.Equal(2, turnBorders.Count);

            var firstTexts = TextBlocksOf((Panel)turnBorders[0].Child!).Select(block => block.Text).ToList();
            Assert.Contains("1 · initiator (claude)", firstTexts);
            Assert.Contains("opening argument", firstTexts);

            var secondTexts = TextBlocksOf((Panel)turnBorders[1].Child!).Select(block => block.Text).ToList();
            Assert.Contains("2 · responder (agy)", secondTexts);
            Assert.Contains("counter argument", secondTexts);

            var expander = ((Panel)turnBorders[0].Child!).Children.OfType<Expander>().Single();
            Assert.False(expander.IsExpanded);
            Assert.Equal("the-seed-prompt", ((TextBlock)expander.Content!).Text);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [AvaloniaFact]
    public async Task A_malformed_line_renders_as_an_explicit_marker_between_intact_turns()
    {
        var roomDirectory = await CreatePumpedRoomDirectoryAsync(TestContext.Current.CancellationToken);
        try
        {
            var (window, transcriptDirectory) = await LoadWithTranscriptOnStepAsync(
                roomDirectory, Architect,
                TurnLine(1, "initiator", "claude", "p", "first") + "\n" +
                "{\"Sequence\":2,\"Role\":\"respon" + "\n" +
                TurnLine(3, "initiator", "claude", "p", "third") + "\n",
                TestContext.Current.CancellationToken);

            window.ShowConversation(transcriptDirectory, "architect — conversation");

            var conversationPanel = window.FindViewControl<StackPanel>("ConversationPanel")!;
            // Header, turn 1, marker, turn 3 — the marker sits in place, later turns still render.
            Assert.Equal(2, conversationPanel.Children.OfType<Border>().Count());
            var marker = conversationPanel.Children.OfType<TextBlock>()
                .Single(block => block.Text!.StartsWith("line 2:"));
            Assert.Contains("not a schema-valid turn", marker.Text);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [AvaloniaFact]
    public async Task Refresh_rerenders_the_selected_conversation_and_picks_up_appended_turns()
    {
        var roomDirectory = await CreatePumpedRoomDirectoryAsync(TestContext.Current.CancellationToken);
        try
        {
            var (window, transcriptDirectory) = await LoadWithTranscriptOnStepAsync(
                roomDirectory, Architect,
                TurnLine(1, "initiator", "claude", "p", "first") + "\n",
                TestContext.Current.CancellationToken);

            window.ShowConversation(transcriptDirectory, "architect — conversation");

            var conversationPanel = window.FindViewControl<StackPanel>("ConversationPanel")!;
            Assert.Single(conversationPanel.Children.OfType<Border>());

            // A still-running exchange appends a turn; the next load-on-refresh must show it
            // without re-selecting anything.
            await File.AppendAllTextAsync(
                Path.Combine(transcriptDirectory, TranscriptProjectionLoader.TranscriptFileName),
                TurnLine(2, "responder", "agy", "p", "second") + "\n",
                TestContext.Current.CancellationToken);
            await window.LoadAsync(roomDirectory, TestContext.Current.CancellationToken);

            Assert.Equal(2, conversationPanel.Children.OfType<Border>().Count());
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    /// <summary>
    /// #468: see <see cref="MainWindow.ShowSelectedStepFirstOutputAsync"/>'s remarks for the two
    /// read models that disagreed and why the fix compares step ids rather than clearing on every
    /// <c>SelectedStep</c>-changed event.
    /// </summary>
    [AvaloniaFact]
    public async Task SelectingAStepWithNoConversation_ClearsAPriorStepsRenderedPanel()
    {
        var roomDirectory = await CreatePumpedRoomDirectoryAsync(TestContext.Current.CancellationToken);
        try
        {
            var (window, transcriptDirectory) = await LoadWithTranscriptOnStepAsync(
                roomDirectory, Architect,
                TurnLine(1, "initiator", "claude", "seed", "opening") + "\n",
                TestContext.Current.CancellationToken);

            window.ShowConversation(transcriptDirectory, "architect — conversation");
            var conversationPanel = window.FindViewControl<StackPanel>("ConversationPanel")!;
            Assert.Single(conversationPanel.Children.OfType<Border>()); // control arm: it did render

            window.ViewModel.SelectStepById(Critic.Value); // critic's execution has no transcript

            Assert.Empty(conversationPanel.Children); // the regression this test exists to catch
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    /// <summary>
    /// See <see cref="MainWindow.OpenAsync"/>'s room-switch remarks for why this is cleared there.
    /// Both rooms here reuse the same "architect" step id (the same fixture, per
    /// <see cref="CreatePumpedRoomDirectoryAsync"/>) — the collision that made the gap real.
    /// </summary>
    [AvaloniaFact]
    public async Task OpeningADifferentRoom_ClearsAPriorRoomsRenderedPanel_EvenOnAMatchingStepId()
    {
        var roomA = await CreatePumpedRoomDirectoryAsync(TestContext.Current.CancellationToken);
        var roomB = await CreatePumpedRoomDirectoryAsync(TestContext.Current.CancellationToken);
        try
        {
            var projectionA = await RoomProjectionLoader.LoadAsync(roomA, TestContext.Current.CancellationToken);
            var executionIdA = projectionA.Lineage.Executions.Single(e => e.StepId == Architect).ExecutionId;
            var transcriptDirectoryA = Path.Combine(roomA, "artifacts", $"execution_{executionIdA}");
            await File.WriteAllTextAsync(
                Path.Combine(transcriptDirectoryA, TranscriptProjectionLoader.TranscriptFileName),
                TurnLine(1, "initiator", "claude", "seed", "opening") + "\n",
                TestContext.Current.CancellationToken);

            var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));
            // OpenAsync, not LoadAsync directly — this reproduces the real path (_session
            // bookkeeping the room-switch guard reads) rather than the plain-load shortcut every
            // other test in this file uses for a single room.
            await window.OpenAsync(roomA, TestContext.Current.CancellationToken);

            window.ShowConversation(transcriptDirectoryA, "architect — conversation");
            var conversationPanel = window.FindViewControl<StackPanel>("ConversationPanel")!;
            Assert.Single(conversationPanel.Children.OfType<Border>()); // control arm: it did render

            await window.OpenAsync(roomB, TestContext.Current.CancellationToken);

            Assert.Empty(conversationPanel.Children); // the regression this test exists to catch
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomA);
            DirectoryCleanup.DeleteRecursively(roomB);
        }
    }

    [AvaloniaFact]
    public async Task An_execution_without_a_transcript_gets_no_conversation_row_at_all()
    {
        var roomDirectory = await CreatePumpedRoomDirectoryAsync(TestContext.Current.CancellationToken);
        try
        {
            var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));
            await window.LoadAsync(roomDirectory, TestContext.Current.CancellationToken);

            var entriesPanel = window.FindViewControl<StackPanel>("ConversationExecutionsPanel")!;
            Assert.Empty(entriesPanel.Children);
            Assert.Empty(window.FindViewControl<StackPanel>("ConversationPanel")!.Children);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }
}
