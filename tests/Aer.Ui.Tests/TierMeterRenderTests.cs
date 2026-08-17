using System.Linq;
using Aer.Ui.Controls;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;
using ShapePath = Avalonia.Controls.Shapes.Path;

namespace Aer.Ui.Tests;

/// <summary>
/// #1318 second reader: <see cref="DesignTokenDriftTests.EveryDepthAndEffortStepIsDrawnByBothToolkits"/>
/// admits it cannot see whether <c>TierMeter</c> actually renders the ascending meter the geometry
/// describes — it only checks that each step's geometry key and mark widget exist. It could not have
/// caught the shipped defect: <c>Stretch="Uniform"</c> computes its scale/position from each step
/// geometry's OWN <c>Bounds</c> rather than the shared 16x16 authoring grid, so three differently-sized
/// steps sharing one canvas were each independently scaled to fill their own box — concentric circles,
/// nested rectangles, not an ascending meter. This test renders the real control and asserts what that
/// defect actually breaks: <see cref="Shape.RenderedGeometry"/> (the geometry Avalonia actually paints,
/// stretch transform included) must keep each step at its own authored position, not collapse every
/// step onto the same centre.
/// </summary>
public class TierMeterRenderTests
{
    [AvaloniaFact]
    public void DepthMeterSteps_RenderAtDistinctAuthoredPositions()
    {
        AssertStepsRenderAtDistinctPositions("Icon.DepthStep1", "Icon.DepthStep2", "Icon.DepthStep3");
    }

    [AvaloniaFact]
    public void EffortMeterSteps_RenderAtDistinctAuthoredPositions()
    {
        AssertStepsRenderAtDistinctPositions("Icon.EffortStep1", "Icon.EffortStep2", "Icon.EffortStep3", "Icon.EffortStep4");
    }

    private static void AssertStepsRenderAtDistinctPositions(params string[] geometryKeys)
    {
        var app = Application.Current!;
        var steps = geometryKeys
            .Select(key => app.FindResource(key) as Geometry ?? throw new InvalidOperationException($"Resource '{key}' is missing or not a Geometry."))
            .Select(geometry => new TierMeterStep(geometry, IsFilled: true))
            .ToList();

        var meter = new TierMeter { Steps = steps, Width = 16, Height = 16 };
        var window = new Window { Content = meter };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var paths = meter.GetVisualDescendants().OfType<ShapePath>().ToList();
        Assert.Equal(steps.Count, paths.Count);

        // The precise shape of the bug: Stretch="Uniform" computes its scale/translate transform from
        // the GEOMETRY'S OWN Bounds (not the shared 16x16 authoring grid), so RenderedGeometry.Bounds
        // (what Avalonia actually paints, transform included) no longer matches the geometry's own
        // authored Bounds -- each step gets independently rescaled/repositioned to fill its own box.
        // Stretch="None" applies no transform at all, so the two must be pixel-identical. This is
        // stronger than just checking the steps differ from EACH OTHER: depth's dots (same aspect
        // ratio, different radii) render exactly concentric under Uniform and are caught by a
        // distinctness check alone, but effort's bars (different aspect ratios per step) happen to
        // scale to different sizes under the same bug and would slip past a same-vs-different check
        // even though every one of them is still wrong.
        for (var i = 0; i < paths.Count; i++)
        {
            var authored = steps[i].Geometry.Bounds;
            var rendered = paths[i].RenderedGeometry!.Bounds;
            Assert.True(
                rendered == authored,
                $"Step {i + 1} ({geometryKeys[i]}) rendered at {rendered} but was authored at {authored} -- " +
                "it was rescaled/repositioned off the shared design grid instead of drawn at its own " +
                "absolute position (the Stretch=\"Uniform\" defect).");
        }

        // And the consequence that actually matters on screen: with positions preserved, the steps'
        // rendered bounds -- authored to ascend across the family -- must not all coincide.
        var bounds = paths.Select(p => p.RenderedGeometry!.Bounds).ToList();
        Assert.Equal(bounds.Count, bounds.Distinct().Count());
    }

    /// <summary>
    /// The second BLOCKER (#1318 second reader): a stroked unfilled step's interior gap was too fine
    /// a fraction of its own area to read as hollow at chip size, so every tier read as "all steps
    /// solid" regardless of fill. The fix is a muted solid fill, not a stroke -- this asserts the
    /// resolved <see cref="Shape.Opacity"/> actually differs between a filled and an unfilled step
    /// once styles apply, not just that the XAML says it should.
    /// </summary>
    [AvaloniaFact]
    public void UnfilledStep_ResolvesToAMutedOpacity_NotFullOpacityOrAStroke()
    {
        var app = Application.Current!;
        var geometry = (Geometry)app.FindResource("Icon.EffortStep1")!;
        var meter = new TierMeter
        {
            Steps = [new TierMeterStep(geometry, IsFilled: true), new TierMeterStep(geometry, IsFilled: false)],
            Width = 16,
            Height = 16,
        };
        var window = new Window { Content = meter };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var paths = meter.GetVisualDescendants().OfType<ShapePath>().ToList();
        Assert.Equal(2, paths.Count);

        var filled = paths[0];
        var unfilled = paths[1];

        // Never a stroke: the fix replaced the filled/stroke split with a filled/muted-fill split.
        Assert.Null(filled.Stroke);
        Assert.Null(unfilled.Stroke);
        Assert.NotNull(filled.Fill);
        Assert.NotNull(unfilled.Fill);

        Assert.Equal(1.0, filled.Opacity);
        // Roughly 30-35% of full weight (#1318 second reader's own number) -- and, critically, LESS
        // than filled, which is the whole discriminating channel this fix restores.
        Assert.True(unfilled.Opacity is >= 0.25 and <= 0.40, $"unfilled step opacity was {unfilled.Opacity}, expected roughly 0.30-0.35");
        Assert.True(unfilled.Opacity < filled.Opacity);
    }
}
