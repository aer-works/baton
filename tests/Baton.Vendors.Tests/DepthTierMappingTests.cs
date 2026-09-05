namespace Baton.Vendors.Tests;

/// <summary>
/// #1339's polarity set, extended by #1342: a claude-vendored worker on a recorded alias resolves its
/// tier; an agy-vendored worker on a placed catalogue id resolves its tier (one representative row per
/// arm of the placement rule, not every id); an unrecognised or retired model resolves no tier for
/// either vendor. See <see cref="DepthTierMapping"/>'s own doc comment for why the last case can never
/// be a happy-path oversight -- a test covering only "resolves" cannot fail against a producer that
/// forwards every model unconditionally.
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
    [InlineData("gemini-3.1-pro-high", "deep")]
    [InlineData("claude-opus-4-6-thinking", "deep")]
    [InlineData("gemini-3.8-flash-high", "balanced")]
    [InlineData("gemini-3.1-pro-low", "balanced")]
    [InlineData("claude-sonnet-4-6", "balanced")]
    [InlineData("gemini-3.8-flash-medium", "fast")]
    [InlineData("gemini-3.8-flash-low", "fast")]
    [InlineData("gpt-oss-120b-medium", "fast")]
    public void Agy_resolves_every_placed_catalogue_entry_to_its_documented_purpose(string model, string expectedPurpose)
    {
        // #1342: one row per arm of the placement rule docs/vendor-capabilities.md states, so a table
        // edit that moves an arm fails here rather than only in the doc.
        Assert.True(DepthTierMapping.TryResolve("agy", model, out var purpose));
        Assert.Equal(expectedPurpose, purpose);
    }

    [Theory]
    [InlineData("gemini-3.5-flash-high")]
    [InlineData("gemini-3.6-flash-thinking")]
    public void Agy_resolves_no_tier_for_an_id_the_table_does_not_carry(string model)
    {
        // A retired family (3.5 left the catalogue before 2026-09-05) and a never-catalogued suffix
        // must fail closed: the table is the placement, and an id outside it is not "probably fast".
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
