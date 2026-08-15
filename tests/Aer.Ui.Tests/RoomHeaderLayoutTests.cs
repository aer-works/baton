using Aer.Ui.Core;
using Aer.Ui.Tests.TestSupport;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Aer.Ui.Tests;

/// <summary>
/// #1224: every control in the room header is fully inside the window at every supported width,
/// with the shape panel open and a room name long enough to push.
/// </summary>
/// <remarks>
/// <para>
/// The issue said no test could catch this and that only a drive could. That is wrong for <em>this</em>
/// failure mode, and the correction is worth keeping: a clipped control is not a rendering artifact,
/// it is layout arithmetic — a headless arrange produces real bounds, and a control whose translated
/// bounds leave the client area is clipped whether or not anyone is looking. So this is enforcement,
/// not illustration.
/// </para>
/// <para>
/// What it does NOT do is detect clipped controls in general. It pins this header at these widths.
/// The class of defect still belongs to driving the built app, which is how it was found.
/// </para>
/// <para>
/// It also does not pin the <em>ellipsis</em>, and that was measured rather than assumed: removing
/// <c>TextTrimming</c> leaves every assertion here green, because what keeps the controls whole is
/// the star/Auto column structure — the name is constrained either way, and trimming only decides
/// whether a person can SEE that it was cut. That half is a rendering claim and belongs to the drive.
/// </para>
/// </remarks>
public class RoomHeaderLayoutTests
{
    private static string NewConfigFilePath() =>
        Path.Combine(Path.GetTempPath(), $"aer-ui-header-config-{Guid.NewGuid():N}", "recent-room-directories.json");

    /// <summary>
    /// A name no header can absorb. The room name is user-chosen, so its length is not the product's
    /// to assume — what the layout owes is that a long one costs the name its full display and
    /// nothing else its place.
    /// </summary>
    private const string LongRoomName =
        "a-room-whose-name-is-long-enough-that-nothing-else-in-the-header-could-possibly-fit-beside-it-ok";

    /// <param name="windowWidth">
    /// 900 is <c>MainWindow.axaml</c>'s own <c>MinWidth</c> — the supported floor, and the width at
    /// which the pre-#1224 arrangement left the header roughly 130px. 1215 is the width the defect
    /// was actually found at while driving.
    /// </param>
    [AvaloniaTheory]
    [InlineData(900d)]
    [InlineData(1215d)]
    public void Every_header_control_is_inside_the_window_with_the_shape_panel_open(double windowWidth)
    {
        var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()))
        {
            Width = windowWidth,
            Height = 800,
        };

        window.ViewModel.CurrentSection = ShellSection.Chat;
        window.ViewModel.Chat.HeadlineText = LongRoomName;
        window.ViewModel.Chat.WorkerChipText = "claude"; // vocabulary-ok: technical adapter setting
        // A workflow room with its workflow on is what makes every header control visible at once —
        // the Shape toggle and the switch are both offered only there. That is the worst case, and
        // the only one in which the panel can be open to compete for the width.
        window.ViewModel.Chat.IsPipelineRoom = true;
        window.ViewModel.Chat.IsWorkflowOn = true;
        window.ViewModel.Chat.IsShapePanelOpen = true;

        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.ApplyTemplate();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        var header = window.ChatHeaderControl;
        Assert.True(header.IsVisible);

        // The panel really is open and really is taking its width — without this the test would pass
        // just as happily against the arrangement the issue was filed about.
        Assert.True(window.IsShapeRegionVisible);

        foreach (var name in new[] { "RoomNameText", "ChatShapeToggle", "WorkflowSwitch", "StopButton" })
        {
            var control = header.FindControl<Control>(name);
            Assert.NotNull(control);
            AssertFullyInside(window, control!, $"{name} at {windowWidth}px");
        }

        // One line, one glance: the controls row is a single row, not wrapped onto two.
        var row = header.FindControl<Grid>("HeaderRow")!;
        var stop = header.FindControl<Button>("StopButton")!;
        Assert.True(
            row.Bounds.Height <= stop.Bounds.Height + 1,
            $"the header row grew past one control's height ({row.Bounds.Height} vs {stop.Bounds.Height}) — it wrapped");

        // And the name is what yielded: arranged narrower than it wanted, while every control got
        // exactly the width it asked for. Asserting only that the name is narrow would pass against a
        // layout that squeezed the controls too — the pairing is what makes this discriminating.
        var nameText = header.FindControl<TextBlock>("RoomNameText")!;
        Assert.True(
            nameText.Bounds.Width < NaturalWidthOf(nameText),
            "the name got its full natural width, so it was not what yielded");

        foreach (var name in new[] { "ChatShapeToggle", "WorkflowSwitch", "StopButton" })
        {
            var control = header.FindControl<Control>(name)!;
            Assert.True(
                control.Bounds.Width >= control.DesiredSize.Width,
                $"{name} was arranged narrower than it asked for — a control yielded, and only the name may");
        }
    }

    /// <summary>
    /// The other polarity of the row above: with the panel CLOSED the header must still hold, which
    /// is the state most rooms are in most of the time.
    /// </summary>
    [AvaloniaFact]
    public void Every_header_control_is_inside_the_window_with_the_shape_panel_closed()
    {
        var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()))
        {
            Width = 900,
            Height = 800,
        };

        window.ViewModel.CurrentSection = ShellSection.Chat;
        window.ViewModel.Chat.HeadlineText = LongRoomName;
        window.ViewModel.Chat.IsPipelineRoom = true;
        window.ViewModel.Chat.IsWorkflowOn = true;
        window.ViewModel.Chat.IsShapePanelOpen = false;

        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.ApplyTemplate();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Assert.False(window.IsShapeRegionVisible);

        foreach (var name in new[] { "RoomNameText", "ChatShapeToggle", "WorkflowSwitch", "StopButton" })
        {
            var control = window.ChatHeaderControl.FindControl<Control>(name);
            Assert.NotNull(control);
            AssertFullyInside(window, control!, $"{name} with the panel closed");
        }
    }

    /// <summary>
    /// #1224's second reader: <c>FindViewControl</c> is the general-purpose by-name lookup the
    /// headless tests share, and it searches an explicit chain of view scopes. The header became a
    /// new scope, so a chain that had not learned about it returned <see langword="null"/> for every
    /// control in it — silently, because the signature is nullable, which is why no existing test
    /// went red.
    /// </summary>
    [AvaloniaFact]
    public void The_shared_by_name_lookup_reaches_the_header_scope()
    {
        var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));
        window.ViewModel.CurrentSection = ShellSection.Chat;
        window.Show();

        // One from the header and one from the transcript: a chain that lost the header scope fails
        // the first, and a chain that somehow replaced rather than extended it fails the second.
        Assert.NotNull(window.FindViewControl<Button>("StopButton"));
        Assert.NotNull(window.FindViewControl<ScrollViewer>("ChatMessagesScroll"));
    }

    /// <summary>
    /// The width <paramref name="source"/>'s text would take if nothing constrained it. Measured on a
    /// throwaway twin rather than by re-measuring the live control, which would corrupt the layout
    /// pass the assertions above are reading. <c>DesiredSize</c> cannot answer this: it is the result
    /// of a Measure that was already given the constrained available width, so for a squeezed
    /// TextBlock it equals the squeezed width and comparing the two is vacuously false.
    /// </summary>
    private static double NaturalWidthOf(TextBlock source)
    {
        var twin = new TextBlock
        {
            Text = source.Text,
            FontSize = source.FontSize,
            FontFamily = source.FontFamily,
            FontWeight = source.FontWeight,
            TextWrapping = Avalonia.Media.TextWrapping.NoWrap,
        };

        twin.Measure(Size.Infinity);
        return twin.DesiredSize.Width;
    }

    /// <summary>
    /// A control's own bounds are relative to its parent, so a clipped one has perfectly innocent
    /// bounds — the reading that matters is where it lands in the window. That is what
    /// <c>TranslatePoint</c> answers, and it is why a naive assertion on <c>Bounds</c> would pass
    /// against the very defect this file exists for.
    /// </summary>
    private static void AssertFullyInside(Window window, Control control, string what)
    {
        Assert.True(control.IsVisible, $"{what}: not visible at all");
        Assert.True(control.Bounds.Width > 0, $"{what}: measured to zero width");

        var topLeft = control.TranslatePoint(new Point(0, 0), window);
        var bottomRight = control.TranslatePoint(new Point(control.Bounds.Width, control.Bounds.Height), window);
        Assert.NotNull(topLeft);
        Assert.NotNull(bottomRight);

        Assert.True(topLeft!.Value.X >= 0, $"{what}: starts left of the window ({topLeft.Value.X})");
        Assert.True(
            bottomRight!.Value.X <= window.Width,
            $"{what}: runs past the right edge (ends at {bottomRight.Value.X}, window is {window.Width})");
    }
}
