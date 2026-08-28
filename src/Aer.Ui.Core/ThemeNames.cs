using Aer.RoomSession;

namespace Aer.Ui.Core;

/// <summary>
/// The three appearance-theme choices (Settings → Appearance, #1068), as the exact strings persisted
/// by <see cref="LocalUiConfigurationStore"/> and shown on the toggle. Defined here in the toolkit-free
/// core so the ViewModel's selected-state and the Avalonia-side <c>AppearanceTheme</c> mapping share one
/// spelling rather than each carrying its own literals. <see cref="System"/> means "follow the OS".
/// </summary>
public static class ThemeNames
{
    public const string Light = "Light";
    public const string Dark = "Dark";
    public const string System = "System";
}
