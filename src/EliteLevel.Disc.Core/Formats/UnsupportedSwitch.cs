namespace EliteLevel.Disc.Core.Formats;

/// <summary>
/// Names NSP and XCI containers this build does not read.
/// An NSP is a PFS0 archive: ASCII "PFS0" at byte 0.
/// An XCI game-card header has ASCII "HEAD" at offset 0x100.
/// </summary>
internal static class UnsupportedSwitch
{
    public static bool Matches(Stream stream, string displayName)
    {
        var extension = Path.GetExtension(displayName);
        if (extension.Equals(".nsp", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".xci", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return HasAscii(stream, 0, "PFS0"u8) || HasAscii(stream, 0x100, "HEAD"u8);
    }

    private static bool HasAscii(Stream stream, long offset, ReadOnlySpan<byte> magic)
    {
        if (!stream.CanSeek || offset < 0 || stream.Length < offset + magic.Length)
        {
            return false;
        }

        var buffer = new byte[magic.Length];
        stream.Position = offset;
        return stream.Read(buffer, 0, buffer.Length) == buffer.Length
            && buffer.AsSpan().SequenceEqual(magic);
    }
}
