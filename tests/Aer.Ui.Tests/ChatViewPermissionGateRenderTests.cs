using Aer.Adapters;
using Aer.Flow.Projection;
using Aer.Ui.Core;
using Aer.Ui.Tests.TestSupport;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;

namespace Aer.Ui.Tests;

/// <summary>
/// The conversational permission gate (0022, #390) rendered against a REAL ChatView, not just the
/// view model. This is the binding-resolution guard the VM tests cannot be: the gate card swaps its
/// DataContext to a <see cref="PendingPermissionViewModel"/> with its own <c>x:DataType</c>, and its
/// rungs are compiled Command bindings — a wrong property name or a bad DataType scope would leave the
/// control absent or throw here while every VM assertion stayed green.
/// </summary>
/// <remarks>
/// Not a claim of visual verification: #981 measured headless control-property assertions staying
/// green while the screen rendered blank (see <see cref="ChatAdapterComboTests"/>). What this pins is
/// that the bindings resolve and the gate enters/leaves the tree with the projection — the live drive
/// is the visual half.
/// </remarks>
public class ChatViewPermissionGateRenderTests
{
    private static string NewConfigFilePath() =>
        Path.Combine(Path.GetTempPath(), $"aer-ui-gate-config-{Guid.NewGuid():N}", "recent-room-directories.json");

    private static PendingPermission ShellAsk(string requestId = "req-1") =>
        new(requestId, "chat-worker", "claude", "Bash", "{\"command\":\"rm -rf build/\"}", "shell", DateTimeOffset.UtcNow);

    [AvaloniaFact]
    public void Gate_IsHidden_WhenNoPermissionPending()
    {
        var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));
        Dispatcher.UIThread.RunJobs();

        var gate = window.FindViewControl<Border>("ChatPermissionGate");
        Assert.NotNull(gate);
        Assert.False(gate!.IsVisible); // control arm: nothing pending → the card is collapsed
    }

    [AvaloniaFact]
    public void Gate_Appears_WhenAPermissionIsSurfaced()
    {
        var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));
        var chat = window.ViewModel.Chat;

        chat.SurfacePendingPermission(ShellAsk(), (_, _, _) => Task.CompletedTask);
        Dispatcher.UIThread.RunJobs();

        var gate = window.FindViewControl<Border>("ChatPermissionGate");
        Assert.NotNull(gate);
        Assert.True(gate!.IsVisible); // the binding on Chat.HasPendingPermission resolved and flipped the card in
    }

    [AvaloniaFact]
    public void AllowOnceButton_IsBound_AndAnswersThroughTheDelegate()
    {
        var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));
        var chat = window.ViewModel.Chat;

        var answers = new List<(string Id, string Kind, string? Reason)>();
        chat.SurfacePendingPermission(ShellAsk("req-99"), (id, kind, reason) =>
        {
            answers.Add((id, kind, reason));
            return Task.CompletedTask;
        });
        Dispatcher.UIThread.RunJobs();

        var allowOnce = window.FindViewControl<Button>("ChatPermissionAllowOnceButton");
        Assert.NotNull(allowOnce);
        // Proves the compiled Command binding resolved to the VM's AllowOnceCommand — invoking the
        // control's own command is what a click does, and it must reach the answer delegate.
        allowOnce!.Command!.Execute(allowOnce.CommandParameter);
        Dispatcher.UIThread.RunJobs();

        var answer = Assert.Single(answers);
        Assert.Equal("req-99", answer.Id);
        Assert.Equal(PermissionDecisionKind.AllowOnce, answer.Kind);
    }

    /// <summary>
    /// #1173's second reader named the gap: <c>PermissionGateKeystrokeTests</c> exercises the pure
    /// <c>PermissionAnswerFor</c> decision with no window — a seam that cannot see routing by
    /// construction (the #1060 "green tests, unwired feature" class). This raises a REAL KeyDown
    /// on the live window with the gate mounted at its transcript position: a bare <c>y</c>
    /// reaches the answer delegate, and the same <c>y</c> with the composer focused does not
    /// (the typing guard).
    /// </summary>
    [AvaloniaFact]
    public void A_bare_y_on_the_live_window_answers_AllowOnce_but_not_while_the_composer_is_focused()
    {
        var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));
        var chat = window.ViewModel.Chat;

        var answers = new List<(string Id, string Kind, string? Reason)>();
        chat.SurfacePendingPermission(ShellAsk("req-key"), (id, kind, reason) =>
        {
            answers.Add((id, kind, reason));
            return Task.CompletedTask;
        });
        Dispatcher.UIThread.RunJobs();

        // Guard arm first: composer focused → y is typing, never an answer.
        var composer = window.FindViewControl<TextBox>("ChatInputBox");
        Assert.NotNull(composer);
        composer!.Focusable = true;
        composer.Focus();
        Dispatcher.UIThread.RunJobs();
        composer.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = Key.Y,
            KeyModifiers = KeyModifiers.None,
        });
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(answers);

        // Focus off the composer: the same bubbled keystroke now answers.
        window.Focus();
        Dispatcher.UIThread.RunJobs();
        window.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = Key.Y,
            KeyModifiers = KeyModifiers.None,
        });
        Dispatcher.UIThread.RunJobs();

        var answer = Assert.Single(answers);
        Assert.Equal("req-key", answer.Id);
        Assert.Equal(PermissionDecisionKind.AllowOnce, answer.Kind);
    }

    [AvaloniaFact]
    public void Gate_IsDescendantOfChatMessagesScroll_WhenSurfaced_AndIsHiddenWhenCleared()
    {
        var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));
        var chat = window.ViewModel.Chat;

        var scroll = window.FindViewControl<ScrollViewer>("ChatMessagesScroll");
        var gate = window.FindViewControl<Border>("ChatPermissionGate");
        Assert.NotNull(scroll);
        Assert.NotNull(gate);

        // With a pending permission surfaced, the gate Border is a descendant of ChatMessagesScroll (the transcript) and visible
        chat.SurfacePendingPermission(ShellAsk(), (_, _, _) => Task.CompletedTask);
        Dispatcher.UIThread.RunJobs();

        Assert.Contains(scroll, gate!.GetLogicalAncestors());
        Assert.True(gate!.IsVisible);

        // With the permission cleared, it is not visible
        chat.SurfacePendingPermission(null, (_, _, _) => Task.CompletedTask);
        Dispatcher.UIThread.RunJobs();

        Assert.False(gate.IsVisible);
    }
}
