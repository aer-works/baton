using Aer.Adapters;
using Aer.Flow.Domain;
using Aer.Ui.Core;
using Aer.Ui.Tests.TestSupport;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;

namespace Aer.Ui.Tests;

/// <summary>
/// 0054 §4/#1307 ruling 1's chip row rendered against a REAL ChatView, not just the ViewModel --
/// <see cref="ChatViewPermissionGateRenderTests"/>'s reason applies here too: the row's compiled
/// Command binding threads through an <c>AncestorType=UserControl</c> RelativeSource
/// (<c>((vm:MainWindowViewModel)DataContext).Chat.SelectTagParticipantCommand</c>), which a wrong
/// DataType scope or property name leaves silently unresolved while every ChatViewModel-only test
/// (<see cref="ChatViewModelTests"/>) stays green -- the exact "195 passing UI tests, feature
/// invisible" failure shape <c>right-instrument</c> names.
/// </summary>
public class ChatTagChipRenderTests
{
    private static string NewConfigFilePath() =>
        Path.Combine(Path.GetTempPath(), $"aer-ui-tagchip-config-{Guid.NewGuid():N}", "recent-room-directories.json");

    private static readonly Participant Orchestrator = new(new WorkerId("claude-1"), "claude-1", "claude", "sonnet", null, IsOrchestrator: true);
    private static readonly Participant Second = new(new WorkerId("claude-2"), "claude-2", "claude", "sonnet", null, IsOrchestrator: false);

    private static SessionMetadata MetadataWithParticipants(params Participant[] participants) => new(
        SessionId: "sess-1",
        RoomDirectoryPath: "/tmp/tagchip-1",
        CurrentAdapter: "claude",
        CurrentVendorSessionId: "vendor-1",
        Model: null,
        WorkingDirectory: null,
        TurnCount: 0,
        SafetyCeiling: 100,
        CreatedAt: DateTimeOffset.UtcNow,
        UpdatedAt: DateTimeOffset.UtcNow,
        Turns: [],
        Participants: [.. participants]);

    [AvaloniaFact]
    public void ChipRow_IsHidden_ForASingleParticipantRoom()
    {
        var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));
        window.ViewModel.CurrentSection = ShellSection.Chat;
        window.Show();
        window.ViewModel.Chat.LoadFromMetadata(MetadataWithParticipants(Orchestrator), "/tmp/tagchip-1");
        Dispatcher.UIThread.RunJobs();

        var row = window.FindViewControl<StackPanel>("ChatTagChipRow");
        Assert.NotNull(row);
        Assert.False(row!.IsVisible); // control arm: the binding resolved to Chat.ShowTagChipRow=false, not an absent control
    }

    [AvaloniaFact]
    public void ChipRow_Appears_AndOneChipPerParticipant_ForATwoParticipantRoom()
    {
        var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));
        window.ViewModel.CurrentSection = ShellSection.Chat;
        window.Show();
        window.ViewModel.Chat.LoadFromMetadata(MetadataWithParticipants(Orchestrator, Second), "/tmp/tagchip-1");
        Dispatcher.UIThread.RunJobs();

        var row = window.FindViewControl<StackPanel>("ChatTagChipRow");
        Assert.NotNull(row);
        Assert.True(row!.IsVisible);

        var chips = row.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("tagChip")).ToList();
        Assert.Equal(2, chips.Count);
        Assert.Contains(chips, b => Equals(b.Content, Orchestrator.Name));
        Assert.Contains(chips, b => Equals(b.Content, Second.Name));
    }

    [AvaloniaFact]
    public void ClickingAChip_SelectsTheTag_ThroughTheCompiledCommandBinding_AndUntagClearsIt()
    {
        var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));
        window.ViewModel.CurrentSection = ShellSection.Chat;
        window.Show();
        var chat = window.ViewModel.Chat;
        chat.LoadFromMetadata(MetadataWithParticipants(Orchestrator, Second), "/tmp/tagchip-1");
        Dispatcher.UIThread.RunJobs();

        var row = window.FindViewControl<StackPanel>("ChatTagChipRow");
        Assert.NotNull(row);
        var secondChip = row!.GetVisualDescendants().OfType<Button>()
            .Single(b => b.Classes.Contains("tagChip") && Equals(b.Content, Second.Name));

        // Invoking the control's own resolved Command is what a click does -- proves the
        // AncestorType=UserControl RelativeSource binding actually reached SelectTagParticipantCommand,
        // not just that the ViewModel method works (ChatViewModelTests already covers that in isolation).
        Assert.NotNull(secondChip.Command);
        secondChip.Command!.Execute(secondChip.CommandParameter);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(Second.Id, chat.SelectedTagParticipantId);
        Assert.Equal("To: claude-2", chat.SelectedTagLabel);

        var untagButton = window.FindViewControl<Button>("ChatTagChipUntagButton");
        Assert.NotNull(untagButton);
        Assert.True(untagButton!.IsVisible);
        untagButton.Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Null(chat.SelectedTagParticipantId);
        Assert.Equal("To: room", chat.SelectedTagLabel);
        Assert.False(untagButton.IsVisible);
    }
}
