using Aer.Adapters;
using Aer.Ui.Tests.TestSupport;
using Avalonia.Headless.XUnit;

namespace Aer.Ui.Tests;

/// <summary>
/// #1321 (pre-existing on <c>main</c>, fixed alongside #1307) -- see
/// <see cref="ChatViewModel.ResetComposerForRoomSwitch"/>'s remarks for the defect and the fix. These
/// drive <see cref="MainWindow.OpenAsync"/> itself, not <see cref="ChatViewModel.Clear"/> in isolation:
/// a test that only proves <c>Clear()</c> works does not prove the room-switch path actually invokes it,
/// which is exactly how the defect survived.
/// </summary>
public class MainWindowRoomSwitchComposerTests
{
    private static string NewConfigFilePath() =>
        Path.Combine(Path.GetTempPath(), $"aer-ui-room-switch-config-{Guid.NewGuid():N}", "recent-room-directories.json");

    private static async Task<string> CreateInteractiveRoomAsync(string sessionId, CancellationToken cancellationToken)
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"ui-room-switch-{Guid.NewGuid():N}");
        await InteractiveSessionMaterializer.MaterializeToDirectoryAsync(
            sessionId: sessionId, roomDirectoryPath: roomDirectory, adapter: "claude", cancellationToken: cancellationToken);
        return roomDirectory;
    }

    [AvaloniaFact]
    public async Task SwitchingToADifferentRoom_DoesNotCarryTheQueueOver()
    {
        var roomA = await CreateInteractiveRoomAsync("sess-switch-queue-a", TestContext.Current.CancellationToken);
        var roomB = await CreateInteractiveRoomAsync("sess-switch-queue-b", TestContext.Current.CancellationToken);
        try
        {
            var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));
            await window.OpenAsync(roomA, TestContext.Current.CancellationToken);
            window.ViewModel.Chat.EnqueueMessage("left behind in room A");
            Assert.True(window.ViewModel.Chat.HasQueuedMessages);

            await window.OpenAsync(roomB, TestContext.Current.CancellationToken);

            Assert.False(window.ViewModel.Chat.HasQueuedMessages);
            Assert.Empty(window.ViewModel.Chat.QueuedMessages);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomA);
            DirectoryCleanup.DeleteRecursively(roomB);
        }
    }

    [AvaloniaFact]
    public async Task SwitchingToADifferentRoom_DoesNotCarryTheDraftOver()
    {
        var roomA = await CreateInteractiveRoomAsync("sess-switch-draft-a", TestContext.Current.CancellationToken);
        var roomB = await CreateInteractiveRoomAsync("sess-switch-draft-b", TestContext.Current.CancellationToken);
        try
        {
            var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));
            await window.OpenAsync(roomA, TestContext.Current.CancellationToken);
            window.ViewModel.Chat.InputText = "half-typed in room A";

            await window.OpenAsync(roomB, TestContext.Current.CancellationToken);

            Assert.Equal(string.Empty, window.ViewModel.Chat.InputText);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomA);
            DirectoryCleanup.DeleteRecursively(roomB);
        }
    }

    /// <summary>#1272's precedent, guarded against this fix: a settle-time reopen of the SAME room must NOT wipe a queue or draft the operator is still watching.</summary>
    [AvaloniaFact]
    public async Task ReopeningTheSameRoom_PreservesTheQueueAndTheDraft()
    {
        var room = await CreateInteractiveRoomAsync("sess-reopen-same", TestContext.Current.CancellationToken);
        try
        {
            var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));
            await window.OpenAsync(room, TestContext.Current.CancellationToken);
            window.ViewModel.Chat.EnqueueMessage("still waiting");
            window.ViewModel.Chat.InputText = "still typing";

            await window.OpenAsync(room, TestContext.Current.CancellationToken);

            Assert.True(window.ViewModel.Chat.HasQueuedMessages);
            Assert.Equal("still waiting", Assert.Single(window.ViewModel.Chat.QueuedMessages).Text);
            Assert.Equal("still typing", window.ViewModel.Chat.InputText);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(room);
        }
    }
}
