using System.Globalization;
using Aer.Ui.Controls;
using Aer.Ui.Core;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Aer.Ui.Converters;

/// <summary>
/// <see cref="AerEffortTier"/>? → the ordered <see cref="TierMeterStep"/> list
/// <c>TierMeter.axaml</c>'s repeater draws (#1318, decision 0058's scope ruling). This is the whole
/// of the UI's canonical-word→mark-parameter map ruling 4 asks for: vocabulary to geometry, never
/// vendor knowledge — the typed <see cref="AerEffortTier"/> is already produced upstream by parsing a
/// worker's raw effort string against exactly the four canonical words (see
/// <c>Aer.Ui.Core.EffortTierParsing</c>), so this converter never sees a raw vendor value at all.
/// </summary>
/// <remarks>
/// Absence renders nothing (ruling 2): a value that is not an <see cref="AerEffortTier"/> — because
/// the binding is null, or the upstream parse rejected a raw or unmapped string — returns
/// <see langword="null"/>, and an <see cref="Avalonia.Controls.ItemsControl"/> bound to a null
/// <c>ItemsSource</c> draws no children. No mark, no empty frame, no reserved outline.
/// </remarks>
public sealed class EffortTierMeterStepsConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not AerEffortTier tier || Application.Current is not { } app)
        {
            return null;
        }

        var filledSteps = tier.FilledSteps();
        var steps = new List<TierMeterStep>(AerEffortTierPresentation.TotalSteps);
        for (var i = 1; i <= AerEffortTierPresentation.TotalSteps; i++)
        {
            if (app.FindResource($"Icon.EffortStep{i}") is Geometry geometry)
            {
                steps.Add(new TierMeterStep(geometry, i <= filledSteps));
            }
        }

        return steps;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Depth's twin of <see cref="EffortTierMeterStepsConverter"/> — see its own doc comment for the shared reasoning.</summary>
public sealed class DepthTierMeterStepsConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not AerDepthTier tier || Application.Current is not { } app)
        {
            return null;
        }

        var filledSteps = tier.FilledSteps();
        var steps = new List<TierMeterStep>(AerDepthTierPresentation.TotalSteps);
        for (var i = 1; i <= AerDepthTierPresentation.TotalSteps; i++)
        {
            if (app.FindResource($"Icon.DepthStep{i}") is Geometry geometry)
            {
                steps.Add(new TierMeterStep(geometry, i <= filledSteps));
            }
        }

        return steps;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
