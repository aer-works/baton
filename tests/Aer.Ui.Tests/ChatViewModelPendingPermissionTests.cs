using Aer.Flow.Projection;
using Aer.Ui.Core;
using Xunit;

namespace Aer.Ui.Tests;

public class ChatViewModelPendingPermissionTests
{
    private static readonly AnswerPermissionDelegate NoopAnswer = (_, _, _) => Task.CompletedTask;

    private static PendingPermission Ask(string requestId) =>
        new(requestId, "chat-worker", "claude", "Bash", "{\"command\":\"ls\"}", "shell", DateTimeOffset.UtcNow);

    [Fact]
    public void SurfacePending_BuildsGate_WhenPermissionAppears()
    {
        var chat = new ChatViewModel();

        chat.SurfacePendingPermission(Ask("req-1"), NoopAnswer);

        Assert.True(chat.HasPendingPermission);
        Assert.Equal("req-1", chat.PendingPermission!.PermissionRequestId);
    }

    [Fact]
    public void SurfacePending_Null_ClearsGate()
    {
        var chat = new ChatViewModel();
        chat.SurfacePendingPermission(Ask("req-1"), NoopAnswer);

        chat.SurfacePendingPermission(null, NoopAnswer);

        Assert.False(chat.HasPendingPermission);
        Assert.Null(chat.PendingPermission);
    }

    [Fact]
    public void SurfacePending_SameRequestId_KeepsLiveInstance_PreservingState()
    {
        var chat = new ChatViewModel();
        chat.SurfacePendingPermission(Ask("req-1"), NoopAnswer);
        var first = chat.PendingPermission!;
        first.IsEnabled = false; // e.g. a mutation is in flight

        chat.SurfacePendingPermission(Ask("req-1"), NoopAnswer);

        Assert.Same(first, chat.PendingPermission);
        Assert.False(chat.PendingPermission!.IsEnabled); // a no-change poll must not reset it
    }

    [Fact]
    public void SurfacePending_DifferentRequestId_BuildsFreshInstance()
    {
        var chat = new ChatViewModel();
        chat.SurfacePendingPermission(Ask("req-1"), NoopAnswer);
        var first = chat.PendingPermission!;

        chat.SurfacePendingPermission(Ask("req-2"), NoopAnswer);

        Assert.NotSame(first, chat.PendingPermission);
        Assert.Equal("req-2", chat.PendingPermission!.PermissionRequestId);
    }
}
