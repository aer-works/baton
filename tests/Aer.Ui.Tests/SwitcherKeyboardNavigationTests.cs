using Aer.Ui.Core;
using Aer.Ui.Tests.TestSupport;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;

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
}
