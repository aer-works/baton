namespace Aer.Ui.Core;

/// <summary>
/// The UI's ONLY map for the depth/effort marks (#1318, decision 0058's scope ruling 4):
/// canonical-word → mark parameter. Vocabulary to geometry, never vendor knowledge — this parses a
/// worker's already-forwarded effort string against exactly the four canonical words 0023 names, and
/// nothing else. A raw vendor value (e.g. <c>"high"</c>) or an unrecognized string is neither
/// canonical nor mapped here; it fails the parse, and the chip renders no mark for it (ruling 2) —
/// the same absence rule a null value gets. <c>Aer.Adapters.EffortTierMapping</c> is the mirror image
/// of this type on the dispatch side (canonical → raw); the two never call into each other, since
/// <c>Aer.Ui.Core</c> must not reference <c>Aer.Adapters</c>' vendor-facing values (0023 constraint 1).
/// </summary>
public static class EffortTierParsing
{
    /// <summary>
    /// True and <paramref name="tier"/> set only when <paramref name="raw"/> is exactly one of the
    /// four canonical effort words. False for null, a raw vendor value, or anything else unmapped —
    /// each of those is an absence to the chip, never a fabricated tier.
    /// </summary>
    public static bool TryParseEffort(string? raw, out AerEffortTier tier)
    {
        switch (raw)
        {
            case "quick":
                tier = AerEffortTier.Quick;
                return true;
            case "standard":
                tier = AerEffortTier.Standard;
                return true;
            case "careful":
                tier = AerEffortTier.Careful;
                return true;
            case "exhaustive":
                tier = AerEffortTier.Exhaustive;
                return true;
            default:
                tier = default;
                return false;
        }
    }

    /// <summary>
    /// Depth's twin of <see cref="TryParseEffort"/>. Nothing produces one of these three words on a
    /// worker yet (#1330 owns the vendor-model→tier register) — this exists so the parse and the mark
    /// control are already exercised structurally, per the #1318 scope ruling's "ship the mechanism
    /// ahead of the producer" call.
    /// </summary>
    public static bool TryParseDepth(string? raw, out AerDepthTier tier)
    {
        switch (raw)
        {
            case "fast":
                tier = AerDepthTier.Fast;
                return true;
            case "balanced":
                tier = AerDepthTier.Balanced;
                return true;
            case "deep":
                tier = AerDepthTier.Deep;
                return true;
            default:
                tier = default;
                return false;
        }
    }
}
