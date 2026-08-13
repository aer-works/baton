using System.Linq;
using System.Reflection;
using Aer.Flow.Projection;
using Aer.Ui.Core;

namespace Aer.Ui.Tests;

/// <summary>
/// The live WS push path <c>RoomProjectionLoaderTests</c> cannot see: the daemon serializes a bare
/// <see cref="RoomProjection"/>, the client rebuilds it from the private <c>ProjectionFrame</c> DTO plus
/// a <c>DirectoryPath</c> sibling. A member the frame omits is dropped on every live update while the
/// room-open HTTP load still carries it — which is exactly how <c>PendingPermission</c> shipped broken
/// (#445/#390): visible on open, blind to the ask appearing or clearing thereafter.
/// </summary>
public class WebSocketProjectionFrameTests
{
    [Fact]
    public void The_frame_mirrors_every_RoomProjection_member_so_none_is_dropped_on_a_live_push()
    {
        // The durable guard, not a spot check: new RoomProjection members carry defaults, so ToProjection
        // and every hand-written construction keep COMPILING when one is missing from the frame. Only a
        // structural comparison catches the next silent drop.
        var frameType = typeof(RoomClient).GetNestedType(
            "ProjectionFrame", BindingFlags.NonPublic | BindingFlags.Public);
        Assert.NotNull(frameType);

        var framed = Primary(frameType!).Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        var projected = Primary(typeof(RoomProjection)).Select(p => p.Name);

        var dropped = projected.Where(m => !framed.Contains(m)).ToList();
        Assert.True(
            dropped.Count == 0,
            $"ProjectionFrame omits RoomProjection member(s), so they are dropped on every live WS push: "
            + string.Join(", ", dropped));
    }

    [Fact]
    public void ToProjection_carries_the_pending_gate_from_the_frame_into_the_projection()
    {
        // The mapping half: the frame HAVING the member is worthless if the reconstruction drops it,
        // which is the second thing that was wrong (the construction was 4-arg). Snapshot/State/etc. are
        // irrelevant here and left null — their shapes are pinned by WireFixtureStalenessTests.
        var pending = new PendingPermission(
            "req-gate-1", "chat-worker", "claude", "Bash", "{}", "shell", DateTimeOffset.UnixEpoch);

        var frame = new RoomClient.ProjectionFrame("C:/tasks/foo", null!, null!, null!, null!, pending);
        var projection = RoomClient.ToProjection(frame);

        Assert.Same(pending, projection.PendingPermission);
    }

    [Fact]
    public void ToProjection_carries_the_answer_history_from_the_frame_into_the_projection()
    {
        // #1142's member, same mapping half as the gate above — plus the null-frame default, which
        // must land as the projection's empty list, not a null a renderer then dereferences.
        var answers = new List<PermissionAnswer>
        {
            new("req-1", "Bash", "Shell", "AllowOnce", null, "op", DateTimeOffset.UnixEpoch, WasRevoked: false)
        };

        var frame = new RoomClient.ProjectionFrame("C:/tasks/foo", null!, null!, null!, null!, null, answers);
        var projection = RoomClient.ToProjection(frame);

        Assert.Same(answers, projection.PermissionAnswers);

        var defaulted = RoomClient.ToProjection(
            new RoomClient.ProjectionFrame("C:/tasks/foo", null!, null!, null!, null!, null));
        Assert.NotNull(defaulted.PermissionAnswers);
        Assert.Empty(defaulted.PermissionAnswers);
    }

    // The record's PRIMARY constructor. A record also emits a copy constructor (one parameter), so the
    // longest-parameter ctor is the declared one.
    private static ParameterInfo[] Primary(Type type) =>
        type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .OrderByDescending(c => c.GetParameters().Length)
            .First()
            .GetParameters();
}
