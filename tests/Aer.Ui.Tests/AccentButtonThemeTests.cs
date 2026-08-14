using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;

namespace Aer.Ui.Tests;

/// <summary>
/// Regression test for issue #209: FluentTheme reserves the class name "accent" on Button for its
/// own <c>SystemAccentColor</c>-based styling (the Windows OS accent color — red or blue depending
/// on the user's Windows settings, not the app's own teal <c>Color.Accent</c> token), and that
/// built-in styling won FluentTheme's specificity fight against setters placed directly on
/// <c>Button.accent</c> under some theme-variant conditions. The fix (<see cref="MainWindow"/>'s
/// <c>Base.axaml</c>) targets <c>Button.accent /template/ ContentPresenter</c> instead. This test
/// forces <see cref="ThemeVariant.Light"/> (the variant screenshots showed rendering the OS red
/// accent before the fix) and asserts the rendered background is the app's own <c>Color.Accent</c>
/// hex — now the generated teal from GeneratedTokens.axaml (design/tokens.json <c>brand.accent</c>),
/// not any system color. Each variant is asserted against its own dictionary's value, which is why
/// the accent brush is emitted once per ThemeDictionary rather than as one shared brush.
/// </summary>
public class AccentButtonThemeTests
{
    [AvaloniaFact]
    public void Accent_button_background_resolves_to_the_apps_color_token_not_the_os_accent_color_in_light_theme()
    {
        var window = new MainWindow { RequestedThemeVariant = ThemeVariant.Light };
        window.Show();

        // Any Classes="accent" button answers this — #1215 retired the header Run button this used
        // to reach for, and the token resolution under test is the theme's, not that button's.
        var accentButton = window.FindViewControl<Button>("ChatSendButton")!;
        accentButton.ApplyTemplate();

        var contentPresenter = accentButton.GetVisualDescendants().OfType<ContentPresenter>().First();

        // GeneratedTokens.axaml's Light dictionary Color.Accent (brand.accent) — not any SystemAccentColor brush.
        Assert.Equal(Color.Parse("#3F8C87"), ((ISolidColorBrush)contentPresenter.Background!).Color);
        Assert.Equal(Color.Parse("#3F8C87"), ((ISolidColorBrush)contentPresenter.BorderBrush!).Color);
    }

    [AvaloniaFact]
    public void Accent_button_background_resolves_to_the_apps_color_token_not_the_os_accent_color_in_dark_theme()
    {
        var window = new MainWindow { RequestedThemeVariant = ThemeVariant.Dark };
        window.Show();

        // Any Classes="accent" button answers this — #1215 retired the header Run button this used
        // to reach for, and the token resolution under test is the theme's, not that button's.
        var accentButton = window.FindViewControl<Button>("ChatSendButton")!;
        accentButton.ApplyTemplate();

        var contentPresenter = accentButton.GetVisualDescendants().OfType<ContentPresenter>().First();

        // GeneratedTokens.axaml's Dark dictionary Color.Accent (brand.accent).
        Assert.Equal(Color.Parse("#5FB3AD"), ((ISolidColorBrush)contentPresenter.Background!).Color);
        Assert.Equal(Color.Parse("#5FB3AD"), ((ISolidColorBrush)contentPresenter.BorderBrush!).Color);
    }
}
