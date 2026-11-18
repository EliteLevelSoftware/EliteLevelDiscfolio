using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace EliteLevel.Disc.Core.Titles;

/// <summary>
/// Plaintext fields of an Xbox 360 XEX2 module. Execution info and the resource
/// directory follow the public XEX2 header (big-endian optional headers; the low
/// byte of a header id selects an inline value or a file offset). The embedded
/// SPA resource is an XDBF database: string tables are XSTR blocks and the title
/// image is the image entry 0x8000. Encrypted executable bytes are not read.
/// </summary>
internal static class XexTitle
{
    private const uint Magic = 0x58455832;
    private const uint ResourceInfo = 0x000002FF;
    private const uint ExecutionInfo = 0x00040006;
    private const uint XdbfMagic = 0x58444246;
    private const uint XstrMagic = 0x58535452;
    private const uint XstcMagic = 0x58535443;
    private const ulong TitleStringId = 0x8000;
    private const ulong XstcId = 0x58535443;

    public static bool TryRead(ReadOnlySpan<byte> data, out string? title, out string? titleId, out string? version, out byte[]? icon)
    {
        title = null;
        titleId = null;
        version = null;
        icon = null;
        if (data.Length < 0x18 || ReadU32(data, 0) != Magic)
        {
            return false;
        }

        var count = ReadU32(data, 0x14);
        if (count > 4096)
        {
            return false;
        }

        uint? execution = null;
        uint? resources = null;
        var directory = 0x18;
        for (var i = 0; i < count; i++)
        {
            var entry = directory + i * 8;
            if (entry + 8 > data.Length)
            {
                break;
            }

            var key = ReadU32(data, entry);
            var value = ReadU32(data, entry + 4);
            if (key == ExecutionInfo)
            {
                execution = value;
            }
            else if (key == ResourceInfo)
            {
                resources = value;
            }
        }

        if (execution is uint exec && exec + 0x18 <= (uint)data.Length)
        {
            version = FormatVersion(ReadU32(data, (int)exec + 4));
            titleId = ReadU32(data, (int)exec + 0x0C).ToString("X8", CultureInfo.InvariantCulture);
        }

        var loadAddress = LoadAddress(data);
        if (resources is uint res)
        {
            TryResource(data, res, loadAddress, ref title, ref icon);
        }

        if (title is null || icon is null)
        {
            Scan(data, ref title, ref icon);
        }

        return titleId is not null || title is not null || icon is not null;
    }

    /// <summary>
    /// Xenia loads the big-endian version dword onto the host and then reads it as
    /// major:4, minor:4, build:16, qfe:8 from the low bit. A zero qfe is omitted.
    /// </summary>
    internal static string? FormatVersion(uint value)
    {
        if (value == 0)
        {
            return null;
        }

        var major = value & 0xF;
        var minor = (value >> 4) & 0xF;
        var build = (value >> 8) & 0xFFFF;
        var qfe = (value >> 24) & 0xFF;
        return qfe == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{major}.{minor}.{build}")
            : string.Create(CultureInfo.InvariantCulture, $"{major}.{minor}.{build}.{qfe}");
    }

    private static uint LoadAddress(ReadOnlySpan<byte> data)
    {
        if (data.Length < 0x14)
        {
            return 0;
        }

        var security = ReadU32(data, 0x10);
        if (security + 0x114 > (uint)data.Length)
        {
            return 0;
        }

        return ReadU32(data, (int)security + 0x110);
    }

    private static void TryResource(ReadOnlySpan<byte> data, uint offset, uint loadAddress, ref string? title, ref byte[]? icon)
    {
        if (offset + 4 > (uint)data.Length)
        {
            return;
        }

        var size = ReadU32(data, (int)offset);
        if (size < 4 || offset + size > (uint)data.Length || size > 1_048_576)
        {
            return;
        }

        var count = (size - 4) / 16;
        for (var i = 0; i < count; i++)
        {
            var rec = (int)offset + 4 + i * 16;
            var address = ReadU32(data, rec + 8);
            var length = ReadU32(data, rec + 12);
            if (!TrySlice(data, address, length, out var slice)
                && !(loadAddress != 0 && address >= loadAddress && TrySlice(data, address - loadAddress, length, out slice)))
            {
                continue;
            }

            Take(slice, ref title, ref icon);
        }
    }

    private static bool TrySlice(ReadOnlySpan<byte> data, uint offset, uint length, out ReadOnlySpan<byte> slice)
    {
        slice = default;
        if (length < 24 || length > data.Length || offset > (uint)data.Length || offset + length > (uint)data.Length)
        {
            return false;
        }

        slice = data.Slice((int)offset, (int)length);
        return ReadU32(slice, 0) == XdbfMagic;
    }

    private static void Scan(ReadOnlySpan<byte> data, ref string? title, ref byte[]? icon)
    {
        var limit = Math.Min(data.Length, DiscRead.HeaderCap);
        for (var i = 0; i + 24 < limit; i++)
        {
            if (data[i] != (byte)'X' || data[i + 1] != (byte)'D' || data[i + 2] != (byte)'B' || data[i + 3] != (byte)'F')
            {
                continue;
            }

            Take(data[i..], ref title, ref icon);
            if (title is not null && icon is not null)
            {
                return;
            }
        }
    }

    private static void Take(ReadOnlySpan<byte> xdbf, ref string? title, ref byte[]? icon)
    {
        if (!TryParseXdbf(xdbf, out var parsedTitle, out var parsedIcon))
        {
            return;
        }

        title ??= parsedTitle;
        icon ??= parsedIcon;
    }

    private static bool TryParseXdbf(ReadOnlySpan<byte> data, out string? title, out byte[]? icon)
    {
        title = null;
        icon = null;
        if (data.Length < 24 || ReadU32(data, 0) != XdbfMagic)
        {
            return false;
        }

        var entryCount = ReadU32(data, 8);
        var entryUsed = ReadU32(data, 12);
        var freeCount = ReadU32(data, 16);
        if (entryCount > 10000 || entryUsed > entryCount || freeCount > 10000)
        {
            return false;
        }

        var table = (long)entryCount * 18 + (long)freeCount * 8;
        if (24 + table > data.Length)
        {
            return false;
        }

        var content = 24 + (int)table;
        var strings = new Dictionary<int, string>();
        int? defaultLanguage = null;
        for (var i = 0; i < entryUsed; i++)
        {
            var entry = 24 + i * 18;
            var section = ReadU16(data, entry);
            var id = ReadU64(data, entry + 2);
            var offset = ReadU32(data, entry + 10);
            var size = ReadU32(data, entry + 14);
            if (size > data.Length - content || offset > data.Length - content || offset + size > data.Length - content)
            {
                continue;
            }

            var block = data.Slice(content + (int)offset, (int)size);
            if (section == 3 && id <= int.MaxValue && TryXstrTitle(block, out var text))
            {
                strings[(int)id] = text;
            }
            else if (section == 2 && id == TitleStringId && icon is null)
            {
                icon = ImageSniff.AsImage(block);
            }
            else if (section == 1 && id == XstcId && block.Length >= 16 && ReadU32(block, 0) == XstcMagic)
            {
                defaultLanguage = (int)ReadU32(block, 12);
            }
        }

        if (strings.TryGetValue(1, out var english))
        {
            title = english;
        }
        else if (defaultLanguage is int language && strings.TryGetValue(language, out var localized))
        {
            title = localized;
        }
        else if (strings.Count > 0)
        {
            title = strings.OrderBy(pair => pair.Key).First().Value;
        }

        return title is not null || icon is not null;
    }

    private static bool TryXstrTitle(ReadOnlySpan<byte> block, out string title)
    {
        title = string.Empty;
        if (block.Length < 14 || ReadU32(block, 0) != XstrMagic || ReadU32(block, 4) != 1)
        {
            return false;
        }

        var count = ReadU16(block, 12);
        var ptr = 14;
        for (var i = 0; i < count && ptr + 4 <= block.Length; i++)
        {
            var id = ReadU16(block, ptr);
            var length = ReadU16(block, ptr + 2);
            ptr += 4;
            if (length > block.Length - ptr)
            {
                return false;
            }

            if (id == TitleStringId)
            {
                var text = Encoding.UTF8.GetString(block.Slice(ptr, length)).Trim('\0', ' ', '\t', '\r', '\n');
                if (text.Length > 0)
                {
                    title = text;
                    return true;
                }
            }

            ptr += length;
        }

        return false;
    }

    private static ushort ReadU16(ReadOnlySpan<byte> data, int offset) =>
        BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset, 2));

    private static uint ReadU32(ReadOnlySpan<byte> data, int offset) =>
        BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, 4));

    private static ulong ReadU64(ReadOnlySpan<byte> data, int offset) =>
        BinaryPrimitives.ReadUInt64BigEndian(data.Slice(offset, 8));
}
