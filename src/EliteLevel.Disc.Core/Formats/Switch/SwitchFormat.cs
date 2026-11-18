using System.Buffers.Binary;
using EliteLevel.Disc.Core;

namespace EliteLevel.Disc.Core.Formats.Switch;

/// <summary>
/// Nintendo Switch NSP and XCI. Detection uses the container, not the extension.
/// NSP is a PFS0 ("PFS0" at byte 0). XCI has "HEAD" at 0x100, or at 0x1100 when a
/// 0x1000-byte key area is still in front, and an HFS0 at the header's partition offset.
/// NSZ and XCZ are named and refused when the file is compressed. A block NSZ is still a
/// PFS0; its NCZ entries are listed and not decompressed.
/// Written from the public XCI, PFS0 and HFS0 layouts. No console keys are included.
/// </summary>
public sealed class SwitchFormat : IImageFormat
{
    private static ReadOnlySpan<byte> Zstd => [0x28, 0xB5, 0x2F, 0xFD];

    public string Id => "switch";
    public string DisplayName => "Nintendo Switch";
    public string SupportPhrase => "Nintendo Switch NSP and XCI";

    public bool CanOpen(Stream probe, ImageHints hints)
    {
        if (!probe.CanSeek)
        {
            return false;
        }

        var position = probe.Position;
        try
        {
            if (IsSwitchExtension(hints.Extension))
            {
                return true;
            }

            return LooksLikePfs(probe) || LooksLikeXci(probe);
        }
        catch (IOException)
        {
            return false;
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
        return Task.FromResult(Open(source, options, Path.GetExtension(options.DisplayName ?? string.Empty)));
    }

    private static IDiscImage Open(Stream source, OpenOptions options, string? extension)
    {
        var keys = SwitchKeys.Load(options.KeysDirectory);
        if (IsZstd(source))
        {
            throw new DiscFormatException(DiscFormatException.SwitchCompressed);
        }

        if (TryXciRoot(source, out var root))
        {
            return SwitchImage.OpenXci(source, root, options.ShowSystemUpdate, keys);
        }

        if (LooksLikePfs(source))
        {
            var range = new StreamRange(source, new object(), 0, source.Length);
            if (PartitionFs.TryParse(range, 0, source.Length, hashed: false, out _, out _))
            {
                return SwitchImage.OpenPfs(source, options.ShowSystemUpdate, keys);
            }
        }

        if (IsCompressedExtension(extension))
        {
            throw new DiscFormatException(DiscFormatException.SwitchCompressed);
        }

        if (IsXciExtension(extension) || LooksLikeXci(source))
        {
            throw new DiscFormatException("This file looks like a Nintendo Switch game card, but it has no HFS0 partition table.");
        }

        if (IsNspExtension(extension) || HasAscii(source, 0, "PFS0"u8))
        {
            throw new DiscFormatException("This file looks like a Nintendo Switch NSP, but the PFS0 header is not valid.");
        }

        throw new DiscFormatException("This file has a Switch extension, but it is not an NSP or an XCI.");
    }

    private static bool TryXciRoot(Stream source, out long root)
    {
        root = 0;
        long headerAt;
        if (HasAscii(source, 0x100, "HEAD"u8))
        {
            headerAt = 0;
        }
        else if (HasAscii(source, 0x1100, "HEAD"u8))
        {
            headerAt = 0x1000;
        }
        else
        {
            return false;
        }

        Span<byte> field = stackalloc byte[8];
        if (!TryRead(source, headerAt + 0x130, field))
        {
            return false;
        }

        var address = BinaryPrimitives.ReadInt64LittleEndian(field);
        if (address < 0 || address > long.MaxValue - headerAt)
        {
            return false;
        }

        var relative = headerAt + address;
        if (HasAscii(source, relative, "HFS0"u8))
        {
            root = relative;
            return true;
        }

        if (address != relative && HasAscii(source, address, "HFS0"u8))
        {
            root = address;
            return true;
        }

        return false;
    }

    private static bool LooksLikePfs(Stream source)
    {
        Span<byte> header = stackalloc byte[0x10];
        if (!TryRead(source, 0, header) || !header[..4].SequenceEqual("PFS0"u8))
        {
            return false;
        }

        var count = BinaryPrimitives.ReadInt32LittleEndian(header[4..]);
        var strings = BinaryPrimitives.ReadInt32LittleEndian(header[8..]);
        if (count < 0 || count > PartitionFs.MaxEntries || strings < 0 || strings > PartitionFs.MaxStringTable)
        {
            return false;
        }

        var headerSize = 0x10L + (count * (long)PartitionFs.PfsEntrySize) + strings;
        return headerSize >= 0x10 && headerSize <= source.Length;
    }

    private static bool LooksLikeXci(Stream source) =>
        HasAscii(source, 0x100, "HEAD"u8) || HasAscii(source, 0x1100, "HEAD"u8);

    private static bool IsZstd(Stream source)
    {
        Span<byte> magic = stackalloc byte[4];
        return TryRead(source, 0, magic) && magic.SequenceEqual(Zstd);
    }

    private static bool HasAscii(Stream source, long offset, ReadOnlySpan<byte> magic)
    {
        Span<byte> buffer = stackalloc byte[4];
        return TryRead(source, offset, buffer) && buffer.SequenceEqual(magic);
    }

    private static bool TryRead(Stream source, long offset, Span<byte> buffer)
    {
        if (offset < 0 || offset > source.Length - buffer.Length)
        {
            return false;
        }

        source.Position = offset;
        return source.Read(buffer) == buffer.Length;
    }

    internal static bool IsSwitchExtension(string? extension) =>
        IsNspExtension(extension) || IsXciExtension(extension) || IsCompressedExtension(extension);

    private static bool IsNspExtension(string? extension) =>
        extension is not null && extension.Equals(".nsp", StringComparison.OrdinalIgnoreCase);

    private static bool IsXciExtension(string? extension) =>
        extension is not null && extension.Equals(".xci", StringComparison.OrdinalIgnoreCase);

    private static bool IsCompressedExtension(string? extension) =>
        extension is not null
        && (extension.Equals(".nsz", StringComparison.OrdinalIgnoreCase) || extension.Equals(".xcz", StringComparison.OrdinalIgnoreCase));
}
