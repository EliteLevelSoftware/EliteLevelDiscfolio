using System.Buffers.Binary;
using System.Text;
using EliteLevel.Disc.Core.IO;

namespace EliteLevel.Disc.Core.Formats.Xdvdfs;

/// <summary>
/// Read-only XDVDFS / XISO reader for original Xbox and Xbox 360 images, including XGD1, XGD2, XGD3
/// and a Redump-style image that keeps a video partition in front of the game partition.
///
/// Written from public format documentation only:
/// xboxdevwiki "XDVDFS" (2048-byte sectors, volume descriptor at sector 32 of the game partition,
/// "MICROSOFT*XBOX*MEDIA" at offset 0 and again at 0x7EC, root directory sector and size,
/// FILETIME, directory attribute bits) and the Xbox-Linux document "Xbox DVD Filesystem Details"
/// (https://multimedia.cx/xdvdfs.html): directory entries form a binary tree, left and right links
/// are dword offsets from the start of the directory table, and unused directory bytes are 0xFF.
///
/// This file is not derived from extract-xiso or XISOSharp.
/// Security sectors, disc authentication and executable decryption are out of scope.
/// </summary>
public sealed class XdvdfsFormat : IImageFormat
{
    public const int SectorSize = 2048;
    public const int DescriptorSector = 32;
    public const long DescriptorOffset = DescriptorSector * (long)SectorSize;

    public static ReadOnlySpan<byte> Magic => "MICROSOFT*XBOX*MEDIA"u8;

    /// <summary>Published game-partition bases. The volume descriptor sits 32 sectors after each base.</summary>
    public static readonly (string Layout, long PartitionBase)[] Layouts =
    [
        ("XGD1", 0x18300000L),
        ("XGD2", 0x0FD90000L),
        ("XGD3", 0x02080000L),
        ("plain", 0L)
    ];

    public string Id => "xdvdfs";
    public string DisplayName => "XDVDFS";
    public string SupportPhrase => "Xbox and Xbox 360 XDVDFS";

    public bool CanOpen(Stream probe, ImageHints hints)
    {
        if (!probe.CanSeek)
        {
            return false;
        }

        var position = probe.Position;
        try
        {
            return TryLocate(probe, out _);
        }
        finally
        {
            if (probe.CanSeek)
            {
                probe.Position = position;
            }
        }
    }

    public Task<IDiscImage> OpenAsync(Stream source, OpenOptions options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryLocate(source, out var location))
        {
            throw new DiscFormatException(DiscFormatException.Unrecognized);
        }

        var volume = XdvdfsVolume.Open(source, location, options.ShowSystemUpdate);
        return Task.FromResult<IDiscImage>(volume);
    }

    internal static bool TryLocate(Stream stream, out XdvdfsLocation location)
    {
        foreach (var (layout, partitionBase) in Layouts)
        {
            var descriptor = partitionBase + DescriptorOffset;
            if (IsDescriptor(stream, descriptor, partitionBase))
            {
                var video = partitionBase > 0 && HasVideoMarker(stream, partitionBase);
                location = new XdvdfsLocation(partitionBase, descriptor, layout, video);
                return true;
            }
        }

        if (IsDescriptor(stream, 0, 0))
        {
            location = new XdvdfsLocation(0, 0, "plain", false);
            return true;
        }

        location = default;
        return false;
    }

    private static bool IsDescriptor(Stream stream, long descriptorOffset, long partitionBase)
    {
        if (descriptorOffset < 0 || descriptorOffset + SectorSize > stream.Length)
        {
            return false;
        }

        var buffer = new byte[SectorSize];
        stream.Position = descriptorOffset;
        if (stream.Read(buffer, 0, SectorSize) != SectorSize)
        {
            return false;
        }

        if (!buffer.AsSpan(0, Magic.Length).SequenceEqual(Magic))
        {
            return false;
        }

        if (!buffer.AsSpan(0x7EC, Magic.Length).SequenceEqual(Magic))
        {
            return false;
        }

        var rootSector = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(0x14));
        var rootSize = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(0x18));
        if (rootSize > 32 * 1024 * 1024 || (rootSize & 3) != 0)
        {
            return false;
        }

        if (rootSize == 0)
        {
            return true;
        }

        var start = partitionBase + rootSector * (long)SectorSize;
        return start >= 0 && start + rootSize <= stream.Length;
    }

    private static bool HasVideoMarker(Stream stream, long partitionBase)
    {
        var length = (int)Math.Min(partitionBase, 64 * 1024);
        if (length < 8)
        {
            return false;
        }

        var buffer = new byte[length];
        stream.Position = 0;
        var read = stream.Read(buffer, 0, length);
        return Encoding.ASCII.GetString(buffer, 0, read).Contains("CD001", StringComparison.Ordinal);
    }
}

internal readonly record struct XdvdfsLocation(long PartitionBase, long DescriptorOffset, string Layout, bool VideoPartition);

internal sealed class XdvdfsVolume : IDiscImage
{
    private readonly Stream _source;
    private bool _disposed;

    private XdvdfsVolume(Stream source, ImageIdentity identity, DiscNode root)
    {
        _source = source;
        Identity = identity;
        Root = root;
    }

    public ImageIdentity Identity { get; }
    public IDiscNode Root { get; }

    public static XdvdfsVolume Open(Stream source, XdvdfsLocation location, bool showSystem)
    {
        var header = new byte[XdvdfsFormat.SectorSize];
        source.Position = location.DescriptorOffset;
        if (source.Read(header, 0, header.Length) != header.Length)
        {
            throw new DiscFormatException(DiscFormatException.Unrecognized);
        }

        var rootSector = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0x14));
        var rootSize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0x18));
        var gate = new object();
        var seenDirectories = new HashSet<long>();
        var root = DiscNode.Directory(string.Empty, "/", showSystem);
        var children = ReadDirectory(source, gate, location.PartitionBase, rootSector, rootSize, "/", showSystem, seenDirectories);
        root.SetChildren(children);
        var stats = NodeWalk.Measure(root);
        var partitions = location.VideoPartition
            ? new[] { "Video", "Game" }
            : new[] { "Game" };
        var layout = location.VideoPartition ? location.Layout + " Redump" : location.Layout;
        var identity = new ImageIdentity(
            "xdvdfs",
            "XDVDFS",
            null,
            NodeWalk.DetectPlatform(root, "xdvdfs"),
            layout,
            location.VideoPartition,
            partitions,
            stats.Files,
            stats.Directories,
            stats.Bytes,
            stats.ProtectedFiles,
            "image");
        return new XdvdfsVolume(source, identity, root);
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        _source.Dispose();
        return ValueTask.CompletedTask;
    }

    private static List<DiscNode> ReadDirectory(
        Stream source,
        object gate,
        long partitionBase,
        uint sector,
        uint size,
        string parentPath,
        bool showSystem,
        HashSet<long> seenDirectories)
    {
        var children = new List<DiscNode>();
        if (size == 0 || sector == 0)
        {
            return children;
        }

        var key = ((long)sector << 32) | size;
        if (!seenDirectories.Add(key))
        {
            return children;
        }

        var offset = partitionBase + sector * (long)XdvdfsFormat.SectorSize;
        if (offset < 0 || size > 32 * 1024 * 1024 || offset + size > source.Length)
        {
            return children;
        }

        var table = new byte[size];
        source.Position = offset;
        if (source.Read(table, 0, table.Length) != table.Length)
        {
            return children;
        }

        foreach (var entry in ParseEntries(table))
        {
            if (!IsSafeName(entry.Name))
            {
                continue;
            }

            var path = VirtualPath.Combine(parentPath, entry.Name);
            if (entry.IsDirectory)
            {
                var dir = DiscNode.Directory(entry.Name, path, showSystem);
                dir.SetChildren(ReadDirectory(source, gate, partitionBase, entry.Sector, entry.Size, path, showSystem, seenDirectories));
                children.Add(dir);
            }
            else
            {
                var fileOffset = partitionBase + entry.Sector * (long)XdvdfsFormat.SectorSize;
                if (entry.Size > 0 && (fileOffset < 0 || fileOffset + entry.Size > source.Length))
                {
                    continue;
                }

                var length = entry.Size;
                var start = entry.Size == 0 ? 0 : fileOffset;
                children.Add(DiscNode.File(entry.Name, path, length, showSystem, () =>
                    new WindowStream(source, gate, start, length)));
            }
        }

        return children;
    }

    private static List<XdvdfsEntry> ParseEntries(byte[] table)
    {
        var entries = new List<XdvdfsEntry>();
        var visited = new HashSet<int>();
        Walk(0);
        return entries;

        void Walk(int offset)
        {
            if (offset < 0 || offset + 14 > table.Length || !visited.Add(offset))
            {
                return;
            }

            if (table[offset] == 0xFF && table[offset + 1] == 0xFF)
            {
                return;
            }

            var left = BinaryPrimitives.ReadUInt16LittleEndian(table.AsSpan(offset));
            var right = BinaryPrimitives.ReadUInt16LittleEndian(table.AsSpan(offset + 2));
            var sector = BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(offset + 4));
            var size = BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(offset + 8));
            var attributes = table[offset + 12];
            var nameLength = table[offset + 13];
            if (nameLength == 0 || offset + 14 + nameLength > table.Length)
            {
                return;
            }

            var name = Encoding.Latin1.GetString(table, offset + 14, nameLength);
            if (left is not 0 and not 0xFFFF)
            {
                Walk(left * 4);
            }

            entries.Add(new XdvdfsEntry(name, sector, size, (attributes & 0x10) != 0));
            if (right is not 0 and not 0xFFFF)
            {
                Walk(right * 4);
            }
        }
    }

    private static bool IsSafeName(string name)
    {
        if (name.Length == 0 || name.Length > 255)
        {
            return false;
        }

        return name.IndexOfAny(['/', '\\', '\0']) < 0 && name is not "." and not "..";
    }

    private readonly record struct XdvdfsEntry(string Name, uint Sector, uint Size, bool IsDirectory);
}
