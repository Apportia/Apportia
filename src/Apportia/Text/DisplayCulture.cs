using System.Globalization;

namespace Apportia.Text;

public static class DisplayCulture
{
    public static CultureInfo Current { get; set; } = CultureInfo.InvariantCulture;
}