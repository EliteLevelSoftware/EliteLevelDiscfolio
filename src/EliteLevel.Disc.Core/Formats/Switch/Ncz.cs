using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using ZstdSharp;

namespace EliteLevel.Disc.Core.Formats.Switch;

/// <summary>
/// NCZ is an NCA whose body was compressed with zstd.
/// The first 0x4000 bytes are the original NCA header and are not compressed.
/// An NCZSECTN table follows: a section count, then one 0x40-byte record per section
/// (offset, size, crypto type, padding, 16-byte key, 16-byte counter).
/// Crypto types 3 and 4 are AES-CTR (BKTR uses the same counter). The compressed bytes
/// are the decrypted section, and the key in the record restores the original ciphertext
/// without prod.keys. Type 1 is already plaintext. Type 2 is AES-XTS; the record holds
/// only 16 key bytes, so the 32-byte key is taken from the NCA key area when the caller
/// supplied keys. A plaintext gap before the first section is part of the compressed stream.
/// The part of the first section that overlaps the preserved header is not in the stream.
/// After the table, either one zstd frame follows, or an NCZBLOCK header and one zstd
/// frame per block. A block whose stored size is at least its plain size is stored raw.
/// Written from the public NCZ layout. No console keys are included.
/// </summary>
internal static class NczFile
{
    public const int Preserved = 0x4000;
    public const int SectionBytes = 0x40;
    public const int MaxSections = 4096;
    public const int MaxBlocks = 1_000_000;
    public const long MaxLength = 1L << 40;

    public static bool TryOpen(IByteSource file, SwitchKeys keys, out NczSource? source, out string? error)
    {
        source = null;
        error = null;
        if (!TryDescribe(file, out var description, out error))
        {
            return false;
        }

        source = new NczSource(file, description, keys);
        return true;
    }

    public static string ToNcaName(string name)
    {
        return name.EndsWith(".ncz", StringComparison.OrdinalIgnoreCase)
            ? string.Concat(name.AsSpan(0, name.Length - 4), ".nca")
            : name;
    }

    private static bool TryDescribe(IByteSource file, out Description description, out string? error)
    {
        description = default;
        error = null;
        if (file.Length < Preserved + 8)
        {
            error = "This NCZ has no NCZSECTN header.";
            return false;
        }

        Span<byte> magic = stackalloc byte[8];
        if (PartitionFs.ReadExact(file, Preserved, magic) != 8 || !magic.SequenceEqual("NCZSECTN"u8))
        {
            error = "This NCZ has no NCZSECTN header.";
            return false;
        }

        Span<byte> countBytes = stackalloc byte[8];
        if (PartitionFs.ReadExact(file, Preserved + 8, countBytes) != 8)
        {
            error = "This NCZ section table is truncated.";
            return false;
        }

        var countValue = BinaryPrimitives.ReadUInt64LittleEndian(countBytes);
        if (countValue == 0 || countValue > MaxSections)
        {
            error = "This NCZ section count is not valid.";
            return false;
        }

        var count = (int)countValue;
        var tableBytes = count * SectionBytes;
        var tableAt = Preserved + 16;
        if (tableAt > file.Length || tableBytes > file.Length - tableAt)
        {
            error = "This NCZ section table is truncated.";
            return false;
        }

        var table = new byte[tableBytes];
        if (PartitionFs.ReadExact(file, tableAt, table) != table.Length)
        {
            error = "This NCZ section table is truncated.";
            return false;
        }

        var sections = new Section[count];
        for (var i = 0; i < count; i++)
        {
            var entry = table.AsSpan(i * SectionBytes, SectionBytes);
            if (!TrySection(entry, out sections[i]))
            {
                error = "This NCZ section is outside the decompressed file.";
                return false;
            }
        }

        if (!TryPieces(sections, out var pieces, out var plainLength, out error))
        {
            return false;
        }

        long cursor = tableAt + tableBytes;
        Span<byte> next = stackalloc byte[8];
        var block = (BlockMap?)null;
        if (cursor <= file.Length - 8 && PartitionFs.ReadExact(file, cursor, next) == 8 && next.SequenceEqual("NCZBLOCK"u8))
        {
            if (!TryBlock(file, cursor, plainLength, out block, out var payload, out error))
            {
                return false;
            }

            cursor = payload;
        }
        else if (plainLength > 0 && cursor >= file.Length)
        {
            error = "This NCZ stream ended before the section data was complete.";
            return false;
        }

        description = new Description(pieces, plainLength, cursor, block);
        return true;
    }

    private static bool TrySection(ReadOnlySpan<byte> entry, out Section section)
    {
        section = default;
        var offset = ReadLong(entry);
        var size = ReadLong(entry[8..]);
        var crypto = ReadLong(entry[16..]);
        if (offset < 0 || size < 0 || crypto < 0 || offset > MaxLength || size > MaxLength)
        {
            return false;
        }

        if (size > 0 && offset > MaxLength - size)
        {
            return false;
        }

        section = new Section(offset, size, crypto, entry.Slice(0x20, 16).ToArray(), entry.Slice(0x30, 16).ToArray());
        return true;
    }

    private static bool TryPieces(Section[] sections, out Piece[] pieces, out long plainLength, out string? error)
    {
        pieces = [];
        plainLength = 0;
        error = null;
        var logical = new List<Section>(sections.Length + 1);
        if (sections[0].Offset > Preserved)
        {
            var gap = sections[0].Offset - Preserved;
            logical.Add(new Section(Preserved, gap, 1, [], []));
        }

        logical.AddRange(sections);
        var built = new List<Piece>(logical.Count);
        long output = Preserved;
        long plain = 0;
        var first = true;
        foreach (var section in logical)
        {
            var start = section.Offset;
            var end = section.Offset + section.Size;
            if (first)
            {
                first = false;
                if (start < Preserved)
                {
                    var skip = Math.Min(Preserved - start, section.Size);
                    start += skip;
                }
            }

            var length = end - start;
            if (length < 0)
            {
                error = "This NCZ section is outside the decompressed file.";
                return false;
            }

            if (length == 0)
            {
                continue;
            }

            if (output > MaxLength - length || plain > MaxLength - length)
            {
                error = "This NCZ section is outside the decompressed file.";
                return false;
            }

            built.Add(new Piece(output, length, plain, start, section.Crypto, section.Key, section.Counter));
            output += length;
            plain += length;
        }

        pieces = built.ToArray();
        plainLength = plain;
        return true;
    }

    private static bool TryBlock(IByteSource file, long headerAt, long plainLength, out BlockMap? block, out long payload, out string? error)
    {
        block = null;
        payload = headerAt;
        error = null;
        const int fixedBytes = 8 + 4 + 4 + 8;
        if (headerAt > file.Length - fixedBytes)
        {
            error = "This NCZ block header is truncated.";
            return false;
        }

        var header = new byte[fixedBytes];
        if (PartitionFs.ReadExact(file, headerAt, header) != header.Length)
        {
            error = "This NCZ block header is truncated.";
            return false;
        }

        var exponent = header[11];
        if (exponent is < 14 or > 32)
        {
            error = "This NCZ block size must be between 14 and 32.";
            return false;
        }

        var count = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12));
        var declared = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(16));
        if (count > MaxBlocks || declared < 0)
        {
            error = "This NCZ block list does not match the section data.";
            return false;
        }

        var blockSize = 1L << exponent;
        var expected = plainLength == 0 ? 0 : (plainLength + blockSize - 1) / blockSize;
        if (count != expected || declared != plainLength)
        {
            error = "This NCZ block list does not match the section data.";
            return false;
        }

        var listAt = headerAt + fixedBytes;
        var listBytes = (long)count * 4;
        if (listBytes > int.MaxValue || listAt > file.Length || listBytes > file.Length - listAt)
        {
            error = "This NCZ block list is truncated.";
            return false;
        }

        var list = new byte[listBytes];
        if (listBytes > 0 && PartitionFs.ReadExact(file, listAt, list) != list.Length)
        {
            error = "This NCZ block list is truncated.";
            return false;
        }

        var stored = new long[count];
        var offsets = new long[count];
        var cursor = listAt + listBytes;
        for (var i = 0; i < count; i++)
        {
            var size = BinaryPrimitives.ReadUInt32LittleEndian(list.AsSpan(i * 4));
            if (size == 0)
            {
                error = "This NCZ block is empty.";
                return false;
            }

            if (cursor > file.Length || size > file.Length - cursor)
            {
                error = "This NCZ block list runs past the end of the file.";
                return false;
            }

            offsets[i] = cursor;
            stored[i] = size;
            cursor += size;
        }

        block = new BlockMap(blockSize, offsets, stored, plainLength);
        payload = cursor;
        return true;
    }

    private static long ReadLong(ReadOnlySpan<byte> bytes)
    {
        var value = BinaryPrimitives.ReadUInt64LittleEndian(bytes);
        return value > long.MaxValue ? -1 : (long)value;
    }

    internal readonly record struct Description(Piece[] Pieces, long PlainLength, long Payload, BlockMap? Blocks);

    private readonly record struct Section(long Offset, long Size, long Crypto, byte[] Key, byte[] Counter);

    internal sealed class Piece
    {
        public Piece(long outputStart, long length, long plainStart, long absolute, long crypto, byte[] key, byte[] counter)
        {
            OutputStart = outputStart;
            Length = length;
            PlainStart = plainStart;
            Absolute = absolute;
            Crypto = crypto;
            Key = key;
            Counter = counter;
        }

        public long OutputStart { get; }
        public long Length { get; }
        public long PlainStart { get; }
        public long Absolute { get; }
        public long Crypto { get; }
        public byte[] Key { get; }
        public byte[] Counter { get; }
    }

    internal sealed class BlockMap
    {
        public BlockMap(long blockSize, long[] offsets, long[] stored, long plainLength)
        {
            BlockSize = blockSize;
            Offsets = offsets;
            Stored = stored;
            PlainLength = plainLength;
        }

        public long BlockSize { get; }
        public long[] Offsets { get; }
        public long[] Stored { get; }
        public long PlainLength { get; }

        public long PlainSize(int index)
        {
            if (index < 0 || index >= Offsets.Length)
            {
                return 0;
            }

            if (index == Offsets.Length - 1)
            {
                var remainder = PlainLength % BlockSize;
                if (remainder > 0)
                {
                    return remainder;
                }
            }

            return BlockSize;
        }
    }

    internal sealed class NczSource : IByteSource, IDisposable
    {
        private readonly IByteSource _file;
        private readonly Piece[] _pieces;
        private readonly IPlain _plain;
        private readonly object _gate = new();
        private byte[]? _xts;
        private bool _disposed;

        internal NczSource(IByteSource file, Description description, SwitchKeys keys)
            : this(file, description)
        {
            if (NeedsXts(description.Pieces))
            {
                _xts = Nca.TryXtsKey(this, keys);
            }
        }

        private NczSource(IByteSource file, Description description)
        {
            _file = file;
            _pieces = description.Pieces;
            Length = Preserved;
            foreach (var piece in _pieces)
            {
                Length = piece.OutputStart + piece.Length;
            }

            _plain = description.Blocks is null
                ? new SolidPlain(file, description.Payload, description.PlainLength)
                : new BlockPlain(file, description.Blocks);
        }

        public long Length { get; }

        public int ReadAt(long position, Span<byte> destination)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (position < 0 || position >= Length || destination.IsEmpty)
            {
                return 0;
            }

            var count = (int)Math.Min(destination.Length, Length - position);
            var done = 0;
            lock (_gate)
            {
                while (done < count)
                {
                    var pos = position + done;
                    if (pos < Preserved)
                    {
                        var take = (int)Math.Min(count - done, Preserved - pos);
                        var got = _file.ReadAt(pos, destination.Slice(done, take));
                        if (got <= 0)
                        {
                            break;
                        }

                        done += got;
                        continue;
                    }

                    var piece = Find(pos);
                    var into = (int)Math.Min(count - done, piece.OutputStart + piece.Length - pos);
                    var relative = pos - piece.OutputStart;
                    WritePiece(piece, relative, destination.Slice(done, into));
                    done += into;
                }
            }

            return done;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _plain.Dispose();
            Clear(_xts);
            _xts = null;
            foreach (var piece in _pieces)
            {
                Clear(piece.Key);
                Clear(piece.Counter);
            }
        }

        private void WritePiece(Piece piece, long relative, Span<byte> destination)
        {
            if (piece.Crypto == 2 && _xts is { Length: 32 })
            {
                WriteXts(piece, relative, destination);
                return;
            }

            _plain.Read(piece.PlainStart + relative, destination);
            if (piece.Crypto is 3 or 4)
            {
                if (piece.Key.Length != 16 || piece.Counter.Length < 8)
                {
                    throw new DiscFormatException("This NCZ section is missing its AES-CTR key.");
                }

                NintendoAes.Ctr(destination, piece.Key, piece.Counter, piece.Absolute + relative);
            }
        }

        private void WriteXts(Piece piece, long relative, Span<byte> destination)
        {
            if (piece.Absolute % NintendoAes.Sector != 0 || piece.Length % NintendoAes.Sector != 0)
            {
                throw new DiscFormatException("This NCZ section uses AES-XTS but does not start on a 512-byte boundary.");
            }

            var absolute = piece.Absolute + relative;
            var skip = (int)(absolute % NintendoAes.Sector);
            var sectorAbsolute = absolute - skip;
            var aligned = skip + destination.Length;
            var rounded = (aligned + NintendoAes.Sector - 1) / NintendoAes.Sector * NintendoAes.Sector;
            if (sectorAbsolute < piece.Absolute || rounded > piece.Length - (sectorAbsolute - piece.Absolute))
            {
                throw new DiscFormatException("This NCZ section uses AES-XTS but does not start on a 512-byte boundary.");
            }

            var buffer = ArrayPool<byte>.Shared.Rent(rounded);
            try
            {
                var plainAt = piece.PlainStart + (sectorAbsolute - piece.Absolute);
                _plain.Read(plainAt, buffer.AsSpan(0, rounded));
                NintendoAes.Xts(buffer.AsSpan(0, rounded), _xts!, sectorAbsolute / NintendoAes.Sector, encrypt: true);
                buffer.AsSpan(skip, destination.Length).CopyTo(destination);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
            }
        }

        private Piece Find(long position)
        {
            var lo = 0;
            var hi = _pieces.Length - 1;
            while (lo <= hi)
            {
                var mid = (lo + hi) >> 1;
                var piece = _pieces[mid];
                if (position < piece.OutputStart)
                {
                    hi = mid - 1;
                }
                else if (position >= piece.OutputStart + piece.Length)
                {
                    lo = mid + 1;
                }
                else
                {
                    return piece;
                }
            }

            throw new DiscFormatException("This NCZ section is outside the decompressed file.");
        }

        private static bool NeedsXts(Piece[] pieces)
        {
            foreach (var piece in pieces)
            {
                if (piece.Crypto == 2)
                {
                    return true;
                }
            }

            return false;
        }

        private static void Clear(byte[]? secret)
        {
            if (secret is { Length: > 0 })
            {
                CryptographicOperations.ZeroMemory(secret);
            }
        }
    }

    private interface IPlain : IDisposable
    {
        void Read(long offset, Span<byte> destination);
    }

    private sealed class SolidPlain : IPlain
    {
        private const int CacheLimit = 1 << 20;
        private readonly IByteSource _file;
        private readonly long _payload;
        private readonly long _length;
        private readonly object _gate = new();
        private FileStream? _cache;
        private DecompressionStream? _decoder;
        private long _filled;
        private bool _failed;
        private bool _disposed;

        public SolidPlain(IByteSource file, long payload, long length)
        {
            _file = file;
            _payload = payload;
            _length = length;
        }

        public void Read(long offset, Span<byte> destination)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (destination.IsEmpty)
            {
                return;
            }

            if (offset < 0 || offset > _length || destination.Length > _length - offset)
            {
                throw new DiscFormatException("This NCZ stream ended before the section data was complete.");
            }

            lock (_gate)
            {
                if (_failed)
                {
                    throw new DiscFormatException("This NCZ stream is not valid zstd.");
                }

                try
                {
                    Ensure(offset + destination.Length);
                    _cache!.Position = offset;
                    ReadExactly(_cache, destination, "This NCZ stream ended before the section data was complete.");
                }
                catch (DiscFormatException)
                {
                    _failed = true;
                    throw;
                }
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _decoder?.Dispose();
            _cache?.Dispose();
        }

        private void Ensure(long end)
        {
            if (_filled >= end)
            {
                return;
            }

            _cache ??= CreateCache();
            if (_decoder is null)
            {
                try
                {
                    _decoder = new DecompressionStream(new SequentialRange(_file, _payload, _file.Length), 0, checkEndOfStream: false, leaveOpen: true);
                }
                catch (ZstdException ex)
                {
                    throw new DiscFormatException("This NCZ stream is not valid zstd.", ex);
                }
            }
            var buffer = ArrayPool<byte>.Shared.Rent(Math.Min(CacheLimit, 65536));
            try
            {
                while (_filled < end)
                {
                    var want = (int)Math.Min(buffer.Length, end - _filled);
                    int got;
                    try
                    {
                        got = _decoder.Read(buffer, 0, want);
                    }
                    catch (ZstdException ex)
                    {
                        throw new DiscFormatException("This NCZ stream is not valid zstd.", ex);
                    }

                    if (got <= 0)
                    {
                        throw new DiscFormatException("This NCZ stream ended before the section data was complete.");
                    }

                    _cache.Position = _filled;
                    _cache.Write(buffer, 0, got);
                    _filled += got;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        private static FileStream CreateCache()
        {
            var path = Path.Combine(Path.GetTempPath(), "discfolio-ncz-" + Guid.NewGuid().ToString("N"));
            return new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536, FileOptions.DeleteOnClose);
        }
    }

    private sealed class BlockPlain : IPlain
    {
        private const int CacheLimit = 1 << 20;
        private readonly IByteSource _file;
        private readonly BlockMap _blocks;
        private byte[]? _cached;
        private int _cachedId = -1;
        private bool _disposed;

        public BlockPlain(IByteSource file, BlockMap blocks)
        {
            _file = file;
            _blocks = blocks;
        }

        public void Read(long offset, Span<byte> destination)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (destination.IsEmpty)
            {
                return;
            }

            if (offset < 0 || offset > _blocks.PlainLength || destination.Length > _blocks.PlainLength - offset)
            {
                throw new DiscFormatException("This NCZ stream ended before the section data was complete.");
            }

            var done = 0;
            while (done < destination.Length)
            {
                var position = offset + done;
                var id = (int)(position / _blocks.BlockSize);
                var skip = (int)(position % _blocks.BlockSize);
                var plain = _blocks.PlainSize(id);
                if (id < 0 || id >= _blocks.Offsets.Length || plain <= skip)
                {
                    throw new DiscFormatException("This NCZ block list does not match the section data.");
                }

                var take = (int)Math.Min(destination.Length - done, plain - skip);
                Copy(id, skip, destination.Slice(done, take));
                done += take;
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _cached = null;
        }

        private void Copy(int id, int skip, Span<byte> destination)
        {
            var plain = _blocks.PlainSize(id);
            var stored = _blocks.Stored[id];
            if (stored >= plain)
            {
                var got = _file.ReadAt(_blocks.Offsets[id] + skip, destination);
                if (got != destination.Length)
                {
                    throw new DiscFormatException("This NCZ block list runs past the end of the file.");
                }

                return;
            }

            if (plain > CacheLimit)
            {
                Stream(id, skip, destination);
                return;
            }

            var block = Cached(id, (int)plain, (int)stored);
            block.AsSpan(skip, destination.Length).CopyTo(destination);
        }

        private byte[] Cached(int id, int plain, int stored)
        {
            if (_cachedId == id && _cached is not null)
            {
                return _cached;
            }

            var compressed = new byte[stored];
            if (PartitionFs.ReadExact(_file, _blocks.Offsets[id], compressed) != stored)
            {
                throw new DiscFormatException("This NCZ block list runs past the end of the file.");
            }

            var output = new byte[plain];
            int got;
            try
            {
                using var decompressor = new Decompressor();
                got = decompressor.Unwrap(compressed, output);
            }
            catch (ZstdException ex)
            {
                throw new DiscFormatException("This NCZ block is not valid zstd.", ex);
            }

            if (got != plain)
            {
                throw new DiscFormatException("This NCZ block decompressed to the wrong size.");
            }

            _cached = output;
            _cachedId = id;
            return output;
        }

        private void Stream(int id, int skip, Span<byte> destination)
        {
            var window = new SequentialRange(_file, _blocks.Offsets[id], _blocks.Offsets[id] + _blocks.Stored[id]);
            using var decoder = new DecompressionStream(window, 0, checkEndOfStream: false, leaveOpen: true);
            try
            {
                Discard(decoder, skip);
                ReadExactly(decoder, destination, "This NCZ block decompressed to the wrong size.");
            }
            catch (ZstdException ex)
            {
                throw new DiscFormatException("This NCZ block is not valid zstd.", ex);
            }
        }

        private static void Discard(Stream stream, int count)
        {
            var buffer = ArrayPool<byte>.Shared.Rent(Math.Min(Math.Max(count, 1), 65536));
            try
            {
                while (count > 0)
                {
                    var got = stream.Read(buffer, 0, Math.Min(buffer.Length, count));
                    if (got <= 0)
                    {
                        throw new DiscFormatException("This NCZ block decompressed to the wrong size.");
                    }

                    count -= got;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    private sealed class SequentialRange : Stream
    {
        private readonly IByteSource _source;
        private readonly long _end;
        private long _position;

        public SequentialRange(IByteSource source, long start, long end)
        {
            _source = source;
            _position = start;
            _end = end;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _end;
        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_position >= _end || buffer.IsEmpty)
            {
                return 0;
            }

            var count = (int)Math.Min(buffer.Length, _end - _position);
            var got = _source.ReadAt(_position, buffer[..count]);
            if (got > 0)
            {
                _position += got;
            }

            return got;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static void ReadExactly(Stream stream, Span<byte> destination, string message)
    {
        var done = 0;
        while (done < destination.Length)
        {
            var got = stream.Read(destination[done..]);
            if (got <= 0)
            {
                throw new DiscFormatException(message);
            }

            done += got;
        }
    }
}
