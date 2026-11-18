using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace EliteLevel.Disc.Core.Titles;

/// <summary>
/// Plaintext fields of an original Xbox XBE. The certificate holds the title id
/// and UTF-16LE title name; $$XTIMAGE (or $$XSIMAGE) is the embedded title image
/// when that section is a PNG or JPEG, or contains one. Section bytes that are
/// only a packed texture are left unused.
/// </summary>
internal static class XbeTitle
{
    public static bool TryRead(ReadOnlySpan<byte> data, out string? title, out string? titleId, out string? version, out byte[]? icon)
    {
        title = null;
        titleId = null;
        version = null;
        icon = null;
        if (data.Length < 0x124 || data[0] != (byte)'X' || data[1] != (byte)'B' || data[2] != (byte)'E' || data[3] != (byte)'H')
        {
            return false;
        }

        var baseAddress = ReadU32(data, 0x104);
        var certificate = ReadU32(data, 0x118);
        var sectionCount = ReadU32(data, 0x11C);
        var sectionHeaders = ReadU32(data, 0x120);
        if (!TryOffset(certificate, baseAddress, data.Length, out var certOff) || certOff + 0x5C > data.Length)
        {
            return false;
        }

        titleId = ReadU32(data, certOff + 0x08).ToString("X8", CultureInfo.InvariantCulture);
        title = TitleName(data.Slice(certOff + 0x0C, 80));
        version = Version(data, certOff);

        if (sectionCount is > 0 and <= 64 && TryOffset(sectionHeaders, baseAddress, data.Length, out var sectionOff))
        {
            icon = TitleImage(data, sectionOff, sectionCount, baseAddress);
        }

        return title is not null || titleId is not null || icon is not null;
    }

    private static string? Version(ReadOnlySpan<byte> data, int certOff)
    {
        if (certOff + 4 > data.Length)
        {
            return null;
        }

        var size = ReadU32(data, certOff);
        int versionOff;
        if (size >= 0x520)
        {
            versionOff = certOff + 0x51C;
        }
        else if (size >= 0xB0)
        {
            versionOff = certOff + 0xAC;
        }
        else
        {
            return null;
        }

        if (versionOff + 4 > data.Length)
        {
            return null;
        }

        var value = ReadU32(data, versionOff);
        if (value == 0)
        {
            return null;
        }

        return value.ToString(CultureInfo.InvariantCulture);
    }

    private static string? TitleName(ReadOnlySpan<byte> utf16)
    {
        var chars = new char[40];
        var count = 0;
        for (var i = 0; i + 1 < utf16.Length && count < chars.Length; i += 2)
        {
            var c = (char)(utf16[i] | (utf16[i + 1] << 8));
            if (c == '\0')
            {
                break;
            }

            chars[count++] = c;
        }

        var text = new string(chars, 0, count).Trim();
        return text.Length == 0 ? null : text;
    }

    private static byte[]? TitleImage(ReadOnlySpan<byte> data, int sectionOff, uint sectionCount, uint baseAddress)
    {
        byte[]? save = null;
        for (var i = 0; i < sectionCount; i++)
        {
            var header = sectionOff + i * 0x38;
            if (header + 0x38 > data.Length)
            {
                break;
            }

            var rawAddress = ReadU32(data, header + 0x0C);
            var rawSize = ReadU32(data, header + 0x10);
            var nameAddress = ReadU32(data, header + 0x14);
            if (!TryOffset(nameAddress, baseAddress, data.Length, out var nameOff))
            {
                continue;
            }

            var name = Ascii(data, nameOff);
            if (name is null)
            {
                continue;
            }

            var image = SectionImage(data, rawAddress, rawSize);
            if (image is null)
            {
                continue;
            }

            if (name.Equals("$$XTIMAGE", StringComparison.OrdinalIgnoreCase))
            {
                return image;
            }

            if (name.Equals("$$XSIMAGE", StringComparison.OrdinalIgnoreCase))
            {
                save ??= image;
            }
        }

        return save;
    }

    private static byte[]? SectionImage(ReadOnlySpan<byte> data, uint rawAddress, uint rawSize)
    {
        if (rawSize == 0 || rawAddress >= (uint)data.Length)
        {
            return null;
        }

        var available = (uint)data.Length - rawAddress;
        var take = (int)Math.Min(rawSize, Math.Min(available, DiscRead.ImageCap));
        return ImageSniff.AsImage(data.Slice((int)rawAddress, take));
    }

    private static string? Ascii(ReadOnlySpan<byte> data, int offset)
    {
        var end = offset;
        var limit = Math.Min(data.Length, offset + 32);
        while (end < limit && data[end] != 0)
        {
            if (data[end] < 32 || data[end] > 126)
            {
                return null;
            }

            end++;
        }

        if (end == offset)
        {
            return null;
        }

        return Encoding.ASCII.GetString(data.Slice(offset, end - offset));
    }

    private static bool TryOffset(uint address, uint baseAddress, int length, out int offset)
    {
        offset = 0;
        if (address < baseAddress)
        {
            return false;
        }

        var value = address - baseAddress;
        if (value >= (uint)length)
        {
            return false;
        }

        offset = (int)value;
        return true;
    }

    private static uint ReadU32(ReadOnlySpan<byte> data, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4));
}
