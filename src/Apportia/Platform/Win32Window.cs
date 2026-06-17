using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.Threading;

namespace Apportia.Platform;

public static partial class Win32Window
{
    public static void ApplyDarkTitlebar(Window window)
    {
        var isDark = Application.Current?.ActualThemeVariant == ThemeVariant.Dark;
        ApplyDarkTitlebar(window, isDark);
    }

    public static void ApplyDarkTitlebar(Window window, bool dark)
    {
        if (!OperatingSystem.IsWindows())
            return;

        // Must be deferred: calling these APIs synchronously in OnOpened re-enters
        // Avalonia's WndProc via WM_NCPAINT and causes a crash.
        Dispatcher.UIThread.Post(() =>
        {
            var hwnd = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (hwnd == IntPtr.Zero)
                return;

            var value = dark ? 1 : 0;
            DwmSetWindowAttribute(hwnd, 0x14 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref value, sizeof(int));

            // Windows 11 (build 22000+) repaints on its own after DwmSetWindowAttribute.
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
                return;

            // WM_NCACTIVATE round-trip forces Windows 10 to repaint the titlebar immediately.
            SendMessage(hwnd, 0x0086 /* WM_NCACTIVATE */, IntPtr.Zero, IntPtr.Zero);
            SendMessage(hwnd, 0x0086 /* WM_NCACTIVATE */, new IntPtr(1), IntPtr.Zero);
        }, DispatcherPriority.Background);
    }

    public static void BringToForeground(Window window)
    {
        if (!OperatingSystem.IsWindows())
            return;
        var hwnd = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd == IntPtr.Zero)
            return;
        ShowWindow(hwnd, 9 /* SW_RESTORE */);
        SetForegroundWindow(hwnd);
    }

    public static void AllowAnyForeground()
    {
        if (OperatingSystem.IsWindows())
            AllowSetForegroundWindow(unchecked((uint)-1) /* ASFW_ANY */);
    }

    [LibraryImport("dwmapi.dll")]
    private static partial void DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageA")]
    private static partial void SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll")]
    private static partial void SetForegroundWindow(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    private static partial void ShowWindow(IntPtr hWnd, int nCmdShow);

    [LibraryImport("user32.dll")]
    private static partial void AllowSetForegroundWindow(uint dwProcessId);
}