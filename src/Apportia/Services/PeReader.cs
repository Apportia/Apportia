using System.Diagnostics;
using System.Text;
using System.Xml.Linq;
using Avalonia.Media.Imaging;

namespace Apportia.Services;

public readonly record struct IconVariant(Bitmap Icon, string Tooltip);

public static class PeReader
{
    public static (string Name, string Description) ReadVersionInfo(string exePath)
    {
        try
        {
            var vi = FileVersionInfo.GetVersionInfo(exePath);
            var name = vi.ProductName?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(name))
                name = vi.FileDescription?.Trim() ?? string.Empty;
            return (name, vi.FileDescription?.Trim() ?? string.Empty);
        }
        catch
        {
            return (string.Empty, string.Empty);
        }
    }

    public static string ReadManifestVersion(string exePath)
    {
        try
        {
            using var fs = new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var br = new BinaryReader(fs);

            if (!TryGetRsrcSection(br, fs, out var rsrcRaw, out var rsrcVa))
                return string.Empty;

            long RvaToFile(uint rva)
            {
                return rsrcRaw + (rva - rsrcVa);
            }

            foreach (var leaf in CollectByType(br, fs, rsrcRaw, 24)) // RT_MANIFEST
            {
                fs.Seek(RvaToFile(leaf.DataRva), SeekOrigin.Begin);
                var data = br.ReadBytes((int)leaf.Size);
                var version = ParseAssemblyIdentityVersion(data);
                if (!string.IsNullOrEmpty(version))
                    return version;
            }
        }
        catch
        {
            /* ignore – caller falls back to empty */
        }

        return string.Empty;
    }

    private static string ParseAssemblyIdentityVersion(byte[] data)
    {
        try
        {
            var enc = data switch
            {
                [0xFF, 0xFE, ..] => Encoding.Unicode,
                [0xFE, 0xFF, ..] => Encoding.BigEndianUnicode,
                _ => Encoding.UTF8
            };

            var xml = enc.GetString(data).TrimStart('\uFEFF');
            var doc = XDocument.Parse(xml);
            var identity = doc.Descendants()
                              .FirstOrDefault(e => e.Name.LocalName == "assemblyIdentity");
            return identity?.Attribute("version")?.Value ?? string.Empty;
        }
        catch
        {
            /* XML parse failure or unsupported encoding – caller treats missing version as empty */
            return string.Empty;
        }
    }

    public static List<IconVariant> ReadIcoFile(string icoPath)
    {
        var pngEntries = new List<(IconVariant Variant, int Size)>();
        var dibEntries = new List<(IconVariant Variant, int Size)>();

        try
        {
            using var fs = new FileStream(icoPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var br = new BinaryReader(fs);

            br.ReadUInt16(); // reserved
            if (br.ReadUInt16() != 1)
                return [];
            var count = br.ReadUInt16();

            var entries = new (byte Width, uint Size, uint Offset)[count];
            for (var i = 0; i < count; i++)
            {
                var width = br.ReadByte();
                br.ReadBytes(7); // bHeight, bColorCount, bReserved, wPlanes, wBitCount
                var size = br.ReadUInt32();
                var offset = br.ReadUInt32();
                entries[i] = (width, size, offset);
            }

            foreach (var (width, size, offset) in entries)
            {
                try
                {
                    fs.Seek(offset, SeekOrigin.Begin);
                    var data = br.ReadBytes((int)size);

                    if (IsPng(data))
                    {
                        using var ms = new MemoryStream(data);
                        var bmp = new Bitmap(ms);
                        pngEntries.Add((new IconVariant(bmp, BuildTooltip(bmp)), bmp.PixelSize.Width));
                    }
                    else
                    {
                        var wrapped = WrapDibInIco(data, width);
                        using var ms = new MemoryStream(wrapped);
                        var bmp = new Bitmap(ms);
                        dibEntries.Add((new IconVariant(bmp, BuildTooltip(bmp)), bmp.PixelSize.Width));
                    }
                }
                catch
                {
                    /* ignore malformed entry */
                }
            }
        }
        catch
        {
            /* corrupt or unreadable .ico file – return whatever was collected */
        }

        return
        [
            ..pngEntries.OrderByDescending(e => e.Size).Select(e => e.Variant),
            ..dibEntries.OrderByDescending(e => e.Size).Select(e => e.Variant)
        ];
    }

    public static List<IconVariant> TryExtractAllIcons(string exePath)
    {
        var result = new List<IconVariant>();
        try
        {
            foreach (var meta in ExtractIconMeta(exePath))
            {
                try
                {
                    var loadable = IsPng(meta.Data) ? meta.Data : WrapDibInIco(meta.Data, meta.Width);
                    using var ms = new MemoryStream(loadable);
                    var bmp = new Bitmap(ms);
                    var tooltip = BuildTooltip(bmp);
                    result.Add(new IconVariant(bmp, tooltip));
                }
                catch
                {
                    /* ignore malformed icon data */
                }
            }
        }
        catch
        {
            /* ignore – returns whatever was collected so far */
        }

        return result;
    }

    private static string BuildTooltip(Bitmap bmp)
    {
        var px = bmp.PixelSize;
        return $"{px.Width} x {px.Height} px";
    }

    private static IEnumerable<IconMeta> ExtractIconMeta(string exePath)
    {
        using var fs = new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var br = new BinaryReader(fs);

        if (!TryGetRsrcSection(br, fs, out var rsrcRaw, out var rsrcVa))
            yield break;

        var iconDataById = new Dictionary<uint, byte[]>();
        foreach (var leaf in CollectByType(br, fs, rsrcRaw, 3))
        {
            fs.Seek(RvaToFile(leaf.DataRva), SeekOrigin.Begin);
            iconDataById[leaf.Id] = br.ReadBytes((int)leaf.Size);
        }

        if (iconDataById.Count == 0)
            yield break;

        var seen = new HashSet<uint>();
        var icons = new List<IconMeta>();

        foreach (var group in CollectByType(br, fs, rsrcRaw, 14))
        {
            fs.Seek(RvaToFile(group.DataRva), SeekOrigin.Begin);
            ParseGrpicondir(br.ReadBytes((int)group.Size), iconDataById, seen, icons);
        }

        foreach (var item in icons.OrderByDescending(x => x.Width == 0 ? 256 : x.Width))
            yield return item;
        yield break;

        long RvaToFile(uint rva)
        {
            return rsrcRaw + (rva - rsrcVa);
        }
    }

    private static void ParseGrpicondir(
        byte[] groupData,
        Dictionary<uint, byte[]> iconDataById,
        HashSet<uint> seen,
        List<IconMeta> output)
    {
        if (groupData.Length < 6)
            return;
        using var gr = new BinaryReader(new MemoryStream(groupData));
        gr.ReadUInt16();
        gr.ReadUInt16(); // reserved, type
        var count = gr.ReadUInt16();

        for (var i = 0; i < count; i++)
        {
            // GRPICONDIRENTRY (14 bytes):
            // BYTE bWidth, BYTE bHeight, BYTE bColorCount, BYTE bReserved,
            // WORD wPlanes, WORD wBitCount, DWORD dwBytesInRes, WORD nId
            var width = gr.ReadByte();
            gr.ReadBytes(7); // bHeight, bColorCount, bReserved, wPlanes, wBitCount
            gr.ReadUInt32(); // dwBytesInRes
            var id = gr.ReadUInt16();

            if (seen.Add(id) && iconDataById.TryGetValue(id, out var data))
                output.Add(new IconMeta(data, width));
        }
    }

    private static bool TryGetRsrcSection(
        BinaryReader br,
        FileStream fs,
        out long rsrcRaw,
        out uint rsrcVa)
    {
        rsrcRaw = 0;
        rsrcVa = 0;
        if (br.ReadUInt16() != 0x5A4D)
            return false;
        fs.Seek(0x3C, SeekOrigin.Begin);
        long peOffset = br.ReadUInt32();

        fs.Seek(peOffset, SeekOrigin.Begin);
        if (br.ReadUInt32() != 0x00004550)
            return false;

        fs.Seek(peOffset + 6, SeekOrigin.Begin);
        var numSections = br.ReadUInt16();
        fs.Seek(peOffset + 20, SeekOrigin.Begin);
        var optHeaderSize = br.ReadUInt16();
        fs.Seek(peOffset + 24, SeekOrigin.Begin);
        var magic = br.ReadUInt16();
        if (magic != 0x10B && magic != 0x20B)
            return false;

        var ddBase = peOffset + 24 + (magic == 0x20B ? 112L : 96L);
        fs.Seek(ddBase + 16, SeekOrigin.Begin);
        var resourceRva = br.ReadUInt32();
        if (resourceRva == 0)
            return false;

        var sectionsStart = peOffset + 24 + optHeaderSize;
        for (var i = 0; i < numSections; i++)
        {
            fs.Seek(sectionsStart + i * 40L + 8, SeekOrigin.Begin);
            var vSize = br.ReadUInt32();
            var vAddr = br.ReadUInt32();
            br.ReadUInt32();
            var rawPtr = br.ReadUInt32();
            if (vAddr > resourceRva || resourceRva >= vAddr + vSize)
                continue;
            rsrcVa = vAddr;
            rsrcRaw = rawPtr;
            return true;
        }

        return false;
    }

    private static List<Leaf> CollectByType(BinaryReader br, FileStream fs, long rsrcBase, uint typeId)
    {
        fs.Seek(rsrcBase, SeekOrigin.Begin);
        ReadDirCounts(br, out var named, out var idCount);
        for (var i = 0; i < named + idCount; i++)
        {
            var n = br.ReadUInt32();
            var d = br.ReadUInt32();
            if ((n & 0x80000000) == 0 && (n & 0x7FFFFFFF) == typeId && (d & 0x80000000) != 0)
                return WalkSubdir(br, fs, rsrcBase, d & 0x7FFFFFFF, 0);
        }

        return [];
    }

    private static List<Leaf> WalkSubdir(BinaryReader br, FileStream fs, long rsrcBase, long dirOff, uint parentId)
    {
        var result = new List<Leaf>();
        fs.Seek(rsrcBase + dirOff, SeekOrigin.Begin);
        ReadDirCounts(br, out var named, out var idCount);

        var entries = new (uint Id, bool IsSubdir, uint Offset)[named + idCount];
        for (var i = 0; i < entries.Length; i++)
        {
            var n = br.ReadUInt32();
            var d = br.ReadUInt32();
            entries[i] = (n & 0x7FFFFFFF, (d & 0x80000000) != 0, d & 0x7FFFFFFF);
        }

        foreach (var (id, isSubdir, offset) in entries)
        {
            var myId = parentId != 0 ? parentId : id;
            if (isSubdir)
            {
                result.AddRange(WalkSubdir(br, fs, rsrcBase, offset, myId));
                continue;
            }

            fs.Seek(rsrcBase + offset, SeekOrigin.Begin);
            result.Add(new Leaf(myId, br.ReadUInt32(), br.ReadUInt32()));
        }

        return result;
    }

    private static void ReadDirCounts(BinaryReader br, out int named, out int id)
    {
        br.ReadUInt32();
        br.ReadUInt32();
        br.ReadUInt16();
        br.ReadUInt16();
        named = br.ReadUInt16();
        id = br.ReadUInt16();
    }

    private static bool IsPng(byte[] data)
    {
        return data.Length > 4 && data[0] == 0x89 && data[1] == 0x50;
    }

    private static byte[] WrapDibInIco(byte[] dibData, byte width)
    {
        var ico = new byte[6 + 16 + dibData.Length];
        using var iw = new BinaryWriter(new MemoryStream(ico));
        iw.Write((ushort)0);
        iw.Write((ushort)1);
        iw.Write((ushort)1);
        iw.Write(width);
        iw.Write(width);
        iw.Write((ushort)0);
        iw.Write((ushort)1);
        iw.Write((ushort)0);
        iw.Write((uint)dibData.Length);
        iw.Write((uint)(6 + 16));
        Array.Copy(dibData, 0, ico, 6 + 16, dibData.Length);
        return ico;
    }

    private readonly record struct Leaf(uint Id, uint DataRva, uint Size);

    private readonly record struct IconMeta(byte[] Data, byte Width);
}
