namespace EliteLevel.Disc.Core.Drives;

/// <summary>
/// Read-only sector device. Callers align reads to <see cref="SectorSize"/>.
/// Implementations open the media for reading and never write to it.
/// </summary>
public interface IBlockDevice : IDisposable
{
    string Id { get; }

    long Length { get; }

    int SectorSize { get; }

    /// <summary>
    /// Reads <paramref name="count"/> bytes at <paramref name="offset"/>.
    /// A short read is returned only at the end of the device.
    /// A failed sector throws <see cref="IOException"/>.
    /// </summary>
    int Read(long offset, byte[] buffer, int bufferOffset, int count);
}

/// <summary>
/// In-memory disc used by tests. The byte array is never modified.
/// <paramref name="transientFailures"/> is how many times each sector throws before a later read succeeds.
/// Sectors in <paramref name="badSectors"/> always throw.
/// Reads at or past <paramref name="unreadableFrom"/> throw, which stands in for a locked game partition.
/// </summary>
public sealed class MemoryBlockDevice : IBlockDevice
{
    private readonly byte[] _data;
    private readonly long _unreadableFrom;
    private readonly Dictionary<long, int> _transient;
    private readonly HashSet<long> _bad;
    private readonly object _gate = new();
    private bool _disposed;

    public MemoryBlockDevice(
        byte[] data,
        long? length = null,
        long unreadableFrom = long.MaxValue,
        IReadOnlyDictionary<long, int>? transientFailures = null,
        IReadOnlyCollection<long>? badSectors = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.LongLength > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(data));
        }

        _data = data;
        Length = length ?? data.LongLength;
        if (Length < data.LongLength)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        _unreadableFrom = unreadableFrom;
        _transient = transientFailures is null
            ? new Dictionary<long, int>()
            : new Dictionary<long, int>(transientFailures);
        _bad = badSectors is null ? new HashSet<long>() : new HashSet<long>(badSectors);
    }

    public string Id { get; init; } = "memory";

    public long Length { get; }

    public int SectorSize => OpticalDrives.SectorSize;

    public byte[] Data => _data;

    public MemoryBlockDevice Clone()
    {
        return new MemoryBlockDevice(_data, Length, _unreadableFrom, _transient, _bad) { Id = Id };
    }

    public int Read(long offset, byte[] buffer, int bufferOffset, int count)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(buffer);
        if (offset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        if ((uint)bufferOffset > (uint)buffer.Length || (uint)count > (uint)(buffer.Length - bufferOffset))
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        if (count == 0 || offset >= Length)
        {
            return 0;
        }

        var available = (int)Math.Min(count, Length - offset);
        var sectorSize = SectorSize;
        for (var pos = offset; pos < offset + available;)
        {
            EnsureSector(pos / sectorSize);
            var sectorEnd = (pos / sectorSize + 1) * sectorSize;
            pos = Math.Min(offset + available, sectorEnd);
        }

        var written = 0;
        var cursor = offset;
        while (written < available)
        {
            if (cursor >= _unreadableFrom)
            {
                throw new IOException($"Read error at sector {cursor / sectorSize}.");
            }

            if (cursor >= _data.LongLength)
            {
                Array.Clear(buffer, bufferOffset + written, available - written);
                return available;
            }

            var slice = (int)Math.Min(available - written, _data.LongLength - cursor);
            if (cursor + slice > _unreadableFrom)
            {
                slice = (int)(_unreadableFrom - cursor);
            }

            Buffer.BlockCopy(_data, (int)cursor, buffer, bufferOffset + written, slice);
            written += slice;
            cursor += slice;
        }

        return available;
    }

    public void Dispose() => _disposed = true;

    private void EnsureSector(long sector)
    {
        lock (_gate)
        {
            if (_bad.Contains(sector))
            {
                throw new IOException($"Read error at sector {sector}.");
            }

            if (_transient.TryGetValue(sector, out var left) && left > 0)
            {
                _transient[sector] = left - 1;
                throw new IOException($"Read error at sector {sector}.");
            }
        }
    }
}

/// <summary>
/// Seekable read-only view of a block device. Reads are served from whole sectors
/// so format parsers can ask for any range. The device is not written.
/// </summary>
public sealed class SectorStream : Stream
{
    private readonly IBlockDevice _device;
    private readonly bool _leaveOpen;
    private readonly byte[] _sector;
    private long _position;
    private long _cachedOffset = -1;
    private int _cachedCount;
    private bool _disposed;

    public SectorStream(IBlockDevice device, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(device);
        _device = device;
        _leaveOpen = leaveOpen;
        _sector = new byte[Math.Max(1, device.SectorSize)];
    }

    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite => false;

    public override long Length => _device.Length;

    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_position >= Length || buffer.IsEmpty)
        {
            return 0;
        }

        var wanted = (int)Math.Min(buffer.Length, Length - _position);
        var filled = 0;
        var sectorSize = _sector.Length;
        while (filled < wanted)
        {
            var sectorOffset = _position / sectorSize * sectorSize;
            var skip = (int)(_position - sectorOffset);
            var count = (int)Math.Min(sectorSize, Length - sectorOffset);
            if (_cachedOffset != sectorOffset || _cachedCount != count)
            {
                var read = _device.Read(sectorOffset, _sector, 0, count);
                if (read != count)
                {
                    throw new IOException($"Short read at offset {sectorOffset}.");
                }

                _cachedOffset = sectorOffset;
                _cachedCount = count;
            }

            var copy = Math.Min(wanted - filled, count - skip);
            _sector.AsSpan(skip, copy).CopyTo(buffer.Slice(filled, copy));
            filled += copy;
            _position += copy;
        }

        return filled;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var next = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => Length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };
        if (next < 0)
        {
            throw new IOException("Cannot seek before the start of the disc.");
        }

        _position = next;
        return _position;
    }

    public override void SetLength(long value) => throw new NotSupportedException("The disc is read-only.");

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("The disc is read-only.");

    protected override void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (disposing && !_leaveOpen)
        {
            _device.Dispose();
        }

        base.Dispose(disposing);
    }
}
