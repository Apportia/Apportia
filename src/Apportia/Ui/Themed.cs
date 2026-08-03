using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace Apportia.Ui;

public static class Themed
{
    public static IBrush Brush(Control host, string key)
    {
        var variant = ResolveVariant(host);
        if (host.TryFindResource(key, variant, out var value) && value is IBrush brush)
            return brush;
        return Contrast(variant);
    }

    public static IBrush Contrast(Control host)
    {
        return Contrast(ResolveVariant(host));
    }

    private static IBrush Contrast(ThemeVariant variant)
    {
        return variant == ThemeVariant.Light ? Brushes.Black : Brushes.White;
    }

    public static IBrush Shift(Control host, string baseKey, double amount)
    {
        var variant = ResolveVariant(host);
        var baseColor = host.TryFindResource(baseKey, variant, out var value) && value is ISolidColorBrush solid
            ? solid.Color
            : variant == ThemeVariant.Light
                ? Colors.White
                : Color.FromRgb(0x1D, 0x21, 0x22);
        return new SolidColorBrush(variant == ThemeVariant.Light ? Darken(baseColor, amount) : Lighten(baseColor, amount));
    }

    private static Color Darken(Color c, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        return Color.FromArgb(c.A,
                              (byte)(c.R * (1 - amount)),
                              (byte)(c.G * (1 - amount)),
                              (byte)(c.B * (1 - amount)));
    }

    private static Color Lighten(Color c, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        return Color.FromArgb(c.A,
                              (byte)(c.R + (255 - c.R) * amount),
                              (byte)(c.G + (255 - c.G) * amount),
                              (byte)(c.B + (255 - c.B) * amount));
    }

    private static ThemeVariant ResolveVariant(Control host)
    {
        if (host is ThemeVariantScope scope)
            return scope.ActualThemeVariant;
        var topLevel = TopLevel.GetTopLevel(host);
        return topLevel?.ActualThemeVariant
               ?? Application.Current?.ActualThemeVariant
               ?? ThemeVariant.Default;
    }
}
