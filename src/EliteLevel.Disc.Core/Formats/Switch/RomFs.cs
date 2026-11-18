using System.Buffers.Binary;
using System.Text;

namespace EliteLevel.Disc.Core.Formats.Switch;

/// <summary>
/// RomFS as documented for Switch content: a 0x50-byte header, directory and file tables, then file bytes.
/// A directory entry is 0x18 bytes plus a name padded to 4. A file entry is 0x20 bytes plus a name padded to 4.
/// Links are offsets inside their own table, or 0xFFFFFFFF when there is no link.
/// File bytes start at <c>fileDataOffset + entry.offset</c> within the RomFS image.
/// </summary>
internal static class RomFs
{
    public const int HeaderSize = 0x50;
    public const uint NoLink = 0xFFFFFFFF;
    public const int MaxMetadata = 32 * 1024 * 1024;

    public sealed class Node
    {
        public required string Name { get; init; }
        public required bool Directory { get; init; }
        public long DataOffset { get; init; }
        public long Size { get; init; }
        public List<Node> Children { get; } = [];
    }

    public static Node? TryParse(IByteSource source, long imageOffset, long imageSize)
    {
        if (imageOffset < 0 || imageSize < HeaderSize || imageOffset > source.Length - HeaderSize)
        {
            return null;
        }

        var available = Math.Min(imageSize, source.Length - imageOffset);
        Span<byte> header = stackalloc byte[HeaderSize];
        if (PartitionFs.ReadExact(source, imageOffset, header) != HeaderSize)
        {
            return null;
        }

        var headerSize = BinaryPrimitives.ReadInt64LittleEndian(header);
        var dirTable = BinaryPrimitives.ReadInt64LittleEndian(header[0x18..]);
        var dirSize = BinaryPrimitives.ReadInt64LittleEndian(header[0x20..]);
        var fileTable = BinaryPrimitives.ReadInt64LittleEndian(header[0x38..]);
        var fileSize = BinaryPrimitives.ReadInt64LittleEndian(header[0x40..]);
        var dataOffset = BinaryPrimitives.ReadInt64LittleEndian(header[0x48..]);
        if (headerSize < HeaderSize || headerSize > 0x200 || dataOffset < 0 || dataOffset > available)
        {
            return null;
        }

        if (!InRange(dirTable, dirSize, available) || !InRange(fileTable, fileSize, available) || dirSize < 0x18)
        {
            return null;
        }

        var metaEnd = Math.Max(dirTable + dirSize, fileTable + fileSize);
        if (metaEnd < HeaderSize || metaEnd > MaxMetadata || metaEnd > available)
        {
            return null;
        }

        var blob = new byte[metaEnd];
        if (PartitionFs.ReadExact(source, imageOffset, blob) != blob.Length)
        {
            return null;
        }

        var root = new Node { Name = "", Directory = true };
        var seenDirs = new HashSet<uint>();
        Fill(root, 0, blob, dirTable, dirSize, fileTable, fileSize, dataOffset, seenDirs, 0);
        return root;
    }

    private static void Fill(
        Node directory,
        uint dirOff,
        byte[] blob,
        long dirTable,
        long dirSize,
        long fileTable,
        long fileSize,
        long dataOffset,
        HashSet<uint> seenDirs,
        int depth)
    {
        if (depth > 32 || !seenDirs.Add(dirOff) || !TryDir(blob, dirTable, dirSize, dirOff, out var entry))
        {
            return;
        }

        var seenFiles = new HashSet<uint>();
        var file = entry.FirstFile;
        while (file != NoLink)
        {
            if (!seenFiles.Add(file) || !TryFile(blob, fileTable, fileSize, file, out var fileEntry))
            {
                break;
            }

            if (fileEntry.Name is not null
                && fileEntry.DataOffset >= 0
                && fileEntry.Size >= 0
                && dataOffset <= long.MaxValue - fileEntry.DataOffset)
            {
                directory.Children.Add(new Node
                {
                    Name = fileEntry.Name,
                    Directory = false,
                    DataOffset = dataOffset + fileEntry.DataOffset,
                    Size = fileEntry.Size
                });
            }

            file = fileEntry.Sibling;
        }

        var child = entry.Child;
        var seenChildren = new HashSet<uint>();
        while (child != NoLink)
        {
            if (!seenChildren.Add(child) || !TryDir(blob, dirTable, dirSize, child, out var childEntry))
            {
                break;
            }

            if (childEntry.Name is not null)
            {
                var node = new Node { Name = childEntry.Name, Directory = true };
                Fill(node, child, blob, dirTable, dirSize, fileTable, fileSize, dataOffset, seenDirs, depth + 1);
                directory.Children.Add(node);
            }

            child = childEntry.Sibling;
        }
    }

    private readonly record struct DirEntry(uint Sibling, uint Child, uint FirstFile, string? Name);

    private readonly record struct FileEntry(uint Sibling, long DataOffset, long Size, string? Name);

    private static bool TryDir(byte[] blob, long table, long size, uint offset, out DirEntry entry)
    {
        entry = default;
        if (size < 0x18 || offset > size - 0x18)
        {
            return false;
        }

        var start = table + offset;
        if (start < 0 || start + 0x18 > blob.Length)
        {
            return false;
        }

        var span = blob.AsSpan((int)start);
        var name = ReadName(span[0x18..], BinaryPrimitives.ReadInt32LittleEndian(span[0x14..]), allowEmpty: offset == 0);
        entry = new DirEntry(
            BinaryPrimitives.ReadUInt32LittleEndian(span[4..]),
            BinaryPrimitives.ReadUInt32LittleEndian(span[8..]),
            BinaryPrimitives.ReadUInt32LittleEndian(span[0xC..]),
            name);
        return true;
    }

    private static bool TryFile(byte[] blob, long table, long size, uint offset, out FileEntry entry)
    {
        entry = default;
        if (size < 0x20 || offset > size - 0x20)
        {
            return false;
        }

        var start = table + offset;
        if (start < 0 || start + 0x20 > blob.Length)
        {
            return false;
        }

        var span = blob.AsSpan((int)start);
        entry = new FileEntry(
            BinaryPrimitives.ReadUInt32LittleEndian(span[4..]),
            BinaryPrimitives.ReadInt64LittleEndian(span[8..]),
            BinaryPrimitives.ReadInt64LittleEndian(span[0x10..]),
            ReadName(span[0x20..], BinaryPrimitives.ReadInt32LittleEndian(span[0x1C..]), allowEmpty: false));
        return true;
    }

    private static string? ReadName(ReadOnlySpan<byte> tail, int length, bool allowEmpty)
    {
        if (length < 0 || length > 1024 || length > tail.Length)
        {
            return null;
        }

        if (length == 0)
        {
            return allowEmpty ? "" : null;
        }

        var name = Encoding.UTF8.GetString(tail[..length]).TrimEnd('\0').Trim();
        if (name.Length == 0 || name is "." or ".." || name.IndexOfAny(['/', '\\', ':']) >= 0)
        {
            return null;
        }

        return name;
    }

    private static bool InRange(long offset, long size, long available)
    {
        if (offset < 0 || size < 0 || offset > available)
        {
            return false;
        }

        return size <= available - offset;
    }
}
