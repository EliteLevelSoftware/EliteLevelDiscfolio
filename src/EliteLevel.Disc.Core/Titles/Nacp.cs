using System.Text;

namespace EliteLevel.Disc.Core.Titles;

/// <summary>
/// NACP control block. Sixteen language slots of 0x300 bytes start at 0.
/// American English is slot 0: a 0x200-byte name and a 0x100-byte publisher.
/// The display version is 0x10 bytes at 0x3060.
/// </summary>
internal static class Nacp
{
    public const int LanguageSize = 0x300;
    public const int NameSize = 0x200;
    public const int LanguageCount = 16;
    public const int VersionOffset = 0x3060;
    public const int VersionSize = 0x10;

    public static bool TryRead(ReadOnlySpan<byte> data, out string? title, out string? version)
    {
        title = null;
        version = null;
        if (data.Length < LanguageSize)
        {
            return false;
        }

        var slots = Math.Min(LanguageCount, data.Length / LanguageSize);
        for (var i = 0; i < slots; i++)
        {
            title = Text(data.Slice(i * LanguageSize, Math.Min(NameSize, data.Length - (i * LanguageSize))));
            if (title is not null)
            {
                break;
            }
        }

        if (data.Length >= VersionOffset + VersionSize)
        {
            version = Text(data.Slice(VersionOffset, VersionSize));
        }

        return title is not null || version is not null;
    }

    private static string? Text(ReadOnlySpan<byte> bytes)
    {
        var end = bytes.IndexOf((byte)0);
        if (end >= 0)
        {
            bytes = bytes[..end];
        }

        if (bytes.IsEmpty)
        {
            return null;
        }

        var text = Encoding.UTF8.GetString(bytes).Trim();
        return text.Length == 0 ? null : text;
    }
}
