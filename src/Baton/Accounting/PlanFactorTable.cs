using System.Text.Json;
using System.Text.Json.Serialization;

namespace Baton.Accounting;

/// <summary>
/// What a plan-meter estimate could be resolved to. Three outcomes, never collapsed to a number:
/// <see cref="Estimated"/> (factors known), <see cref="Unknown"/> (a factor that APPLIES has no
/// measured value — e.g. a live discount window whose percent the operator does not know), and
/// <see cref="Unmeasured"/> (this vendor's meter has never been measured at all).
/// </summary>
/// <remarks>
/// <b>There is deliberately no "assume 1.0" fallback.</b> A missing factor silently defaulting to 1.0
/// produces a plausible number that looks exactly like a measured one, which is the failure mode
/// #1849 exists to avoid — it would let a promotional window the operator knows exists, but cannot
/// quantify, be reported as full price.
/// </remarks>
public enum PlanFactorStatus
{
    Estimated,
    Unknown,
    Unmeasured,
}

/// <summary>A per-dimension weighting applied to a plan-meter estimate, with its provenance.</summary>
public sealed record PlanDimensionWeight(
    [property: JsonPropertyName("factor")] decimal Factor,
    [property: JsonPropertyName("source")] string Source);

/// <summary>
/// A promotional or otherwise time-bounded adjustment to one model's plan-meter cost.
/// <see cref="Percent"/> is nullable ON PURPOSE: a window that is known to exist but whose size is
/// not known must evaluate to <see cref="PlanFactorStatus.Unknown"/>, not to no discount.
/// </summary>
public sealed record PlanDiscountWindow(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("effectiveFrom")] DateTime EffectiveFrom,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("effectiveTo")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    DateTime? EffectiveTo = null,
    [property: JsonPropertyName("percent")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    decimal? Percent = null);

/// <summary>One vendor's plan-meter factors. <see cref="Unmeasured"/> vendors carry no factors at all.</summary>
public sealed record PlanVendorFactors(
    [property: JsonPropertyName("unmeasured")] bool Unmeasured = false,
    [property: JsonPropertyName("dimensionWeights")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyDictionary<string, PlanDimensionWeight>? DimensionWeights = null,
    [property: JsonPropertyName("discountWindows")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<PlanDiscountWindow>? DiscountWindows = null);

/// <summary>The resolved factors for one (vendor, model, instant), or why they could not be resolved.</summary>
/// <param name="Weights">
/// Per-dimension multipliers to apply on top of list price. A dimension absent from this map is
/// weighted 1.0 — that IS the measured claim for an unlisted dimension (the plan meter charges it
/// like list price), distinct from <see cref="PlanFactorStatus.Unknown"/>, which says the whole
/// estimate is not resolvable.
/// </param>
public sealed record PlanFactorResolution(
    PlanFactorStatus Status,
    IReadOnlyDictionary<PriceDimension, decimal> Weights,
    decimal DiscountMultiplier = 1m);

/// <summary>
/// A repository-owned, versioned table of how a SUBSCRIPTION plan's meter is believed to weight token
/// dimensions, as distinct from what the vendor's API list price charges for them
/// (<see cref="PriceCatalog"/>). Both numbers appear on every row, each labelled; neither is an
/// invoice or a quota reading (#1849).
/// </summary>
/// <remarks>
/// The seeded content is operator-supplied and cited as such — including its confidence. The Anthropic
/// cache-read weight is an unverified operator measurement, and the Sonnet 5 discount window is
/// recorded with its percent ABSENT because the operator does not know it, which is what makes that
/// model's plan estimate resolve to <see cref="PlanFactorStatus.Unknown"/> rather than to full price.
/// Bump <see cref="Version"/> on every content change, same rule as <see cref="PriceCatalog"/>.
/// </remarks>
public sealed record PlanFactorTable(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("vendors")] IReadOnlyDictionary<string, PlanVendorFactors> Vendors)
{
    /// <summary>The table Baton ships with. See the type remarks for the provenance of each entry.</summary>
    public static PlanFactorTable Default { get; } = new(
        Id: "baton-plan-factors",
        Version: "2026-09-04.0",
        Vendors: new Dictionary<string, PlanVendorFactors>(StringComparer.OrdinalIgnoreCase)
        {
            ["claude"] = new(
                Unmeasured: false,
                DimensionWeights: new Dictionary<string, PlanDimensionWeight>(StringComparer.OrdinalIgnoreCase)
                {
                    ["cacheRead"] = new(0.10m, "operator measurement 2026-09-04, unverified"),
                },
                DiscountWindows:
                [
                    // Percent deliberately absent: the operator knows the window exists and does not
                    // know its size. Absent -> Unknown; a 1.0 here would report full price as measured.
                    new PlanDiscountWindow(
                        Model: "claude-sonnet-5",
                        EffectiveFrom: new DateTime(2026, 9, 4, 0, 0, 0, DateTimeKind.Utc),
                        Source: "operator, 2026-09-04 (#1849): promotional window known, percent unknown",
                        EffectiveTo: new DateTime(2026, 9, 18, 0, 0, 0, DateTimeKind.Utc)),
                ]),
            ["agy"] = new(Unmeasured: true),
        });

    /// <summary>
    /// Parses a table from JSON — the same explicit-document seam <see cref="PriceCatalog.Parse"/>
    /// provides, and for the same reason.
    /// </summary>
    public static PlanFactorTable Parse(string json)
    {
        ArgumentException.ThrowIfNullOrEmpty(json);
        return JsonSerializer.Deserialize<PlanFactorTable>(json)
            ?? throw new JsonException("A plan-factor table document deserialized to null.");
    }

    /// <summary>
    /// Resolves the factors in force for <paramref name="vendor"/>/<paramref name="model"/> at
    /// <paramref name="at"/>. An adapter this table has never heard of resolves to
    /// <see cref="PlanFactorStatus.Unmeasured"/> — the same answer as an explicitly-unmeasured vendor,
    /// because both mean "nobody has measured this meter", which is the honest reading of silence.
    /// </summary>
    public PlanFactorResolution Resolve(string? vendor, string? model, DateTime at)
    {
        var empty = new Dictionary<PriceDimension, decimal>();

        if (vendor is not { Length: > 0 }
            || !Vendors.TryGetValue(vendor, out var factors)
            || factors is null
            || factors.Unmeasured)
        {
            return new PlanFactorResolution(PlanFactorStatus.Unmeasured, empty);
        }

        foreach (var window in factors.DiscountWindows ?? [])
        {
            if (model is not { Length: > 0 }
                || !string.Equals(window.Model, model, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (window.EffectiveFrom > at || (window.EffectiveTo is { } to && at >= to))
            {
                continue;
            }

            if (window.Percent is not { } percent)
            {
                return new PlanFactorResolution(PlanFactorStatus.Unknown, empty);
            }

            return new PlanFactorResolution(
                PlanFactorStatus.Estimated, ReadWeights(factors), DiscountMultiplier: 1m - (percent / 100m));
        }

        return new PlanFactorResolution(PlanFactorStatus.Estimated, ReadWeights(factors));
    }

    private static IReadOnlyDictionary<PriceDimension, decimal> ReadWeights(PlanVendorFactors factors)
    {
        var weights = new Dictionary<PriceDimension, decimal>();
        foreach (var dimension in Enum.GetValues<PriceDimension>())
        {
            if (factors.DimensionWeights?.TryGetValue(PriceCatalog.DimensionKey(dimension), out var weight) == true
                && weight is not null)
            {
                weights[dimension] = weight.Factor;
            }
        }

        return weights;
    }
}
