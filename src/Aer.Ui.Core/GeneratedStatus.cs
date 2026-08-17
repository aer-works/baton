// GENERATED FILE — DO NOT EDIT.
// Source: design/tokens.json
// Regenerate: pixi run tokens
//
// Hand edits are reverted by the next regeneration and fail CI in the meantime
// (Aer.Architecture.Tests). Change the token file instead.

namespace Aer.Ui.Core;

/// <summary>The five states from #334's split — the vocabulary every status-rendering surface uses.</summary>
public enum AerStatus
{
    Idle,
    Working,
    NeedsInput,
    ReadyForReview,
    Finished,
    Failed,
    Cancelled,
    Stopped,
    Queued,
    OutOfPlan,
    Unavailable,
    WaitingToStart,
    WaitingOnLock,
}

/// <summary>
/// Decision 0006: a status must never be conveyed by hue alone, so every state carries a mark
/// and a word. Any surface that renders <see cref="ColorResourceKey"/> must also render
/// <see cref="MarkResourceKey"/> and <see cref="Label"/> — colour is the third channel, never
/// the only one.
/// </summary>
public static class AerStatusPresentation
{
    /// <summary>
    /// The resource key of the <c>StreamGeometry</c> that draws this status's mark, defined in
    /// <c>Aer.Ui/Theme/Icons.axaml</c>. A shape rather than a character: the shipped faces do not
    /// cover the codepoints originally chosen, and between them carry no checkmark and no cross
    /// at all, so a text glyph cannot express this set on both platforms (#458).
    /// </summary>
    public static string MarkResourceKey(this AerStatus status) => status switch
    {
        AerStatus.Idle => "Icon.Dot",
        AerStatus.Working => "Icon.Ring",
        AerStatus.NeedsInput => "Icon.Bubble",
        AerStatus.ReadyForReview => "Icon.Eye",
        AerStatus.Finished => "Icon.Check",
        AerStatus.Failed => "Icon.Cross",
        AerStatus.Cancelled => "Icon.Dash",
        AerStatus.Stopped => "Icon.Square",
        AerStatus.Queued => "Icon.Ellipsis",
        AerStatus.OutOfPlan => "Icon.Ellipsis",
        AerStatus.Unavailable => "Icon.Slashed",
        AerStatus.WaitingToStart => "Icon.Clock",
        AerStatus.WaitingOnLock => "Icon.Lock",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unmapped status."),
    };

    /// <summary>The status in words — rendered alongside the mark, never replaced by it.</summary>
    public static string Label(this AerStatus status) => status switch
    {
        AerStatus.Idle => "Idle",
        AerStatus.Working => "Working",
        AerStatus.NeedsInput => "Needs input",
        AerStatus.ReadyForReview => "Ready for review",
        AerStatus.Finished => "Finished",
        AerStatus.Failed => "Failed",
        AerStatus.Cancelled => "Cancelled",
        AerStatus.Stopped => "Stopped",
        AerStatus.Queued => "Queued",
        AerStatus.OutOfPlan => "Out of plan",
        AerStatus.Unavailable => "Unavailable",
        AerStatus.WaitingToStart => "Waiting to start",
        AerStatus.WaitingOnLock => "Waiting on lock",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unmapped status."),
    };

    /// <summary>
    /// The key of this status's <c>Color</c> in the generated theme dictionaries. A colour, not a
    /// brush: it resolves per theme variant, so a consumer must look it up against the live
    /// variant rather than through the theme-oblivious overload (the washed-out DAG boxes of
    /// #204/#205 were exactly that mistake).
    /// </summary>
    public static string ColorResourceKey(this AerStatus status) => status switch
    {
        AerStatus.Idle => "StatusIdleColor",
        AerStatus.Working => "StatusWorkingColor",
        AerStatus.NeedsInput => "StatusNeedsInputColor",
        AerStatus.ReadyForReview => "StatusReadyForReviewColor",
        AerStatus.Finished => "StatusFinishedColor",
        AerStatus.Failed => "StatusFailedColor",
        AerStatus.Cancelled => "StatusCancelledColor",
        AerStatus.Stopped => "StatusStoppedColor",
        AerStatus.Queued => "StatusQueuedColor",
        AerStatus.OutOfPlan => "StatusOutOfPlanColor",
        AerStatus.Unavailable => "StatusUnavailableColor",
        AerStatus.WaitingToStart => "StatusWaitingToStartColor",
        AerStatus.WaitingOnLock => "StatusWaitingOnLockColor",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unmapped status."),
    };

    /// <summary>
    /// Whether this status's mark is painted solid rather than stroked. Stated in the token file
    /// so both toolkits obey one instruction: the Avalonia call sites previously set only
    /// <c>Stroke</c> while Flutter's painter filled the same closed path, so one status drew as an
    /// outline on desktop and a solid on mobile (#461).
    /// </summary>
    public static bool MarkIsFilled(this AerStatus status) => status switch
    {
        AerStatus.Idle => true,
        AerStatus.Working => false,
        AerStatus.NeedsInput => true,
        AerStatus.ReadyForReview => false,
        AerStatus.Finished => false,
        AerStatus.Failed => false,
        AerStatus.Cancelled => false,
        AerStatus.Stopped => false,
        AerStatus.Queued => true,
        AerStatus.OutOfPlan => true,
        AerStatus.Unavailable => false,
        AerStatus.WaitingToStart => false,
        AerStatus.WaitingOnLock => false,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unmapped status."),
    };
}


/// <summary>#1318's depth meter tiers — <c>design/tokens.json</c> is the source.</summary>
public enum AerDepthTier
{
    Fast,
    Balanced,
    Deep,
}

/// <summary>Fill count and label per <see cref="AerDepthTier"/> tier.</summary>
public static class AerDepthTierPresentation
{
    public const int TotalSteps = 3;

    public static int FilledSteps(this AerDepthTier tier) => tier switch
    {
        AerDepthTier.Fast => 1,
        AerDepthTier.Balanced => 2,
        AerDepthTier.Deep => 3,
        _ => throw new ArgumentOutOfRangeException(nameof(tier), tier, "Unmapped AerDepthTier."),
    };

    public static string Label(this AerDepthTier tier) => tier switch
    {
        AerDepthTier.Fast => "Fast",
        AerDepthTier.Balanced => "Balanced",
        AerDepthTier.Deep => "Deep",
        _ => throw new ArgumentOutOfRangeException(nameof(tier), tier, "Unmapped AerDepthTier."),
    };
}

/// <summary>#1318's effort meter tiers — <c>design/tokens.json</c> is the source.</summary>
public enum AerEffortTier
{
    Quick,
    Standard,
    Careful,
    Exhaustive,
}

/// <summary>Fill count and label per <see cref="AerEffortTier"/> tier.</summary>
public static class AerEffortTierPresentation
{
    public const int TotalSteps = 4;

    public static int FilledSteps(this AerEffortTier tier) => tier switch
    {
        AerEffortTier.Quick => 1,
        AerEffortTier.Standard => 2,
        AerEffortTier.Careful => 3,
        AerEffortTier.Exhaustive => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(tier), tier, "Unmapped AerEffortTier."),
    };

    public static string Label(this AerEffortTier tier) => tier switch
    {
        AerEffortTier.Quick => "Quick",
        AerEffortTier.Standard => "Standard",
        AerEffortTier.Careful => "Careful",
        AerEffortTier.Exhaustive => "Exhaustive",
        _ => throw new ArgumentOutOfRangeException(nameof(tier), tier, "Unmapped AerEffortTier."),
    };
}
