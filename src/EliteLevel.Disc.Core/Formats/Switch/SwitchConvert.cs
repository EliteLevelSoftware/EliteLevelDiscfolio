using System.Security.Cryptography;
using System.Text;
using EliteLevel.Disc.Core.IO;

namespace EliteLevel.Disc.Core.Formats.Switch;

/// <summary>Writes an NSZ or XCZ back out as an NSP or XCI. NCZ entries become decompressed NCAs. The source is not modified.</summary>
public static class SwitchConvert
{
    public readonly record struct ConvertProgress(long BytesWritten, long TotalBytes, string Current);

    public readonly record struct ConvertResult(string OutputPath, long BytesWritten, string Sha256);

    public static async Task<ConvertResult> ConvertAsync(
        string inputPath,
        string outputPath,
        OpenOptions? options = null,
        IProgress<ConvertProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new OpenOptions();
        var sourcePath = Path.GetFullPath(inputPath);
        var destinationPath = Path.GetFullPath(outputPath);
        if (string.Equals(sourcePath, destinationPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("Convert will not overwrite the source file.");
        }

        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("Could not find the image.", sourcePath);
        }

        var parent = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        await using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var keys = SwitchKeys.Load(options.KeysDirectory);
        var gate = new object();
        var range = new StreamRange(input, gate, 0, input.Length);
        var owned = new List<IDisposable>();
        try
        {
            PlannedContainer plan;
            long prefix;
            if (SwitchFormat.TryXciRoot(input, out var root))
            {
                prefix = root;
                plan = PlanXci(range, root, keys, owned);
            }
            else if (SwitchFormat.LooksLikePfs(input)
                && PartitionFs.TryParse(range, 0, input.Length, hashed: false, out var entries, out _))
            {
                prefix = 0;
                plan = PlanPartition(range, entries, hashed: false, keys, owned);
            }
            else
            {
                throw new DiscFormatException(DiscFormatException.SwitchCompressed);
            }

            var total = prefix + plan.TotalSize;
            try
            {
                await using var output = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
                using var writer = new HashingWriter(output, total, progress);
                if (prefix > 0)
                {
                    writer.Current = "header";
                    await CopyRangeAsync(range, 0, prefix, writer, cancellationToken).ConfigureAwait(false);
                }

                await WritePartitionAsync(plan, writer, cancellationToken).ConfigureAwait(false);
                if (writer.Written != total)
                {
                    throw new DiscFormatException("The decompressed container is not the size its entries describe.");
                }

                return new ConvertResult(destinationPath, writer.Written, writer.Digest());
            }
            catch
            {
                TryDelete(destinationPath);
                throw;
            }
        }
        finally
        {
            foreach (var item in owned)
            {
                item.Dispose();
            }
        }
    }

    private static PlannedContainer PlanXci(IByteSource source, long root, SwitchKeys keys, List<IDisposable> owned)
    {
        if (!PartitionFs.TryParse(source, root, source.Length, hashed: true, out var entries, out _))
        {
            throw new DiscFormatException("This file looks like a Nintendo Switch game card, but it has no HFS0 partition table.");
        }

        var planned = new List<PlannedEntry>(entries.Count);
        foreach (var entry in entries)
        {
            var end = entry.Offset > long.MaxValue - entry.Size ? source.Length : Math.Min(source.Length, entry.Offset + entry.Size);
            if (entry.Size >= 0x10
                && PartitionFs.HasMagic(source, entry.Offset, "HFS0"u8)
                && PartitionFs.TryParse(source, entry.Offset, end, hashed: true, out var nested, out _))
            {
                var partition = PlanPartition(source, nested, hashed: true, keys, owned);
                planned.Add(new PlannedEntry(entry.Name, partition.TotalSize, (output, token) => WritePartitionAsync(partition, output, token)));
                continue;
            }

            var offset = entry.Offset;
            var size = entry.Size;
            planned.Add(new PlannedEntry(entry.Name, size, (output, token) => CopyRangeAsync(source, offset, size, output, token)));
        }

        return new PlannedContainer(true, planned);
    }

    private static PlannedContainer PlanPartition(IByteSource source, IReadOnlyList<PartitionFs.Entry> entries, bool hashed, SwitchKeys keys, List<IDisposable> owned)
    {
        var planned = new List<PlannedEntry>(entries.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (entry.Name.EndsWith(".ncz", StringComparison.OrdinalIgnoreCase))
            {
                var window = new SourceWindow(source, entry.Offset, entry.Size);
                if (!NczFile.TryOpen(window, keys, out var ncz, out var error) || ncz is null)
                {
                    throw new DiscFormatException(entry.Name + ": " + (error ?? "This NCZ could not be decompressed."));
                }

                owned.Add(ncz);
                var name = Unique(NczFile.ToNcaName(entry.Name), seen);
                var size = ncz.Length;
                planned.Add(new PlannedEntry(name, size, async (output, token) =>
                {
                    try
                    {
                        await CopySourceAsync(ncz, output, token).ConfigureAwait(false);
                    }
                    finally
                    {
                        ncz.Dispose();
                    }
                }));
                continue;
            }

            var rawName = Unique(entry.Name, seen);
            var rawOffset = entry.Offset;
            var rawSize = entry.Size;
            planned.Add(new PlannedEntry(rawName, rawSize, (output, token) => CopyRangeAsync(source, rawOffset, rawSize, output, token)));
        }

        return new PlannedContainer(hashed, planned);
    }

    private static async Task WritePartitionAsync(PlannedContainer container, Stream output, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var header = BuildHeader(container);
        await output.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        foreach (var entry in container.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (output is HashingWriter writer)
            {
                writer.Current = entry.Name;
            }

            await entry.WriteAsync(output, cancellationToken).ConfigureAwait(false);
        }
    }

    private static byte[] BuildHeader(PlannedContainer container)
    {
        var names = new byte[container.Entries.Count][];
        var raw = 0;
        for (var i = 0; i < container.Entries.Count; i++)
        {
            names[i] = Encoding.UTF8.GetBytes(container.Entries[i].Name + "\0");
            raw += names[i].Length;
        }

        var strings = raw == 0 ? 0 : (raw + 0xF) & ~0xF;
        var entrySize = container.Hashed ? PartitionFs.HfsEntrySize : PartitionFs.PfsEntrySize;
        var header = 0x10 + (container.Entries.Count * entrySize) + strings;
        var result = new byte[header];
        (container.Hashed ? "HFS0"u8 : "PFS0"u8).CopyTo(result);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(4), container.Entries.Count);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(8), strings);
        var nameAt = 0;
        long dataAt = 0;
        for (var i = 0; i < container.Entries.Count; i++)
        {
            var at = 0x10 + (i * entrySize);
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(result.AsSpan(at), dataAt);
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(result.AsSpan(at + 8), container.Entries[i].Size);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(at + 0x10), nameAt);
            names[i].CopyTo(result.AsSpan(0x10 + (container.Entries.Count * entrySize) + nameAt));
            nameAt += names[i].Length;
            dataAt += container.Entries[i].Size;
        }

        return result;
    }

    private static async Task CopyRangeAsync(IByteSource source, long offset, long size, Stream output, CancellationToken cancellationToken)
    {
        var buffer = new byte[256 * 1024];
        long done = 0;
        while (done < size)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = (int)Math.Min(buffer.Length, size - done);
            var got = source.ReadAt(offset + done, buffer.AsSpan(0, count));
            if (got <= 0)
            {
                throw new DiscFormatException("The container ended before this entry was copied.");
            }

            await output.WriteAsync(buffer.AsMemory(0, got), cancellationToken).ConfigureAwait(false);
            done += got;
        }
    }

    private static async Task CopySourceAsync(IByteSource source, Stream output, CancellationToken cancellationToken)
    {
        var buffer = new byte[256 * 1024];
        long done = 0;
        while (done < source.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = (int)Math.Min(buffer.Length, source.Length - done);
            var got = source.ReadAt(done, buffer.AsSpan(0, count));
            if (got <= 0)
            {
                throw new DiscFormatException("This NCZ stream ended before the section data was complete.");
            }

            await output.WriteAsync(buffer.AsMemory(0, got), cancellationToken).ConfigureAwait(false);
            done += got;
        }
    }

    private static string Unique(string name, HashSet<string> seen)
    {
        if (seen.Add(name))
        {
            return name;
        }

        for (var i = 2; i < 10_000; i++)
        {
            var next = name + "~" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (seen.Add(next))
            {
                return next;
            }
        }

        throw new DiscFormatException("This container has too many entries with the same name.");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class PlannedContainer
    {
        public PlannedContainer(bool hashed, List<PlannedEntry> entries)
        {
            Hashed = hashed;
            Entries = entries;
            long data = 0;
            foreach (var entry in entries)
            {
                data += entry.Size;
            }

            DataSize = data;
            var rawNames = 0;
            foreach (var entry in entries)
            {
                rawNames += Encoding.UTF8.GetByteCount(entry.Name) + 1;
            }

            var strings = rawNames == 0 ? 0 : (rawNames + 0xF) & ~0xF;
            var entrySize = hashed ? PartitionFs.HfsEntrySize : PartitionFs.PfsEntrySize;
            HeaderSize = 0x10 + (entries.Count * entrySize) + strings;
        }

        public bool Hashed { get; }
        public List<PlannedEntry> Entries { get; }
        public long DataSize { get; }
        public int HeaderSize { get; }
        public long TotalSize => HeaderSize + DataSize;
    }

    private sealed class PlannedEntry
    {
        public PlannedEntry(string name, long size, Func<Stream, CancellationToken, Task> writeAsync)
        {
            Name = name;
            Size = size;
            WriteAsync = writeAsync;
        }

        public string Name { get; }
        public long Size { get; }
        public Func<Stream, CancellationToken, Task> WriteAsync { get; }
    }

    private sealed class HashingWriter : Stream
    {
        private readonly Stream _inner;
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private readonly long _total;
        private readonly IProgress<ConvertProgress>? _progress;

        public HashingWriter(Stream inner, long total, IProgress<ConvertProgress>? progress)
        {
            _inner = inner;
            _total = total;
            _progress = progress;
        }

        public long Written { get; private set; }
        public string Current { get; set; } = "";
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _total;

        public override long Position
        {
            get => Written;
            set => throw new NotSupportedException();
        }

        public string Digest() => Hex.Format(_hash.GetCurrentHash());

        public override void Flush() => _inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            _hash.AppendData(buffer);
            _inner.Write(buffer);
            Note(buffer.Length);
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _hash.AppendData(buffer.Span);
            await _inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            Note(buffer.Length);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _hash.Dispose();
            }

            base.Dispose(disposing);
        }

        private void Note(int count)
        {
            Written += count;
            _progress?.Report(new ConvertProgress(Written, _total, Current));
        }
    }
}
