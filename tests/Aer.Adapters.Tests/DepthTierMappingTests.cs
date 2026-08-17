namespace Aer.Adapters.Tests;

/// <summary>
/// #1339: the polarity set the issue names -- a claude-vendored worker on a recorded alias resolves
/// its tier; an agy-vendored worker resolves no tier; an unrecognised model resolves no tier. See
/// <see cref="DepthTierMapping"/>'s own doc comment for why the third case can never be a happy-path
/// oversight -- a test covering only "claude resolves" cannot fail against a producer that forwards
/// every model unconditionally.
/// </summary>
public class DepthTierMappingTests
{
    [Theory]
    [InlineData("opus", "deep")]
    [InlineData("sonnet", "balanced")]
    [InlineData("haiku", "fast")]
    public void Claude_resolves_every_recorded_alias_to_its_documented_purpose(string model, string expectedPurpose)
    {
        Assert.True(DepthTierMapping.TryResolve("claude", model, out var purpose));
        Assert.Equal(expectedPurpose, purpose);
    }

    [Theory]
    [InlineData("gemini-3.6-flash-thinking")]
    [InlineData("claude-sonnet-4-6")]
    [InlineData("gpt-oss-120b-medium")]
    public void Agy_resolves_no_tier_for_any_of_its_catalogue_entries(string model)
    {
        // #1330 deliberately left agy's entire column unrecorded -- this must stay false for every
        // model in its catalogue, not just an arbitrary sample, so the absence is a rule and not a
        // gap in coverage.
        Assert.False(DepthTierMapping.TryResolve("agy", model, out var purpose));
        Assert.Equal(string.Empty, purpose);
    }

    [Fact]
    public void Claude_resolves_no_tier_for_a_model_the_table_does_not_carry()
    {
        // A raw claude model id (rather than one of the three recorded aliases) is exactly the shape
        // constraint 1's own escape hatch (#566) could forward unresolved -- this must still fail
        // closed, never guess at a nearby tier.
        Assert.False(DepthTierMapping.TryResolve("claude", "claude-opus-4-8", out var purpose));
        Assert.Equal(string.Empty, purpose);
    }

    [Fact]
    public void An_unrecognized_adapter_name_resolves_no_tier_even_for_a_claude_alias()
    {
        Assert.False(DepthTierMapping.TryResolve("dialogue", "opus", out var purpose));
        Assert.Equal(string.Empty, purpose);
    }

    [Fact]
    public void A_null_model_resolves_no_tier_regardless_of_adapter()
    {
        Assert.False(DepthTierMapping.TryResolve("claude", null, out var purpose));
        Assert.Equal(string.Empty, purpose);
    }

    [Fact]
    public void CanonicalWords_carries_exactly_the_three_words_the_registered_table_names()
    {
        Assert.Equal(["deep", "balanced", "fast"], DepthTierMapping.CanonicalWords);
    }
}
