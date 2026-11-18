namespace EliteLevel.Disc.Core.Formats.Switch;

internal interface IByteSource
{
    long Length { get; }

    int ReadAt(long position, Span<byte> destination);
}

/// <summary>A window of a shared stream. Every read takes the gate so seeks do not collide.</summary>
internal sealed class StreamRange : IByteSource
{
    private readonly Stream _stream;
    private readonly object _gate;
    private readonly long _start;

    public StreamRange(Stream stream, object gate, long start, long length)
    {
        if (start < 0 || length < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(start));
        }

        _stream = stream;
        _gate = gate;
        _start = start;
        Length = length;
    }

    public long Length { get; }

    public int ReadAt(long position, Span<byte> destination)
    {
        if (position < 0 || position >= Length || destination.IsEmpty)
        {
            return 0;
        }

        var wanted = (int)Math.Min(destination.Length, Length - position);
        var done = 0;
        while (done < wanted)
        {
            int read;
            lock (_gate)
            {
                _stream.Position = _start + position + done;
                read = _stream.Read(destination.Slice(done, wanted - done));
            }

            if (read <= 0)
            {
                break;
            }

            done += read;
        }

        return done;
    }
}

internal sealed class SourceWindow : IByteSource
{
    private readonly IByteSource _inner;
    private readonly long _start;

    public SourceWindow(IByteSource inner, long start, long length)
    {
        if (start < 0 || length < 0 || start > inner.Length || length > inner.Length - start)
        {
            throw new ArgumentOutOfRangeException(nameof(start));
        }

        _inner = inner;
        _start = start;
        Length = length;
    }

    public long Length { get; }

    public int ReadAt(long position, Span<byte> destination)
    {
        if (position < 0 || position >= Length || destination.IsEmpty)
        {
            return 0;
        }

        var count = (int)Math.Min(destination.Length, Length - position);
        return _inner.ReadAt(_start + position, destination[..count]);
    }
}

internal sealed class ByteSourceStream : Stream
{
    private readonly IByteSource _source;
    private readonly long _start;
    private readonly long _length;
    private long _position;
    private bool _disposed;

    public ByteSourceStream(IByteSource source, long start, long length)
    {
        if (start < 0 || length < 0 || start > source.Length || length > source.Length - start)
        {
            throw new ArgumentOutOfRangeException(nameof(start));
        }

        _source = source;
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

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_position >= _length || buffer.IsEmpty)
        {
            return 0;
        }

        var count = (int)Math.Min(buffer.Length, _length - _position);
        var read = _source.ReadAt(_start + _position, buffer[..count]);
        if (read > 0)
        {
            _position += read;
        }

        return read;
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
