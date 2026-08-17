using Avalonia.Media;

namespace Aer.Ui.Controls;

/// <summary>
/// One drawable position of a depth/effort meter (#1318, decision 0058's scope ruling) — the
/// hand-drawn geometry at that position (the same shape at every tier within its family; see
/// <c>Icons.axaml</c>'s <c>Icon.DepthStep*</c>/<c>Icon.EffortStep*</c>) and whether the bound tier
/// fills it. Produced only by <c>Converters/TierMeterConverters.cs</c>, never authored directly.
/// </summary>
public sealed record TierMeterStep(Geometry Geometry, bool IsFilled);
