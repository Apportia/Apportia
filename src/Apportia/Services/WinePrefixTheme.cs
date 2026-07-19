using System.Diagnostics;
using System.Globalization;
using System.Text;
using Apportia.Platform;
using Avalonia.Media;

namespace Apportia.Services;

public static class WinePrefixTheme
{
    private const int VerifyRetries = 3;
    private const int QuietWindowMs = 1500;

    private static readonly Lock DesiredLock = new();
    private static bool? _desiredDark;
    private static bool _desiredForce;
    private static long _desiredTickMs;
    private static Task? _worker;

    // Bypass the debounced worker: runs one apply-cycle end-to-end and returns when done.
    // Use at app-launch where the theme must be visible on first paint.
    public static async Task ApplyImmediatelyAsync(bool isDark, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsLinux())
            return;
        if (!SettingsService.Load().WineApplyTheme)
            return;
        if (WineService.ResolveWineBinary() is null)
            return;
        await ApplyOnceAsync(isDark, false, ct);
    }

    public static Task ApplyAsync(bool isDark, bool force = false, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsLinux() || !SettingsService.Load().WineApplyTheme || WineService.ResolveWineBinary() is null)
            return Task.CompletedTask;

        lock (DesiredLock)
        {
            _desiredDark = isDark;
            _desiredForce |= force;
            _desiredTickMs = Environment.TickCount64;
            // Shared singleton worker — per-caller ct would let one caller cancel work queued by another.
            _worker ??= Task.Run(() => WorkerLoopAsync(CancellationToken.None), CancellationToken.None);
        }

        return Task.CompletedTask;
    }

    private static async Task WorkerLoopAsync(CancellationToken ct)
    {
        try
        {
            while (TryClaimNextRequest(out var isDark, out var force, out var waitMs))
            {
                if (waitMs > 0)
                {
                    await Task.Delay(waitMs, ct);
                    continue;
                }

                await ApplyOnceAsync(isDark, force, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown signal — leave the queue as-is.
        }
    }

    private static bool TryClaimNextRequest(out bool isDark, out bool force, out int waitMs)
    {
        lock (DesiredLock)
        {
            if (_desiredDark is null)
            {
                _worker = null;
                isDark = false;
                force = false;
                waitMs = 0;
                return false;
            }

            var elapsed = Environment.TickCount64 - _desiredTickMs;
            var remaining = QuietWindowMs - elapsed;
            if (remaining > 0)
            {
                isDark = false;
                force = false;
                waitMs = (int)remaining;
                return true;
            }

            isDark = _desiredDark.Value;
            force = _desiredForce;
            _desiredDark = null;
            _desiredForce = false;
            waitMs = 0;
            return true;
        }
    }

    private static async Task ApplyOnceAsync(bool isDark, bool force, CancellationToken ct)
    {
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

        // Seed fonts before wineboot so its font-registry pass picks them up.
        WinePrefixFonts.Apply();

        if (!File.Exists(userReg))
        {
            // Explicit wineboot first — an implicit one racing with regedit /S clobbers imported keys.
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
                // Flush user.reg to disk by waiting for the last wine client to exit.
                await RunWineserverAsync(wine, ct, "-w");
            }
            catch
            {
                // Theme is cosmetic; retry loop below re-verifies via marker.
            }
            finally
            {
                try
                {
                    File.Delete(regFile);
                }
                catch
                {
                    /* temp file; ok if it leaks */
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
