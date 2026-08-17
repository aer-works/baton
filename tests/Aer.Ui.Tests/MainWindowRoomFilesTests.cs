using Aer.Flow.Dispatch;
using Aer.Flow.Domain;
using Aer.Flow.Mutation;
using Aer.Flow.Store;
using Aer.Flow.Templates;
using Aer.Ui.Tests.TestSupport;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;

namespace Aer.Ui.Tests;

/// <summary>
/// Drives the real <see cref="MainWindow"/>'s Files section (#1340, 0021 §2) through its actual
/// rendered controls — the same headless-Avalonia, real-<c>MutationInterface</c>-pump approach
/// <see cref="MainWindowArtifactLineageAndDiffTests"/> established for the lineage surface it
/// re-groups. A projector test can prove <see cref="RoomFilesProjector"/> derives the right facts;
/// only this can prove no execution id — short or long — actually reaches a rendered string, which
/// is why 0021 §2's own gate is checked here rather than only in <c>RoomFilesProjectorTests</c>.
/// </summary>
public class MainWindowRoomFilesTests
{
    private static readonly StepId Architect = new("architect");
    private static readonly StepId Critic = new("critic");
    private static readonly StepId Publisher = new("publisher");

    private static string NewConfigFilePath() =>
        Path.Combine(Path.GetTempPath(), $"aer-ui-room-files-config-{Guid.NewGuid():N}", "recent-room-directories.json");

    private static async Task<(string RoomDirectory, IReadOnlyList<ExecutionId> ExecutionIds)> CreatePumpedRoomDirectoryAsync(
        CancellationToken cancellationToken)
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "three-step-linear-workflow.json");
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"ui-room-files-window-{Guid.NewGuid():N}");

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
                new WorkflowId("wf-ui-room-files-window-e2e"),
                roomDirectory,
                snapshot,
                bindings,
                Path.Combine(roomDirectory, "artifacts"),
                reader,
                writer,
                dispatcher,
                cancellationToken: cancellationToken);
        }

        var projection = await RoomProjectionLoader.LoadAsync(roomDirectory, cancellationToken);
        var executionIds = projection.Lineage.Executions.Select(e => e.ExecutionId).ToList();
        return (roomDirectory, executionIds);
    }

    /// <summary>
    /// RoomView (the "room view" the Files section lives in) is a real, rendered part of the shell
    /// only while the Shape panel is open beside the transcript (#1196 slice 3's <c>ApplyShellLayout</c>)
    /// — closed by default, and only offered at all (<c>Chat.IsShapeToggleVisible</c>) once
    /// <c>OpenAsync</c> (not the lower-level <c>LoadAsync</c>) has classified the room as an active
    /// workflow. Opens it the same way a person does — through the real <c>ChatShapeToggle</c>
    /// control — rather than poking <c>ChatViewModel.IsShapePanelOpen</c> directly, so this is still
    /// the real wiring, not a shortcut around it.
    /// </summary>
    private static async Task OpenRoomWithShapePanelAsync(MainWindow window, string roomDirectory, CancellationToken cancellationToken)
    {
        await window.OpenAsync(roomDirectory, cancellationToken);
        window.Show();

        var shapeToggle = window.FindViewControl<ToggleButton>("ChatShapeToggle")!;
        shapeToggle.IsChecked = true;
        window.UpdateLayout();
    }

    [AvaloniaFact]
    public async Task LoadAsync_renders_room_files_with_names_version_counts_author_and_time()
    {
        var (roomDirectory, _) = await CreatePumpedRoomDirectoryAsync(TestContext.Current.CancellationToken);
        try
        {
            var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));
            await window.LoadAsync(roomDirectory, TestContext.Current.CancellationToken);

            var filesList = window.FindViewControl<ItemsControl>("RoomFilesList")!;
            var rows = filesList.ItemsSource!.Cast<RoomFileViewModel>().ToList();

            Assert.Equal(["plan", "review", "summary"], rows.Select(r => r.Name).OrderBy(n => n, StringComparer.Ordinal));
            foreach (var row in rows)
            {
                Assert.StartsWith("1 version · latest ", row.SummaryText);
                Assert.Single(row.Versions);
                // "{worker} · {HH:mm}" — this fixture's Worker equals the producing step's own id.
                Assert.Matches(@"^\w+ · (\d{2}:\d{2}|time not recorded)$", row.Versions[0].Label);
            }
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [AvaloniaFact]
    public async Task Expanding_a_file_and_clicking_a_version_previews_its_real_content()
    {
        var (roomDirectory, _) = await CreatePumpedRoomDirectoryAsync(TestContext.Current.CancellationToken);
        try
        {
            var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));
            await OpenRoomWithShapePanelAsync(window, roomDirectory, TestContext.Current.CancellationToken);

            var filesList = window.FindViewControl<ItemsControl>("RoomFilesList")!;
            var expanders = filesList.GetVisualDescendants().OfType<Expander>().ToList();
            var planExpander = expanders.Single(e => (string)e.Header! == "plan");
            planExpander.IsExpanded = true;
            window.UpdateLayout();

            // Expander's own header is a ToggleButton, which derives from Button — excluded by
            // requiring a real Command (the header toggle has none; the version chip's is bound to
            // ArtifactFileViewModel.PreviewCommand).
            var planButton = planExpander.GetVisualDescendants().OfType<Button>().Single(b => b.Command != null);
            planButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

            var previewBox = window.FindViewControl<TextBox>("ArtifactPreviewBox")!;
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (string.IsNullOrEmpty(previewBox.Text) && DateTime.UtcNow < deadline)
            {
                await Task.Delay(10, TestContext.Current.CancellationToken);
            }

            Assert.Contains("the-plan", previewBox.Text);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    /// <summary>
    /// 0021 §2's own gate, checked where only a rendered surface can check it (a projector test
    /// proves what a fact means, never what a label actually renders): walks every string this
    /// slice's own controls put on screen — the Files section (real rendered <see cref="TextBlock"/>/
    /// <see cref="Button"/> content, expanders forced open since Avalonia's <see cref="Expander"/>
    /// only realizes its content once expanded) and the two step-drill-in chip labels #1340
    /// relabelled (read as the exact <c>Label</c> string a <c>Button</c>'s <c>Content</c> binds to,
    /// rather than fighting <c>TabControl</c>'s lazy tab-content realization to reach the same
    /// string a different way) — and asserts none of them contain a recorded <see cref="ExecutionId"/>,
    /// short id or long, anywhere. Deliberately scoped to what this slice renders: the Attempts tab,
    /// the conversation label, the decision lines, and the Details expander still legitimately show
    /// short ids — that is the out-of-scope Details-panel cleanup the issue names as the next slice.
    /// </summary>
    [AvaloniaFact]
    public async Task No_execution_id_short_or_long_appears_anywhere_this_slice_renders()
    {
        var (roomDirectory, executionIds) = await CreatePumpedRoomDirectoryAsync(TestContext.Current.CancellationToken);
        try
        {
            var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));
            await OpenRoomWithShapePanelAsync(window, roomDirectory, TestContext.Current.CancellationToken);

            var filesList = window.FindViewControl<ItemsControl>("RoomFilesList")!;
            foreach (var expander in filesList.GetVisualDescendants().OfType<Expander>())
            {
                expander.IsExpanded = true;
            }

            window.UpdateLayout();

            var renderedFilesSectionTexts = filesList.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text)
                .Concat(filesList.GetVisualDescendants().OfType<Button>().Select(b => b.Content as string))
                .Where(text => !string.IsNullOrEmpty(text))
                .Select(text => text!)
                .ToList();

            var chipLabels = window.ViewModel.RoomSteps
                .SelectMany(step => step.OutputFiles.Concat(step.PromptFiles))
                .Select(file => file.Label)
                .ToList();

            Assert.NotEmpty(renderedFilesSectionTexts);
            Assert.NotEmpty(chipLabels);

            var allTexts = renderedFilesSectionTexts.Concat(chipLabels).ToList();
            foreach (var executionId in executionIds)
            {
                var fullId = executionId.ToString()!;
                var shortId = PlainLanguage.ShortId(fullId);
                Assert.DoesNotContain(allTexts, text => text.Contains(fullId, StringComparison.Ordinal));
                Assert.DoesNotContain(allTexts, text => text.Contains(shortId, StringComparison.Ordinal));
            }
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }
}
