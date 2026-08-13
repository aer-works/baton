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

    [Fact]
    public void SurfacePending_InterleavesAnswersWithTurnsInTimestampOrder_AndFormatsWordings()
    {
        var chat = new ChatViewModel();
        var baseTime = new DateTimeOffset(2026, 8, 12, 10, 0, 0, TimeSpan.Zero);

        var turns = new[]
        {
            new Aer.Adapters.SessionTurn(1, "claude", "First turn", "Response 1", baseTime, false, false),
            new Aer.Adapters.SessionTurn(2, "claude", "Second turn", "Response 2", baseTime.AddMinutes(10), false, false)
        };

        var metadata = new Aer.Adapters.SessionMetadata(
            SessionId: "sess-1",
            RoomDirectoryPath: "C:/tasks/foo",
            CurrentAdapter: "claude",
            CurrentVendorSessionId: "vendor-1",
            Model: null,
            WorkingDirectory: null,
            TurnCount: turns.Length,
            SafetyCeiling: 100,
            CreatedAt: baseTime,
            UpdatedAt: baseTime.AddMinutes(10),
            Turns: [.. turns]);

        chat.LoadFromMetadata(metadata, "C:/tasks/foo");

        var answers = new List<PermissionAnswer>
        {
            new("req-1", "Bash", "Shell", "AllowOnce", null, "op", baseTime.AddMinutes(2), WasRevoked: false),
            new("req-2", "Edit", "Files", "Deny", "user declined", "op", baseTime.AddMinutes(5), WasRevoked: false),
            new("req-3", "Bash", "Shell", "", "turn_ended", "", baseTime.AddMinutes(12), WasRevoked: true)
        };

        chat.SurfacePendingPermission(null, answers, NoopAnswer);

        Assert.Equal(7, chat.Messages.Count);

        Assert.Equal("You", chat.Messages[0].SenderLabel);
        Assert.False(chat.Messages[0].IsSystem);

        Assert.Equal("claude", chat.Messages[1].SenderLabel);

        Assert.Equal("System", chat.Messages[2].SenderLabel);
        Assert.True(chat.Messages[2].IsSystem);
        Assert.Equal("Allowed once — Bash", chat.Messages[2].Text);

        Assert.Equal("System", chat.Messages[3].SenderLabel);
        Assert.True(chat.Messages[3].IsSystem);
        Assert.Equal("Denied — Edit: user declined", chat.Messages[3].Text);

        Assert.Equal("You", chat.Messages[4].SenderLabel);
        Assert.Equal("claude", chat.Messages[5].SenderLabel);

        Assert.Equal("System", chat.Messages[6].SenderLabel);
        Assert.True(chat.Messages[6].IsSystem);
        Assert.Equal("Expired unanswered — turn ended", chat.Messages[6].Text);
    }
}
