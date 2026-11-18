using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using EliteLevel.Disc.Cli;
using EliteLevel.Disc.Core;
using EliteLevel.Disc.Core.Extract;
using EliteLevel.Disc.Core.Formats.Switch;
using EliteLevel.Disc.Core.Titles;

namespace EliteLevel.Disc.Core.Tests;

public class SwitchTests
{
    [Fact]
    public void Xts_tweak_is_the_sector_as_a_big_endian_integer()
    {
        var tweak = new byte[16];
        NintendoAes.WritePlainTweak(tweak, 1);
        Assert.Equal(1, tweak[15]);
        Assert.Equal(0, tweak[0]);
        NintendoAes.WritePlainTweak(tweak, 0x0102);
        Assert.Equal(0x01, tweak[14]);
        Assert.Equal(0x02, tweak[15]);

        var one = new byte[16];
        one[15] = 1;
        NintendoAes.MultiplyTweak(one);
        Assert.Equal(2, one[15]);

        var high = new byte[16];
        high[0] = 0x80;
        NintendoAes.MultiplyTweak(high);
        Assert.Equal(0, high[0]);
        Assert.Equal(0x87, high[15]);
    }

    [Fact]
    public void Xts_and_ctr_round_trip_with_dummy_keys()
    {
        var xtsKey = Pattern(32, 3);
        var plain = new byte[0x400];
        for (var i = 0; i < plain.Length; i++)
        {
            plain[i] = (byte)(i * 3 + 1);
        }

        var cipher = plain.ToArray();
        NintendoAes.Xts(cipher, xtsKey, sector: 6, encrypt: true);
        Assert.NotEqual(plain, cipher);
        NintendoAes.Xts(cipher, xtsKey, sector: 6, encrypt: false);
        Assert.Equal(plain, cipher);

        var ctrKey = Pattern(16, 9);
        var upper = Pattern(8, 4);
        var body = new byte[50];
        for (var i = 0; i < body.Length; i++)
        {
            body[i] = (byte)(0x40 + i);
        }

        var encrypted = body.ToArray();
        NintendoAes.Ctr(encrypted, ctrKey, upper, absoluteOffset: 0xC03);
        Assert.NotEqual(body, encrypted);
        NintendoAes.Ctr(encrypted, ctrKey, upper, absoluteOffset: 0xC03);
        Assert.Equal(body, encrypted);
    }

    [Fact]
    public async Task Hand_built_pfs0_and_hfs0_round_trip_raw_bytes()
    {
        var nsp = HandPfs0("hi.txt", "hello"u8.ToArray());
        using var temp = new TempDir();
        var path = Path.Combine(temp.Path, "hand.nsp");
        await File.WriteAllBytesAsync(path, nsp);
        await using var image = await DiscImages.OpenAsync(path);
        Assert.Equal("switch", image.Identity.FormatId);
        Assert.Equal("NSP", image.Identity.FormatDisplayName);
        Assert.Equal("Nintendo Switch", image.Identity.Platform);
        Assert.Equal("hello", await ReadAsync(Find(image.Root, "hi.txt")));

        var hfs = HandHfs0("hi.txt", "hello"u8.ToArray());
        var xci = new byte[0x200 + hfs.Length];
        "HEAD"u8.CopyTo(xci.AsSpan(0x100));
        BinaryPrimitives.WriteInt64LittleEndian(xci.AsSpan(0x130), 0x200);
        hfs.CopyTo(xci.AsSpan(0x200));
        var card = Path.Combine(temp.Path, "hand.xci");
        await File.WriteAllBytesAsync(card, xci);
        await using var game = await DiscImages.OpenAsync(card);
        Assert.Equal("XCI", game.Identity.FormatDisplayName);
        Assert.Equal("hello", await ReadAsync(Find(game.Root, "hi.txt")));
    }

    [Fact]
    public async Task Xci_lists_partitions_and_extracts_raw_entries()
    {
        var secure = SwitchBuilder.Hfs0(("note.txt", "secure-bytes"u8.ToArray()));
        var update = SwitchBuilder.Hfs0(("patch.txt", "update-bytes"u8.ToArray()));
        var normal = SwitchBuilder.Hfs0(("icon.txt", "normal-bytes"u8.ToArray()));
        var logo = SwitchBuilder.Hfs0();
        var xci = SwitchBuilder.Xci(false, ("secure", secure), ("update", update), ("normal", normal), ("logo", logo));
        using var temp = new TempDir();
        var path = Path.Combine(temp.Path, "card.xci");
        await File.WriteAllBytesAsync(path, xci);

        await using var image = await DiscImages.OpenAsync(path);
        Assert.Equal("Nintendo Switch", image.Identity.Platform);
        Assert.Contains("secure", image.Identity.Layout);
        Assert.Contains("update", image.Identity.Layout);
        Assert.Contains("normal", image.Identity.Layout);
        Assert.Contains("logo", image.Identity.Layout);
        Assert.Equal("secure-bytes", await ReadAsync(Find(image.Root, "note.txt")));
        Assert.Equal("update-bytes", await ReadAsync(Find(image.Root, "patch.txt")));

        var dest = Path.Combine(temp.Path, "out");
        var result = await new Extractor().ExtractAsync([Find(image.Root, "note.txt")], new DirectoryInfo(dest), new ExtractOptions(), null, CancellationToken.None);
        Assert.Equal(1, result.Extracted);
        Assert.Equal("secure-bytes", await File.ReadAllTextAsync(Path.Combine(dest, "secure", "note.txt")));

        var prefixed = SwitchBuilder.Xci(true, ("secure", secure));
        var prefixedPath = Path.Combine(temp.Path, "prefixed.xci");
        await File.WriteAllBytesAsync(prefixedPath, prefixed);
        await using var withKeyArea = await DiscImages.OpenAsync(prefixedPath);
        Assert.Equal("secure-bytes", await ReadAsync(Find(withKeyArea.Root, "note.txt")));
    }

    [Fact]
    public async Task Nsp_without_keys_lists_the_container_and_names_the_title_id()
    {
        using var temp = new TempDir();
        var built = SwitchBuilder.Sample(temp.Path);
        await using var image = await DiscImages.OpenAsync(built.Nsp);
        Assert.Equal("Nintendo Switch", image.Identity.Platform);
        Assert.Contains("does not include keys", image.Identity.Notice);
        Assert.Equal(built.Nca.Length, Find(image.Root, "control.nca").Size);
        Assert.Equal("ticket", await ReadAsync(Find(image.Root, "0100000000010000.tik")));
        Assert.Equal("cert", await ReadAsync(Find(image.Root, "0100000000010000.cert")));
        Assert.DoesNotContain(Names(image.Root), name => name.Contains("readme", StringComparison.OrdinalIgnoreCase));

        var meta = TitleMetadataReader.Read(image);
        Assert.Equal("0100000000010000", meta.TitleId);
        Assert.Null(meta.Title);
        Assert.Null(meta.Icon);
    }

    [Fact]
    public async Task Encrypted_nca_opens_with_dummy_keys()
    {
        using var temp = new TempDir();
        var built = SwitchBuilder.Sample(temp.Path);
        await using var image = await DiscImages.OpenAsync(built.Nsp, new OpenOptions { KeysDirectory = built.ProdKeys });
        Assert.Null(image.Identity.Notice);
        var readme = Find(image.Root, "readme.txt");
        Assert.Equal("northwind", await ReadAsync(readme));
        var nested = Find(image.Root, "note.txt");
        Assert.Equal("nested", await ReadAsync(nested));
        Assert.Equal("/control.nca/RomFS/data/note.txt", nested.FullPath);

        var one = new byte[readme.Size];
        using (var stream = readme.OpenRead())
        {
            for (var i = 0; i < one.Length; i++)
            {
                Assert.Equal(1, stream.Read(one, i, 1));
            }
        }

        Assert.Equal(Encoding.ASCII.GetBytes("northwind"), one);

        var meta = TitleMetadataReader.Read(image);
        Assert.Equal("Northwind", meta.Title);
        Assert.Equal("0100000000010000", meta.TitleId);
        Assert.Equal("1.2.3", meta.Version);
        Assert.Equal("icon_AmericanEnglish.dat", meta.IconName);
        Assert.Equal(SwitchBuilder.Jpeg(), meta.Icon);

        var dest = Path.Combine(temp.Path, "art");
        var art = await Run("art", built.Nsp, "--keys", built.ProdKeys, "--out", dest);
        Assert.Equal(0, art.Code);
        Assert.True(File.Exists(Path.Combine(dest, "icon.jpg")));

        var info = await Run("info", built.Nsp, "--keys", built.ProdKeys);
        Assert.Equal(0, info.Code);
        Assert.Contains("Northwind", info.Stdout);
        Assert.Contains("0100000000010000", info.Stdout);
        Assert.Contains("1.2.3", info.Stdout);
        Assert.Contains("Keys", info.Stdout);
        Assert.DoesNotContain(File.ReadAllText(built.ProdKeys), info.Stdout);
        Assert.DoesNotContain(File.ReadAllText(built.ProdKeys), info.Stderr);

        var list = await Run("list", built.Nsp, "--keys", built.ProdKeys, "--path", "/control.nca/RomFS");
        Assert.Equal(0, list.Code);
        Assert.Contains("readme.txt", list.Stdout);
        Assert.Contains("data/", list.Stdout);

        var extracted = Path.Combine(temp.Path, "out");
        var extract = await Run("extract", built.Nsp, "--keys", built.ProdKeys, "--path", "/control.nca/RomFS/readme.txt", "--out", extracted);
        Assert.Equal(0, extract.Code);
        Assert.Equal("northwind", await File.ReadAllTextAsync(Path.Combine(extracted, "control.nca", "RomFS", "readme.txt")));
    }

    [Fact]
    public async Task Wrong_header_key_leaves_the_nca_encrypted()
    {
        using var temp = new TempDir();
        var built = SwitchBuilder.Sample(temp.Path);
        var wrong = Path.Combine(temp.Path, "wrong.keys");
        await File.WriteAllTextAsync(wrong, "header_key = " + Convert.ToHexString(Pattern(32, 99)) + "\n");
        await using var image = await DiscImages.OpenAsync(built.Nsp, new OpenOptions { KeysDirectory = wrong });
        Assert.Contains("did not decrypt", image.Identity.Notice);
        Assert.False(Find(image.Root, "control.nca").IsDirectory);
        Assert.DoesNotContain(Names(image.Root), name => name == "readme.txt");
    }

    [Fact]
    public async Task Title_key_and_xts_and_nca2_use_dummy_keys()
    {
        using var temp = new TempDir();
        var headerKey = Pattern(32, 11);
        var titleKek = Pattern(16, 12);
        var titleKey = Pattern(16, 13);
        var rights = new byte[16];
        rights[0] = 0x01;
        rights[7] = 0x42;
        var encryptedTitle = titleKey.ToArray();
        NintendoAes.Ecb(encryptedTitle, titleKek, encrypt: true);
        Assert.NotEqual(titleKey, encryptedTitle);

        var exefs = SwitchBuilder.Pfs0(("main", "exe-bytes"u8.ToArray()), ("main.npdm", "meta"u8.ToArray()));
        var nca = SwitchBuilder.BuildNca(new SwitchBuilder.NcaSpec
        {
            HeaderKey = headerKey,
            Payload = exefs,
            RomFs = false,
            Encryption = 3,
            CtrKey = titleKey,
            CtrUpper = Pattern(8, 14),
            ProgramId = 0x0100000000000042,
            ContentType = 0,
            RightsId = rights
        });
        var nsp = SwitchBuilder.Pfs0(("program.nca", nca));
        var nspPath = Path.Combine(temp.Path, "title.nsp");
        var prod = Path.Combine(temp.Path, "prod.keys");
        var titles = Path.Combine(temp.Path, "title.keys");
        await File.WriteAllBytesAsync(nspPath, nsp);
        await File.WriteAllTextAsync(prod, "header_key = " + Convert.ToHexString(headerKey) + "\ntitlekek_00 = " + Convert.ToHexString(titleKek) + "\n");
        await File.WriteAllTextAsync(titles, Convert.ToHexString(rights) + " = " + Convert.ToHexString(encryptedTitle) + "\n");

        await using var image = await DiscImages.OpenAsync(nspPath, new OpenOptions { KeysDirectory = prod });
        Assert.Equal("exe-bytes", await ReadAsync(Find(image.Root, "main")));
        Assert.Equal("/program.nca/ExeFS/main", Find(image.Root, "main").FullPath);

        var xtsKey = Pattern(32, 21);
        var kaek = Pattern(16, 22);
        var pfs = SwitchBuilder.Pfs0(("note.txt", "xts-ok"u8.ToArray()));
        var xtsNca = SwitchBuilder.BuildNca(new SwitchBuilder.NcaSpec
        {
            HeaderKey = headerKey,
            Kaek = kaek,
            Payload = pfs,
            RomFs = false,
            Encryption = 2,
            XtsKey = xtsKey,
            ProgramId = 0x0100000000000099,
            ContentType = 4,
            Nca2 = true
        });
        var card = SwitchBuilder.Xci(false, ("secure", SwitchBuilder.Hfs0(("data.nca", xtsNca))));
        var cardPath = Path.Combine(temp.Path, "xts.xci");
        var xtsProd = Path.Combine(temp.Path, "xts-prod.keys");
        await File.WriteAllBytesAsync(cardPath, card);
        await File.WriteAllTextAsync(xtsProd, "header_key = " + Convert.ToHexString(headerKey) + "\nkey_area_key_application_00 = " + Convert.ToHexString(kaek) + "\n");
        await using var xtsImage = await DiscImages.OpenAsync(cardPath, new OpenOptions { KeysDirectory = xtsProd });
        Assert.Equal("xts-ok", await ReadAsync(Find(xtsImage.Root, "note.txt")));
    }

    [Fact]
    public async Task Compressed_switch_images_are_named_and_refused()
    {
        using var temp = new TempDir();
        var solid = Path.Combine(temp.Path, "game.nsz");
        await File.WriteAllBytesAsync(solid, [0x28, 0xB5, 0x2F, 0xFD, 1, 2, 3, 4]);
        var solidError = await Assert.ThrowsAsync<DiscFormatException>(() => DiscImages.OpenAsync(solid));
        Assert.Equal(DiscFormatException.SwitchCompressed, solidError.Message);

        var xcz = Path.Combine(temp.Path, "game.xcz");
        await File.WriteAllBytesAsync(xcz, [9, 9, 9, 9]);
        var xczError = await Assert.ThrowsAsync<DiscFormatException>(() => DiscImages.OpenAsync(xcz));
        Assert.Equal(DiscFormatException.SwitchCompressed, xczError.Message);

        var block = Path.Combine(temp.Path, "block.nsz");
        await File.WriteAllBytesAsync(block, SwitchBuilder.Pfs0(("game.ncz", "compressed"u8.ToArray()), ("note.txt", "plain"u8.ToArray())));
        await using var image = await DiscImages.OpenAsync(block);
        Assert.Equal("NSZ", image.Identity.FormatDisplayName);
        Assert.Contains("NCZSECTN", image.Identity.Notice);
        Assert.Equal("plain", await ReadAsync(Find(image.Root, "note.txt")));
        Assert.Equal("compressed", await ReadAsync(Find(image.Root, "game.ncz")));

        var cli = await Run("info", solid, "--json");
        Assert.Equal(2, cli.Code);
        using var doc = JsonDocument.Parse(cli.Stdout);
        Assert.Equal("unsupported", doc.RootElement.GetProperty("error").GetProperty("kind").GetString());
        Assert.Equal(DiscFormatException.SwitchCompressed, doc.RootElement.GetProperty("error").GetProperty("message").GetString());

        var missing = await Run("info", solid, "--keys", Path.Combine(temp.Path, "missing.keys"));
        Assert.Equal(1, missing.Code);
        Assert.Contains("does not exist", missing.Stderr);
    }

    [Fact]
    public async Task Renamed_and_incomplete_containers_keep_a_clear_message()
    {
        using var temp = new TempDir();
        var renamed = Path.Combine(temp.Path, "archive.bin");
        await File.WriteAllBytesAsync(renamed, SwitchBuilder.Pfs0(("note.txt", "renamed"u8.ToArray())));
        await using var image = await DiscImages.OpenAsync(renamed);
        Assert.Equal("switch", image.Identity.FormatId);
        Assert.Equal("renamed", await ReadAsync(Find(image.Root, "note.txt")));

        var bad = new byte[16];
        "PFS0"u8.CopyTo(bad);
        BinaryPrimitives.WriteInt32LittleEndian(bad.AsSpan(4), 8);
        BinaryPrimitives.WriteInt32LittleEndian(bad.AsSpan(8), 64);
        var badPath = Path.Combine(temp.Path, "broken.nsp");
        await File.WriteAllBytesAsync(badPath, bad);
        var error = await Assert.ThrowsAsync<DiscFormatException>(() => DiscImages.OpenAsync(badPath));
        Assert.Contains("PFS0 header is not valid", error.Message);

        var shortCard = Path.Combine(temp.Path, "short.xci");
        await File.WriteAllBytesAsync(shortCard, new byte[16]);
        var cardError = await Assert.ThrowsAsync<DiscFormatException>(() => DiscImages.OpenAsync(shortCard));
        Assert.Contains("no HFS0 partition table", cardError.Message);
    }

    private static byte[] HandPfs0(string name, byte[] data)
    {
        var text = Encoding.UTF8.GetBytes(name + "\0");
        var header = 0x10 + 0x18 + text.Length;
        var file = new byte[header + data.Length];
        "PFS0"u8.CopyTo(file);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(4), 1);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(8), text.Length);
        BinaryPrimitives.WriteInt64LittleEndian(file.AsSpan(0x10), 0);
        BinaryPrimitives.WriteInt64LittleEndian(file.AsSpan(0x18), data.Length);
        text.CopyTo(file.AsSpan(0x28));
        data.CopyTo(file.AsSpan(header));
        return file;
    }

    private static byte[] HandHfs0(string name, byte[] data)
    {
        var text = Encoding.UTF8.GetBytes(name + "\0");
        var header = 0x10 + 0x40 + text.Length;
        var file = new byte[header + data.Length];
        "HFS0"u8.CopyTo(file);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(4), 1);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(8), text.Length);
        BinaryPrimitives.WriteInt64LittleEndian(file.AsSpan(0x10), 0);
        BinaryPrimitives.WriteInt64LittleEndian(file.AsSpan(0x18), data.Length);
        text.CopyTo(file.AsSpan(0x50));
        data.CopyTo(file.AsSpan(header));
        return file;
    }

    private static byte[] Pattern(int length, int seed)
    {
        var data = new byte[length];
        var rng = new Random(seed);
        rng.NextBytes(data);
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

    private static List<string> Names(IDiscNode node)
    {
        var names = new List<string>();
        void Walk(IDiscNode current)
        {
            foreach (var child in current.Children)
            {
                names.Add(child.Name);
                if (child.IsDirectory)
                {
                    Walk(child);
                }
            }
        }

        Walk(node);
        return names;
    }

    private static async Task<string> ReadAsync(IDiscNode node)
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

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "discfolio-switch", Guid.NewGuid().ToString("N"));
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
}

internal static class SwitchBuilder
{
    public sealed class NcaSpec
    {
        public byte[] HeaderKey { get; init; } = [];
        public byte[]? Kaek { get; init; }
        public byte[]? XtsKey { get; init; }
        public byte[]? CtrKey { get; init; }
        public byte[]? CtrUpper { get; init; }
        public byte[]? RightsId { get; init; }
        public byte[] Payload { get; init; } = [];
        public bool RomFs { get; init; } = true;
        public byte Encryption { get; init; } = 3;
        public ulong ProgramId { get; init; }
        public byte ContentType { get; init; } = 2;
        public bool Nca2 { get; init; }
    }

    public sealed record SampleSet(string Nsp, string ProdKeys, byte[] Nca, byte[] SectionKey, byte[] SectionCounter);

    public static SampleSet Sample(string directory)
    {
        var headerKey = new byte[32];
        var kaek = new byte[16];
        var ctr = new byte[16];
        var upper = new byte[8];
        new Random(7).NextBytes(headerKey);
        new Random(8).NextBytes(kaek);
        new Random(9).NextBytes(ctr);
        new Random(10).NextBytes(upper);
        var rom = RomFs(
            ("readme.txt", "northwind"u8.ToArray()),
            ("data/note.txt", "nested"u8.ToArray()),
            ("control.nacp", Nacp("Northwind", "1.2.3")),
            ("icon_AmericanEnglish.dat", Jpeg()));
        var nca = BuildNca(new NcaSpec
        {
            HeaderKey = headerKey,
            Kaek = kaek,
            CtrKey = ctr,
            CtrUpper = upper,
            Payload = rom,
            RomFs = true,
            Encryption = 3,
            ProgramId = 0x0100000000010000,
            ContentType = 2
        });
        var xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <ContentMeta>
              <Type>Application</Type>
              <Id>0100000000010000</Id>
              <Version>65536</Version>
            </ContentMeta>
            """u8.ToArray();
        var nsp = Pfs0(
            ("control.nca", nca),
            ("0100000000010000.tik", "ticket"u8.ToArray()),
            ("0100000000010000.cert", "cert"u8.ToArray()),
            ("0100000000010000.cnmt.xml", xml));
        var nspPath = Path.Combine(directory, "northwind.nsp");
        var keysPath = Path.Combine(directory, "prod.keys");
        File.WriteAllBytes(nspPath, nsp);
        File.WriteAllText(keysPath, "header_key = " + Convert.ToHexString(headerKey) + "\nkey_area_key_application_00 = " + Convert.ToHexString(kaek) + "\n");
        var counter = new byte[16];
        upper.CopyTo(counter);
        return new SampleSet(nspPath, keysPath, nca, ctr, counter);
    }

    public static byte[] Jpeg() => [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x00, 0xFF, 0xD9];

    public static byte[] Nacp(string title, string version)
    {
        var data = new byte[0x4000];
        Encoding.UTF8.GetBytes(title).CopyTo(data);
        Encoding.UTF8.GetBytes(version).CopyTo(data.AsSpan(0x3060));
        return data;
    }

    public static byte[] Pfs0(params (string Name, byte[] Data)[] files) => Partition(files, hashed: false);

    public static byte[] Hfs0(params (string Name, byte[] Data)[] files) => Partition(files, hashed: true);

    public static byte[] Xci(bool keyArea, params (string Name, byte[] Data)[] partitions)
    {
        var root = Hfs0(partitions);
        var headerAt = keyArea ? 0x1000 : 0;
        var rootAt = headerAt + 0x200;
        var file = new byte[rootAt + root.Length];
        "HEAD"u8.CopyTo(file.AsSpan(headerAt + 0x100));
        BinaryPrimitives.WriteInt64LittleEndian(file.AsSpan(headerAt + 0x130), 0x200);
        root.CopyTo(file.AsSpan(rootAt));
        return file;
    }

    public static byte[] BuildNca(NcaSpec spec)
    {
        var padded = (spec.Payload.Length + 0x1FF) & ~0x1FF;
        var body = new byte[padded == 0 ? 0x200 : padded];
        spec.Payload.CopyTo(body);
        var header = new byte[Nca.HeaderSize];
        header[Nca.Magic] = (byte)'N';
        header[Nca.Magic + 1] = (byte)'C';
        header[Nca.Magic + 2] = (byte)'A';
        header[Nca.Magic + 3] = spec.Nca2 ? (byte)'2' : (byte)'3';
        header[Nca.Distribution] = spec.Encryption == 2 ? (byte)1 : (byte)0;
        header[Nca.ContentType] = spec.ContentType;
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(Nca.ContentSize), (ulong)(Nca.HeaderSize + body.Length));
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(Nca.ProgramId), spec.ProgramId);
        if (spec.RightsId is { Length: 16 })
        {
            spec.RightsId.CopyTo(header.AsSpan(Nca.RightsId));
        }

        const uint start = 6;
        var blocks = (uint)(body.Length / NintendoAes.Sector);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(Nca.FsEntry), start);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(Nca.FsEntry + 4), start + blocks);
        var fs = header.AsSpan(Nca.FsHeaderBase, NintendoAes.Sector);
        BinaryPrimitives.WriteUInt16LittleEndian(fs, 2);
        fs[Nca.FsType] = spec.RomFs ? (byte)0 : (byte)1;
        fs[Nca.HashType] = spec.RomFs ? (byte)3 : (byte)2;
        fs[Nca.EncryptionType] = spec.Encryption;
        if (spec.RomFs)
        {
            "IVFC"u8.CopyTo(fs[Nca.HashData..]);
            BinaryPrimitives.WriteInt32LittleEndian(fs[(Nca.HashData + 4)..], 0x20000);
            BinaryPrimitives.WriteInt32LittleEndian(fs[(Nca.HashData + 8)..], 0x20);
            BinaryPrimitives.WriteInt32LittleEndian(fs[(Nca.HashData + 0xC)..], 1);
            BinaryPrimitives.WriteInt64LittleEndian(fs[(Nca.HashData + 0x18)..], body.Length);
        }
        else
        {
            BinaryPrimitives.WriteInt32LittleEndian(fs[(Nca.HashData + 0x20)..], NintendoAes.Sector);
            BinaryPrimitives.WriteInt32LittleEndian(fs[(Nca.HashData + 0x24)..], 1);
            BinaryPrimitives.WriteInt64LittleEndian(fs[(Nca.HashData + 0x30)..], body.Length);
        }

        (spec.CtrUpper ?? new byte[8]).CopyTo(fs[Nca.CtrUpper..]);
        if (spec.RightsId is not { Length: 16 } && spec.Kaek is { Length: 16 })
        {
            var area = new byte[0x40];
            if (spec.XtsKey is { Length: 32 })
            {
                spec.XtsKey.CopyTo(area);
            }

            if (spec.CtrKey is { Length: 16 })
            {
                spec.CtrKey.CopyTo(area.AsSpan(32));
            }

            for (var i = 0; i < 4; i++)
            {
                NintendoAes.Ecb(area.AsSpan(i * 16, 16), spec.Kaek, encrypt: true);
            }

            area.CopyTo(header.AsSpan(Nca.KeyArea));
        }

        if (spec.Nca2)
        {
            NintendoAes.Xts(header.AsSpan(0, 0x400), spec.HeaderKey, 0, encrypt: true);
            for (var i = 0; i < 4; i++)
            {
                NintendoAes.Xts(header.AsSpan(Nca.FsHeaderBase + (i * NintendoAes.Sector), NintendoAes.Sector), spec.HeaderKey, 0, encrypt: true);
            }
        }
        else
        {
            NintendoAes.Xts(header, spec.HeaderKey, 0, encrypt: true);
        }

        if (spec.Encryption == 2 && spec.XtsKey is { Length: 32 })
        {
            NintendoAes.Xts(body, spec.XtsKey, start, encrypt: true);
        }
        else if (spec.Encryption == 3 && spec.CtrKey is { Length: 16 })
        {
            NintendoAes.Ctr(body, spec.CtrKey, spec.CtrUpper ?? new byte[8], start * NintendoAes.Sector);
        }

        var file = new byte[header.Length + body.Length];
        header.CopyTo(file);
        body.CopyTo(file.AsSpan(header.Length));
        return file;
    }

    public static byte[] RomFs(params (string Path, byte[] Data)[] files)
    {
        var root = new Dir("", null);
        foreach (var file in files)
        {
            var parts = file.Path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            var dir = root;
            for (var i = 0; i < parts.Length - 1; i++)
            {
                var next = dir.Dirs.FirstOrDefault(item => item.Name == parts[i]);
                if (next is null)
                {
                    next = new Dir(parts[i], dir);
                    dir.Dirs.Add(next);
                }

                dir = next;
            }

            dir.Files.Add((parts[^1], file.Data));
        }

        var dirs = new List<Dir>();
        void Collect(Dir dir)
        {
            dirs.Add(dir);
            foreach (var child in dir.Dirs)
            {
                Collect(child);
            }
        }

        Collect(root);
        int Align(int value) => (value + 3) & ~3;
        int DirBytes(Dir dir) => dir.Parent is null ? 0x18 : Align(0x18 + Encoding.UTF8.GetByteCount(dir.Name));
        int FileBytes(string name) => Align(0x20 + Encoding.UTF8.GetByteCount(name));
        var dirOffset = new Dictionary<Dir, int>();
        var cursor = 0;
        foreach (var dir in dirs)
        {
            dirOffset[dir] = cursor;
            cursor += DirBytes(dir);
        }

        var dirTableSize = cursor;
        var fileOffset = new Dictionary<(Dir Dir, int Index), int>();
        cursor = 0;
        foreach (var dir in dirs)
        {
            for (var i = 0; i < dir.Files.Count; i++)
            {
                fileOffset[(dir, i)] = cursor;
                cursor += FileBytes(dir.Files[i].Name);
            }
        }

        var fileTableSize = cursor;
        const int headerSize = 0x50;
        const int hash = 4;
        var dirTable = headerSize + hash;
        var fileTable = dirTable + dirTableSize + hash;
        var dataAt = (fileTable + fileTableSize + 0xF) & ~0xF;
        var dataCursor = 0;
        var dataOffset = new Dictionary<(Dir Dir, int Index), int>();
        foreach (var dir in dirs)
        {
            for (var i = 0; i < dir.Files.Count; i++)
            {
                dataOffset[(dir, i)] = dataCursor;
                dataCursor += dir.Files[i].Data.Length;
            }
        }

        var blob = new byte[dataAt + dataCursor];
        void W32(int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(at), value);
        void W64(int at, long value) => BinaryPrimitives.WriteInt64LittleEndian(blob.AsSpan(at), value);
        W64(0, headerSize);
        W64(0x08, headerSize);
        W64(0x10, hash);
        W64(0x18, dirTable);
        W64(0x20, dirTableSize);
        W64(0x28, dirTable + dirTableSize);
        W64(0x30, hash);
        W64(0x38, fileTable);
        W64(0x40, fileTableSize);
        W64(0x48, dataAt);
        blob.AsSpan(headerSize, hash).Fill(0xFF);
        blob.AsSpan(dirTable + dirTableSize, hash).Fill(0xFF);

        foreach (var dir in dirs)
        {
            var at = dirTable + dirOffset[dir];
            W32(at, dir.Parent is null ? 0 : (uint)dirOffset[dir.Parent]);
            W32(at + 4, Sibling(dir, dirOffset));
            W32(at + 8, dir.Dirs.Count == 0 ? 0xFFFFFFFFu : (uint)dirOffset[dir.Dirs[0]]);
            W32(at + 0xC, dir.Files.Count == 0 ? 0xFFFFFFFFu : (uint)fileOffset[(dir, 0)]);
            W32(at + 0x10, 0xFFFFFFFFu);
            var name = dir.Parent is null ? [] : Encoding.UTF8.GetBytes(dir.Name);
            W32(at + 0x14, (uint)name.Length);
            name.CopyTo(blob.AsSpan(at + 0x18));
            for (var i = 0; i < dir.Files.Count; i++)
            {
                var fat = fileTable + fileOffset[(dir, i)];
                var bytes = Encoding.UTF8.GetBytes(dir.Files[i].Name);
                W32(fat, (uint)dirOffset[dir]);
                W32(fat + 4, i + 1 < dir.Files.Count ? (uint)fileOffset[(dir, i + 1)] : 0xFFFFFFFFu);
                W64(fat + 8, dataOffset[(dir, i)]);
                W64(fat + 0x10, dir.Files[i].Data.Length);
                W32(fat + 0x18, 0xFFFFFFFFu);
                W32(fat + 0x1C, (uint)bytes.Length);
                bytes.CopyTo(blob.AsSpan(fat + 0x20));
                dir.Files[i].Data.CopyTo(blob.AsSpan(dataAt + dataOffset[(dir, i)]));
            }
        }

        return blob;
    }

    private static uint Sibling(Dir dir, Dictionary<Dir, int> offsets)
    {
        if (dir.Parent is null)
        {
            return 0xFFFFFFFF;
        }

        var list = dir.Parent.Dirs;
        var index = list.IndexOf(dir);
        return index < 0 || index + 1 >= list.Count ? 0xFFFFFFFF : (uint)offsets[list[index + 1]];
    }

    private static byte[] Partition((string Name, byte[] Data)[] files, bool hashed)
    {
        var names = files.Select(file => Encoding.UTF8.GetBytes(file.Name + "\0")).ToArray();
        var raw = names.Sum(name => name.Length);
        var strings = raw == 0 ? 0 : (raw + 0xF) & ~0xF;
        var entry = hashed ? 0x40 : 0x18;
        var header = 0x10 + (files.Length * entry) + strings;
        var size = header;
        foreach (var file in files)
        {
            size += file.Data.Length;
        }

        var result = new byte[size];
        (hashed ? "HFS0"u8 : "PFS0"u8).CopyTo(result);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(4), files.Length);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(8), strings);
        var nameAt = 0;
        var dataAt = 0;
        for (var i = 0; i < files.Length; i++)
        {
            var at = 0x10 + (i * entry);
            BinaryPrimitives.WriteInt64LittleEndian(result.AsSpan(at), dataAt);
            BinaryPrimitives.WriteInt64LittleEndian(result.AsSpan(at + 8), files[i].Data.Length);
            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(at + 0x10), nameAt);
            names[i].CopyTo(result.AsSpan(0x10 + (files.Length * entry) + nameAt));
            files[i].Data.CopyTo(result.AsSpan(header + dataAt));
            nameAt += names[i].Length;
            dataAt += files[i].Data.Length;
        }

        return result;
    }

    private sealed class Dir(string name, Dir? parent)
    {
        public string Name { get; } = name;
        public Dir? Parent { get; } = parent;
        public List<Dir> Dirs { get; } = [];
        public List<(string Name, byte[] Data)> Files { get; } = [];
    }
}
