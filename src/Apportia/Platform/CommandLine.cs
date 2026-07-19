namespace Apportia.Platform;

/// Original stays untouched for native binaries; Mapped rewrites absolute
/// Linux paths to Wine's Z: drive. On non-Linux platforms both are identical.
public sealed class CommandLine
{
    private CommandLine(string original, string mapped)
    {
        Original = original;
        Mapped = mapped;
    }

    public string Original { get; }
    public string Mapped { get; }

    public static CommandLine FromUser(string[] args)
    {
        var original = Combine(args);
        if (!OperatingSystem.IsLinux())
            return new CommandLine(original, original);
        var mapped = Combine(args.Select(ConvertArgForWine).ToArray());
        return new CommandLine(original, mapped);
    }

    public static CommandLine Raw(string args)
    {
        return new CommandLine(args, args);
    }

    private static string Combine(IEnumerable<string> args)
    {
        return string.Join(
            " ",
            args.Select(a => a.Contains(' ') && !a.StartsWith('"') && !a.StartsWith('\'')
                            ? $"\"{a}\""
                            : a));
    }

    private static string ConvertArgForWine(string arg)
    {
        var eqIdx = arg.IndexOf('=');
        if (eqIdx <= 0 || arg[..eqIdx].Contains('/'))
            return ConvertPathForWine(arg);
        var key = arg[..eqIdx];
        var value = arg[(eqIdx + 1)..];
        return key + "=" + ConvertPathForWine(value);
    }

    private static string ConvertPathForWine(string path)
    {
        if (path.Length >= 2 && char.IsLetter(path[0]) && path[1] == ':' || !path.StartsWith('/'))
            return path;
        return "Z:" + path.Replace('/', '\\');
    }
}
