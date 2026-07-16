using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Apportia.Ui;

/// Shifts a window vertically by half its Height delta so the visual center stays fixed.
public static class WindowAutoRecenter
{
    public static void Attach(Window window)
    {
        var lastHeight = 0.0;
        window.PropertyChanged += (_, e) =>
        {
            if (e.Property != Layoutable.HeightProperty)
                return;
            var newHeight = window.Height;
            if (lastHeight <= 0 || double.IsNaN(newHeight))
            {
                lastHeight = newHeight;
                return;
            }

            var delta = newHeight - lastHeight;
            lastHeight = newHeight;
            var shift = (int)(delta / 2);
            if (shift == 0)
                return;
            window.Position = new PixelPoint(window.Position.X, window.Position.Y - shift);
        };
    }
}