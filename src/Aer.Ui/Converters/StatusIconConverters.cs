using System.Globalization;
using Aer.Flow.Domain;
using Aer.RoomSession;
using Aer.Ui.Core;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Aer.Ui.Converters;

/// <summary>
/// Post-M19 design review (issue #206): design-language.md's status→icon table, materialized as
/// one mapping every status-rendering surface goes through, so the same status always draws the
/// same glyph ("color + icon + word, never color alone" — <see cref="RoomCardViewModel"/>'s own
/// comment named this intent; nothing consumed it until now).
/// </summary>
internal static class StatusIconMap
{
    /// <summary>
    /// #458: <see cref="StepStatus.Paused"/> drew <c>Icon.Dot</c> — the same mark as Pending and
    /// Cancelled — so the one state that means "this is waiting on you" was shaped identically to
    /// "nothing is happening here", leaving colour as the only difference. That is the failure
    /// decision 0006's rule exists to prevent, and it was live.
    /// </summary>
    /// <remarks>
    /// <see cref="StepStatus"/> alone cannot distinguish a pause awaiting a *reply* from one awaiting
    /// a *review* — that lives in the step's <see cref="Aer.Flow.Domain.PausePointKind"/>, which this
    /// converter is not given. It therefore draws the reply mark for both, which is right for the
    /// common case and no worse than the single dot it replaces. #336 replaces this mapping wholesale
    /// with <c>AerStatus</c>, which carries the distinction.
    /// </remarks>
    public static string GeometryKeyFor(StepStatus status) => status switch
    {
        StepStatus.Running => "Icon.Ring",
        StepStatus.Succeeded => "Icon.Check",
        StepStatus.Failed or StepStatus.Rejected => "Icon.Cross",
        StepStatus.Paused => "Icon.Bubble",
        // #461: cancelled is no longer "idle". Stopping something on purpose is an outcome, and
        // rendering it as the pending dot said nothing happened.
        StepStatus.Cancelled => "Icon.Dash",
        StepStatus.Pending => "Icon.Dot",
        // #616: Pending is named above and the discard throws — a new StepStatus member must
        // choose its mark deliberately, never inherit the not-started dot silently.
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unmapped step status."),
    };

    /// <summary>
    /// Whether a status's mark is painted solid rather than stroked (#461). Delegates to the
    /// generated table so the fill decision is stated once, in <c>design/tokens.json</c> — the call
    /// sites used to set <c>Stroke</c> and never <c>Fill</c>, so a mark authored as a solid on mobile
    /// rendered as an outline here.
    /// </summary>
    public static bool IsFilled(string geometryKey) =>
        Enum.GetValues<AerStatus>().Any(status => status.MarkResourceKey() == geometryKey && status.MarkIsFilled());

    public static string ColorKeyFor(StepStatus status) => status switch
    {
        StepStatus.Running => "Status.Working",
        StepStatus.Succeeded => "Status.Finished",
        StepStatus.Failed or StepStatus.Rejected => "Status.Failed",
        StepStatus.Paused => "Status.NeedsInput",
        StepStatus.Cancelled => "Status.Idle",
        StepStatus.Pending => "Status.Idle",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unmapped step status."), // #616
    };

    /// <summary>Same #458 correction as the <see cref="StepStatus"/> overload above: NeedsYou was a dot.</summary>
    public static string GeometryKeyFor(RoomCardStatus status) => status switch
    {
        RoomCardStatus.Running => "Icon.Ring",
        RoomCardStatus.NeedsYou => "Icon.Bubble",
        RoomCardStatus.Finished => "Icon.Check",
        RoomCardStatus.Failed => "Icon.Cross",
        RoomCardStatus.Cancelled => "Icon.Dash",
        // #461: the stale-list state gets its own mark. It previously borrowed Icon.Refresh, the
        // Retry *action*'s glyph — a state wearing an action's icon invites clicking it.
        // #616: Unavailable is named and the discard throws — a new RoomCardStatus member must
        // not silently render as the stale-list state.
        RoomCardStatus.Unavailable => "Icon.Slashed",
        // 0026 (#1116): waiting on a plan reset, not stopped (Dash would claim Cancelled's "you
        // stopped it") and not broken — the ellipsis is the honest "more to come, later" mark.
        RoomCardStatus.OutOfPlan => "Icon.Ellipsis",
        // #1219: a square outline, the one hard-edged silhouette in the set. Not Dash, which is
        // Cancelled's "you stopped it" — nobody stopped this one, its process died.
        RoomCardStatus.Stopped => "Icon.Square",
        // #1296: distinct from Icon.Ellipsis (Queued), which is a step-level mark -- see design/
        // tokens.json's status prose for why the two coexist.
        RoomCardStatus.WaitingToStart => "Icon.Clock",
        // #1299: a padlock, distinct from Icon.Ellipsis (OutOfPlan) -- see design/tokens.json's
        // status prose for why the two must not share a mark (StatusMarkMappingTests pins this).
        RoomCardStatus.WaitingOnLock => "Icon.Lock",
        // #616 made this throw so a new member cannot silently render as some other state. Worth
        // knowing what that actually buys: driving the app for #1219 showed a genuinely missing
        // mapping as an *empty space* in the switcher rather than as a crash, because this runs
        // inside a binding and Avalonia swallows what a converter throws. The throw still stops the
        // wrong mark being drawn; it does not make a missing one loud. That is what the eye is for.
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unmapped card status."),
    };

    public static string ColorKeyFor(RoomCardStatus status) => status switch
    {
        RoomCardStatus.Running => "Status.Working",
        RoomCardStatus.NeedsYou => "Status.NeedsInput",
        RoomCardStatus.Finished => "Status.Finished",
        RoomCardStatus.Failed => "Status.Failed",
        // Cancelled shares the muted brush rather than earning a hue: it is a quiet outcome, and
        // colouring it like a failure is exactly the alarm #461 exists to remove.
        RoomCardStatus.Cancelled => "Status.Idle",
        // Honest name for the unavailable state (#1140).
        RoomCardStatus.Unavailable => "Status.Unavailable",
        // 0026 §5/0018 band 4: a quiet wait, muted like Cancelled — the status text carries the
        // reset instant (or "reset unknown"), so the color never has to shout (register now names it #1140).
        RoomCardStatus.OutOfPlan => "Status.OutOfPlan",
        // #1219: quiet, for Cancelled's reason — a room whose process died is not an emergency, and
        // the mark and the word carry which of the quiet outcomes it is.
        RoomCardStatus.Stopped => "Status.Stopped",
        // #1296: same muted family as Cancelled/Stopped -- a full concurrency house is normal
        // operation, not a warning.
        RoomCardStatus.WaitingToStart => "Status.WaitingToStart",
        RoomCardStatus.WaitingOnLock => "Status.WaitingOnLock",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unmapped card status."), // #616
    };
}

/// <summary>
/// Status → the mark's fill brush, or <c>null</c> where the mark is stroked (#461). Paired with
/// <see cref="StatusToIconGeometryConverter"/> at every call site: a <c>Path</c> that sets only
/// <c>Stroke</c> renders a closed shape as an outline, so before this a mark authored solid drew
/// solid on the phone and hollow on the desktop. The decision now comes from the token file.
/// </summary>
public sealed class StatusToIconFillConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var geometryKey = value switch
        {
            StepStatus stepStatus => StatusIconMap.GeometryKeyFor(stepStatus),
            RoomCardStatus cardStatus => StatusIconMap.GeometryKeyFor(cardStatus),
            _ => null,
        };

        if (geometryKey is null || !StatusIconMap.IsFilled(geometryKey) || Application.Current is not { } app)
        {
            return null;
        }

        var colorKey = value switch
        {
            StepStatus stepStatus => StatusIconMap.ColorKeyFor(stepStatus),
            RoomCardStatus cardStatus => StatusIconMap.ColorKeyFor(cardStatus),
            _ => null,
        };

        // Same live-variant lookup as the stroke converter below — the theme-oblivious overload is
        // what washed out the DAG boxes in #204/#205.
        return colorKey is not null && app.TryFindResource(colorKey, app.ActualThemeVariant, out var resource) && resource is IBrush brush
            ? brush
            : null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Status → glyph. Icon geometries live outside <c>ThemeDictionaries</c> (one shape, not
/// themed), so an ordinary theme-oblivious resource lookup is safe here — unlike the brush lookup
/// below.</summary>
public sealed class StatusToIconGeometryConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch
        {
            StepStatus stepStatus => StatusIconMap.GeometryKeyFor(stepStatus),
            RoomCardStatus cardStatus => StatusIconMap.GeometryKeyFor(cardStatus),
            _ => null,
        };

        return key is null ? null : Application.Current?.FindResource(key);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Status → the same brush the DAG node/border for that status already uses. Explicit
/// <see cref="ThemeVariant"/> argument, not the theme-oblivious <c>FindResource(key)</c> overload
/// that caused the washed-out DAG boxes (issue #204/#205) — <c>Application.Current.ActualThemeVariant</c>
/// is the live variant the running app renders in.</summary>
public sealed class StatusToIconBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch
        {
            StepStatus stepStatus => StatusIconMap.ColorKeyFor(stepStatus),
            RoomCardStatus cardStatus => StatusIconMap.ColorKeyFor(cardStatus),
            _ => null,
        };

        if (key is null || Application.Current is not { } app)
        {
            return Brushes.Transparent;
        }

        return app.TryFindResource(key, app.ActualThemeVariant, out var resource) && resource is IBrush brush
            ? brush
            : Brushes.Transparent;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
