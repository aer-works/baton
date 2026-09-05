namespace Baton.Vendors;

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
/// <c>agy</c>'s entire column unrecorded until an operator-run measurement placed it (#1342,
/// 2026-09-05). <see cref="TryResolve"/> returns false for every model string a table does not carry
/// (a retired agy id, a raw claude model id) and for every unrecognized adapter name — never a guessed
/// tier. A caller reads false as "this worker's depth mark
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
    /// <c>agy</c>'s catalogue placed by the operator-run measurement of 2026-09-05 (#1342), from the
    /// live <c>agy models</c> output of that day. The placement RULE is stated once, in
    /// <c>docs/vendor-capabilities.md</c>'s "`agy` — placed by family and effort"; this table is that
    /// rule applied to the fourteen ids the catalogue carried; the same paragraph says what happens
    /// to an id this table does not carry (nothing here guesses).
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> AgyByModel =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["gemini-3.1-pro-high"] = Deep,
            ["claude-opus-4-6-thinking"] = Deep,
            ["gemini-3.8-flash-high"] = Balanced,
            ["gemini-3.7-flash-high"] = Balanced,
            ["gemini-3.6-flash-high"] = Balanced,
            ["gemini-3.1-pro-low"] = Balanced,
            ["claude-sonnet-4-6"] = Balanced,
            ["gemini-3.8-flash-medium"] = Fast,
            ["gemini-3.8-flash-low"] = Fast,
            ["gemini-3.7-flash-medium"] = Fast,
            ["gemini-3.7-flash-low"] = Fast,
            ["gemini-3.6-flash-medium"] = Fast,
            ["gemini-3.6-flash-low"] = Fast,
            ["gpt-oss-120b-medium"] = Fast,
        };

    private static readonly IReadOnlyDictionary<string, string> CodexByModel =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["gpt-6-astra"] = Deep,
            ["gpt-5.6-sol"] = Deep,
            ["gpt-5.6-terra"] = Balanced,
            ["gpt-5.6-luna"] = Fast,
        };

    /// <summary>
    /// True and <paramref name="purpose"/> set only when <paramref name="adapterName"/> is a vendor
    /// this mapping knows and <paramref name="model"/> is one of that vendor's rows in
    /// <c>docs/vendor-capabilities.md</c>'s canonical model-purpose table. False for a null model, an
    /// unrecognized adapter name, or a model that table does not carry for that adapter (a retired or
    /// not-yet-placed agy id, a raw claude model id) — each of those is an absence the caller must
    /// render as no mark, never a default.
    /// </summary>
    public static bool TryResolve(string? adapterName, string? model, out string purpose)
    {
        if (model is not null)
        {
            var byModel = adapterName switch
            {
                "claude" => ClaudeByModel,
                "agy" => AgyByModel,
                "codex" => CodexByModel,
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
