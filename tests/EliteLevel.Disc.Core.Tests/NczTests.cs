using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EliteLevel.Disc.Cli;
using EliteLevel.Disc.Core;
using EliteLevel.Disc.Core.Formats;
using EliteLevel.Disc.Core.Formats.Switch;
using EliteLevel.Disc.Core.Hash;
using EliteLevel.Disc.Core.Titles;
using ZstdSharp;

namespace EliteLevel.Disc.Core.Tests;

public class NczTests
{
    [Fact]
    public async Task Solid_ncz_matches_the_original_nca_and_browses_with_keys()
    {
        using var temp = new TempDir();
        var built = SwitchBuilder.Sample(temp.Path);
        Assert.True(built.Nca.Length > NczFile.Preserved);
        var ncz = NczBuilder.Solid(built.Nca, SectionOf(built));
        var nsz = Path.Combine(temp.Path, "northwind.nsz");
        await File.WriteAllBytesAsync(nsz, SwitchBuilder.Pfs0(
            ("control.ncz", ncz),
            ("0100000000010000.tik", "ticket"u8.ToArray())));

        await using (var image = await DiscImages.OpenAsync(nsz))
        {
            Assert.Equal("NSZ", image.Identity.FormatDisplayName);
            Assert.Contains("does not include keys", image.Identity.Notice);
            var stored = Find(image.Root, "control.nca");
            Assert.False(stored.IsDirectory);
            Assert.Equal(built.Nca.Length, stored.Size);
            Assert.Equal(built.Nca, ReadAll(stored));
            var backwards = new byte[built.Nca.Length];
            using var stream = stored.OpenRead();
            stream.Seek(built.Nca.Length - 16, SeekOrigin.Begin);
            Assert.Equal(16, stream.Read(backwards, built.Nca.Length - 16, 16));
            stream.Position = 0;
            Assert.Equal(built.Nca.Length, stream.Read(backwards));
            Assert.Equal(built.Nca, backwards);
        }

        await using (var image = await DiscImages.OpenAsync(nsz, new OpenOptions { KeysDirectory = built.ProdKeys }))
        {
            Assert.Null(image.Identity.Notice);
            Assert.Equal("northwind", await ReadTextAsync(Find(image.Root, "readme.txt")));
            Assert.Equal("Northwind", TitleMetadataReader.Read(image).Title);
            Assert.Equal("0100000000010000", TitleMetadataReader.Read(image).TitleId);
            var folder = (DiscNode)Find(image.Root, "control.nca");
            Assert.True(folder.IsDirectory);
            using var container = folder.OpenContainer!.Invoke();
            Assert.Equal(built.Nca, ReadStream(container));
        }

        var extracted = Path.Combine(temp.Path, "files");
        var plain = await Run("extract", nsz, "--keys", built.ProdKeys, "--out", extracted);
        Assert.Equal(0, plain.Code);
        Assert.Equal("northwind", await File.ReadAllTextAsync(Path.Combine(extracted, "control.nca", "RomFS", "readme.txt")));

        var decompressed = Path.Combine(temp.Path, "nca");
        var whole = await Run("extract", nsz, "--keys", built.ProdKeys, "--decompress", "--out", decompressed);
        Assert.Equal(0, whole.Code);
        Assert.Equal(built.Nca, await File.ReadAllBytesAsync(Path.Combine(decompressed, "control.nca")));
        Assert.False(Directory.Exists(Path.Combine(decompressed, "control.nca", "RomFS")));
        Assert.DoesNotContain(File.ReadAllText(built.ProdKeys), whole.Stdout);
        Assert.DoesNotContain(File.ReadAllText(built.ProdKeys), whole.Stderr);
    }

    [Fact]
    public async Task Block_ncz_round_trips_and_xcz_converts()
    {
        using var temp = new TempDir();
        var built = SwitchBuilder.Sample(temp.Path);
        var ncz = NczBuilder.Blocks(built.Nca, exponent: 14, rawLast: false, SectionOf(built));
        Assert.True(ncz.AsSpan().IndexOf("NCZBLOCK"u8) >= 0);
        var xcz = Path.Combine(temp.Path, "card.xcz");
        await File.WriteAllBytesAsync(xcz, SwitchBuilder.Xci(false, ("secure", SwitchBuilder.Hfs0(("control.ncz", ncz)))));

        await using var image = await DiscImages.OpenAsync(xcz, new OpenOptions { KeysDirectory = built.ProdKeys });
        Assert.Equal("XCZ", image.Identity.FormatDisplayName);
        Assert.Equal("northwind", await ReadTextAsync(Find(image.Root, "readme.txt")));
        var middle = new byte[64];
        var folder = (DiscNode)Find(image.Root, "control.nca");
        using (var stream = folder.OpenContainer!.Invoke())
        {
            stream.Position = 0x3FF0;
            Assert.Equal(64, stream.Read(middle));
        }

        Assert.Equal(built.Nca.AsSpan(0x3FF0, 64).ToArray(), middle);

        var xci = Path.Combine(temp.Path, "card.xci");
        var seen = new List<SwitchConvert.ConvertProgress>();
        var converted = await SwitchConvert.ConvertAsync(xcz, xci, new OpenOptions { KeysDirectory = built.ProdKeys }, new ListProgress(seen));
        Assert.Equal(converted.BytesWritten, new FileInfo(xci).Length);
        Assert.Equal(converted.Sha256, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(xci))).ToLowerInvariant());
        Assert.Contains(seen, item => item.BytesWritten == item.TotalBytes && item.TotalBytes > 0);

        await using var opened = await DiscImages.OpenAsync(xci, new OpenOptions { KeysDirectory = built.ProdKeys });
        Assert.Equal("XCI", opened.Identity.FormatDisplayName);
        Assert.Equal(built.Nca, ReadNode(Find(opened.Root, "control.nca")));

        var cli = await Run("convert", xcz, "--out", Path.Combine(temp.Path, "again.xci"), "--json", "--keys", built.ProdKeys);
        Assert.Equal(0, cli.Code);
        using var doc = JsonDocument.Parse(cli.Stdout);
        Assert.Equal(converted.Sha256, doc.RootElement.GetProperty("sha256").GetString());
        Assert.DoesNotContain(Convert.ToHexString(built.SectionKey), cli.Stdout, StringComparison.OrdinalIgnoreCase);

        var missing = await Run("convert", xcz);
        Assert.Equal(1, missing.Code);
        Assert.Contains("--out", missing.Stderr);
    }

    [Fact]
    public async Task Solid_and_block_sections_restore_plaintext_gaps_and_raw_blocks()
    {
        var key = Pattern(16, 4);
        var counter = Pattern(16, 5);
        var file = new byte[0x4000 + 0x200 + 20_000];
        file.AsSpan(0, 0x4000).Fill(0xA5);
        file.AsSpan(0x4000, 0x200).Fill(0x11);
        var body = new byte[20_000];
        for (var i = 0; i < body.Length; i++)
        {
            body[i] = (byte)(i % 251);
        }

        body.CopyTo(file.AsSpan(0x4200));
        NintendoAes.Ctr(file.AsSpan(0x4200, body.Length), key, counter, 0x4200);
        var section = new NczBuilder.Section(0x4200, body.Length, 3, key, counter);
        var solid = NczBuilder.Solid(file, section);
        Assert.Equal(file, ReadNcz(solid));

        var blocked = NczBuilder.Blocks(file, 14, rawLast: true, section);
        Assert.Equal(file, ReadNcz(blocked));
        var sizes = BlockSizes(blocked, sections: 1);
        Assert.True(sizes[0] < 1 << 14);
        Assert.Equal(file.Length - 0x4000 - ((sizes.Count - 1) << 14), sizes[^1]);

        var plain = new NczBuilder.Section(0x4000, 0x100, 1, new byte[16], new byte[16]);
        var ctr = new NczBuilder.Section(0x4100, 0x300, 3, key, counter);
        var mixed = new byte[0x4400];
        mixed.AsSpan(0x4000, 0x100).Fill(0x22);
        mixed.AsSpan(0x4100, 0x300).Fill(0x33);
        NintendoAes.Ctr(mixed.AsSpan(0x4100, 0x300), key, counter, 0x4100);
        Assert.Equal(mixed, ReadNcz(NczBuilder.Solid(mixed, plain, ctr)));

        var unaligned = new byte[0x4003 + 0x80];
        unaligned.AsSpan(0, 0x4000).Fill(0x44);
        unaligned.AsSpan(0x4000).Fill(0x55);
        var odd = new NczBuilder.Section(0x4003, 0x80, 3, key, counter);
        NintendoAes.Ctr(unaligned.AsSpan(0x4003, 0x80), key, counter, 0x4003);
        var restored = ReadNcz(NczBuilder.Solid(unaligned, odd));
        Assert.Equal(unaligned, restored);
    }

    [Fact]
    public async Task Xts_sections_match_the_original_when_keys_are_present()
    {
        using var temp = new TempDir();
        var headerKey = Pattern(32, 11);
        var kaek = Pattern(16, 12);
        var xts = Pattern(32, 13);
        var payload = new byte[0x4000];
        for (var i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)(0x40 + (i % 17));
        }

        var nca = SwitchBuilder.BuildNca(new SwitchBuilder.NcaSpec
        {
            HeaderKey = headerKey,
            Kaek = kaek,
            XtsKey = xts,
            Payload = payload,
            RomFs = false,
            Encryption = 2,
            ProgramId = 0x0100000000000099,
            ContentType = 4
        });
        var section = new NczBuilder.Section(0xC00, nca.Length - 0xC00, 2, new byte[16], new byte[16], xts);
        var ncz = NczBuilder.Solid(nca, section);
        var nsp = Path.Combine(temp.Path, "xts.nsz");
        var keys = Path.Combine(temp.Path, "prod.keys");
        await File.WriteAllBytesAsync(nsp, SwitchBuilder.Pfs0(("data.ncz", ncz)));
        await File.WriteAllTextAsync(keys, "header_key = " + Convert.ToHexString(headerKey) + "\nkey_area_key_application_00 = " + Convert.ToHexString(kaek) + "\n");

        await using (var locked = await DiscImages.OpenAsync(nsp))
        {
            var bytes = ReadAll(Find(locked.Root, "data.nca"));
            Assert.Equal(nca.AsSpan(0, NczFile.Preserved).ToArray(), bytes.AsSpan(0, NczFile.Preserved).ToArray());
            Assert.NotEqual(nca, bytes);
        }

        await using (var opened = await DiscImages.OpenAsync(nsp, new OpenOptions { KeysDirectory = keys }))
        {
            Assert.Equal(nca, ReadNode(Find(opened.Root, "data.nca")));
        }
    }

    [Fact]
    public async Task Corrupt_ncz_explains_the_problem_and_convert_can_be_cancelled()
    {
        using var temp = new TempDir();
        var sibling = SwitchBuilder.Pfs0(("note.txt", "plain"u8.ToArray()), ("game.ncz", Truncated()));
        var path = Path.Combine(temp.Path, "bad.nsz");
        await File.WriteAllBytesAsync(path, sibling);
        await using (var image = await DiscImages.OpenAsync(path))
        {
            Assert.Equal("plain", await ReadTextAsync(Find(image.Root, "note.txt")));
            Assert.Contains("truncated", image.Identity.Notice);
            Assert.Equal("game.ncz", Find(image.Root, "game.ncz").Name);
        }

        var exponent = Path.Combine(temp.Path, "exponent.nsz");
        await File.WriteAllBytesAsync(exponent, SwitchBuilder.Pfs0(("game.ncz", BadExponent())));
        await using (var image = await DiscImages.OpenAsync(exponent))
        {
            Assert.Contains("between 14 and 32", image.Identity.Notice);
        }

        var garbage = Path.Combine(temp.Path, "garbage.nsz");
        await File.WriteAllBytesAsync(garbage, SwitchBuilder.Pfs0(("game.ncz", GarbageFrame())));
        await using (var image = await DiscImages.OpenAsync(garbage))
        {
            var error = Assert.Throws<DiscFormatException>(() => ReadAll(Find(image.Root, "game.nca")));
            Assert.True(
                error.Message.Contains("zstd", StringComparison.OrdinalIgnoreCase)
                || error.Message.Contains("ended before", StringComparison.Ordinal),
                error.Message);
        }

        var wrong = Path.Combine(temp.Path, "wrong.nsz");
        await File.WriteAllBytesAsync(wrong, SwitchBuilder.Pfs0(("game.ncz", WrongBlock())));
        await using (var image = await DiscImages.OpenAsync(wrong))
        {
            var error = Assert.Throws<DiscFormatException>(() => ReadAll(Find(image.Root, "game.nca")));
            Assert.Contains("wrong size", error.Message);
        }

        var built = SwitchBuilder.Sample(temp.Path);
        var nsz = Path.Combine(temp.Path, "northwind.nsz");
        await File.WriteAllBytesAsync(nsz, SwitchBuilder.Pfs0(("control.ncz", NczBuilder.Solid(built.Nca, SectionOf(built)))));
        var cancelled = Path.Combine(temp.Path, "cancelled.nsp");
        using var already = new CancellationTokenSource();
        already.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SwitchConvert.ConvertAsync(nsz, cancelled, cancellationToken: already.Token));
        Assert.False(File.Exists(cancelled));

        using var mid = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SwitchConvert.ConvertAsync(nsz, cancelled, progress: new CancelProgress(mid), cancellationToken: mid.Token));
        Assert.False(File.Exists(cancelled));
        await Assert.ThrowsAsync<IOException>(() => SwitchConvert.ConvertAsync(nsz, nsz));
    }

    private static NczBuilder.Section SectionOf(SwitchBuilder.SampleSet built) =>
        new(0xC00, built.Nca.Length - 0xC00, 3, built.SectionKey, built.SectionCounter);

    private static byte[] ReadNcz(byte[] ncz)
    {
        var window = new ArraySource(ncz);
        Assert.True(NczFile.TryOpen(window, SwitchKeys.None, out var source, out var error), error);
        using (source)
        {
            var data = new byte[source!.Length];
            Assert.Equal(data.Length, source.ReadAt(0, data));
            var tail = new byte[32];
            var at = Math.Max(0, data.Length - 32);
            Assert.Equal(data.Length - at, source.ReadAt(at, tail.AsSpan(0, data.Length - at)));
            Assert.Equal(data.AsSpan(at).ToArray(), tail.AsSpan(0, data.Length - at).ToArray());
            return data;
        }
    }

    private static List<int> BlockSizes(byte[] ncz, int sections)
    {
        var at = NczFile.Preserved + 16 + (sections * NczFile.SectionBytes);
        Assert.Equal("NCZBLOCK", Encoding.ASCII.GetString(ncz, at, 8));
        var count = BitConverter.ToInt32(ncz, at + 12);
        var sizes = new List<int>(count);
        for (var i = 0; i < count; i++)
        {
            sizes.Add(BitConverter.ToInt32(ncz, at + 24 + (i * 4)));
        }

        return sizes;
    }

    private static byte[] Truncated()
    {
        var file = new byte[NczFile.Preserved + 16];
        "NCZSECTN"u8.CopyTo(file.AsSpan(NczFile.Preserved));
        BitConverter.TryWriteBytes(file.AsSpan(NczFile.Preserved + 8), 4);
        "NCZ"u8.CopyTo(file);
        return file;
    }

    private static byte[] BadExponent()
    {
        var file = new byte[NczFile.Preserved + 16 + NczFile.SectionBytes + 24];
        "NCZSECTN"u8.CopyTo(file.AsSpan(NczFile.Preserved));
        BitConverter.TryWriteBytes(file.AsSpan(NczFile.Preserved + 8), 1L);
        BitConverter.TryWriteBytes(file.AsSpan(NczFile.Preserved + 16), (long)NczFile.Preserved);
        BitConverter.TryWriteBytes(file.AsSpan(NczFile.Preserved + 24), 16L);
        BitConverter.TryWriteBytes(file.AsSpan(NczFile.Preserved + 32), 1L);
        var block = NczFile.Preserved + 16 + NczFile.SectionBytes;
        "NCZBLOCK"u8.CopyTo(file.AsSpan(block));
        file[block + 11] = 10;
        return file;
    }

    private static byte[] GarbageFrame()
    {
        var plain = new byte[NczFile.Preserved + 64];
        plain.AsSpan(NczFile.Preserved).Fill(0x7E);
        var ncz = NczBuilder.Solid(plain, new NczBuilder.Section(NczFile.Preserved, 64, 1, new byte[16], new byte[16]));
        var payload = NczFile.Preserved + 16 + NczFile.SectionBytes;
        Array.Fill(ncz, (byte)0xFF, payload, ncz.Length - payload);
        return ncz;
    }

    private static byte[] WrongBlock()
    {
        var tiny = NczBuilder.Compress(new byte[10]);
        var file = new byte[NczFile.Preserved + 16 + NczFile.SectionBytes + 24 + 4 + tiny.Length];
        "NCZSECTN"u8.CopyTo(file.AsSpan(NczFile.Preserved));
        BitConverter.TryWriteBytes(file.AsSpan(NczFile.Preserved + 8), 1L);
        BitConverter.TryWriteBytes(file.AsSpan(NczFile.Preserved + 16), (long)NczFile.Preserved);
        BitConverter.TryWriteBytes(file.AsSpan(NczFile.Preserved + 24), 100L);
        BitConverter.TryWriteBytes(file.AsSpan(NczFile.Preserved + 32), 1L);
        var block = NczFile.Preserved + 16 + NczFile.SectionBytes;
        "NCZBLOCK"u8.CopyTo(file.AsSpan(block));
        file[block + 8] = 2;
        file[block + 9] = 1;
        file[block + 11] = 14;
        BitConverter.TryWriteBytes(file.AsSpan(block + 12), 1);
        BitConverter.TryWriteBytes(file.AsSpan(block + 16), 100L);
        BitConverter.TryWriteBytes(file.AsSpan(block + 24), tiny.Length);
        tiny.CopyTo(file.AsSpan(block + 28));
        return file;
    }

    private static byte[] Pattern(int length, int seed)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    private static IDiscNode Find(IDiscNode node, string name)
    {
        foreach (var child in node.Children)
        {
            if (child.Name == name)
            {
                return child;
            }

            if (child.IsDirectory)
            {
                try
                {
                    return Find(child, name);
                }
                catch (FileNotFoundException)
                {
                }
            }
        }

        throw new FileNotFoundException(name);
    }

    private static byte[] ReadNode(IDiscNode node)
    {
        if (node is DiscNode concrete && concrete.OpenContainer is not null)
        {
            using var stream = concrete.OpenContainer();
            return ReadStream(stream);
        }

        return ReadAll(node);
    }

    private static byte[] ReadAll(IDiscNode node)
    {
        using var stream = node.OpenRead();
        return ReadStream(stream);
    }

    private static byte[] ReadStream(Stream stream)
    {
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private static async Task<string> ReadTextAsync(IDiscNode node)
    {
        using var reader = new StreamReader(node.OpenRead(), Encoding.ASCII);
        return await reader.ReadToEndAsync();
    }

    private static async Task<(int Code, string Stdout, string Stderr)> Run(params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var code = await CliHost.RunAsync(args, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    private sealed class ListProgress(List<SwitchConvert.ConvertProgress> items) : IProgress<SwitchConvert.ConvertProgress>
    {
        public void Report(SwitchConvert.ConvertProgress value) => items.Add(value);
    }

    private sealed class CancelProgress(CancellationTokenSource cancel) : IProgress<SwitchConvert.ConvertProgress>
    {
        public void Report(SwitchConvert.ConvertProgress value)
        {
            if (value.BytesWritten > 0)
            {
                cancel.Cancel();
            }
        }
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "discfolio-ncz", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, true);
            }
            catch (IOException)
            {
            }
        }
    }

    private sealed class ArraySource(byte[] data) : IByteSource
    {
        public long Length => data.Length;

        public int ReadAt(long position, Span<byte> destination)
        {
            if (position < 0 || position >= data.Length || destination.IsEmpty)
            {
                return 0;
            }

            var count = (int)Math.Min(destination.Length, data.Length - position);
            data.AsSpan((int)position, count).CopyTo(destination);
            return count;
        }
    }
}

internal static class NczBuilder
{
    public readonly record struct Section(long Offset, long Size, ulong Crypto, byte[] Key, byte[] Counter, byte[]? XtsKey = null);

    public static byte[] Solid(byte[] nca, params Section[] sections) => Pack(nca, sections, null, false);

    public static byte[] Blocks(byte[] nca, int exponent, bool rawLast, params Section[] sections) => Pack(nca, sections, exponent, rawLast);

    public static byte[] Compress(byte[] plain)
    {
        using var compressor = new Compressor(3);
        var bound = Compressor.GetCompressBound(plain.Length);
        var dest = new byte[bound];
        var written = compressor.Wrap(plain, dest);
        return dest[..written];
    }

    private static byte[] Pack(byte[] nca, Section[] sections, int? exponent, bool rawLast)
    {
        if (nca.Length < NczFile.Preserved || sections.Length == 0)
        {
            throw new InvalidOperationException("A synthetic NCZ needs a preserved header and one section.");
        }

        var plain = Payload(nca, sections);
        var compressed = exponent is null ? Compress(plain) : Block(plain, exponent.Value, rawLast);
        using var output = new MemoryStream();
        output.Write(nca, 0, NczFile.Preserved);
        output.Write("NCZSECTN"u8.ToArray());
        WriteU64(output, (ulong)sections.Length);
        foreach (var section in sections)
        {
            WriteU64(output, (ulong)section.Offset);
            WriteU64(output, (ulong)section.Size);
            WriteU64(output, section.Crypto);
            WriteU64(output, 0);
            output.Write(Fit(section.Key));
            output.Write(Fit(section.Counter));
        }

        output.Write(compressed);
        return output.ToArray();
    }

    private static byte[] Payload(byte[] nca, Section[] sections)
    {
        using var output = new MemoryStream();
        var first = true;
        if (sections[0].Offset > NczFile.Preserved)
        {
            WritePiece(output, nca, NczFile.Preserved, sections[0].Offset - NczFile.Preserved, 1, [], [], null);
            first = false;
        }

        foreach (var section in sections)
        {
            var start = section.Offset;
            var length = section.Size;
            if (first && start < NczFile.Preserved)
            {
                var skip = Math.Min(NczFile.Preserved - start, section.Size);
                start += skip;
                length -= skip;
            }

            first = false;
            if (length > 0)
            {
                WritePiece(output, nca, start, length, section.Crypto, section.Key, section.Counter, section.XtsKey);
            }
        }

        return output.ToArray();
    }

    private static void WritePiece(Stream output, byte[] nca, long start, long length, ulong crypto, byte[] key, byte[] counter, byte[]? xts)
    {
        var chunk = nca.AsSpan((int)start, (int)length).ToArray();
        if (crypto is 3 or 4)
        {
            NintendoAes.Ctr(chunk, key, counter, start);
        }
        else if (xts is { Length: 32 })
        {
            NintendoAes.Xts(chunk, xts, start / NintendoAes.Sector, encrypt: false);
        }

        output.Write(chunk);
    }

    private static byte[] Block(byte[] plain, int exponent, bool rawLast)
    {
        var blockSize = 1 << exponent;
        var chunks = new List<byte[]>();
        for (var offset = 0; offset < plain.Length; offset += blockSize)
        {
            var slice = plain.AsSpan(offset, Math.Min(blockSize, plain.Length - offset)).ToArray();
            var last = offset + blockSize >= plain.Length;
            if (last && rawLast)
            {
                chunks.Add(slice);
                continue;
            }

            var compressed = Compress(slice);
            chunks.Add(compressed.Length < slice.Length ? compressed : slice);
        }

        using var output = new MemoryStream();
        output.Write("NCZBLOCK"u8.ToArray());
        output.WriteByte(2);
        output.WriteByte(1);
        output.WriteByte(0);
        output.WriteByte((byte)exponent);
        WriteU32(output, (uint)chunks.Count);
        WriteU64(output, (ulong)plain.Length);
        foreach (var chunk in chunks)
        {
            WriteU32(output, (uint)chunk.Length);
        }

        foreach (var chunk in chunks)
        {
            output.Write(chunk);
        }

        return output.ToArray();
    }

    private static byte[] Fit(byte[] value)
    {
        var data = new byte[16];
        value.AsSpan(0, Math.Min(16, value.Length)).CopyTo(data);
        return data;
    }

    private static void WriteU32(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BitConverter.TryWriteBytes(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteU64(Stream stream, ulong value)
    {
        Span<byte> bytes = stackalloc byte[8];
        BitConverter.TryWriteBytes(bytes, value);
        stream.Write(bytes);
    }
}
