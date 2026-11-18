namespace EliteLevel.Disc.Core.Titles;

/// <summary>Recognises PNG, JPEG, BMP and ICO payloads, including ones embedded after a small header.</summary>
internal static class ImageSniff
{
    public static byte[]? AsImage(ReadOnlySpan<byte> data)
    {
        if (data.Length < 8)
        {
            return null;
        }

        if (IsPng(data) || IsJpeg(data) || IsBmp(data) || IsIco(data))
        {
            return data.ToArray();
        }

        var search = 0;
        while (search < data.Length)
        {
            var png = IndexOf(data[search..], [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
            if (png < 0)
            {
                break;
            }

            png += search;
            var end = PngEnd(data, png);
            if (end > png)
            {
                return data.Slice(png, end - png).ToArray();
            }

            search = png + 1;
        }

        search = 0;
        while (search < data.Length)
        {
            var jpg = IndexOf(data[search..], [0xFF, 0xD8, 0xFF]);
            if (jpg < 0)
            {
                break;
            }

            jpg += search;
            var end = JpegEnd(data, jpg);
            if (end > jpg)
            {
                return data.Slice(jpg, end - jpg).ToArray();
            }

            search = jpg + 3;
        }

        return null;
    }

    public static string Extension(ReadOnlySpan<byte> data)
    {
        if (IsPng(data))
        {
            return ".png";
        }

        if (IsJpeg(data))
        {
            return ".jpg";
        }

        if (IsBmp(data))
        {
            return ".bmp";
        }

        if (IsIco(data))
        {
            return ".ico";
        }

        return ".img";
    }

    private static bool IsPng(ReadOnlySpan<byte> data) =>
        data.Length >= 8
        && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47
        && data[4] == 0x0D && data[5] == 0x0A && data[6] == 0x1A && data[7] == 0x0A;

    private static bool IsJpeg(ReadOnlySpan<byte> data) =>
        data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF;

    private static bool IsBmp(ReadOnlySpan<byte> data) =>
        data.Length >= 2 && data[0] == (byte)'B' && data[1] == (byte)'M';

    private static bool IsIco(ReadOnlySpan<byte> data) =>
        data.Length >= 4 && data[0] == 0 && data[1] == 0 && data[2] == 1 && data[3] == 0;

    private static int PngEnd(ReadOnlySpan<byte> data, int start)
    {
        var i = start + 8;
        for (var chunks = 0; chunks < 64 && i + 12 <= data.Length; chunks++)
        {
            var length = ReadU32(data, i);
            if (length > 16 * 1024 * 1024)
            {
                return -1;
            }

            var next = i + 12 + (int)length;
            if (next < i || next > data.Length)
            {
                return -1;
            }

            var iend = data[i + 4] == (byte)'I' && data[i + 5] == (byte)'E' && data[i + 6] == (byte)'N' && data[i + 7] == (byte)'D';
            if (iend)
            {
                return next;
            }

            i = next;
        }

        return -1;
    }

    private static int JpegEnd(ReadOnlySpan<byte> data, int start)
    {
        for (var i = start + 2; i + 1 < data.Length; i++)
        {
            if (data[i] == 0xFF && data[i + 1] == 0xD9)
            {
                return i + 2;
            }
        }

        return -1;
    }

    private static int IndexOf(ReadOnlySpan<byte> data, ReadOnlySpan<byte> pattern)
    {
        if (pattern.Length == 0 || data.Length < pattern.Length)
        {
            return -1;
        }

        for (var i = 0; i <= data.Length - pattern.Length; i++)
        {
            if (data.Slice(i, pattern.Length).SequenceEqual(pattern))
            {
                return i;
            }
        }

        return -1;
    }

    private static uint ReadU32(ReadOnlySpan<byte> data, int offset) =>
        ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16) | ((uint)data[offset + 2] << 8) | data[offset + 3];
}
