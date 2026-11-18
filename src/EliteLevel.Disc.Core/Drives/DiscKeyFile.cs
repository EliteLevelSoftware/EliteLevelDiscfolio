using System.IO.Compression;
using System.Text;

namespace EliteLevel.Disc.Core.Drives;

/// <summary>
/// A disc key the user already has. The bytes are not formatted by <see cref="ToString"/>
/// and are not written anywhere by this type.
/// </summary>
public sealed class DiscKey
{
    public DiscKey(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length != 16)
        {
            throw new ArgumentException("A disc key is 16 bytes.", nameof(bytes));
        }

        Bytes = bytes.ToArray();
    }

    public byte[] Bytes { get; }

    public override string ToString() => "disc key";
}

/// <summary>
/// Loads a disc key the user supplies: a 32-character hex file, a 16-byte file, or a .ird (plain or gzip).
/// The key is not derived and is not written back out.
/// </summary>
public static class DiscKeyFile
{
    public static bool TryLoad(string path, out DiscKey? key, out string? error)
    {
        key = null;
        error = null;
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                error = "Could not find the disc key file.";
                return false;
            }

            var info = new FileInfo(path);
            if (info.Length > 8 * 1024 * 1024)
            {
                error = "That file is not a .ird or a 16-byte disc key.";
                return false;
            }

            var bytes = File.ReadAllBytes(path);
            if (IsGzip(bytes))
            {
                var plain = Gunzip(bytes);
                if (plain is null)
                {
                    error = "That file is not a .ird or a 16-byte disc key.";
                    return false;
                }

                bytes = plain;
            }

            if (IsIrd(bytes))
            {
                if (!TryParseIrd(bytes, out var data1))
                {
                    error = "That .ird file could not be read. A 16-byte key file or a 32-character hex key also works.";
                    return false;
                }

                key = new DiscKey(data1);
                return true;
            }

            if (TryParseHex(bytes, out var hex))
            {
                key = new DiscKey(hex);
                return true;
            }

            if (bytes.Length == 16)
            {
                key = new DiscKey(bytes);
                return true;
            }

            error = "That file is not a .ird or a 16-byte disc key.";
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = "The disc key file could not be read.";
            return false;
        }
    }

    private static bool IsGzip(byte[] bytes) => bytes.Length > 2 && bytes[0] == 0x1F && bytes[1] == 0x8B;

    private static bool IsIrd(byte[] bytes) =>
        bytes.Length > 4 && bytes[0] == (byte)'3' && bytes[1] == (byte)'I' && bytes[2] == (byte)'R' && bytes[3] == (byte)'D';

    private static byte[]? Gunzip(byte[] bytes)
    {
        try
        {
            using var input = new MemoryStream(bytes, writable: false);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            gzip.CopyTo(output);
            if (output.Length > 16 * 1024 * 1024)
            {
                return null;
            }

            return output.ToArray();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            return null;
        }
    }

    private static bool TryParseHex(byte[] bytes, out byte[] key)
    {
        key = [];
        if (bytes.Length is 0 or > 256)
        {
            return false;
        }

        foreach (var value in bytes)
        {
            if (value is not (9 or 10 or 13 or 32) && value is < 32 or > 126)
            {
                return false;
            }
        }

        var text = Encoding.ASCII.GetString(bytes).Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            text = text[2..];
        }

        var hex = new string(text.Where(static c => !char.IsWhiteSpace(c) && c is not ':' and not '-').ToArray());
        if (hex.Length != 32)
        {
            return false;
        }

        try
        {
            key = Convert.FromHexString(hex);
            return key.Length == 16;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// Version 6 through 9, as written by the usual .ird tools: title, gzip blobs, region and file hashes, then data1.
    /// data1 is returned as stored. It is not decrypted.
    /// </summary>
    private static bool TryParseIrd(byte[] data, out byte[] data1)
    {
        data1 = [];
        try
        {
            using var stream = new MemoryStream(data, writable: false);
            using var reader = new BinaryReader(stream);
            var magic = reader.ReadBytes(4);
            if (magic.Length != 4 || Encoding.ASCII.GetString(magic) != "3IRD")
            {
                return false;
            }

            var version = reader.ReadByte();
            if (version is < 6 or > 9)
            {
                return false;
            }

            if (reader.ReadBytes(9).Length != 9)
            {
                return false;
            }

            var titleLength = reader.ReadByte();
            if (!Skip(reader, titleLength))
            {
                return false;
            }

            if (!Skip(reader, 4 + 5 + 5))
            {
                return false;
            }

            if (version == 7 && !Skip(reader, 4))
            {
                return false;
            }

            if (!SkipBlock(reader) || !SkipBlock(reader))
            {
                return false;
            }

            var regions = reader.ReadByte();
            if (!Skip(reader, regions * 16))
            {
                return false;
            }

            var files = reader.ReadInt32();
            if (files < 0 || files > 250_000 || !Skip(reader, files * 24L))
            {
                return false;
            }

            if (!Skip(reader, 4))
            {
                return false;
            }

            if (version >= 9 && !Skip(reader, 115))
            {
                return false;
            }

            var key = reader.ReadBytes(16);
            if (key.Length != 16)
            {
                return false;
            }

            data1 = key;
            return true;
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or ArgumentException)
        {
            return false;
        }
    }

    private static bool SkipBlock(BinaryReader reader)
    {
        var length = reader.ReadInt32();
        if (length is < 0 or > 4 * 1024 * 1024)
        {
            return false;
        }

        return Skip(reader, length);
    }

    private static bool Skip(BinaryReader reader, long count)
    {
        if (count < 0 || reader.BaseStream.Position > reader.BaseStream.Length - count)
        {
            return false;
        }

        reader.BaseStream.Position += count;
        return true;
    }
}
