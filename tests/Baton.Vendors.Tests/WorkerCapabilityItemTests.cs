using Baton.Vendors;

namespace Baton.Vendors.Tests;

/// <summary>
/// #615 (0020 clause 1): which capability kinds a user can invoke is vendor-kind semantics, stated
/// once here in the adapter layer — previously the chat picker's row record computed it for itself.
/// A golden map over every canonical kind, so a new kind added without classifying it turns into a
/// red test here rather than a row that silently lands in whichever picker section the default gave.
/// </summary>
public class WorkerCapabilityItemTests
{
    [Theory]
    [InlineData("command", true)]
    [InlineData("skill", true)]
    [InlineData("agent", true)]
    [InlineData("mode", false)]
    [InlineData("plugin", false)]
    public void Invokability_is_stated_per_canonical_kind(string kind, bool expected)
    {
        var item = new WorkerCapabilityItem("name", kind, "description");
        Assert.Equal(expected, item.IsInvokable);
    }

    [Fact]
    public void An_unknown_kind_is_not_invokable()
    {
        // The safe default for a kind this layer has never classified: informational, never an
        // action the picker offers to send.
        Assert.False(new WorkerCapabilityItem("name", "future-kind", "description").IsInvokable);
    }
}
