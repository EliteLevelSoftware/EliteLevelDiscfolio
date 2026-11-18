using System.Buffers.Binary;
using System.Text;

namespace EliteLevel.Disc.Core.Formats.Switch;

/// <summary>
/// PFS0 (NSP, ExeFS) and HFS0 (game card partitions).
/// Both are a 0x10-byte header, a file table, a string table, then file data.
/// A PFS0 entry is 0x18 bytes. An HFS0 entry is 0x40 bytes: the same fields, 8 reserved bytes, then a SHA-256.
/// File offsets are relative to the first byte after the string table.
/// Written from the public container layout. Hashes are not checked.
/// </summary>
internal static class PartitionFs
{
    public const int PfsEntrySize = 0x18;
    public const int HfsEntrySize = 0x40;
    public const int MaxEntries = 100_000;
    public const int MaxStringTable = 8 * 1024 * 1024;

    public readonly record struct Entry(string Name, long Offset, long Size);

    public static bool HasMagic(IByteSource source, long offset, ReadOnlySpan<byte> magic)
    {
        if (offset < 0 || offset > source.Length - magic.Length)
        {
            return false;
        }

        Span<byte> buffer = stackalloc byte[4];
        return source.ReadAt(offset, buffer) == 4 && buffer.SequenceEqual(magic);
    }

    public static bool TryParse(IByteSource source, long offset, long end, bool hashed, out IReadOnlyList<Entry> entries, out long dataStart)
    {
        entries = [];
        dataStart = 0;
        if (offset < 0 || end < offset || end > source.Length || end - offset < 0x10)
        {
            return false;
        }

        Span<byte> header = stackalloc byte[0x10];
        if (source.ReadAt(offset, header) != 0x10)
        {
            return false;
        }

        var magic = hashed ? "HFS0"u8 : "PFS0"u8;
        if (!header[..4].SequenceEqual(magic))
        {
            return false;
        }

        var count = BinaryPrimitives.ReadInt32LittleEndian(header[4..]);
        var stringSize = BinaryPrimitives.ReadInt32LittleEndian(header[8..]);
        if (count < 0 || count > MaxEntries || stringSize < 0 || stringSize > MaxStringTable)
        {
            return false;
        }

        var entrySize = hashed ? HfsEntrySize : PfsEntrySize;
        long tableBytes = (long)count * entrySize;
        if (!TryAdd(offset, 0x10, out var tableAt) || !TryAdd(tableAt, tableBytes, out var stringsAt))
        {
            return false;
        }

        if (!TryAdd(stringsAt, stringSize, out dataStart) || dataStart > end)
        {
            return false;
        }

        var table = new byte[tableBytes];
        if (tableBytes > 0 && ReadExact(source, tableAt, table) != table.Length)
        {
            return false;
        }

        var strings = new byte[stringSize];
        if (stringSize > 0 && ReadExact(source, stringsAt, strings) != strings.Length)
        {
            return false;
        }

        var list = new List<Entry>(count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < count; i++)
        {
            var entry = table.AsSpan(i * entrySize, entrySize);
            var relative = BinaryPrimitives.ReadInt64LittleEndian(entry);
            var size = BinaryPrimitives.ReadInt64LittleEndian(entry[8..]);
            var nameAt = BinaryPrimitives.ReadInt32LittleEndian(entry[0x10..]);
            if (relative < 0 || size < 0 || nameAt < 0)
            {
                continue;
            }

            if (!TryAdd(dataStart, relative, out var absolute) || !TryAdd(absolute, size, out var fileEnd))
            {
                continue;
            }

            if (absolute > source.Length)
            {
                continue;
            }

            // Some dumps round the last partition up by one media unit without writing the padding.
            if (fileEnd > source.Length)
            {
                if (fileEnd - source.Length > NintendoAes.Sector)
                {
                    continue;
                }

                size = source.Length - absolute;
            }

            if (end < source.Length && fileEnd > end + NintendoAes.Sector)
            {
                continue;
            }

            var name = Unique(ReadName(strings, nameAt), seen);
            if (name is null)
            {
                continue;
            }

            list.Add(new Entry(name, absolute, size));
        }

        entries = list;
        return true;
    }

    private static string? Unique(string? name, HashSet<string> seen)
    {
        if (name is null)
        {
            return null;
        }

        if (seen.Add(name))
        {
            return name;
        }

        for (var i = 2; i < 10_000; i++)
        {
            var next = name + "~" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (seen.Add(next))
            {
                return next;
            }
        }

        return null;
    }

    private static string? ReadName(byte[] strings, int offset)
    {
        if ((uint)offset >= (uint)strings.Length)
        {
            return null;
        }

        var end = offset;
        while (end < strings.Length && strings[end] != 0)
        {
            end++;
        }

        if (end == offset || end - offset > 1024)
        {
            return null;
        }

        var name = Encoding.UTF8.GetString(strings, offset, end - offset).Trim();
        if (name.Length == 0 || name is "." or ".." || name.IndexOfAny(['/', '\\', ':']) >= 0)
        {
            return null;
        }

        return name;
    }

    internal static int ReadExact(IByteSource source, long offset, Span<byte> destination)
    {
        var done = 0;
        while (done < destination.Length)
        {
            var read = source.ReadAt(offset + done, destination[done..]);
            if (read <= 0)
            {
                break;
            }

            done += read;
        }

        return done;
    }

    private static bool TryAdd(long left, long right, out long sum)
    {
        sum = 0;
        if (left < 0 || right < 0 || left > long.MaxValue - right)
        {
            return false;
        }

        sum = left + right;
        return true;
    }
}
