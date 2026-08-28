using Aer.Ui.Core;
using Avalonia;
using Avalonia.Styling;

namespace Aer.Ui;

/// <summary>
/// The Settings → Appearance theme choice (#1068), mapped to an Avalonia <see cref="ThemeVariant"/>.
/// The stored value is one of <see cref="ThemeNames"/> (persisted by
/// <see cref="Aer.RoomSession.LocalUiConfigurationStore"/>); <see cref="ThemeNames.System"/> — and any
/// unrecognised or missing value — resolves to <see cref="ThemeVariant.Default"/>, which is what makes
/// the app follow the OS, exactly as it did before an in-app control existed.
/// </summary>
public static class AppearanceTheme
{
    public static ThemeVariant ToVariant(string? theme) => theme switch
    {
        ThemeNames.Light => ThemeVariant.Light,
        ThemeNames.Dark => ThemeVariant.Dark,
        _ => ThemeVariant.Default,
    };

    /// <summary>Applies <paramref name="theme"/> to the running application immediately (no restart).</summary>
    public static void Apply(string? theme)
    {
        if (Application.Current is { } app)
        {
            app.RequestedThemeVariant = ToVariant(theme);
        }
    }
}
