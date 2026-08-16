using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Aer.Ui.Converters;

/// <summary>
/// M22 review follow-up (issue #250): adapter name → vendor glyph, the same one-mapping-every-surface
/// discipline <see cref="StatusIconMap"/> established for step status. Recognizes only the vendors
/// <c>VendorCliPresence</c> actually probes for (<c>claude</c>, <c>agy</c>) — no icon for a vendor
/// this build can't dispatch to.
/// </summary>
internal static class VendorIconMap
{
    public static string? GeometryKeyFor(string? vendorKey) => vendorKey?.ToLowerInvariant() switch
    {
        "claude" => "Icon.Vendor.Claude",
        "agy" => "Icon.Vendor.Gemini", // vocabulary-ok: vendor key
        _ => "Icon.Dot",
    };

    public static string? ColorKeyFor(string? vendorKey) => vendorKey?.ToLowerInvariant() switch
    {
        "claude" => "Vendor.Claude",
        "agy" => "Vendor.Gemini", // vocabulary-ok: vendor key
        _ => "Color.TextSecondary",
    };

    // #342: the adapter contract key is an internal identifier — this is its one user-facing
    // rendering, the same discipline GeometryKeyFor/ColorKeyFor already apply to the icon/brush
    // halves of the same lookup, so a picker never shows "agy" as primary text.
    public static string DisplayNameFor(string? vendorKey) => vendorKey?.ToLowerInvariant() switch
    {
        "claude" => "Claude",
        "agy" => "Gemini", // vocabulary-ok: vendor key
        _ => vendorKey ?? string.Empty,
    };
}

/// <summary>Vendor key → glyph. Icon geometries live outside <c>ThemeDictionaries</c> (one shape,
/// not themed), so an ordinary theme-oblivious resource lookup is safe here — unlike the brush
/// lookup below.</summary>
public sealed class VendorIconGeometryConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = VendorIconMap.GeometryKeyFor(value as string);
        return key is null ? null : Application.Current?.FindResource(key);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Vendor key → its one user-facing display name (#342) — see <see cref="VendorIconMap.DisplayNameFor"/>.</summary>
public sealed class VendorDisplayNameConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => VendorIconMap.DisplayNameFor(value as string);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Vendor key → brand brush, resolved against the live theme variant — same
/// <see cref="StatusToIconBrushConverter"/> precedent (issue #204/#205's washed-out-color fix).</summary>
public sealed class VendorIconBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = VendorIconMap.ColorKeyFor(value as string);
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
