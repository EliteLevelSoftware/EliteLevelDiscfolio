using System.Text;

namespace EliteLevel.Disc.Core.Titles;

/// <summary>
/// PARAM.SFO index. The layout is the public SFO container: a 20-byte header,
/// 16-byte index entries, a key table and a data table. Values stay in memory.
/// </summary>
internal static class ParamSfo
{
    public static bool TryRead(ReadOnlySpan<byte> data, out SfoFile file)
    {
        file = SfoFile.Empty;
        if (data.Length < 20 || data[0] != 0x00 || data[1] != (byte)'P' || data[2] != (byte)'S' || data[3] != (byte)'F')
        {
            return false;
        }

        var keyStart = ReadU32(data, 8);
        var dataStart = ReadU32(data, 12);
        var count = ReadU32(data, 16);
        if (count > 512 || keyStart > (uint)data.Length || dataStart > (uint)data.Length)
        {
            return false;
        }

        var values = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var formats = new Dictionary<string, ushort>(StringComparer.Ordinal);
        var index = 20;
        for (var i = 0; i < count; i++)
        {
            if (index + 16 > data.Length)
            {
                return false;
            }

            var keyOffset = ReadU16(data, index);
            var format = ReadU16(data, index + 2);
            var length = ReadU32(data, index + 4);
            var valueOffset = ReadU32(data, index + 12);
            index += 16;

            var keyPos = (int)keyStart + keyOffset;
            if (keyPos < 0 || keyPos >= data.Length)
            {
                continue;
            }

            var keyEnd = keyPos;
            while (keyEnd < data.Length && data[keyEnd] != 0)
            {
                keyEnd++;
            }

            if (keyEnd == keyPos)
            {
                continue;
            }

            var key = Encoding.UTF8.GetString(data.Slice(keyPos, keyEnd - keyPos));
            var valuePos = (long)dataStart + valueOffset;
            if (length > 1_048_576 || valuePos < 0 || valuePos > data.Length)
            {
                continue;
            }

            var take = (int)Math.Min(length, (uint)(data.Length - valuePos));
            values[key] = data.Slice((int)valuePos, take).ToArray();
            formats[key] = format;
        }

        file = new SfoFile(values, formats);
        return true;
    }

    private static ushort ReadU16(ReadOnlySpan<byte> data, int offset) =>
        (ushort)(data[offset] | (data[offset + 1] << 8));

    private static uint ReadU32(ReadOnlySpan<byte> data, int offset) =>
        (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24));
}

internal sealed class SfoFile
{
    private readonly Dictionary<string, byte[]> _values;
    private readonly Dictionary<string, ushort> _formats;

    public SfoFile(Dictionary<string, byte[]> values, Dictionary<string, ushort> formats)
    {
        _values = values;
        _formats = formats;
    }

    public static SfoFile Empty { get; } = new([], []);

    public string? Text(string key)
    {
        if (!_values.TryGetValue(key, out var bytes) || bytes.Length == 0)
        {
            return null;
        }

        var end = bytes.Length;
        while (end > 0 && bytes[end - 1] == 0)
        {
            end--;
        }

        var text = Encoding.UTF8.GetString(bytes, 0, end).Trim();
        return text.Length == 0 ? null : text;
    }

    public int? UInt32(string key)
    {
        if (!_formats.TryGetValue(key, out var format) || format != 0x0002)
        {
            return null;
        }

        if (!_values.TryGetValue(key, out var bytes) || bytes.Length < 4)
        {
            return null;
        }

        return bytes[0] | (bytes[1] << 8) | (bytes[2] << 16) | (bytes[3] << 24);
    }
}
