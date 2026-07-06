using System.Diagnostics;
using System.Globalization;
using System.Text;
using Apportia.Platform;
using Avalonia.Media;

namespace Apportia.Services;

/// Writes Apportia's resolved Linux theme colors into the bundled Wine prefix's user.reg
/// and force-disables the Windows theme engine. No-op when system Wine is in use.
public static class WinePrefixTheme
{
    public static async Task ApplyAsync(bool isDark, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsLinux())
            return;
        if (!SettingsService.Load().WineMode.Equals("Bundled", StringComparison.OrdinalIgnoreCase))
            return;
        var wine = WineService.ResolveWineBinary();
        if (wine is null)
            return;
        var colors = LinuxTheme.GetColors(isDark);
        if (colors is null)
            return;

        var regFile = Path.Combine(Path.GetTempPath(), $"apportia-theme-{Guid.NewGuid():N}.reg");
        await File.WriteAllTextAsync(regFile, BuildRegContent(colors), new UTF8Encoding(false), ct);
        try
        {
            var psi = new ProcessStartInfo(wine)
            {
                ArgumentList = { "regedit", "/S", regFile },
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            WineService.ApplyEnv(psi);
            using var proc = Process.Start(psi);
            if (proc is null)
                return;
            await proc.WaitForExitAsync(ct);
        }
        catch
        {
            /* best-effort — theme is cosmetic */
        }
        finally
        {
            try
            {
                File.Delete(regFile);
            }
            catch
            {
                /* ignore */
            }
        }
    }

    private static string BuildRegContent(LinuxThemeColors c)
    {
        var window = Rgb(c.Window);
        var category = Rgb(c.Category);
        var colHeader = Rgb(c.ColHeader);
        var separator = Rgb(c.Separator);
        var border = Rgb(c.ControlBorder);
        var hover = Rgb(c.Hover);
        var text = Rgb(c.Text);
        var subText = Rgb(c.SubText);
        var selection = Rgb(c.SelectionText);

        var colors = new (string Key, string Value)[]
        {
            ("ActiveBorder", window),
            ("ActiveTitle", colHeader),
            ("AppWorkspace", window),
            ("Background", window),
            ("ButtonAlternateFace", separator),
            ("ButtonDkShadow", category),
            ("ButtonFace", window),
            ("ButtonHilight", border),
            ("ButtonLight", border),
            ("ButtonShadow", category),
            ("ButtonText", text),
            ("GradientActiveTitle", colHeader),
            ("GradientInactiveTitle", category),
            ("GrayText", subText),
            ("Hilight", hover),
            ("HilightText", selection),
            ("HotTrackingColor", hover),
            ("InactiveBorder", window),
            ("InactiveTitle", window),
            ("InactiveTitleText", subText),
            ("InfoText", text),
            ("InfoWindow", category),
            ("Menu", window),
            ("MenuBar", window),
            ("MenuHilight", hover),
            ("MenuText", text),
            ("Scrollbar", window),
            ("TitleText", text),
            ("Window", window),
            ("WindowFrame", border),
            ("WindowText", text)
        };

        var sb = new StringBuilder();
        sb.AppendLine("REGEDIT4");
        sb.AppendLine();

        sb.AppendLine("[HKEY_CURRENT_USER\\Control Panel\\Colors]");
        foreach (var (k, v) in colors)
            sb.AppendLine($"\"{k}\"=\"{v}\"");
        sb.AppendLine();

        sb.AppendLine("[HKEY_CURRENT_USER\\Software\\Microsoft\\Windows\\CurrentVersion\\ThemeManager\\Control Panel\\Colors]");
        foreach (var (k, v) in colors)
            sb.AppendLine($"\"{k}\"=\"{v}\"");
        sb.AppendLine();

        sb.AppendLine("[HKEY_CURRENT_USER\\Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize]");
        sb.AppendLine("\"AppsUseLightTheme\"=dword:00000000");
        sb.AppendLine("\"SystemUsesLightTheme\"=dword:00000000");
        sb.AppendLine();

        sb.AppendLine("[HKEY_CURRENT_USER\\Software\\Microsoft\\Windows\\CurrentVersion\\ThemeManager]");
        sb.AppendLine("\"ThemeActive\"=\"0\"");
        sb.AppendLine("\"ColorName\"=-");
        sb.AppendLine("\"DllName\"=-");
        sb.AppendLine();

        return sb.ToString();
    }

    private static string Rgb(Color c)
    {
        return string.Create(CultureInfo.InvariantCulture, $"{c.R} {c.G} {c.B}");
    }
}
