using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using EliteLevel.Disc.Core.Formats;
using EliteLevel.Disc.Core.IO;

namespace EliteLevel.Disc.Core.Formats.Switch;

/// <summary>
/// NCA header and section layout from the public NCA description.
/// The first 0xC00 bytes are AES-XTS. Magic sits at 0x200. Each file-system header is 0x200 bytes at 0x400.
/// NCA2 encrypts every section header as sector 0. NCA3 uses the real sector number.
/// A section body starts on a 0x200-byte boundary and is None, AES-XTS, or AES-CTR.
/// Patch sections (AES-CTR-EX) are left unread.
/// </summary>
internal static class Nca
{
    public const int HeaderSize = 0xC00;
    public const int Magic = 0x200;
    public const int Distribution = 0x204;
    public const int ContentType = 0x205;
    public const int KeyGenerationOld = 0x206;
    public const int KaekIndex = 0x207;
    public const int ContentSize = 0x208;
    public const int ProgramId = 0x210;
    public const int KeyGeneration = 0x220;
    public const int RightsId = 0x230;
    public const int FsEntry = 0x240;
    public const int KeyArea = 0x300;
    public const int FsHeaderBase = 0x400;
    public const int FsType = 2;
    public const int HashType = 3;
    public const int EncryptionType = 4;
    public const int HashData = 8;
    public const int CtrUpper = 0x140;

    public static bool TryOpen(IByteSource file, SwitchKeys keys, out byte[] header)
    {
        header = [];
        var headerKey = keys.HeaderKey;
        if (headerKey is null || file.Length < HeaderSize)
        {
            return false;
        }

        var buffer = new byte[HeaderSize];
        if (PartitionFs.ReadExact(file, 0, buffer) != HeaderSize)
        {
            return false;
        }

        NintendoAes.Xts(buffer, headerKey, sector: 0, encrypt: false);
        if (!IsMagic(buffer))
        {
            return false;
        }

        if (buffer[Magic + 3] == (byte)'2')
        {
            FixNca2SectionHeaders(buffer, headerKey);
        }

        header = buffer;
        return IsMagic(header);
    }

    /// <summary>
    /// The 32-byte AES-XTS key from the key area, when the caller's keys decrypt the header.
    /// NCZ stores only 16 key bytes per section, so an XTS section is restored with this key.
    /// </summary>
    internal static byte[]? TryXtsKey(IByteSource file, SwitchKeys keys)
    {
        if (!TryOpen(file, keys, out var header) || HasRights(header))
        {
            return null;
        }

        var material = BodyKeys(header, keys, titleKey: null);
        if (material.Xts is not { Length: 32 })
        {
            return null;
        }

        foreach (var value in material.Xts)
        {
            if (value != 0)
            {
                return material.Xts;
            }
        }

        return null;
    }

    public static ulong ReadProgramId(ReadOnlySpan<byte> header) => BinaryPrimitives.ReadUInt64LittleEndian(header[ProgramId..]);

    public static int MasterKey(ReadOnlySpan<byte> header)
    {
        var generation = Math.Max(header[KeyGenerationOld], header[KeyGeneration]);
        return generation > 0 ? generation - 1 : 0;
    }

    public static List<DiscNode> Expand(IByteSource file, byte[] header, SwitchKeys keys, string path, bool showSystem)
    {
        var direct = ExpandWith(file, header, keys, path, showSystem, titleKey: null);
        if (direct.Count > 0 || !HasRights(header))
        {
            return direct;
        }

        var candidates = new List<byte[]>();
        keys.TitleKeyCandidates(header.AsSpan(RightsId, 16), MasterKey(header), candidates);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            var hex = Convert.ToHexString(candidate);
            if (!seen.Add(hex))
            {
                continue;
            }

            var nodes = ExpandWith(file, header, keys, path, showSystem, candidate);
            if (nodes.Count > 0)
            {
                return nodes;
            }
        }

        return [];
    }

    private static List<DiscNode> ExpandWith(IByteSource file, byte[] header, SwitchKeys keys, string path, bool showSystem, byte[]? titleKey)
    {
        var nodes = new List<DiscNode>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var body = BodyKeys(header, keys, titleKey);
        for (var index = 0; index < 4; index++)
        {
            if (!TrySection(file, header, index, body, out var section))
            {
                continue;
            }

            var folder = section.Kind == SectionKind.RomFs
                ? RomFolder(section, path, showSystem, Take(used, "RomFS"))
                : PfsFolder(section, path, showSystem, used);
            if (folder is not null)
            {
                nodes.Add(folder);
            }
        }

        return nodes;
    }

    private static string Take(HashSet<string> used, string name)
    {
        if (used.Add(name))
        {
            return name;
        }

        for (var i = 2; i < 100; i++)
        {
            var next = name + i.ToString(CultureInfo.InvariantCulture);
            if (used.Add(next))
            {
                return next;
            }
        }

        return name;
    }

    private static DiscNode? RomFolder(Section section, string path, bool showSystem, string name)
    {
        if (section.LevelSize <= 0)
        {
            return null;
        }

        var rom = RomFs.TryParse(section.Body, section.LevelOffset, section.LevelSize);
        if (rom is null)
        {
            return null;
        }

        var folder = DiscNode.Directory(name, VirtualPath.Combine(path, name), showSystem);
        folder.SetChildren(RomChildren(section.Body, rom, folder.FullPath, showSystem, section.LevelOffset));
        return folder.AllChildren.Count == 0 ? null : folder;
    }

    private static List<DiscNode> RomChildren(IByteSource body, RomFs.Node node, string path, bool showSystem, long imageBase)
    {
        var children = new List<DiscNode>();
        foreach (var child in node.Children)
        {
            var childPath = VirtualPath.Combine(path, child.Name);
            if (child.Directory)
            {
                var dir = DiscNode.Directory(child.Name, childPath, showSystem);
                dir.SetChildren(RomChildren(body, child, childPath, showSystem, imageBase));
                children.Add(dir);
                continue;
            }

            if (child.DataOffset < 0 || child.Size < 0 || imageBase > long.MaxValue - child.DataOffset)
            {
                continue;
            }

            var start = imageBase + child.DataOffset;
            var size = child.Size;
            if (start < 0 || start > body.Length || size > body.Length - start)
            {
                continue;
            }

            children.Add(DiscNode.File(child.Name, childPath, size, showSystem, () => new ByteSourceStream(body, start, size)));
        }

        return children;
    }

    private static DiscNode? PfsFolder(Section section, string path, bool showSystem, HashSet<string> used)
    {
        if (!PartitionFs.TryParse(section.Body, section.LevelOffset, section.LevelOffset + section.LevelSize, hashed: false, out var entries, out _))
        {
            return null;
        }

        var exefs = entries.Any(entry =>
            entry.Name.Equals("main", StringComparison.OrdinalIgnoreCase)
            || entry.Name.Equals("main.npdm", StringComparison.OrdinalIgnoreCase));
        var name = Take(used, exefs ? "ExeFS" : "PFS0");
        var folder = DiscNode.Directory(name, VirtualPath.Combine(path, name), showSystem);
        var children = new List<DiscNode>();
        foreach (var entry in entries)
        {
            if (entry.Offset < 0 || entry.Size < 0 || entry.Offset > section.Body.Length || entry.Size > section.Body.Length - entry.Offset)
            {
                continue;
            }

            var start = entry.Offset;
            var size = entry.Size;
            children.Add(DiscNode.File(entry.Name, VirtualPath.Combine(folder.FullPath, entry.Name), size, showSystem, () => new ByteSourceStream(section.Body, start, size)));
        }

        if (children.Count == 0)
        {
            return null;
        }

        folder.SetChildren(children);
        return folder;
    }

    private static bool TrySection(IByteSource file, byte[] header, int index, BodyMaterial keys, out Section section)
    {
        section = default;
        var entry = header.AsSpan(FsEntry + (index * 0x10));
        var startBlock = BinaryPrimitives.ReadUInt32LittleEndian(entry);
        var endBlock = BinaryPrimitives.ReadUInt32LittleEndian(entry[4..]);
        if (endBlock <= startBlock)
        {
            return false;
        }

        long start = startBlock * (long)NintendoAes.Sector;
        long end = endBlock * (long)NintendoAes.Sector;
        if (start < HeaderSize || start >= file.Length)
        {
            return false;
        }

        if (end > file.Length)
        {
            end = file.Length;
        }

        end = start + ((end - start) / NintendoAes.Sector * NintendoAes.Sector);
        if (end <= start)
        {
            return false;
        }

        var fs = header.AsSpan(FsHeaderBase + (index * NintendoAes.Sector), NintendoAes.Sector);
        if (fs[0] == 0 && fs[FsType] == 0 && fs[EncryptionType] == 0)
        {
            return false;
        }

        var fsType = fs[FsType];
        var hashType = fs[HashType];
        var encryption = fs[EncryptionType];
        if (hashType == 0)
        {
            hashType = fsType == 0 ? (byte)3 : (byte)2;
        }

        if (encryption == 0)
        {
            encryption = header[Distribution] == 1 ? (byte)2 : (byte)3;
        }

        if (!TryLevel(fs, hashType, end - start, out var levelOffset, out var levelSize))
        {
            return false;
        }

        IByteSource body = new SourceWindow(file, start, end - start);
        if (encryption == 2)
        {
            if (keys.Xts is not { Length: 32 })
            {
                return false;
            }

            body = new CipherSource(body, start, xts: true, keys.Xts, ctrUpper: null);
        }
        else if (encryption == 3)
        {
            if (keys.Ctr is not { Length: 16 })
            {
                return false;
            }

            body = new CipherSource(body, start, xts: false, keys.Ctr, fs.Slice(CtrUpper, 8).ToArray());
        }
        else if (encryption != 1)
        {
            return false;
        }

        var kind = fsType switch
        {
            0 => SectionKind.RomFs,
            1 => SectionKind.Pfs,
            _ => SectionKind.None
        };
        if (kind == SectionKind.None)
        {
            return false;
        }

        section = new Section(body, levelOffset, levelSize, kind);
        return true;
    }

    private static bool TryLevel(ReadOnlySpan<byte> fs, byte hashType, long sectionLength, out long offset, out long size)
    {
        offset = 0;
        size = 0;
        var hash = fs[HashData..];
        if (hashType == 2)
        {
            var layers = BinaryPrimitives.ReadInt32LittleEndian(hash[0x24..]);
            if (layers is < 1 or > 5)
            {
                return false;
            }

            var region = 0x28 + ((layers - 1) * 0x10);
            offset = BinaryPrimitives.ReadInt64LittleEndian(hash[region..]);
            size = BinaryPrimitives.ReadInt64LittleEndian(hash[(region + 8)..]);
        }
        else if (hashType == 3)
        {
            if (!hash[..4].SequenceEqual("IVFC"u8))
            {
                return false;
            }

            var max = BinaryPrimitives.ReadInt32LittleEndian(hash[0xC..]);
            if (max is < 1 or > 6)
            {
                max = 6;
            }

            long bestSize = -1;
            for (var i = 0; i < max; i++)
            {
                var level = hash[(0x10 + (i * 0x18))..];
                var levelOffset = BinaryPrimitives.ReadInt64LittleEndian(level);
                var levelSize = BinaryPrimitives.ReadInt64LittleEndian(level[8..]);
                if (levelSize > bestSize)
                {
                    bestSize = levelSize;
                    offset = levelOffset;
                    size = levelSize;
                }
            }
        }
        else
        {
            return false;
        }

        return offset >= 0 && size > 0 && offset <= sectionLength && size <= sectionLength - offset;
    }

    private static BodyMaterial BodyKeys(byte[] header, SwitchKeys keys, byte[]? titleKey)
    {
        byte[]? xts = null;
        byte[]? ctr = titleKey is { Length: 16 } ? titleKey : null;
        if (!HasRights(header))
        {
            var kaek = keys.KeyAreaKey(header[KaekIndex], MasterKey(header));
            if (kaek is not null)
            {
                var area = header.AsSpan(KeyArea, 0x40).ToArray();
                for (var i = 0; i < 4; i++)
                {
                    NintendoAes.Ecb(area.AsSpan(i * 16, 16), kaek, encrypt: false);
                }

                xts = area.AsSpan(0, 32).ToArray();
                ctr ??= area.AsSpan(32, 16).ToArray();
            }
        }

        return new BodyMaterial(xts, ctr);
    }

    private static bool HasRights(ReadOnlySpan<byte> header)
    {
        foreach (var value in header.Slice(RightsId, 16))
        {
            if (value != 0)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsMagic(ReadOnlySpan<byte> header)
    {
        return header.Length >= Magic + 4
            && header[Magic] == (byte)'N'
            && header[Magic + 1] == (byte)'C'
            && header[Magic + 2] == (byte)'A'
            && header[Magic + 3] is (byte)'0' or (byte)'1' or (byte)'2' or (byte)'3';
    }

    private static void FixNca2SectionHeaders(byte[] header, byte[] headerKey)
    {
        for (var i = 0; i < 4; i++)
        {
            var slice = header.AsSpan(FsHeaderBase + (i * NintendoAes.Sector), NintendoAes.Sector);
            var sector = 2 + i;
            NintendoAes.Xts(slice, headerKey, sector, encrypt: true);
            NintendoAes.Xts(slice, headerKey, sector: 0, encrypt: false);
        }
    }

    private enum SectionKind
    {
        None,
        RomFs,
        Pfs
    }

    private readonly record struct Section(IByteSource Body, long LevelOffset, long LevelSize, SectionKind Kind);

    private readonly record struct BodyMaterial(byte[]? Xts, byte[]? Ctr);

    private sealed class CipherSource : IByteSource
    {
        private readonly IByteSource _raw;
        private readonly long _sectionStart;
        private readonly bool _xts;
        private readonly byte[] _key;
        private readonly byte[]? _upper;

        public CipherSource(IByteSource raw, long sectionStart, bool xts, byte[] key, byte[]? ctrUpper)
        {
            _raw = raw;
            _sectionStart = sectionStart;
            _xts = xts;
            _key = key;
            _upper = ctrUpper;
            Length = raw.Length;
        }

        public long Length { get; }

        public int ReadAt(long position, Span<byte> destination)
        {
            if (position < 0 || position >= Length || destination.IsEmpty)
            {
                return 0;
            }

            var count = (int)Math.Min(destination.Length, Length - position);
            var done = 0;
            while (done < count)
            {
                var pos = position + done;
                var skip = (int)(pos % NintendoAes.Sector);
                var sectorStart = pos - skip;
                var maxEnd = Math.Min(Length, sectorStart + (32 * 1024));
                var sectorBytes = (int)(maxEnd - sectorStart);
                sectorBytes -= sectorBytes % NintendoAes.Sector;
                if (sectorBytes < NintendoAes.Sector)
                {
                    break;
                }

                var rented = ArrayPool<byte>.Shared.Rent(sectorBytes);
                try
                {
                    var got = PartitionFs.ReadExact(_raw, sectorStart, rented.AsSpan(0, sectorBytes));
                    var aligned = got - (got % NintendoAes.Sector);
                    if (aligned <= skip)
                    {
                        break;
                    }

                    var plain = rented.AsSpan(0, aligned);
                    var absolute = _sectionStart + sectorStart;
                    if (_xts)
                    {
                        NintendoAes.Xts(plain, _key, absolute / NintendoAes.Sector, encrypt: false);
                    }
                    else
                    {
                        NintendoAes.Ctr(plain, _key, _upper ?? throw new InvalidOperationException("CTR needs its upper counter."), absolute);
                    }

                    var take = Math.Min(aligned - skip, count - done);
                    plain.Slice(skip, take).CopyTo(destination.Slice(done, take));
                    done += take;
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(rented, clearArray: true);
                }
            }

            return done;
        }
    }
}
