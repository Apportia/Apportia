using System.Text.RegularExpressions;
using Avalonia.Media;

namespace Apportia.Platform;

public sealed record LinuxThemeColors(
    Color Window,
    Color Category,
    Color ColHeader,
    Color Separator,
    Color ControlBorder,
    Color Hover,
    Color Text,
    Color SubText,
    Color SelectionText);

public static class LinuxTheme
{
    private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static LinuxThemeColors? GetColors(bool isDark)
    {
        return TryGetKdeColors(isDark) ?? TryGetGtkColors();
    }

    private static LinuxThemeColors? TryGetKdeColors(bool isDark)
    {
        try
        {
            var ini = TryParseIniFile(Path.Combine(Home, ".config", "kdeglobals"));
            if (ini == null)
                return null;

            ini.TryGetValue("KDE", out var kde);

            // AutomaticLookAndFeel lets KDE manage separate dark/light packages
            var automatic = kde != null
                            && kde.TryGetValue("AutomaticLookAndFeel", out var autoVal)
                            && autoVal.Equals("true", StringComparison.OrdinalIgnoreCase);
            if (automatic)
            {
                var lafKey = isDark ? "DefaultDarkLookAndFeel" : "DefaultLightLookAndFeel";
                if (!kde!.TryGetValue(lafKey, out var lafAuto))
                    return null;
                var scheme = FindLookAndFeelColorScheme(lafAuto);
                return scheme != null ? BuildKdeColors(scheme) : null;
            }

            string? activeScheme = null;
            if (ini.TryGetValue("General", out var general)
                && general.TryGetValue("ColorScheme", out var directScheme)
                && !string.IsNullOrWhiteSpace(directScheme))
            {
                activeScheme = directScheme;
            }
            else if (kde != null && kde.TryGetValue("LookAndFeelPackage", out var laf))
            {
                activeScheme = FindLookAndFeelColorScheme(laf);
            }

            if (activeScheme == null)
                return null;

            var activeColors = BuildKdeColors(activeScheme);
            if (activeColors == null)
                return null;

            if (IsDark(activeColors.Window) == isDark)
                return activeColors;

            // Opposite mode requested, resolve its look-and-feel
            if (kde == null)
                return null;
            var oppKey = isDark ? "DefaultDarkLookAndFeel" : "DefaultLightLookAndFeel";
            if (!kde.TryGetValue(oppKey, out var oppLaf))
                return null;
            var oppScheme = FindLookAndFeelColorScheme(oppLaf);
            return oppScheme != null ? BuildKdeColors(oppScheme) : null;
        }
        catch
        {
            /* file I/O or parse failure — fall through to GTK */
            return null;
        }
    }

    private static bool IsDark(Color c)
    {
        return (c.R + c.G + c.B) / 3 < 128;
    }


    private static string? FindLookAndFeelColorScheme(string lookAndFeel)
    {
        string[] dirs =
        [
            Path.Combine(Home, ".local", "share", "plasma", "look-and-feel"),
            "/usr/share/plasma/look-and-feel"
        ];

        foreach (var dir in dirs)
        {
            var ini = TryParseIniFile(Path.Combine(dir, lookAndFeel, "contents", "defaults"));
            if (ini == null)
                continue;

            foreach (var (key, section) in ini)
            {
                if (key.StartsWith("kdeglobals", StringComparison.OrdinalIgnoreCase)
                    && section.TryGetValue("ColorScheme", out var scheme))
                    return scheme;
            }
        }

        return null;
    }

    private static LinuxThemeColors? BuildKdeColors(string colorScheme)
    {
        string[] dirs =
        [
            Path.Combine(Home, ".local", "share", "color-schemes"),
            "/usr/share/color-schemes"
        ];

        foreach (var dir in dirs)
        {
            var ini = TryParseIniFile(Path.Combine(dir, colorScheme + ".colors"));
            if (ini == null)
                continue;

            if (!ini.TryGetValue("Colors:Window", out var window))
                continue;
            if (!window.TryGetValue("BackgroundNormal", out var bgNormal))
                continue;

            var bg = ParseKdeRgb(bgNormal);
            if (bg == null)
                continue;

            ini.TryGetValue("Colors:View", out var view);
            ini.TryGetValue("Colors:Selection", out var sel);
            ini.TryGetValue("Colors:Header", out var header);

            var bgColor = bg.Value;
            var category = KdeColor(view, "BackgroundNormal") ?? KdeColor(window, "BackgroundAlternate") ?? bgColor;
            var text = KdeColor(view, "ForegroundNormal") ?? KdeColor(window, "ForegroundNormal") ?? GuessText(bgColor);
            var subText = KdeColor(view, "ForegroundInactive") ?? KdeColor(window, "ForegroundNormal") ?? text;

            // BackgroundAlternate equals Window in many KDE themes, so blend as last resort
            var headerBg = KdeColor(header, "BackgroundNormal");
            var viewBgAlt = KdeColor(view, "BackgroundAlternate");
            var colHeader = headerBg
                            ?? (viewBgAlt != null && viewBgAlt != bgColor ? viewBgAlt : null)
                            ?? Blend(bgColor, category, 0.45f);

            var controlBorder = Blend(bgColor, subText, 0.25f);

            return new LinuxThemeColors(
                bgColor,
                category,
                colHeader,
                controlBorder,
                controlBorder,
                WithAlpha(KdeColor(sel, "BackgroundNormal"), 77) ?? category,
                text,
                subText,
                KdeColor(sel, "ForegroundNormal") ?? text);
        }

        return null;
    }

    private static Color? KdeColor(Dictionary<string, string>? section, string key)
    {
        return section != null && section.TryGetValue(key, out var val) ? ParseKdeRgb(val) : null;
    }

    private static LinuxThemeColors? TryGetGtkColors()
    {
        try
        {
            foreach (var version in new[] { "gtk-4.0", "gtk-3.0" })
            {
                var colors = TryParseGtkCssFile(Path.Combine(Home, ".config", version, "colors.css"));
                if (colors != null)
                    return colors;
            }

            var themeName = ReadGtkThemeName();
            if (themeName == null)
                return null;

            string[] themeDirs =
            [
                Path.Combine(Home, ".local", "share", "themes"),
                "/usr/share/themes"
            ];

            foreach (var baseDir in themeDirs)
            {
                foreach (var subDir in new[] { "gtk-4.0", "gtk-3.0" })
                {
                    var colors = TryParseGtkCssFile(Path.Combine(baseDir, themeName, subDir, "gtk.css"));
                    if (colors != null)
                        return colors;
                }
            }
        }
        catch
        {
            /* file I/O or parse failure — native colors unavailable */
        }

        return null;
    }

    private static string? ReadGtkThemeName()
    {
        foreach (var version in new[] { "gtk-4.0", "gtk-3.0" })
        {
            var ini = TryParseIniFile(Path.Combine(Home, ".config", version, "settings.ini"));
            if (ini != null && ini.TryGetValue("Settings", out var s) && s.TryGetValue("gtk-theme-name", out var name))
                return name;
        }

        return null;
    }

    private static LinuxThemeColors? TryParseGtkCssFile(string path)
    {
        return File.Exists(path) ? BuildGtkColors(File.ReadAllText(path)) : null;
    }

    private static LinuxThemeColors? BuildGtkColors(string css)
    {
        var bg = GtkColor(css, "window_bg_color", "theme_bg_color");
        if (bg == null)
            return null;

        var fg = GtkColor(css, "window_fg_color", "theme_fg_color");
        var viewBg = GtkColor(css, "theme_base_color", "content_view_bg");
        var viewFg = GtkColor(css, "theme_text_color");
        var selBg = GtkColor(css, "theme_selected_bg_color", "theme_hovering_selected_bg_color");
        var selFg = GtkColor(css, "theme_selected_fg_color");
        var subFg = GtkColor(css, "insensitive_fg_color", "theme_unfocused_view_text_color");

        var bgColor = bg.Value;
        var fgColor = fg ?? GuessText(bgColor);
        var text = viewFg ?? fgColor;
        var subText = subFg ?? Blend(fgColor, bgColor, 0.35f);

        return new LinuxThemeColors(
            bgColor,
            viewBg ?? bgColor,
            viewBg ?? bgColor,
            Blend(bgColor, fgColor, 0.14f),
            Blend(bgColor, subText, 0.45f),
            WithAlpha(selBg, 77) ?? Blend(bgColor, fgColor, 0.08f),
            text,
            subText,
            selFg ?? fgColor);
    }

    private static Color? GtkColor(string css, params string[] names)
    {
        const string colorValue = @"(#[0-9a-fA-F]{3,8}|rgb\([^)]+\)|rgba\([^)]+\))";
        foreach (var name in names)
        {
            var m = Regex.Match(css, $@"@define-color\s+{Regex.Escape(name)}(?:_\w+)?\s+{colorValue}");
            if (m.Success && Color.TryParse(m.Groups[1].Value.Trim(), out var c))
                return c;
        }

        return null;
    }

    private static Color? WithAlpha(Color? color, byte alpha)
    {
        return color.HasValue ? Color.FromArgb(alpha, color.Value.R, color.Value.G, color.Value.B) : null;
    }

    private static Color GuessText(Color bg)
    {
        return bg.R < 128 ? Colors.White : Colors.Black;
    }

    private static Color Blend(Color a, Color b, float t)
    {
        return Color.FromArgb(
            (byte)(a.A + (b.A - a.A) * t),
            (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t),
            (byte)(a.B + (b.B - a.B) * t));
    }

    private static Color? ParseKdeRgb(string value)
    {
        var parts = value.Split(',');
        return parts.Length == 3
               && byte.TryParse(parts[0].Trim(), out var r)
               && byte.TryParse(parts[1].Trim(), out var g)
               && byte.TryParse(parts[2].Trim(), out var b)
            ? Color.FromRgb(r, g, b)
            : null;
    }

    private static Dictionary<string, Dictionary<string, string>>? TryParseIniFile(string path)
    {
        return File.Exists(path) ? ParseIni(File.ReadAllText(path)) : null;
    }

    private static Dictionary<string, Dictionary<string, string>> ParseIni(string text)
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string>? current = null;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                var section = line[1..^1].Trim();
                current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                result[section] = current;
                continue;
            }

            if (current == null || line.StartsWith('#') || line.StartsWith(';'))
                continue;

            var sep = line.IndexOf('=');
            if (sep <= 0)
                continue;

            current[line[..sep].Trim()] = line[(sep + 1)..].Trim();
        }

        return result;
    }
}