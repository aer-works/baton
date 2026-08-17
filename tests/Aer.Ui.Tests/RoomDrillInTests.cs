using Aer.Ui.Tests.TestSupport;
using System.Text.Json;
using Aer.Flow.Domain;
using Aer.Flow.Store;
using Aer.Flow.Templates;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;

namespace Aer.Ui.Tests;

/// <summary>
/// M19 Phase 3 (issue #188): the per-step drill-in — <see cref="StepItemViewModel"/> built by
/// <see cref="MainWindowViewModel.RebuildTaskSteps"/> on every load, plain-language primary text,
/// needs-you-first auto-selection, selection surviving refresh, and the outputs/conversation/
/// decisions slices. Room directories built from hand-written <see cref="FlowEvent"/>s, matching
/// <see cref="MainWindowProjectionTests"/>' convention.
/// </summary>
public class RoomDrillInTests
{
    private static readonly StepId Architect = new("architect");
    private static readonly StepId Critic = new("critic");

    private static WorkflowDefinitionSnapshot TwoStepSnapshot() => SnapshotBinder.Bind(new WorkflowDefinition(
        new WorkflowTemplateId("architect-critic"),
        WorkflowTemplateVersion: 1,
        Steps:
        [
            // #1191: corrected to the file names a real run would carry — NavigationShellTests'
            // own snapshot fixture records why. This suite hand-writes its ExecutionSucceeded
            // events, so nothing here would ever have objected.
            new WorkflowStepDefinition(Architect, "architect", ["goal.md"], ["plan.md"], DependsOn: [], RetryPolicy: new RetryPolicy(3)),
            new WorkflowStepDefinition(
                Critic, "critic", ["plan.md"], ["review.md"], DependsOn: [Architect], RetryPolicy: new RetryPolicy(1),
                PausePoint: new PausePoint(SupersedeTargets: [Architect])),
        ]));

    private static ExecutionRequest MakeRequest(ExecutionId executionId, StepId stepId)
        => new(
            executionId,
            new WorkflowId("wf-1"),
            stepId,
            "worker",
            Inputs: [],
            Outputs: [],
            Timeout: TimeSpan.FromMinutes(10),
            Environment: [],
            UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>());

    private static string NewConfigFilePath() =>
        Path.Combine(Path.GetTempPath(), $"aer-ui-drillin-config-{Guid.NewGuid():N}", "recent-room-directories.json");

    private static async Task<string> CreateRoomDirectoryAsync(
        WorkflowDefinitionSnapshot snapshot, IEnumerable<FlowEvent> events, CancellationToken cancellationToken)
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"ui-drillin-{Guid.NewGuid():N}");
        await SnapshotBinder.PersistAsync(snapshot, Path.Combine(roomDirectory, "snapshot.json"), cancellationToken);

        await using (var writer = new FlowEventLogWriter(Path.Combine(roomDirectory, "flow.jsonl")))
        {
            foreach (var flowEvent in events)
            {
                await writer.AppendAsync(flowEvent, cancellationToken);
            }
        }

        return roomDirectory;
    }

    /// <summary>Paused at critic after one architect failure + success; a-2 and c-1 each have a durable output file.</summary>
    private static async Task<string> CreatePausedRoomDirectoryAsync(CancellationToken cancellationToken)
    {
        var roomDirectory = await CreateRoomDirectoryAsync(
            TwoStepSnapshot(),
            [
                new FlowEvent.ExecutionRequestAccepted(MakeRequest(new ExecutionId("a-1"), Architect)),
                new FlowEvent.ExecutionFailed(
                    new ExecutionId("a-1"),
                    FailureClassification.Retryable,
                    "Contract not satisfied: 'plan' is missing"),
                new FlowEvent.ExecutionRequestAccepted(MakeRequest(new ExecutionId("a-2"), Architect)),
                new FlowEvent.ExecutionSucceeded(new ExecutionId("a-2")),
                new FlowEvent.ExecutionRequestAccepted(MakeRequest(new ExecutionId("c-1"), Critic)),
                new FlowEvent.ExecutionSucceeded(new ExecutionId("c-1")),
                new FlowEvent.WorkflowPaused(new ExecutionId("c-1"), Critic),
            ],
            cancellationToken);

        var architectOutputDirectory = Path.Combine(roomDirectory, "artifacts", "execution_a-2");
        Directory.CreateDirectory(architectOutputDirectory);
        await File.WriteAllTextAsync(Path.Combine(architectOutputDirectory, "plan.md"), "The plan.", cancellationToken);

        var outputDirectory = Path.Combine(roomDirectory, "artifacts", "execution_c-1");
        Directory.CreateDirectory(outputDirectory);
        await File.WriteAllTextAsync(Path.Combine(outputDirectory, "review.md"), "The critique.", cancellationToken);
        return roomDirectory;
    }

    [AvaloniaFact]
    public async Task LoadAsync_builds_plain_language_step_items_and_auto_selects_the_paused_step()
    {
        var roomDirectory = await CreatePausedRoomDirectoryAsync(TestContext.Current.CancellationToken);
        try
        {
            var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));
            await window.LoadAsync(roomDirectory, TestContext.Current.CancellationToken);

            Assert.Equal("Waiting for your review", window.ViewModel.RoomHeadlineText);

            Assert.Collection(
                window.ViewModel.RoomSteps,
                architect =>
                {
                    Assert.Equal("architect", architect.StepId);
                    Assert.Equal("Done", architect.PlainStatusText);
                    // #597's polarity pair, on one surface: the failed attempt carries the reason
                    // Flow computed, the succeeded one carries none. A renderer that appended the
                    // suffix unconditionally, or dropped it entirely, fails one row or the other.
                    Assert.Equal(
                        [
                            "Attempt 1 of 2: Failed — can be retried (a-1) — Contract not satisfied: 'plan' is missing",
                            "Attempt 2 of 2: Done (a-2)",
                        ],
                        architect.AttemptLines);
                    Assert.False(architect.IsPaused);
                },
                critic =>
                {
                    Assert.Equal("critic", critic.StepId);
                    Assert.Equal("Waiting for your review", critic.PlainStatusText);
                    Assert.True(critic.IsPaused);
                });

            // Needs-you-first: the paused step's drill-in opens itself, and its inline decision
            // card is the same live VM the M15 decision surface rebuilt — one authority, not two.
            var selected = Assert.IsType<StepItemViewModel>(window.ViewModel.SelectedStep);
            Assert.Equal("critic", selected.StepId);
            Assert.True(selected.IsSelected);
            Assert.Same(Assert.Single(window.ViewModel.PausedSteps), selected.PausedStep);

            // #1191: The evidence panel opens on the evidence ("Outputs" tab).
            var tabControl = window.StepDetailTabControl;
            Assert.NotNull(tabControl);
            var selectedTab = Assert.IsType<TabItem>(tabControl.SelectedItem);
            Assert.Equal("Outputs", selectedTab.Header);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [AvaloniaFact]
    public async Task Output_file_preview_command_renders_into_the_preview_box()
    {
        var roomDirectory = await CreatePausedRoomDirectoryAsync(TestContext.Current.CancellationToken);
        try
        {
            var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));
            await window.LoadAsync(roomDirectory, TestContext.Current.CancellationToken);

            var critic = window.ViewModel.RoomSteps.Single(step => step.StepId == "critic");
            var file = Assert.Single(critic.OutputFiles);
            Assert.Equal("review.md (c-1)", file.Label);

            await file.PreviewCommand.ExecuteAsync(null);

            Assert.Equal("The critique.", window.FindViewControl<TextBox>("ArtifactPreviewBox")!.Text);
            Assert.True(file.IsSelected);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    /// <summary>
    /// Regression test for issue #211: the preview box used to be pure imperative control state
    /// with nothing hooking <see cref="MainWindowViewModel.SelectedStep"/> changing, so switching
    /// steps left the *previous* step's last-previewed output showing. Now it clears and
    /// auto-loads the newly-selected step's own first output, and the chip that produced the
    /// shown content carries <see cref="ArtifactFileViewModel.IsSelected"/>.
    /// </summary>
    [AvaloniaFact]
    public async Task Switching_the_selected_step_clears_and_reloads_the_output_preview()
    {
        var roomDirectory = await CreatePausedRoomDirectoryAsync(TestContext.Current.CancellationToken);
        try
        {
            var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));
            await window.LoadAsync(roomDirectory, TestContext.Current.CancellationToken);

            // Needs-you-first auto-selects critic; its own single output auto-loads too.
            var previewBox = window.FindViewControl<TextBox>("ArtifactPreviewBox")!;
            await PollUntilAsync(() => previewBox.Text == "The critique.", TestContext.Current.CancellationToken);

            var critic = window.ViewModel.RoomSteps.Single(step => step.StepId == "critic");
            Assert.True(Assert.Single(critic.OutputFiles).IsSelected);

            // Switching to architect must not keep showing critic's content — it clears, then
            // auto-loads architect's own first output.
            window.ViewModel.SelectStepById("architect");
            await PollUntilAsync(() => previewBox.Text == "The plan.", TestContext.Current.CancellationToken);

            var architect = window.ViewModel.RoomSteps.Single(step => step.StepId == "architect");
            Assert.True(Assert.Single(architect.OutputFiles).IsSelected);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    /// <summary>Polls instead of a fixed delay — the preview load is fired-and-forgotten off a PropertyChanged handler, the same genuine-race shape <see cref="MainWindowArtifactLineageAndDiffTests"/> already documented for the click-handler path.</summary>
    private static async Task PollUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10, cancellationToken);
        }

        Assert.True(condition());
    }

    /// <summary>
    /// Issue #292: an ordinary step's durably-captured prompt surfaces via its own PromptFiles slice,
    /// not mixed into OutputFiles' always-visible chips -- reusing the same output-file preview
    /// mechanism (ArtifactFileViewModel/PreviewCommand) rather than a bespoke rendering path.
    /// </summary>
    [AvaloniaFact]
    public async Task A_captured_prompt_file_surfaces_as_PromptFiles_and_is_excluded_from_OutputFiles()
    {
        var roomDirectory = await CreatePausedRoomDirectoryAsync(TestContext.Current.CancellationToken);
        var outputDirectory = Path.Combine(roomDirectory, "artifacts", "execution_c-1");
        await File.WriteAllTextAsync(
            Path.Combine(outputDirectory, "prompt.txt"), "Review the plan.", TestContext.Current.CancellationToken);
        try
        {
            var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));
            await window.LoadAsync(roomDirectory, TestContext.Current.CancellationToken);

            var critic = window.ViewModel.RoomSteps.Single(step => step.StepId == "critic");

            // Still just the one real output -- prompt.txt never leaks into the output-files chips.
            var outputFile = Assert.Single(critic.OutputFiles);
            Assert.Equal("review.md (c-1)", outputFile.Label);

            var promptFile = Assert.Single(critic.PromptFiles);
            Assert.Equal("Prompt (c-1)", promptFile.Label);
            Assert.True(critic.HasPromptFiles);

            await promptFile.PreviewCommand.ExecuteAsync(null);

            Assert.Equal("Review the plan.", window.FindViewControl<TextBox>("ArtifactPreviewBox")!.Text);
            Assert.True(promptFile.IsSelected);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    /// <summary>
    /// #868: selecting critic fires an auto-preview of its first output (review.md) off the
    /// unawaited <see cref="MainWindow.ShowSelectedStepFirstOutputAsync"/> fire-and-forget
    /// subscription; an explicit preview of a different file (prompt.txt) issued right after used to
    /// race it — whichever <c>File.ReadAllTextAsync</c> finished last won, regardless of which the
    /// user actually asked for last. The original CI failure (#868) caught this only by luck, on two
    /// small files whose read order was not controlled. This forces the adversarial ordering rather
    /// than betting on it: the auto-previewed file's read is parked through
    /// <see cref="MainWindow.ReadArtifactTextAsync"/> until the explicit preview has already landed,
    /// then released, so the superseded read provably finishes LAST. (The first version forced the
    /// window with a 150MB fixture instead, which reproduces nothing reliably in either direction —
    /// see that seam's own remarks.)
    /// </summary>
    [AvaloniaFact]
    public async Task Explicit_preview_of_a_different_file_survives_a_slower_in_flight_auto_preview()
    {
        var roomDirectory = await CreatePausedRoomDirectoryAsync(TestContext.Current.CancellationToken);
        var outputDirectory = Path.Combine(roomDirectory, "artifacts", "execution_c-1");
        await File.WriteAllTextAsync(
            Path.Combine(outputDirectory, "prompt.txt"), "Review the plan.", TestContext.Current.CancellationToken);
        try
        {
            var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));
            var releaseAutoPreview = new TaskCompletionSource();
            window.ReadArtifactTextAsync = async (filePath, token) =>
            {
                var content = await File.ReadAllTextAsync(filePath, token);
                if (filePath.EndsWith("review.md", StringComparison.Ordinal))
                {
                    await releaseAutoPreview.Task;
                }

                return content;
            };

            // Needs-you-first auto-selects critic, firing the unawaited auto-preview of review.md
            // off the SelectedStep handler -- which the seam above parks mid-read.
            await window.LoadAsync(roomDirectory, TestContext.Current.CancellationToken);

            var critic = window.ViewModel.RoomSteps.Single(step => step.StepId == "critic");
            var promptFile = Assert.Single(critic.PromptFiles);
            var autoPreviewed = critic.OutputFiles.First();
            var previewBox = window.FindViewControl<TextBox>("ArtifactPreviewBox")!;

            // The explicit request, issued while the auto-preview's read is provably still parked.
            await promptFile.PreviewCommand.ExecuteAsync(null);
            Assert.Equal("Review the plan.", previewBox.Text);

            // Now let the superseded read finish, after the newer one has already written the box.
            releaseAutoPreview.SetResult();
            await autoPreviewed.PreviewCommand.ExecutionTask!;

            Assert.Equal("Review the plan.", previewBox.Text);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    /// <summary>
    /// #868's fix polarity check, the other direction: a genuinely newer preview must still win even
    /// though it was issued (and may complete) after an older one -- a fix that simply dropped every
    /// second overlapping request would pass the test above but break ordinary fast clicking. The
    /// older read is parked through the same seam and released only after the newer one has
    /// written, so "the older request finishes last" is guaranteed rather than raced for — and the
    /// box must still show the newer file both before and after that older read completes.
    /// </summary>
    [AvaloniaFact]
    public async Task A_newer_preview_request_still_wins_even_when_issued_immediately_after_an_older_one()
    {
        var roomDirectory = await CreatePausedRoomDirectoryAsync(TestContext.Current.CancellationToken);
        var outputDirectory = Path.Combine(roomDirectory, "artifacts", "execution_c-1");
        var olderFilePath = Path.Combine(outputDirectory, "older.txt");
        var newerFilePath = Path.Combine(outputDirectory, "newer.txt");
        await File.WriteAllTextAsync(olderFilePath, "Older content.", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(newerFilePath, "Newer content.", TestContext.Current.CancellationToken);
        try
        {
            var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));
            await window.LoadAsync(roomDirectory, TestContext.Current.CancellationToken);

            var releaseOlder = new TaskCompletionSource();
            window.ReadArtifactTextAsync = async (filePath, token) =>
            {
                var content = await File.ReadAllTextAsync(filePath, token);
                if (filePath.EndsWith("older.txt", StringComparison.Ordinal))
                {
                    await releaseOlder.Task;
                }

                return content;
            };

            var olderPreviewTask = window.ShowArtifactPreviewAsync(olderFilePath, TestContext.Current.CancellationToken);
            var newerPreviewTask = window.ShowArtifactPreviewAsync(newerFilePath, TestContext.Current.CancellationToken);

            // The newer request completes first and writes; the older one is still parked.
            await newerPreviewTask;
            var previewBox = window.FindViewControl<TextBox>("ArtifactPreviewBox")!;
            Assert.Equal("Newer content.", previewBox.Text);

            // Release the older read so it finishes LAST -- the ordering that broke this before.
            releaseOlder.SetResult();
            await olderPreviewTask;

            Assert.Equal("Newer content.", previewBox.Text);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    /// <summary>
    /// #871, whose cost the matching catch in <see cref="MainWindow.ShowArtifactPreviewAsync"/>
    /// records. Both halves are asserted here: a cancelled read neither throws nor writes the box.
    /// </summary>
    [AvaloniaFact]
    public async Task A_cancelled_preview_neither_throws_nor_writes_the_box()
    {
        var roomDirectory = await CreatePausedRoomDirectoryAsync(TestContext.Current.CancellationToken);
        var filePath = Path.Combine(roomDirectory, "artifacts", "execution_c-1", "review.md");
        try
        {
            var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));
            await window.LoadAsync(roomDirectory, TestContext.Current.CancellationToken);

            var previewBox = window.FindViewControl<TextBox>("ArtifactPreviewBox")!;
            var before = previewBox.Text;

            using var alreadyCancelled = new CancellationTokenSource();
            await alreadyCancelled.CancelAsync();

            // The assertion is that this await completes at all: unguarded, it throws
            // OperationCanceledException, which from a fire-and-forget caller is silent.
            await window.ShowArtifactPreviewAsync(filePath, alreadyCancelled.Token);

            Assert.Equal(before, previewBox.Text);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [AvaloniaFact]
    public async Task A_step_with_no_captured_prompt_reports_no_prompt_files()
    {
        var roomDirectory = await CreatePausedRoomDirectoryAsync(TestContext.Current.CancellationToken);
        try
        {
            var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));
            await window.LoadAsync(roomDirectory, TestContext.Current.CancellationToken);

            var critic = window.ViewModel.RoomSteps.Single(step => step.StepId == "critic");

            Assert.False(critic.HasPromptFiles);
            Assert.Empty(critic.PromptFiles);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [AvaloniaFact]
    public async Task Selection_follows_step_id_across_refresh_and_the_dag_click_entry_point()
    {
        var roomDirectory = await CreatePausedRoomDirectoryAsync(TestContext.Current.CancellationToken);
        try
        {
            var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));
            await window.OpenAsync(roomDirectory, TestContext.Current.CancellationToken);

            window.ViewModel.SelectStepById("architect");
            Assert.Equal("architect", window.ViewModel.SelectedStep!.StepId);

            await window.RefreshAsync(TestContext.Current.CancellationToken);

            // Items are rebuilt wholesale; the selection re-anchors by step id, not instance.
            Assert.Equal("architect", window.ViewModel.SelectedStep!.StepId);
            Assert.True(window.ViewModel.SelectedStep.IsSelected);

            window.ViewModel.SelectStepById("no-such-step");
            Assert.Equal("architect", window.ViewModel.SelectedStep!.StepId);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [AvaloniaFact]
    public async Task Decision_lines_render_in_plain_language_on_the_decided_step()
    {
        var roomDirectory = await CreateRoomDirectoryAsync(
            TwoStepSnapshot(),
            [
                new FlowEvent.ExecutionRequestAccepted(MakeRequest(new ExecutionId("a-1"), Architect)),
                new FlowEvent.ExecutionSucceeded(new ExecutionId("a-1")),
                new FlowEvent.ExecutionRequestAccepted(MakeRequest(new ExecutionId("c-1"), Critic)),
                new FlowEvent.ExecutionSucceeded(new ExecutionId("c-1")),
                new FlowEvent.WorkflowPaused(new ExecutionId("c-1"), Critic),
                new FlowEvent.ExternalDecisionRecorded(
                    new DecisionId("decision-1"), new ExecutionId("c-1"), DecisionType.Supersede, Architect, null),
            ],
            TestContext.Current.CancellationToken);
        try
        {
            var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));
            await window.LoadAsync(roomDirectory, TestContext.Current.CancellationToken);

            var critic = window.ViewModel.RoomSteps.Single(step => step.StepId == "critic");
            Assert.Equal(
                ["Sent back to architect (decision on c-1) — not carried out yet"],
                critic.DecisionLines);

            // The send-back's target step carries the same decision — it is about that step too.
            var architect = window.ViewModel.RoomSteps.Single(step => step.StepId == "architect");
            Assert.Equal(
                ["Sent back to architect (decision on c-1) — not carried out yet"],
                architect.DecisionLines);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [AvaloniaFact]
    public async Task A_recorded_transcript_surfaces_as_the_steps_conversation_and_renders_on_show()
    {
        var roomDirectory = await CreatePausedRoomDirectoryAsync(TestContext.Current.CancellationToken);
        var outputDirectory = Path.Combine(roomDirectory, "artifacts", "execution_c-1");
        var turn = JsonSerializer.Serialize(
            new { Sequence = 1, Role = "initiator", Vendor = "claude", Prompt = "p", Text = "hello" });
        await File.WriteAllTextAsync(
            Path.Combine(outputDirectory, "transcript.jsonl"), turn + "\n", TestContext.Current.CancellationToken);
        try
        {
            var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));
            await window.LoadAsync(roomDirectory, TestContext.Current.CancellationToken);

            var critic = window.ViewModel.RoomSteps.Single(step => step.StepId == "critic");
            var conversation = Assert.Single(critic.Conversations);
            Assert.Equal("critic — c-1 (worker)", conversation.Label);

            conversation.ShowCommand.Execute(null);

            var conversationPanel = window.FindViewControl<StackPanel>("ConversationPanel")!;
            Assert.True(conversationPanel.Children.Count >= 2);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [AvaloniaFact]
    public async Task Failed_step_renders_failed_banner_with_reason_and_stderr_excerpt()
    {
        var roomDirectory = await CreateRoomDirectoryAsync(
            TwoStepSnapshot(),
            [
                new FlowEvent.ExecutionRequestAccepted(MakeRequest(new ExecutionId("a-1"), Architect)),
                new FlowEvent.ExecutionFailed(
                    new ExecutionId("a-1"),
                    FailureClassification.Permanent,
                    "Worker exited with non-zero code 1. stderr: migrate: connect ECONNREFUSED 127.0.0.1:5432"),
            ],
            TestContext.Current.CancellationToken);
        try
        {
            var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));
            await window.LoadAsync(roomDirectory, TestContext.Current.CancellationToken);

            var architect = window.ViewModel.RoomSteps.Single(step => step.StepId == "architect");
            Assert.True(architect.HasFailedBanner);
            var banner = architect.FailedBanner;
            Assert.NotNull(banner);
            Assert.Equal("Worker exited with non-zero code 1.", banner.ReasonSentence);
            Assert.Equal("migrate: connect ECONNREFUSED 127.0.0.1:5432", banner.StderrExcerpt);
            Assert.True(banner.HasStderrExcerpt);
            Assert.Contains("Failed · architect · Worker exited with non-zero code 1.", banner.Headline);
            Assert.Equal("Ask architect to fix it", banner.AskWorkerLabel);
            Assert.Equal("Try again (re-run room)", banner.TryAgainLabel);

            // Architect failed permanently and critic depends on it: the workflow is Terminal, so
            // the re-run clone flow applies and Try again is offered. The polarity — a live
            // sibling hides it — is Try_again_is_hidden_while_a_sibling_step_is_still_live.
            Assert.True(banner.CanTryAgain);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [AvaloniaFact]
    public async Task Exhausted_step_renders_no_failed_banner()
    {
        // #1116 review must-fix — why the banner is suppressed for ExhaustedUntil is the gate
        // comment in StepItemProjector.Build. The polarity arm is
        // Failed_step_renders_failed_banner_with_reason_and_stderr_excerpt above (Permanent).
        var roomDirectory = await CreateRoomDirectoryAsync(
            TwoStepSnapshot(),
            [
                new FlowEvent.ExecutionRequestAccepted(MakeRequest(new ExecutionId("a-1"), Architect)),
                new FlowEvent.ExecutionFailed(
                    new ExecutionId("a-1"),
                    FailureClassification.ExhaustedUntil,
                    "quota exhausted",
                    RetryNotBefore: null),
            ],
            TestContext.Current.CancellationToken);
        try
        {
            var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));
            await window.LoadAsync(roomDirectory, TestContext.Current.CancellationToken);

            var architect = window.ViewModel.RoomSteps.Single(step => step.StepId == "architect");
            Assert.False(architect.HasFailedBanner);
            Assert.Null(architect.FailedBanner);
            Assert.Equal("Out of plan — reset unknown", architect.PlainStatusText);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [AvaloniaFact]
    public async Task Tool_denied_failure_renders_a_not_retryable_suffix()
    {
        // #914: a ToolDenied attempt must carry an explanatory "not retryable" suffix, not fall through
        // the switch to the empty default — a denied-tool failure reading as a bare "Failed" is exactly
        // the #597 defect the suffix exists to prevent. Reds against the pre-fix switch, which had no
        // ToolDenied arm.
        var roomDirectory = await CreateRoomDirectoryAsync(
            TwoStepSnapshot(),
            [
                new FlowEvent.ExecutionRequestAccepted(MakeRequest(new ExecutionId("a-1"), Architect)),
                new FlowEvent.ExecutionFailed(
                    new ExecutionId("a-1"),
                    FailureClassification.ToolDenied,
                    "Execution failed: a required tool was auto-denied."),
            ],
            TestContext.Current.CancellationToken);
        try
        {
            var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));
            await window.LoadAsync(roomDirectory, TestContext.Current.CancellationToken);

            var architect = window.ViewModel.RoomSteps.Single(step => step.StepId == "architect");
            Assert.Contains(
                architect.AttemptLines,
                line => line.Contains("Failed — not retryable (a required tool was denied)"));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [AvaloniaFact]
    public async Task Show_full_output_opens_the_attempt_the_banner_quotes_not_the_first()
    {
        // Two attempts, both with transcripts: the banner's reason comes from the newest reasoned
        // attempt (a-2), so its "Show full output" must open a-2's conversation. Index 0 of the
        // chronological collections is a-1 — a different run than the headline describes.
        var roomDirectory = await CreateRoomDirectoryAsync(
            TwoStepSnapshot(),
            [
                new FlowEvent.ExecutionRequestAccepted(MakeRequest(new ExecutionId("a-1"), Architect)),
                new FlowEvent.ExecutionFailed(
                    new ExecutionId("a-1"), FailureClassification.Retryable, "First failure."),
                new FlowEvent.ExecutionRequestAccepted(MakeRequest(new ExecutionId("a-2"), Architect)),
                new FlowEvent.ExecutionFailed(
                    new ExecutionId("a-2"), FailureClassification.Permanent, "Second failure."),
            ],
            TestContext.Current.CancellationToken);
        var turn = JsonSerializer.Serialize(
            new { Sequence = 1, Role = "initiator", Vendor = "claude", Prompt = "p", Text = "hello" });
        foreach (var executionDirectoryName in new[] { "execution_a-1", "execution_a-2" })
        {
            var executionDirectory = Path.Combine(roomDirectory, "artifacts", executionDirectoryName);
            Directory.CreateDirectory(executionDirectory);
            await File.WriteAllTextAsync(
                Path.Combine(executionDirectory, "transcript.jsonl"), turn + "\n", TestContext.Current.CancellationToken);
        }

        try
        {
            var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));
            await window.LoadAsync(roomDirectory, TestContext.Current.CancellationToken);

            var architect = window.ViewModel.RoomSteps.Single(step => step.StepId == "architect");
            Assert.NotNull(architect.FailedBanner);
            Assert.Equal("Second failure.", architect.FailedBanner.ReasonSentence);

            architect.FailedBanner.ShowFullOutputCommand.Execute(null);

            var conversationPanel = window.FindViewControl<StackPanel>("ConversationPanel")!;
            var shownLabel = Assert.IsType<TextBlock>(conversationPanel.Children[0]).Text;
            Assert.Equal("architect — a-2 (worker)", shownLabel);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [AvaloniaFact]
    public async Task Try_again_is_hidden_while_a_sibling_step_is_still_live()
    {
        // Critic is paused awaiting review, architect failed: the workflow is Paused, not
        // Terminal, so Run would resume this directory in place — and for the failed step with no
        // pending obligation that is a silent no-op. The banner must not offer a click that does
        // nothing; Try again appears once the task finishes.
        var independentSnapshot = SnapshotBinder.Bind(new WorkflowDefinition(
            new WorkflowTemplateId("independent-pair"),
            WorkflowTemplateVersion: 1,
            Steps:
            [
                new WorkflowStepDefinition(Architect, "architect", ["goal"], ["plan"], DependsOn: [], RetryPolicy: new RetryPolicy(1)),
                // Self-targeting supersede: the steps are deliberately independent, and the
                // validator only admits a transitive ancestor or the step itself as a target.
                new WorkflowStepDefinition(
                    Critic, "critic", ["brief"], ["review"], DependsOn: [], RetryPolicy: new RetryPolicy(1),
                    PausePoint: new PausePoint(SupersedeTargets: [Critic])),
            ]));
        var roomDirectory = await CreateRoomDirectoryAsync(
            independentSnapshot,
            [
                new FlowEvent.ExecutionRequestAccepted(MakeRequest(new ExecutionId("c-1"), Critic)),
                new FlowEvent.ExecutionSucceeded(new ExecutionId("c-1")),
                new FlowEvent.WorkflowPaused(new ExecutionId("c-1"), Critic),
                new FlowEvent.ExecutionRequestAccepted(MakeRequest(new ExecutionId("a-1"), Architect)),
                new FlowEvent.ExecutionFailed(
                    new ExecutionId("a-1"), FailureClassification.Permanent, "Worker exited with non-zero code 1."),
            ],
            TestContext.Current.CancellationToken);
        try
        {
            var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));
            await window.LoadAsync(roomDirectory, TestContext.Current.CancellationToken);

            var architect = window.ViewModel.RoomSteps.Single(step => step.StepId == "architect");
            Assert.NotNull(architect.FailedBanner);
            Assert.False(architect.FailedBanner.CanTryAgain);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [AvaloniaFact]
    public async Task Succeeded_step_shows_no_failed_banner_polarity()
    {
        var roomDirectory = await CreateRoomDirectoryAsync(
            TwoStepSnapshot(),
            [
                new FlowEvent.ExecutionRequestAccepted(MakeRequest(new ExecutionId("a-1"), Architect)),
                new FlowEvent.ExecutionSucceeded(new ExecutionId("a-1")),
            ],
            TestContext.Current.CancellationToken);
        try
        {
            var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));
            await window.LoadAsync(roomDirectory, TestContext.Current.CancellationToken);

            var architect = window.ViewModel.RoomSteps.Single(step => step.StepId == "architect");
            Assert.False(architect.HasFailedBanner);
            Assert.Null(architect.FailedBanner);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [AvaloniaFact]
    public async Task Ask_worker_to_fix_prefills_chat_input_and_navigates_to_chat()
    {
        var roomDirectory = await CreateRoomDirectoryAsync(
            TwoStepSnapshot(),
            [
                new FlowEvent.ExecutionRequestAccepted(MakeRequest(new ExecutionId("a-1"), Architect)),
                new FlowEvent.ExecutionFailed(
                    new ExecutionId("a-1"),
                    FailureClassification.Permanent,
                    "Worker exited with non-zero code 1. stderr: connect ECONNREFUSED"),
            ],
            TestContext.Current.CancellationToken);
        try
        {
            var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));
            await window.LoadAsync(roomDirectory, TestContext.Current.CancellationToken);

            var architect = window.ViewModel.RoomSteps.Single(step => step.StepId == "architect");
            Assert.NotNull(architect.FailedBanner);

            architect.FailedBanner.AskWorkerToFixCommand.Execute(null);

            Assert.Equal(ShellSection.Chat, window.ViewModel.CurrentSection);
            Assert.Contains("Step 'architect' failed: Worker exited with non-zero code 1.", window.ViewModel.Chat.InputText);
            Assert.False(window.ViewModel.Chat.IsSending);

            // Found live, not by the original assertions: with no session open the draft sat in a
            // property behind "No room open." — AskWorkerToFix's own doc comment carries the story;
            // these two pins are what turn its no-session promise into a red test.
            Assert.False(window.ViewModel.Chat.IsSessionOpen);
            Assert.Equal(roomDirectory, window.ViewModel.Chat.NewChatWorkingDirectory);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [AvaloniaFact]
    public async Task Ask_worker_to_fix_appends_to_a_half_typed_message_instead_of_replacing_it()
    {
        var roomDirectory = await CreateRoomDirectoryAsync(
            TwoStepSnapshot(),
            [
                new FlowEvent.ExecutionRequestAccepted(MakeRequest(new ExecutionId("a-1"), Architect)),
                new FlowEvent.ExecutionFailed(
                    new ExecutionId("a-1"),
                    FailureClassification.Permanent,
                    "Worker exited with non-zero code 1."),
            ],
            TestContext.Current.CancellationToken);
        try
        {
            var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));
            await window.LoadAsync(roomDirectory, TestContext.Current.CancellationToken);

            // The input box can already hold the user's own words (most plausibly in an open
            // session, but the box is one control either way) — the affordance must add its draft,
            // never destroy what was typed.
            window.ViewModel.Chat.InputText = "half-typed note";

            var architect = window.ViewModel.RoomSteps.Single(step => step.StepId == "architect");
            architect.FailedBanner!.AskWorkerToFixCommand.Execute(null);

            Assert.StartsWith("half-typed note", window.ViewModel.Chat.InputText);
            Assert.Contains("Step 'architect' failed:", window.ViewModel.Chat.InputText);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    // #1339 (decision 0058's scope ruling 4): DepthTierMappingTests pins the same polarity set at the
    // mapping itself -- see its own doc comment for why all three cases matter. This exercises it at
    // the desktop's real load path instead: MainWindow.LoadAsync reading a bindings.json resolved
    // through GetWorkerDepthTiers -> Aer.Adapters.DepthTierMapping -> StepItemProjector.Build, landing
    // on StepItemViewModel.DepthTier -- the always-null slot #1318 shipped ahead of this producer.
    [AvaloniaFact]
    public async Task A_claude_worker_on_a_recorded_alias_renders_its_depth_tier()
    {
        var roomDirectory = await CreateRoomDirectoryAsync(TwoStepSnapshot(), [], TestContext.Current.CancellationToken);
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(roomDirectory, "bindings.json"),
                """
                { "architect": { "Adapter": "claude", "Model": "opus" } }
                """,
                TestContext.Current.CancellationToken);

            var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));
            await window.LoadAsync(roomDirectory, TestContext.Current.CancellationToken);

            var architect = window.ViewModel.RoomSteps.Single(step => step.StepId == "architect");
            Assert.Equal(AerDepthTier.Deep, architect.DepthTier);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [AvaloniaFact]
    public async Task An_agy_vendored_worker_renders_no_depth_mark()
    {
        var roomDirectory = await CreateRoomDirectoryAsync(TwoStepSnapshot(), [], TestContext.Current.CancellationToken);
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(roomDirectory, "bindings.json"),
                """
                { "architect": { "Adapter": "agy", "Model": "gemini-3.6-flash-thinking" } }
                """,
                TestContext.Current.CancellationToken);

            var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));
            await window.LoadAsync(roomDirectory, TestContext.Current.CancellationToken);

            var architect = window.ViewModel.RoomSteps.Single(step => step.StepId == "architect");
            Assert.Null(architect.DepthTier);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [AvaloniaFact]
    public async Task An_unrecognised_model_string_renders_no_depth_mark()
    {
        var roomDirectory = await CreateRoomDirectoryAsync(TwoStepSnapshot(), [], TestContext.Current.CancellationToken);
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(roomDirectory, "bindings.json"),
                """
                { "architect": { "Adapter": "claude", "Model": "claude-opus-4-8" } }
                """,
                TestContext.Current.CancellationToken);

            var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));
            await window.LoadAsync(roomDirectory, TestContext.Current.CancellationToken);

            var architect = window.ViewModel.RoomSteps.Single(step => step.StepId == "architect");
            Assert.Null(architect.DepthTier);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }
}

