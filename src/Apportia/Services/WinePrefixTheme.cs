using System.Diagnostics;
using System.Globalization;
using System.Text;
using Apportia.Platform;
using Avalonia.Media;

namespace Apportia.Services;

/// Writes Apportia's resolved Linux theme colors into the bundled Wine prefix's user.reg
/// and force-disables the Windows theme engine. No-op when system Wine is in use.
///
/// A marker key `[HKCU\Software\Apportia]` with `Theme` and `User` values makes the operation
/// idempotent: if user.reg already reports the desired variant for the current $USER we skip
/// wineboot/regedit entirely.
public static class WinePrefixTheme
{
    private const int VerifyRetries = 3;

    public static async Task ApplyAsync(bool isDark, bool force = false, CancellationToken ct = default)
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

        var variant = isDark ? "Dark" : "Light";
        var user = Environment.UserName;
        var userReg = Path.Combine(WineService.PrefixDir, "user.reg");

        if (!force && MarkerMatches(userReg, variant, user))
            return;

        // Ensure wineboot finishes before regedit — implicit wineboot on a fresh or stale prefix
        // races with `regedit /S` and clobbers imported keys.
        // Seed the fonts before wineboot so its font-registry pass picks them up.
        // Guarded by a marker inside the prefix, so this is a no-op after the first successful apply.
        WinePrefixFonts.Apply();

        if (!File.Exists(userReg))
        {
            await RunWineAsync(wine, ct, "wineboot", "--init");
            WinePrefixSanitizer.Sanitize();
        }

        for (var attempt = 0; attempt < VerifyRetries; attempt++)
        {
            var regFile = Path.Combine(Path.GetTempPath(), $"apportia.wine.theme.{Guid.NewGuid():N}.reg");
            await File.WriteAllTextAsync(regFile, BuildRegContent(colors, variant, user), new UTF8Encoding(false), ct);
            try
            {
                await RunWineAsync(wine, ct, "regedit", "/S", regFile);
                // wineserver -w waits for the last wine client to exit, which flushes user.reg to disk.
                await RunWineserverAsync(wine, ct, "-w");
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

            if (!MarkerMatches(userReg, variant, user))
                continue;
            WinePrefixSanitizer.Sanitize();
            return;
        }
    }

    private static bool MarkerMatches(string userReg, string variant, string user)
    {
        if (!File.Exists(userReg))
            return false;
        try
        {
            var text = File.ReadAllText(userReg);
            return text.Contains($"\"Theme\"=\"{variant}\"", StringComparison.Ordinal)
                   && text.Contains($"\"User\"=\"{user}\"", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static async Task RunWineAsync(string wine, CancellationToken ct, params string[] args)
    {
        var psi = new ProcessStartInfo(wine)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        WineService.ApplyEnv(psi);
        using var proc = Process.Start(psi);
        if (proc is null)
            return;
        await proc.WaitForExitAsync(ct);
    }

    private static async Task RunWineserverAsync(string wine, CancellationToken ct, params string[] args)
    {
        var wineserver = Path.Combine(Path.GetDirectoryName(wine) ?? string.Empty, "wineserver");
        if (!File.Exists(wineserver))
            return;
        var psi = new ProcessStartInfo(wineserver)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        WineService.ApplyEnv(psi);
        using var proc = Process.Start(psi);
        if (proc is null)
            return;
        await proc.WaitForExitAsync(ct);
    }

    private static string BuildRegContent(LinuxThemeColors c, string variant, string user)
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
            ("ButtonDkShadow", window),
            ("ButtonFace", window),
            ("ButtonHilight", border),
            ("ButtonLight", window),
            ("ButtonShadow", border),
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
            ("WindowFrame", hover),
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

        var isDark = variant == "Dark";

        sb.AppendLine("[HKEY_CURRENT_USER\\Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize]");
        if (isDark)
        {
            sb.AppendLine("\"AppsUseLightTheme\"=dword:00000000");
            sb.AppendLine("\"SystemUsesLightTheme\"=dword:00000000");
        }
        else
        {
            sb.AppendLine("\"AppsUseLightTheme\"=dword:00000001");
            sb.AppendLine("\"SystemUsesLightTheme\"=dword:00000001");
        }

        sb.AppendLine();

        sb.AppendLine("[HKEY_CURRENT_USER\\Software\\Microsoft\\Windows\\CurrentVersion\\ThemeManager]");
        if (isDark)
        {
            sb.AppendLine("\"ThemeActive\"=\"0\"");
            sb.AppendLine("\"ColorName\"=-");
            sb.AppendLine("\"DllName\"=-");
        }
        else
        {
            sb.AppendLine("\"ThemeActive\"=\"1\"");
            sb.AppendLine("\"ColorName\"=\"Blue\"");
            sb.AppendLine("\"DllName\"=\"C:\\\\windows\\\\resources\\\\themes\\\\aero\\\\aero.msstyles\"");
        }

        sb.AppendLine();

        sb.AppendLine("[HKEY_CURRENT_USER\\Software\\Apportia]");
        sb.AppendLine($"\"Theme\"=\"{variant}\"");
        sb.AppendLine($"\"User\"=\"{user}\"");
        sb.AppendLine();

        return sb.ToString();
    }

    private static string Rgb(Color c)
    {
        return string.Create(CultureInfo.InvariantCulture, $"{c.R} {c.G} {c.B}");
    }
}
