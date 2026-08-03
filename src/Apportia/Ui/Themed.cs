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
        return variant == ThemeVariant.Light ? Brushes.Black : Brushes.White;
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
