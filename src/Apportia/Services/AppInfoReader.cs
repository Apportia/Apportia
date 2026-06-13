using Apportia.Models;

namespace Apportia.Services;

public static class AppInfoReader
{
    public static AppInfo Read(string iniPath)
    {
        var name = string.Empty;
        var description = string.Empty;
        var category = string.Empty;
        var subCategory = string.Empty;
        var homepage = string.Empty;
        var packageVersion = string.Empty;

        try
        {
            var inDetails = false;
            var inVersion = false;
            foreach (var line in File.ReadLines(iniPath))
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith(';'))
                    continue;

                if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
                {
                    inDetails = trimmed.Equals("[Details]", StringComparison.OrdinalIgnoreCase);
                    inVersion = trimmed.Equals("[Version]", StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                var sep = trimmed.IndexOf('=');
                if (sep <= 0)
                    continue;

                var key = trimmed[..sep].Trim();
                var value = trimmed[(sep + 1)..].Trim();

                if (inDetails)
                {
                    if (key.Equals("Name", StringComparison.OrdinalIgnoreCase))
                        name = value;
                    else if (key.Equals("Description", StringComparison.OrdinalIgnoreCase))
                        description = value;
                    else if (key.Equals("Category", StringComparison.OrdinalIgnoreCase))
                        category = value;
                    else if (key.Equals("SubCategory", StringComparison.OrdinalIgnoreCase))
                        subCategory = value;
                    else if (key.Equals("Homepage", StringComparison.OrdinalIgnoreCase))
                        homepage = value;
                }
                else if (inVersion)
                {
                    if (key.Equals("PackageVersion", StringComparison.OrdinalIgnoreCase))
                        packageVersion = value;
                }
            }
        }
        catch
        {
            /* unreadable ini – caller uses fallback defaults */
        }

        return new AppInfo(name, description, category, subCategory, homepage, packageVersion);
    }
}
