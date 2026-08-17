namespace Aer.Adapters.Tests;

/// <summary>
/// #1318: pins <see cref="EffortTierMapping"/>'s three-way split (canonical translated, a vendor's
/// own value passed through, anything else refused) against the table its own remarks cite. See
/// that type's doc comment for why the refusal case matters at all.
/// </summary>
public class EffortTierMappingTests
{
    [Theory]
    [InlineData("quick", "low")]
    [InlineData("standard", "medium")]
    [InlineData("careful", "high")]
    [InlineData("exhaustive", "max")]
    public void Claude_maps_every_canonical_word_to_its_documented_value(string canonical, string expectedRaw)
    {
        Assert.Equal(expectedRaw, EffortTierMapping.ResolveForClaude(canonical));
    }

    [Theory]
    [InlineData("quick", "low")]
    [InlineData("standard", "medium")]
    [InlineData("careful", "high")]
    // See EffortTierMapping's own remarks for why exhaustive lands here too.
    [InlineData("exhaustive", "high")]
    public void Agy_maps_every_canonical_word_to_its_documented_value(string canonical, string expectedRaw)
    {
        Assert.Equal(expectedRaw, EffortTierMapping.ResolveForAgy(canonical));
    }

    [Theory]
    [InlineData("low")]
    [InlineData("medium")]
    [InlineData("high")]
    [InlineData("xhigh")]
    [InlineData("max")]
    public void Claude_passes_its_own_raw_values_through_untouched(string raw)
    {
        Assert.Equal(raw, EffortTierMapping.ResolveForClaude(raw));
    }

    [Theory]
    [InlineData("low")]
    [InlineData("medium")]
    [InlineData("high")]
    public void Agy_passes_its_own_raw_values_through_untouched(string raw)
    {
        Assert.Equal(raw, EffortTierMapping.ResolveForAgy(raw));
    }

    // --- fail closed: an unrecognized word is rejected, never forwarded ---

    [Fact]
    public void Claude_rejects_a_word_that_is_neither_canonical_nor_one_of_its_own_raw_values()
    {
        var ex = Assert.Throws<IncoherentVendorEffortException>(
            () => EffortTierMapping.ResolveForClaude("__not-a-real-value__"));
        Assert.Contains("__not-a-real-value__", ex.Message);
    }

    [Fact]
    public void Agy_rejects_a_word_that_is_neither_canonical_nor_one_of_its_own_raw_values()
    {
        // xhigh is a genuine claude raw value, but not agy's -- and not a canonical word either, so
        // agy must still refuse it rather than forward a value it cannot honour.
        Assert.Throws<IncoherentVendorEffortException>(() => EffortTierMapping.ResolveForAgy("xhigh"));
    }

    [Fact]
    public void Claude_rejects_a_canonical_word_forwarded_by_a_bug_as_though_it_were_raw()
    {
        // Guards the exact failure mode the fail-closed rule exists for: if some future caller ever
        // bypassed ResolveForClaude and handed the literal canonical word straight to claude, that
        // string is not one of claude's own accepted values either -- ResolveForClaude itself must
        // never be the thing that lets it through unresolved.
        Assert.Equal("low", EffortTierMapping.ResolveForClaude(EffortTierMapping.Quick));
        Assert.NotEqual(EffortTierMapping.Quick, EffortTierMapping.ResolveForClaude(EffortTierMapping.Quick));
    }

    [Fact]
    public void IsCanonical_recognizes_exactly_the_four_words_0023_names()
    {
        foreach (var word in EffortTierMapping.CanonicalWords)
        {
            Assert.True(EffortTierMapping.IsCanonical(word));
        }

        Assert.False(EffortTierMapping.IsCanonical("high"));
        Assert.False(EffortTierMapping.IsCanonical("xhigh"));
        Assert.False(EffortTierMapping.IsCanonical(string.Empty));
    }
}
