namespace EliteLevel.Disc.Core.IO;

/// <summary>A seekable window over a shared stream. Every read holds the gate.</summary>
internal sealed class WindowStream : Stream
{
    private readonly Stream _source;
    private readonly object _gate;
    private readonly long _start;
    private readonly long _length;
    private long _position;
    private bool _disposed;

    public WindowStream(Stream source, object gate, long start, long length)
    {
        if (start < 0 || length < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(start));
        }

        _source = source;
        _gate = gate;
        _start = start;
        _length = length;
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_position >= _length || buffer.IsEmpty)
        {
            return 0;
        }

        var count = (int)Math.Min(buffer.Length, _length - _position);
        lock (_gate)
        {
            _source.Position = _start + _position;
            var read = _source.Read(buffer[..count]);
            _position += read;
            return read;
        }
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Read(buffer.Span));
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var next = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };
        if (next < 0)
        {
            throw new IOException("Seek is before the start of the file.");
        }

        _position = next;
        return _position;
    }

    public override void SetLength(long value) => throw new NotSupportedException("Disc images are read-only.");

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("Disc images are read-only.");

    protected override void Dispose(bool disposing) => _disposed = true;
}

/// <summary>
/// Serialises access to a stream that itself seeks a shared base stream, such as an ISO file stream.
/// </summary>
internal sealed class SyncedStream : Stream
{
    private readonly Stream _inner;
    private readonly object _gate;
    private long _position;
    private bool _disposed;

    public SyncedStream(Stream inner, object gate, long length)
    {
        _inner = inner;
        _gate = gate;
        Length = length;
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length { get; }

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

        var count = (int)Math.Min(buffer.Length, Length - _position);
        lock (_gate)
        {
            _inner.Position = _position;
            var read = _inner.Read(buffer[..count]);
            _position += read;
            return read;
        }
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Read(buffer.Span));
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
            throw new IOException("Seek is before the start of the file.");
        }

        _position = next;
        return _position;
    }

    public override void SetLength(long value) => throw new NotSupportedException("Disc images are read-only.");

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("Disc images are read-only.");

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }

        _disposed = true;
    }
}
