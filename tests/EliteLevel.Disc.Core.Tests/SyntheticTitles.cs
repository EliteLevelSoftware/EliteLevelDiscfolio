using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace EliteLevel.Disc.Core.Tests;

/// <summary>Tiny in-memory PARAM.SFO, PNG, XEX/XDBF and XBE structures. No retail bytes.</summary>
public static class SyntheticTitles
{
    public static byte[] Png(byte r, byte g, byte b)
    {
        var raw = new byte[] { 0, r, g, b };
        using var deflated = new MemoryStream();
        using (var zlib = new ZLibStream(deflated, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            zlib.Write(raw);
        }

        using var png = new MemoryStream();
        png.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        WriteChunk(png, "IHDR"u8, Header());
        WriteChunk(png, "IDAT"u8, deflated.ToArray());
        WriteChunk(png, "IEND"u8, []);
        return png.ToArray();

        static byte[] Header()
        {
            var ihdr = new byte[13];
            BinaryPrimitives.WriteUInt32BigEndian(ihdr, 1);
            BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), 1);
            ihdr[8] = 8;
            ihdr[9] = 2;
            return ihdr;
        }
    }

    public static byte[] Sfo(string title, string titleId, string version, string category, int parental)
    {
        (string Key, ushort Format, byte[] Data)[] entries =
        [
            ("TITLE", 0x0004, Text(title)),
            ("TITLE_ID", 0x0004, Text(titleId)),
            ("APP_VER", 0x0004, Text(version)),
            ("CATEGORY", 0x0004, Text(category)),
            ("PARENTAL_LEVEL", 0x0002, BitConverter.GetBytes(parental))
        ];

        var keyStart = 20 + entries.Length * 16;
        var keyBytes = entries.Select(entry => Encoding.UTF8.GetBytes(entry.Key + "\0")).ToArray();
        var dataStart = keyStart + keyBytes.Sum(bytes => bytes.Length);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(new byte[] { 0x00, 0x50, 0x53, 0x46 });
        writer.Write(0x00000101);
        writer.Write(keyStart);
        writer.Write(dataStart);
        writer.Write(entries.Length);
        var keyCursor = 0;
        var dataCursor = 0;
        foreach (var entry in entries)
        {
            writer.Write((ushort)keyCursor);
            writer.Write(entry.Format);
            writer.Write(entry.Data.Length);
            writer.Write(entry.Data.Length);
            writer.Write(dataCursor);
            keyCursor += Encoding.UTF8.GetByteCount(entry.Key) + 1;
            dataCursor += entry.Data.Length;
        }

        foreach (var key in keyBytes)
        {
            writer.Write(key);
        }

        foreach (var entry in entries)
        {
            writer.Write(entry.Data);
        }

        return stream.ToArray();
    }

    public static byte[] Xex(uint titleId, uint version, string title, byte[]? icon, bool resourceInFile)
    {
        var xdbf = Xdbf(title, icon);
        const int xdbfOffset = 0x54;
        var buffer = new byte[xdbfOffset + xdbf.Length];
        Be(buffer, 0, 0x58455832);
        Be(buffer, 8, (uint)buffer.Length);
        Be(buffer, 16, (uint)buffer.Length);
        Be(buffer, 20, 2);
        Be(buffer, 0x18, 0x000002FF);
        Be(buffer, 0x1C, 0x40);
        Be(buffer, 0x20, 0x00040006);
        Be(buffer, 0x24, 0x28);
        Be(buffer, 0x2C, version);
        Be(buffer, 0x30, version);
        Be(buffer, 0x34, titleId);
        buffer[0x38] = 2;
        buffer[0x3A] = 1;
        buffer[0x3B] = 1;
        Be(buffer, 0x40, 20);
        Encoding.ASCII.GetBytes(titleId.ToString("X8")).CopyTo(buffer.AsSpan(0x44, 8));
        Be(buffer, 0x4C, resourceInFile ? (uint)xdbfOffset : 0x82000000);
        Be(buffer, 0x50, (uint)xdbf.Length);
        xdbf.CopyTo(buffer.AsSpan(xdbfOffset));
        return buffer;
    }

    public static byte[] Xbe(uint titleId, string title, byte[] image, uint version)
    {
        const uint baseAddress = 0x10000;
        const int imageOffset = 0x4A0;
        var buffer = new byte[imageOffset + image.Length];
        buffer[0] = (byte)'X';
        buffer[1] = (byte)'B';
        buffer[2] = (byte)'E';
        buffer[3] = (byte)'H';
        Le(buffer, 0x104, baseAddress);
        Le(buffer, 0x108, (uint)buffer.Length);
        Le(buffer, 0x118, baseAddress + 0x200);
        Le(buffer, 0x11C, 1);
        Le(buffer, 0x120, baseAddress + 0x400);
        Le(buffer, 0x200, 0x1D0);
        Le(buffer, 0x208, titleId);
        Encoding.Unicode.GetBytes(title).CopyTo(buffer.AsSpan(0x20C));
        Le(buffer, 0x200 + 0xAC, version);
        Le(buffer, 0x400 + 0x0C, imageOffset);
        Le(buffer, 0x400 + 0x10, (uint)image.Length);
        Le(buffer, 0x400 + 0x14, baseAddress + 0x480);
        Encoding.ASCII.GetBytes("$$XTIMAGE").CopyTo(buffer.AsSpan(0x480));
        image.CopyTo(buffer.AsSpan(imageOffset));
        return buffer;
    }

    private static byte[] Xdbf(string title, byte[]? icon)
    {
        var titleBytes = Encoding.UTF8.GetBytes(title);
        var strings = new byte[14 + 4 + titleBytes.Length];
        Be(strings, 0, 0x58535452);
        Be(strings, 4, 1);
        Be(strings, 8, (uint)strings.Length);
        Be16(strings, 12, 1);
        Be16(strings, 14, 0x8000);
        Be16(strings, 16, (ushort)titleBytes.Length);
        titleBytes.CopyTo(strings.AsSpan(18));

        var blocks = new List<byte[]> { strings };
        var sections = new List<ushort> { 3 };
        var ids = new List<ulong> { 1 };
        if (icon is not null)
        {
            blocks.Add(icon);
            sections.Add(2);
            ids.Add(0x8000);
        }

        using var stream = new MemoryStream();
        Be(stream, 0x58444246);
        Be(stream, 1);
        Be(stream, (uint)blocks.Count);
        Be(stream, (uint)blocks.Count);
        Be(stream, 0);
        Be(stream, 0);
        var offset = 0;
        for (var i = 0; i < blocks.Count; i++)
        {
            Be16(stream, sections[i]);
            Be64(stream, ids[i]);
            Be(stream, (uint)offset);
            Be(stream, (uint)blocks[i].Length);
            offset += blocks[i].Length;
        }

        foreach (var block in blocks)
        {
            stream.Write(block);
        }

        return stream.ToArray();
    }

    private static byte[] Text(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var text = new byte[bytes.Length + 1];
        bytes.CopyTo(text);
        return text;
    }

    private static void WriteChunk(Stream stream, ReadOnlySpan<byte> type, byte[] data)
    {
        Span<byte> prefix = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(prefix, (uint)data.Length);
        stream.Write(prefix);
        stream.Write(type);
        stream.Write(data);
        Span<byte> crc = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc(type, data));
        stream.Write(crc);
    }

    private static uint Crc(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        Feed(type);
        Feed(data);
        return ~crc;

        void Feed(ReadOnlySpan<byte> bytes)
        {
            foreach (var value in bytes)
            {
                crc ^= value;
                for (var i = 0; i < 8; i++)
                {
                    var mask = (crc & 1) == 1 ? 0xEDB88320u : 0u;
                    crc = (crc >> 1) ^ mask;
                }
            }
        }
    }

    private static void Be(byte[] buffer, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(offset), value);

    private static void Be16(byte[] buffer, int offset, ushort value) =>
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset), value);

    private static void Le(byte[] buffer, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset), value);

    private static void Be(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void Be16(Stream stream, ushort value)
    {
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void Be64(Stream stream, ulong value)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
        stream.Write(bytes);
    }
}
