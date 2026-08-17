namespace Aer.Ui.Tests;

/// <summary>
/// #1318 (decision 0058's scope ruling 2): the chip renders absence as nothing for a null, raw, or
/// unmapped tier — no mark, no empty frame, no reserved outline. <see cref="EffortTierParsing"/> is
/// where that boundary is drawn on the UI side: it is the UI's ONLY map, canonical-word→mark
/// parameter, so a test exercising only one of null/raw/unmapped could pass against an
/// implementation broken on the other two (e.g. one that treats every non-null string as valid).
/// </summary>
public class EffortTierParsingTests
{
    [Theory]
    [InlineData("quick", AerEffortTier.Quick)]
    [InlineData("standard", AerEffortTier.Standard)]
    [InlineData("careful", AerEffortTier.Careful)]
    [InlineData("exhaustive", AerEffortTier.Exhaustive)]
    public void Parses_every_canonical_word_to_its_own_tier(string canonical, AerEffortTier expected)
    {
        Assert.True(EffortTierParsing.TryParseEffort(canonical, out var tier));
        Assert.Equal(expected, tier);
    }

    [Fact]
    public void A_null_effort_fails_the_parse()
    {
        Assert.False(EffortTierParsing.TryParseEffort(null, out _));
    }

    [Theory]
    [InlineData("high")]
    [InlineData("low")]
    [InlineData("medium")]
    [InlineData("xhigh")]
    [InlineData("max")]
    public void A_raw_vendor_value_fails_the_parse(string raw)
    {
        // These are real claude/agy --effort values -- exactly the strings the #566 escape hatch
        // still lets a binding carry. Parsing one as though it were canonical would fabricate a tier
        // the data does not support.
        Assert.False(EffortTierParsing.TryParseEffort(raw, out _));
    }

    [Theory]
    [InlineData("Quick")]
    [InlineData("QUICK")]
    [InlineData("bogus")]
    [InlineData("")]
    public void An_unmapped_value_fails_the_parse(string unmapped)
    {
        Assert.False(EffortTierParsing.TryParseEffort(unmapped, out _));
    }

    [Theory]
    [InlineData("fast", AerDepthTier.Fast)]
    [InlineData("balanced", AerDepthTier.Balanced)]
    [InlineData("deep", AerDepthTier.Deep)]
    public void Depth_parses_every_canonical_word_to_its_own_tier(string canonical, AerDepthTier expected)
    {
        Assert.True(EffortTierParsing.TryParseDepth(canonical, out var tier));
        Assert.Equal(expected, tier);
    }

    [Fact]
    public void Depth_a_null_value_fails_the_parse()
    {
        Assert.False(EffortTierParsing.TryParseDepth(null, out _));
    }

    [Fact]
    public void Depth_an_unmapped_value_fails_the_parse()
    {
        Assert.False(EffortTierParsing.TryParseDepth("bogus", out _));
    }
}
