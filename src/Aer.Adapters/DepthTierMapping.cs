namespace Aer.Adapters;

/// <summary>
/// 0023's canonical model-purpose vocabulary (deep/balanced/fast) resolved from a worker's vendor +
/// model pair (#1339, decision 0058's scope ruling 4). The mirror image of
/// <see cref="EffortTierMapping"/>: effort's canonical word travels forward from a human-authored
/// config value to a vendor flag at dispatch, while depth travels the other way — a vendor's own
/// model string, already recorded on the worker binding, resolves backward to the canonical word a
/// render surface is allowed to see. Both directions share the same rule (0023 constraint 1): the
/// mapping lives only here, stated once, from the table registered in <c>docs/vendor-capabilities.md</c>'s
/// "The canonical model-purpose mapping" — restated in this file's data, never in prose elsewhere.
/// </summary>
/// <remarks>
/// <b>No fallback tier, ever.</b> #1330 registered <c>claude</c>'s three aliases and deliberately left
/// <c>agy</c>'s entire column unrecorded: the corpus's single generic label could not be honestly
/// bridged onto agy's eleven versioned, effort-suffixed catalogue entries. <see cref="TryResolve"/>
/// therefore returns false for every agy model, every unrecognized claude model string, and every
/// unrecognized adapter name — never a guessed tier. A caller reads false as "this worker's depth mark
/// renders nothing," exactly the absence rule <c>EffortTierParsing</c> already applies to effort.
/// </remarks>
public static class DepthTierMapping
{
    public const string Deep = "deep";
    public const string Balanced = "balanced";
    public const string Fast = "fast";

    /// <summary>Every canonical depth word, in the order <c>docs/vendor-capabilities.md</c>'s table names them.</summary>
    public static readonly IReadOnlyList<string> CanonicalWords = [Deep, Balanced, Fast];

    /// <summary>
    /// <c>claude</c> ships no model-list subcommand; its three named aliases
    /// (<see cref="ClaudeWorkerAdapter.ModelAliases"/>) are the stable interface, each landing on a
    /// distinct canonical purpose — no collapse, per <c>docs/vendor-capabilities.md</c>'s "`claude` —
    /// fully placed, no collapse" note.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> ClaudeByModel =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["opus"] = Deep,
            ["sonnet"] = Balanced,
            ["haiku"] = Fast,
        };

    /// <summary>
    /// Deliberately empty. <c>agy models</c> is a real, machine-readable 11-entry catalogue, but no
    /// entry of it is placed into a purpose here — see <c>docs/vendor-capabilities.md</c>'s "`agy` —
    /// model set recorded, purpose column left open" for why bridging it would be exactly the guess
    /// this record's own discipline forbids. Left for a human-run measurement, not this slice.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> AgyByModel =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// True and <paramref name="purpose"/> set only when <paramref name="adapterName"/> is a vendor
    /// this mapping knows and <paramref name="model"/> is one of that vendor's rows in
    /// <c>docs/vendor-capabilities.md</c>'s canonical model-purpose table. False for a null model, an
    /// unrecognized adapter name, or a model that table does not carry for that adapter (every agy
    /// model today) — each of those is an absence the caller must render as no mark, never a default.
    /// </summary>
    public static bool TryResolve(string? adapterName, string? model, out string purpose)
    {
        if (model is not null)
        {
            var byModel = adapterName switch
            {
                "claude" => ClaudeByModel,
                "agy" => AgyByModel,
                _ => null,
            };

            if (byModel is not null && byModel.TryGetValue(model, out var resolved))
            {
                purpose = resolved;
                return true;
            }
        }

        purpose = "";
        return false;
    }
}
