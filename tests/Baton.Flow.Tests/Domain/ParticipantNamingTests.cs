using Baton.Flow.Domain;

namespace Baton.Flow.Tests.Domain;

/// <summary>0054 §1's auto-naming rule (#1305): first-of-vendor keeps the bare vendor name; a second, third, … of the same vendor gets a numbered suffix.</summary>
public class ParticipantNamingTests
{
    [Fact]
    public void First_participant_of_a_vendor_keeps_the_bare_vendor_name()
    {
        Assert.Equal("claude", ParticipantNaming.NextName("claude", existingNames: []));
    }

    [Fact]
    public void Second_participant_of_the_same_vendor_gets_dash_two()
    {
        Assert.Equal("claude-2", ParticipantNaming.NextName("claude", existingNames: ["claude"]));
    }

    [Fact]
    public void Third_participant_of_the_same_vendor_gets_dash_three()
    {
        Assert.Equal("claude-3", ParticipantNaming.NextName("claude", existingNames: ["claude", "claude-2"]));
    }

    [Fact]
    public void A_freed_gap_in_taken_names_is_reused_rather_than_skipped()
    {
        // claude-2 was renamed/removed, leaving a gap below claude-3 -- the next join fills the
        // lowest free suffix rather than always counting past the highest ever used.
        Assert.Equal("claude-2", ParticipantNaming.NextName("claude", existingNames: ["claude", "claude-3"]));
    }

    [Fact]
    public void A_different_vendor_does_not_collide_with_an_existing_name()
    {
        Assert.Equal("gemini", ParticipantNaming.NextName("gemini", existingNames: ["claude"]));
    }
}
