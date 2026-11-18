using System.Security.Cryptography;

namespace EliteLevel.Disc.Core.Formats.Switch;

/// <summary>
/// AES helpers for NCA headers and section bodies.
/// Header and XTS bodies use AES-XTS with 0x200-byte sectors. The tweak is the sector
/// number as a big-endian 128-bit integer (the endianness documented for this format),
/// encrypted with the tweak half of the key, then multiplied in GF(2^128) between blocks.
/// CTR bodies use AES-CTR. The key area and title keys use AES-128-ECB, one block at a time.
/// No console keys are embedded here.
/// </summary>
internal static class NintendoAes
{
    public const int Sector = 0x200;

    public static void WritePlainTweak(Span<byte> tweak, long sector)
    {
        tweak.Clear();
        var value = (ulong)sector;
        for (var i = 0; i < 8; i++)
        {
            tweak[15 - i] = (byte)(value >> (8 * i));
        }
    }

    public static void MultiplyTweak(Span<byte> tweak)
    {
        var carry = 0;
        for (var i = 15; i >= 0; i--)
        {
            var next = tweak[i] >> 7;
            tweak[i] = (byte)((tweak[i] << 1) | carry);
            carry = next;
        }

        if (carry != 0)
        {
            tweak[15] ^= 0x87;
        }
    }

    public static void Xts(Span<byte> data, ReadOnlySpan<byte> key, long sector, bool encrypt)
    {
        if (key.Length != 32)
        {
            throw new ArgumentException("An XTS key is 32 bytes.", nameof(key));
        }

        if (data.Length == 0)
        {
            return;
        }

        if (data.Length % Sector != 0)
        {
            throw new ArgumentException("XTS input must be a multiple of the sector size.", nameof(data));
        }

        using var dataAes = Aes.Create();
        using var tweakAes = Aes.Create();
        dataAes.Mode = CipherMode.ECB;
        dataAes.Padding = PaddingMode.None;
        tweakAes.Mode = CipherMode.ECB;
        tweakAes.Padding = PaddingMode.None;
        dataAes.Key = key[..16].ToArray();
        tweakAes.Key = key[16..].ToArray();
        using var dataTransform = encrypt ? dataAes.CreateEncryptor() : dataAes.CreateDecryptor();
        using var tweakTransform = tweakAes.CreateEncryptor();
        Span<byte> tweak = stackalloc byte[16];
        var blockIn = new byte[16];
        var blockOut = new byte[16];
        for (var offset = 0; offset < data.Length; offset += Sector, sector++)
        {
            WritePlainTweak(tweak, sector);
            CryptBlock(tweakTransform, tweak, blockIn, blockOut);
            var sectorSpan = data.Slice(offset, Sector);
            for (var block = 0; block < Sector; block += 16)
            {
                var slice = sectorSpan.Slice(block, 16);
                Xor(slice, tweak);
                CryptBlock(dataTransform, slice, blockIn, blockOut);
                Xor(slice, tweak);
                MultiplyTweak(tweak);
            }
        }
    }

    public static void Ctr(Span<byte> data, ReadOnlySpan<byte> key, ReadOnlySpan<byte> upper, long absoluteOffset)
    {
        if (key.Length != 16)
        {
            throw new ArgumentException("A CTR key is 16 bytes.", nameof(key));
        }

        if (upper.Length < 8)
        {
            throw new ArgumentException("The CTR upper half is 8 bytes.", nameof(upper));
        }

        if (data.Length == 0)
        {
            return;
        }

        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        aes.Key = key.ToArray();
        using var transform = aes.CreateEncryptor();
        Span<byte> counter = stackalloc byte[16];
        Span<byte> stream = stackalloc byte[16];
        upper[..8].CopyTo(counter);
        var blockIn = new byte[16];
        var blockOut = new byte[16];
        var offset = 0;
        while (offset < data.Length)
        {
            var abs = absoluteOffset + offset;
            WriteU64BigEndian(counter[8..], (ulong)(abs >> 4));
            stream.Clear();
            counter.CopyTo(stream);
            CryptBlock(transform, stream, blockIn, blockOut);
            var skip = (int)(abs & 15);
            var count = Math.Min(16 - skip, data.Length - offset);
            for (var i = 0; i < count; i++)
            {
                data[offset + i] ^= stream[skip + i];
            }

            offset += count;
        }
    }

    public static void Ecb(Span<byte> block, ReadOnlySpan<byte> key, bool encrypt)
    {
        if (block.Length != 16 || key.Length != 16)
        {
            throw new ArgumentException("ECB here is one 16-byte block and a 16-byte key.");
        }

        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        aes.Key = key.ToArray();
        using var transform = encrypt ? aes.CreateEncryptor() : aes.CreateDecryptor();
        CryptBlock(transform, block, new byte[16], new byte[16]);
    }

    private static void CryptBlock(ICryptoTransform transform, Span<byte> block, byte[] input, byte[] output)
    {
        block[..16].CopyTo(input);
        var written = transform.TransformBlock(input, 0, 16, output, 0);
        if (written != 16)
        {
            throw new CryptographicException("AES did not transform a full block.");
        }

        output.AsSpan(0, 16).CopyTo(block);
    }

    private static void Xor(Span<byte> block, ReadOnlySpan<byte> mask)
    {
        for (var i = 0; i < 16; i++)
        {
            block[i] ^= mask[i];
        }
    }

    private static void WriteU64BigEndian(Span<byte> destination, ulong value)
    {
        for (var i = 0; i < 8; i++)
        {
            destination[7 - i] = (byte)(value >> (8 * i));
        }
    }
}
