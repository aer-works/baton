using Aer.Adapters;
using Aer.Flow.Domain;
using Aer.RoomSession;
using Aer.Ui.Core;
using Aer.Ui.Tests.TestSupport;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Aer.Ui.Tests;

/// <summary>
/// #1279 (supersedes closed-as-grouped #268): keyboard-first triage over the switcher. Measured
/// headless before fixing anything — <c>SwitcherList</c> had no way to receive keyboard focus at
/// all (<c>Focusable</c> defaulted <see langword="false"/>, confirmed with and without a forced
/// extra layout pass to rule out a template-timing artifact), so Tab could never reach it and
/// arrow-key traversal, while already implemented by <see cref="ListBox"/> itself, was unreachable
/// without a mouse click first. This only covers the entry-point half of #1279 — Tab-reachability
/// into an expanded row's paused-step buttons, and a "return to the switcher" loop after acting
/// inside an opened room, remain open there.
/// </summary>
public class SwitcherKeyboardNavigationTests
{
    private static string NewConfigFilePath() =>
        Path.Combine(Path.GetTempPath(), $"aer-ui-switcher-kbd-config-{Guid.NewGuid():N}", "recent-room-directories.json");

    private static RoomFleetItem NewItem(string path) =>
        new(path, FriendlyName: path, TypeLabel: "solo-run-template", StatusText: "Idle", PausedStepCount: 0,
            IsArchived: false, Created: DateTimeOffset.UnixEpoch, Updated: DateTimeOffset.UnixEpoch);

    /// <summary>The entry point: opening the window puts keyboard focus on the switcher, with no click.</summary>
    [AvaloniaFact]
    public void Opening_the_window_focuses_the_switcher_list()
    {
        var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var list = window.FindControl<ListBox>("SwitcherList");
        Assert.NotNull(list);
        Assert.True(list!.IsFocused);
    }

    /// <summary>
    /// Once focused, Down moves selection to the next room, and that selection change is what opens
    /// it — <c>Rooms.CurrentItem</c> is the same property <c>MainWindow.axaml.cs</c>'s own
    /// selection-changed handler treats as "open this room" (no separate open action exists).
    /// </summary>
    [AvaloniaFact]
    public void Once_focused_the_down_arrow_moves_selection_to_the_next_room()
    {
        var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));
        window.Show();
        var a = window.ViewModel.Rooms.AddTestItem(NewItem("/rooms/a"));
        var b = window.ViewModel.Rooms.AddTestItem(NewItem("/rooms/b"));
        Dispatcher.UIThread.RunJobs();

        var list = window.FindControl<ListBox>("SwitcherList")!;
        list.SelectedItem = a;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(a.RoomDirectoryPath, window.ViewModel.Rooms.CurrentItem?.RoomDirectoryPath);

        // window.KeyPress routes through the real input pipeline (whatever currently holds focus),
        // unlike list.RaiseEvent(...) which delivers directly to `list` regardless of focus state —
        // the latter would still pass this arm even if the switcher were unfocusable, which is
        // exactly the defect Opening_the_window_focuses_the_switcher_list exists to catch on its own.
        window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(b.RoomDirectoryPath, (list.SelectedItem as RoomFleetItemViewModel)?.RoomDirectoryPath);
        Assert.Equal(b.RoomDirectoryPath, window.ViewModel.Rooms.CurrentItem?.RoomDirectoryPath);
    }

    /// <summary>
    /// Second-reader finding on #1279 (PR #1280) — see
    /// <see cref="MainWindow"/>'s <c>_hasFocusedSwitcherOnOpen</c> field for why this guard exists.
    /// </summary>
    [AvaloniaFact]
    public void RestoringFromTray_DoesNotStealFocusBackToTheSwitcher()
    {
        var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // The "needs you" toggle rather than ChatInputBox: it's always in the visual tree with no
        // IsVisible gating (unlike the composer, which lives inside ChatView and needs a room open
        // to render at all), so it isolates the claim under test — does Opened re-steal focus —
        // from an unrelated visibility precondition.
        var toggle = window.FindControl<ToggleButton>("SwitcherNeedsYouToggle");
        Assert.NotNull(toggle);
        toggle!.Focus();
        Dispatcher.UIThread.RunJobs();
        Assert.True(toggle.IsFocused); // control arm: the move itself worked before the cycle

        window.Hide();
        Dispatcher.UIThread.RunJobs();
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var list = window.FindControl<ListBox>("SwitcherList");
        Assert.False(list!.IsFocused); // the regression this test exists to catch: must not be true
    }

    private static PausedStepViewModel NewPausedStep() => new(
        new StepId("a"), new ExecutionId("exec-1"), [], (_, _, _, _, _, _, _) => Task.CompletedTask);

    /// <summary>
    /// Fable's ruling on #1279's Tab-reachability fork — see the <c>Focusable="False"</c> comment on
    /// this button in <c>MainWindow.axaml</c> for the reasoning. Asserts the shape of that ruling
    /// directly rather than only its consequence (Tab skipping past it), so a future change to
    /// tab-stop ordering elsewhere can't silently make this pass for the wrong reason.
    /// </summary>
    [AvaloniaFact]
    public void TheSwitcherRowsOwnReviewButton_IsDeliberatelyNotFocusable()
    {
        var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()))
        {
            Width = 1215,
            Height = 800,
        };
        var a = window.ViewModel.Rooms.AddTestItem(NewItem("/rooms/a"));
        a.PausedSteps.Add(new InboxItemViewModel(
            "/rooms/a", "Room A", "step-1", "Ready for review", "", PausePointKind.ReadyForReview, _ => Task.CompletedTask));
        a.IsExpanded = true;
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.ApplyTemplate();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        var list = window.FindControl<ListBox>("SwitcherList")!;
        var reviewButton = list.GetVisualDescendants().OfType<Button>()
            .FirstOrDefault(b => b.Command == a.PausedSteps[0].ReviewCommand);
        Assert.NotNull(reviewButton);
        Assert.False(reviewButton!.Focusable);
    }

    /// <summary>
    /// See <c>SwitcherList.KeyDown</c>'s handler comment in <c>MainWindow.axaml.cs</c> for Fable's
    /// ruling behind this handoff. Arrow-key selection already opens the room, the same way
    /// <see cref="Once_focused_the_down_arrow_moves_selection_to_the_next_room"/> proves.
    /// </summary>
    [AvaloniaFact]
    public void EnterOnTheSwitcher_WithAPendingDecision_MovesFocusToItsFirstButton()
    {
        var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()))
        {
            Width = 1215,
            Height = 800,
        };
        window.ViewModel.CurrentSection = ShellSection.Chat;
        window.ViewModel.Chat.OpenPipelineRoom("/tmp/room-1", [NewPausedStep()]);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.ApplyTemplate();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        var list = window.FindControl<ListBox>("SwitcherList")!;
        list.Focus();
        Dispatcher.UIThread.RunJobs();
        Assert.True(list.IsFocused); // control arm: the move itself worked before Enter

        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        var pausedStepsList = window.FindViewControl<ItemsControl>("PausedStepsList")!;
        var reviewButton = pausedStepsList.GetVisualDescendants().OfType<Button>()
            .FirstOrDefault(b => b.Classes.Contains("accent"));
        Assert.NotNull(reviewButton);
        Assert.True(reviewButton!.IsFocused);
        Assert.False(list.IsFocused);
    }

    /// <summary>
    /// The fallback half of the same handoff: with nothing awaiting a decision, Enter still has to
    /// land somewhere usable rather than no-op — the composer is the room's next actionable control.
    /// </summary>
    [AvaloniaFact]
    public void EnterOnTheSwitcher_WithNoPendingDecision_FallsBackToTheComposer()
    {
        var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));
        window.Show();
        // A plain chat session, not a pipeline room — a pipeline room's composer is deliberately
        // disabled ("the composer's honesty", see NavigationShellTests), so a disabled ChatInputBox
        // could never take focus regardless of this handler; a real session is what makes this arm
        // discriminating.
        window.ViewModel.Chat.LoadFromMetadata(
            new SessionMetadata(
                SessionId: "sess-1", RoomDirectoryPath: "/tmp/sess-1", CurrentAdapter: "claude",
                CurrentVendorSessionId: "vendor-1", Model: null, WorkingDirectory: null,
                TurnCount: 0, SafetyCeiling: 100, CreatedAt: DateTimeOffset.UtcNow,
                UpdatedAt: DateTimeOffset.UtcNow, Turns: []),
            "/tmp/sess-1");
        window.ViewModel.CurrentSection = ShellSection.Chat;
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        var list = window.FindControl<ListBox>("SwitcherList")!;
        list.Focus();
        Dispatcher.UIThread.RunJobs();
        Assert.True(list.IsFocused); // control arm

        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.True(window.ChatInputBox.IsFocused);
        Assert.False(list.IsFocused);
    }

    /// <summary>
    /// Second reader on this same PR: see <c>SwitcherList.KeyDown</c>'s handler comment in
    /// <c>MainWindow.axaml.cs</c> for why an idle pipeline room needs its own arm here. A test that
    /// only asserts where focus ends up can't discriminate the fix, because a no-op <c>Focus()</c>
    /// call leaves focus exactly where an unhandled key would too — so this reads <c>e.Handled</c>
    /// itself via a window-level bubble listener (<c>handledEventsToo: true</c>).
    /// </summary>
    [AvaloniaFact]
    public void EnterOnTheSwitcher_InAnIdlePipelineRoom_LeavesTheKeystrokeUnhandled()
    {
        var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));
        window.ViewModel.Chat.OpenPipelineRoom("/tmp/room-1"); // no paused steps
        window.ViewModel.CurrentSection = ShellSection.Chat;
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        var list = window.FindControl<ListBox>("SwitcherList")!;
        list.Focus();
        Dispatcher.UIThread.RunJobs();
        Assert.True(list.IsFocused); // control arm

        var handledAtWindow = false;
        window.AddHandler(
            InputElement.KeyDownEvent,
            (_, e) => { if (e.Key == Key.Enter) handledAtWindow = e.Handled; },
            RoutingStrategies.Bubble,
            handledEventsToo: true);

        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.False(handledAtWindow); // nothing to hand off to — the keystroke must not vanish
        Assert.True(list.IsFocused); // and with nowhere to go, focus stays put
    }
}
