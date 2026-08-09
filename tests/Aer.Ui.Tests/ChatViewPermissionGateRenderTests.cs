using Aer.Adapters;
using Aer.Flow.Projection;
using Aer.Ui.Core;
using Aer.Ui.Tests.TestSupport;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
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
}
