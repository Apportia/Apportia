using System.Runtime.Versioning;

namespace Apportia.Platform;

[SupportedOSPlatform("Linux")]
public static class LinuxBinary
{
    private static readonly byte[] ElfMagic = [0x7f, (byte)'E', (byte)'L', (byte)'F'];

    private static bool IsElfFile(string path)
    {
        if (!File.Exists(path))
            return false;

        Span<byte> header = stackalloc byte[4];
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var read = fs.Read(header);

        return read == 4 && header.SequenceEqual(ElfMagic);
    }

    private static void EnsureExecutable(string path)
    {
        var mode = File.GetUnixFileMode(path);
        const UnixFileMode execBits = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

        if ((mode & execBits) == execBits)
            return;

        File.SetUnixFileMode(path, mode | execBits);
    }

    public static string? TryResolveElf(string exePath)
    {
        var candidate = exePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? exePath[..^4]
            : exePath;

        if (!IsElfFile(candidate))
            return null;

        EnsureExecutable(candidate);
        return candidate;
    }
}
