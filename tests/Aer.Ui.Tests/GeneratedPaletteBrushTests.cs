using System.Collections.Generic;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Xunit;

namespace Aer.Ui.Tests;

/// <summary>
/// #1065: the semantic <c>Color.*</c> brushes the views bind by name are generated one instance PER
/// theme variant, inside each <c>ThemeDictionary</c>. A single shared brush cannot hold both a light
/// and a dark colour at once, so a dark window would silently render the light colour — and that is
/// invisible whenever the app variant and the window variant agree, which is every screenshot and
/// the running app itself. So it is a test, not a look. #209's <see cref="AccentButtonThemeTests"/>
/// covers <c>Color.Accent</c> end-to-end through a window; this covers the whole
/// <c>SemanticBrushAliases</c> set. <c>Color.SurfaceSubtle</c> matters most here: it is the you-turn
/// fill whose light/dark distinctness the transcript styling (#1064) depends on.
/// </summary>
public class GeneratedPaletteBrushTests
{
    // Mirror of TokenGenerator.SemanticBrushAliases: brush key -> the generated <Color> it must equal
    // in each variant. That array lives in a different assembly (tools/) and cannot be imported, so a
    // new alias added there without a row here is the staleness this comment flags — the test would
    // simply stop covering it, never go wrongly green.
    public static IEnumerable<object[]> Aliases() => new[]
    {
        new object[] { "Color.Accent", "BrandAccentColor" },
        new object[] { "Color.OnAccent", "BrandOnAccentColor" },
        new object[] { "Color.Background", "SurfaceGroundColor" },
        new object[] { "Color.SurfaceRaised", "SurfaceRaisedColor" },
        new object[] { "Color.SurfaceSubtle", "SurfaceSunkColor" },
        new object[] { "Color.Border", "SurfaceRuleColor" },
        new object[] { "Color.Text", "TextPrimaryColor" },
        new object[] { "Color.TextSecondary", "TextSecondaryColor" },
    };

    [AvaloniaTheory]
    [MemberData(nameof(Aliases))]
    public void Semantic_brush_resolves_to_its_own_variants_colour(string brushKey, string colorKey)
    {
        var app = Application.Current!;

        var lightColor = Resolve<Color>(app, colorKey, ThemeVariant.Light);
        var darkColor = Resolve<Color>(app, colorKey, ThemeVariant.Dark);
        var lightBrush = Resolve<ISolidColorBrush>(app, brushKey, ThemeVariant.Light);
        var darkBrush = Resolve<ISolidColorBrush>(app, brushKey, ThemeVariant.Dark);

        // Each brush carries its OWN variant's colour. A single brush shared across variants would
        // hold one colour and fail one of these — exactly the defect the first single-brush attempt
        // showed on Color.Accent (the dark window rendered the light teal).
        Assert.Equal(lightColor, lightBrush.Color);
        Assert.Equal(darkColor, darkBrush.Color);

        // Two distinct instances: for all seven keys the light and dark values differ, so a shared
        // brush cannot satisfy both above and this together.
        Assert.NotEqual(lightBrush.Color, darkBrush.Color);
    }

    public static IEnumerable<object[]> StatusAliases() => new[]
    {
        new object[] { "Status.Running", "StatusWorkingColor" },
        new object[] { "Status.NeedsYou", "StatusNeedsInputColor" },
        new object[] { "Status.Succeeded", "StatusFinishedColor" },
        new object[] { "Status.Failed", "StatusFailedColor" },
        new object[] { "Status.Idle", "StatusIdleColor" },
        new object[] { "Status.Stale", "StatusUnavailableColor" },
    };

    // Discrimination limit (#1135 review): the quiet states share one muted colour by design, so
    // for Status.Idle/Status.Stale this cannot tell "aliased from the right token" apart from
    // "aliased from a same-valued sibling" — the generator's alias table stays the eyeball check
    // there. The four loud keys each map a unique value, where this does discriminate.
    [AvaloniaTheory]
    [MemberData(nameof(StatusAliases))]
    public void Status_brush_resolves_to_its_own_variants_colour(string brushKey, string colorKey)
    {
        var app = Application.Current!;

        var lightColor = Resolve<Color>(app, colorKey, ThemeVariant.Light);
        var darkColor = Resolve<Color>(app, colorKey, ThemeVariant.Dark);
        var lightBrush = Resolve<ISolidColorBrush>(app, brushKey, ThemeVariant.Light);
        var darkBrush = Resolve<ISolidColorBrush>(app, brushKey, ThemeVariant.Dark);

        Assert.Equal(lightColor, lightBrush.Color);
        Assert.Equal(darkColor, darkBrush.Color);
        Assert.NotEqual(lightBrush.Color, darkBrush.Color);
    }

    private static T Resolve<T>(Application app, object key, ThemeVariant variant)
    {
        Assert.True(app.TryGetResource(key, variant, out var value), $"{key} unresolved under {variant}");
        return Assert.IsAssignableFrom<T>(value);
    }
}
