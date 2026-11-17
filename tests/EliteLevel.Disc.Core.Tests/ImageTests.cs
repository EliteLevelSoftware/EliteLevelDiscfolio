using System.Security.Cryptography;
using System.Text;
using DiscUtils.Iso9660;
using EliteLevel.Disc.Core.Extract;
using EliteLevel.Disc.Core.Formats;
using EliteLevel.Disc.Core.Hash;
using EliteLevel.Disc.Core.IO;
using EliteLevel.Disc.Core.Search;

namespace EliteLevel.Disc.Core.Tests;

public class ImageTests
{
    [Fact]
    public async Task Iso9660_lists_reads_extracts_and_hashes_joliet_names()
    {
        var body = Encoding.ASCII.GetBytes("hello");
        var longName = "Long Joliet File Name With Spaces.txt";
        using var temp = new TempDir();
        var isoPath = Path.Combine(temp.Path, "northwind.iso");
        var builder = new CDBuilder { UseJoliet = true, VolumeIdentifier = "NORTHWIND" };
        builder.AddFile(@"PS3_GAME\USRDIR\DATA.BIN", body);
        builder.AddFile(@"PS3_GAME\USRDIR\EBOOT.BIN", Encoding.ASCII.GetBytes("encrypted-bytes"));
        builder.AddFile(@"PS3_GAME\LICDIR\LIC.DAT", Encoding.ASCII.GetBytes("license"));
        builder.AddFile(@"PS3_UPDATE\EBOOT.BIN", Encoding.ASCII.GetBytes("update"));
        builder.AddFile(@"PS3_GAME\" + longName, Encoding.UTF8.GetBytes("joliet"));
        builder.Build(isoPath);

        await using var image = await DiscImages.OpenAsync(isoPath);
        Assert.Equal("iso9660", image.Identity.FormatId);
        Assert.Contains("Joliet", image.Identity.FormatDisplayName, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("NORTHWIND", image.Identity.VolumeLabel);
        Assert.Equal("PlayStation 3", image.Identity.Platform);

        var names = Names(image.Root);
        Assert.Contains("/PS3_GAME/" + longName, names);
        var data = Find(image.Root, "DATA.BIN");
        using var stream = data.OpenRead();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        Assert.Equal(body, memory.ToArray());
        Assert.False(stream.CanWrite);

        var hash = await new HashService().ComputeAsync(data, HashAlgorithmKind.Sha256 | HashAlgorithmKind.Sha1 | HashAlgorithmKind.Md5, CancellationToken.None);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant(), hash.Sha256);
        Assert.Equal(Convert.ToHexString(SHA1.HashData(body)).ToLowerInvariant(), hash.Sha1);
        Assert.Equal(Convert.ToHexString(MD5.HashData(body)).ToLowerInvariant(), hash.Md5);

        var dest = Path.Combine(temp.Path, "out");
        var first = await new Extractor().ExtractAsync([image.Root], new DirectoryInfo(dest), new ExtractOptions { SkipExisting = true }, null, CancellationToken.None);
        Assert.True(File.Exists(Path.Combine(dest, "PS3_GAME", "USRDIR", "DATA.BIN")));
        Assert.False(File.Exists(Path.Combine(dest, "PS3_GAME", "USRDIR", "EBOOT.BIN")));
        Assert.False(File.Exists(Path.Combine(dest, "PS3_UPDATE", "EBOOT.BIN")));
        Assert.False(File.Exists(Path.Combine(dest, "PS3_GAME", "LICDIR", "LIC.DAT")));
        Assert.True(first.ProtectedSkipped >= 3);
        Assert.Equal(body, await File.ReadAllBytesAsync(Path.Combine(dest, "PS3_GAME", "USRDIR", "DATA.BIN")));

        var sourceHash = Convert.ToHexString(await SHA256.HashDataAsync(File.OpenRead(isoPath)));
        var second = await new Extractor().ExtractAsync([image.Root], new DirectoryInfo(dest), new ExtractOptions { SkipExisting = true }, null, CancellationToken.None);
        Assert.True(second.Skipped >= 1);
        Assert.Equal(0, second.Extracted);
        Assert.Equal(sourceHash, Convert.ToHexString(await SHA256.HashDataAsync(File.OpenRead(isoPath))));

        var forced = Path.Combine(temp.Path, "forced");
        var third = await new Extractor().ExtractAsync(
            [image.Root],
            new DirectoryInfo(forced),
            new ExtractOptions { IncludeProtected = true },
            null,
            CancellationToken.None);
        Assert.Equal(0, third.ProtectedSkipped);
        Assert.Equal("encrypted-bytes", await File.ReadAllTextAsync(Path.Combine(forced, "PS3_GAME", "USRDIR", "EBOOT.BIN")));
        Assert.Equal("update", await File.ReadAllTextAsync(Path.Combine(forced, "PS3_UPDATE", "EBOOT.BIN")));
    }

    [Fact]
    public async Task Public_spec_xdvdfs_bytes_round_trip_a_single_file()
    {
        var partition = new byte[35 * XdvdfsBuilder.SectorSize];
        var descriptor = partition.AsSpan(32 * XdvdfsBuilder.SectorSize);
        "MICROSOFT*XBOX*MEDIA"u8.CopyTo(descriptor);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(descriptor[0x14..], 33);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(descriptor[0x18..], 2048);
        "MICROSOFT*XBOX*MEDIA"u8.CopyTo(descriptor[0x7EC..]);

        var entry = partition.AsSpan(33 * XdvdfsBuilder.SectorSize);
        entry.Fill(0xFF);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(entry, 0);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(entry[2..], 0);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(entry[4..], 34);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(entry[8..], 5);
        entry[12] = 0x01;
        entry[13] = 9;
        "HELLO.TXT"u8.CopyTo(entry[14..]);

        "hello"u8.CopyTo(partition.AsSpan(34 * XdvdfsBuilder.SectorSize));

        using var temp = new TempDir();
        var path = Path.Combine(temp.Path, "spec.iso");
        await File.WriteAllBytesAsync(path, partition);
        await using var image = await DiscImages.OpenAsync(path);
        Assert.Equal("xdvdfs", image.Identity.FormatId);
        Assert.Equal("plain", image.Identity.Layout);
        var file = Assert.Single(image.Root.Children);
        Assert.Equal("HELLO.TXT", file.Name);
        Assert.Equal(5, file.Size);
        using var reader = new StreamReader(file.OpenRead(), Encoding.ASCII);
        Assert.Equal("hello", reader.ReadToEnd());
    }

    [Fact]
    public async Task Xdvdfs_tree_xgd_offsets_system_update_and_skip_existing()
    {
        var files = new (string Path, byte[] Data)[]
        {
            ("default.xex", Encoding.ASCII.GetBytes("xex")),
            ("media/ICON.PNG", Encoding.ASCII.GetBytes("png-bytes")),
            ("$SystemUpdate/update.bin", Encoding.ASCII.GetBytes("updater"))
        };
        var partition = XdvdfsBuilder.BuildPartition(files);
        using var temp = new TempDir();

        var plain = XdvdfsBuilder.WriteImage(temp.Path, "plain.iso", partition, 0);
        await using (var image = await DiscImages.OpenAsync(plain))
        {
            Assert.Equal("Xbox 360", image.Identity.Platform);
            Assert.DoesNotContain(Names(image.Root), name => name.Contains("$SystemUpdate", StringComparison.OrdinalIgnoreCase));
            Assert.Contains("/default.xex", Names(image.Root));
            Assert.Contains("/media/ICON.PNG", Names(image.Root));
        }

        await using (var shown = await DiscImages.OpenAsync(plain, new OpenOptions { ShowSystemUpdate = true }))
        {
            Assert.Contains(Names(shown.Root), name => name.Contains("$SystemUpdate", StringComparison.OrdinalIgnoreCase));
            var dest = Path.Combine(temp.Path, "skip");
            var extractor = new Extractor();
            var first = await extractor.ExtractAsync([shown.Root], new DirectoryInfo(dest), new ExtractOptions { SkipExisting = true }, null, CancellationToken.None);
            Assert.False(File.Exists(Path.Combine(dest, "$SystemUpdate", "update.bin")));
            Assert.True(File.Exists(Path.Combine(dest, "default.xex")));
            Assert.True(first.ProtectedSkipped >= 1);
            var again = await extractor.ExtractAsync([shown.Root], new DirectoryInfo(dest), new ExtractOptions { SkipExisting = true }, null, CancellationToken.None);
            Assert.True(again.Skipped >= 2);
            Assert.Equal(0, again.Extracted);

            var withUpdate = Path.Combine(temp.Path, "with-update");
            await extractor.ExtractAsync(
                [shown.Root],
                new DirectoryInfo(withUpdate),
                new ExtractOptions { IncludeProtected = true },
                null,
                CancellationToken.None);
            Assert.Equal("updater", await File.ReadAllTextAsync(Path.Combine(withUpdate, "$SystemUpdate", "update.bin")));
        }

        var prefix = new byte[64 * 1024];
        "CD001"u8.CopyTo(prefix.AsSpan(16 * 2048 + 1));
        var xgd3 = XdvdfsBuilder.WriteImage(temp.Path, "xgd3.iso", partition, 0x02080000L, prefix);
        await using (var image = await DiscImages.OpenAsync(xgd3))
        {
            Assert.Equal("XGD3 Redump", image.Identity.Layout);
            Assert.True(image.Identity.VideoPartition);
            Assert.Contains("Video", image.Identity.Partitions);
            Assert.Equal("png-bytes", await ReadAllAsync(Find(image.Root, "ICON.PNG")));
        }

        var xgd1 = XdvdfsBuilder.WriteImage(temp.Path, "xgd1.iso", partition, 0x18300000L);
        await using (var image = await DiscImages.OpenAsync(xgd1))
        {
            Assert.Equal("XGD1", image.Identity.Layout);
            Assert.False(image.Identity.VideoPartition);
            Assert.Equal("xex", await ReadAllAsync(Find(image.Root, "default.xex")));
        }

        var matches = NameSearch.Search((await DiscImages.OpenAsync(plain)).Root, "icon");
        Assert.Contains(matches, node => node.Name == "ICON.PNG");
    }

    [Fact]
    public async Task Folder_dump_is_read_only_and_rejects_garbage()
    {
        using var temp = new TempDir();
        var dump = Path.Combine(temp.Path, "PS3_GAME");
        Directory.CreateDirectory(Path.Combine(dump, "USRDIR"));
        await File.WriteAllTextAsync(Path.Combine(dump, "USRDIR", "NOTE.TXT"), "folder");
        await using var image = await DiscImages.OpenAsync(dump);
        Assert.Equal("folder", image.Identity.FormatId);
        Assert.Equal("PlayStation 3", image.Identity.Platform);
        Assert.Equal("folder", await ReadAllAsync(Find(image.Root, "NOTE.TXT")));

        var garbage = Path.Combine(temp.Path, "nope.bin");
        await File.WriteAllBytesAsync(garbage, new byte[128]);
        var error = await Assert.ThrowsAsync<DiscFormatException>(() => DiscImages.OpenAsync(garbage));
        Assert.Equal(DiscFormatException.Unrecognized, error.Message);
    }

    [Fact]
    public void Unsupported_message_is_built_from_the_readers()
    {
        var message = DiscImages.UnsupportedFileMessage;
        Assert.Equal(DiscFormatException.Unrecognized, message);
        Assert.Contains("ISO 9660/Joliet (including PS3)", message);
        Assert.Contains("Xbox and Xbox 360 XDVDFS", message);
        Assert.Contains("folder dumps", message);
        Assert.DoesNotContain("NSP", message, StringComparison.Ordinal);
        Assert.DoesNotContain("XCI", message, StringComparison.Ordinal);
        var iso = message.IndexOf("ISO 9660/Joliet (including PS3)", StringComparison.Ordinal);
        var xbox = message.IndexOf("Xbox and Xbox 360 XDVDFS", StringComparison.Ordinal);
        var folder = message.IndexOf("folder dumps", StringComparison.Ordinal);
        Assert.True(iso < xbox && xbox < folder);

        foreach (var format in DiscImages.RegisteredFormats)
        {
            Assert.False(string.IsNullOrWhiteSpace(format.SupportPhrase));
            Assert.Contains(format.SupportPhrase, message, StringComparison.Ordinal);
        }

        var withSwitch = DiscImages.DescribeUnsupportedFile(
        [
            new Iso9660Format(),
            new FolderFormat(),
            new PhraseReader("switch", "Nintendo Switch NSP and XCI")
        ]);
        Assert.Contains("ISO 9660/Joliet (including PS3)", withSwitch, StringComparison.Ordinal);
        Assert.Contains("folder dumps", withSwitch, StringComparison.Ordinal);
        Assert.EndsWith("or Nintendo Switch NSP and XCI.", withSwitch, StringComparison.Ordinal);
        Assert.DoesNotContain("Xbox", withSwitch, StringComparison.Ordinal);
    }

    [Fact]
    public void File_summary_adds_protected_entries_extract_will_skip()
    {
        Assert.Equal(TextFormat.Count(22), TextFormat.FileSummary(22, 0));
        Assert.Equal(
            TextFormat.Count(22) + " files, plus " + TextFormat.Count(150) + " protected system entries skipped on extract",
            TextFormat.FileSummary(22, 150));
    }

    [Fact]
    public async Task Switch_containers_are_named_and_refused()
    {
        using var temp = new TempDir();
        var nsp = Path.Combine(temp.Path, "game.nsp");
        await File.WriteAllBytesAsync(nsp, "PFS0"u8.ToArray().Concat(new byte[32]).ToArray());
        var nspError = await Assert.ThrowsAsync<DiscFormatException>(() => DiscImages.OpenAsync(nsp));
        Assert.Equal(DiscFormatException.SwitchUnsupported, nspError.Message);

        var renamed = Path.Combine(temp.Path, "archive.bin");
        await File.WriteAllBytesAsync(renamed, "PFS0"u8.ToArray().Concat(new byte[16]).ToArray());
        var renamedError = await Assert.ThrowsAsync<DiscFormatException>(() => DiscImages.OpenAsync(renamed));
        Assert.Equal(DiscFormatException.SwitchUnsupported, renamedError.Message);

        var xci = new byte[0x120];
        "HEAD"u8.CopyTo(xci.AsSpan(0x100));
        var xciPath = Path.Combine(temp.Path, "card.bin");
        await File.WriteAllBytesAsync(xciPath, xci);
        var xciError = await Assert.ThrowsAsync<DiscFormatException>(() => DiscImages.OpenAsync(xciPath));
        Assert.Equal(DiscFormatException.SwitchUnsupported, xciError.Message);

        var extensionOnly = Path.Combine(temp.Path, "short.xci");
        await File.WriteAllBytesAsync(extensionOnly, new byte[16]);
        var extensionError = await Assert.ThrowsAsync<DiscFormatException>(() => DiscImages.OpenAsync(extensionOnly));
        Assert.Equal(DiscFormatException.SwitchUnsupported, extensionError.Message);

        var isoPath = Path.Combine(temp.Path, "actually-an-iso.nsp");
        var builder = new CDBuilder { UseJoliet = true, VolumeIdentifier = "NORTHWIND" };
        builder.AddFile("readme.txt", Encoding.ASCII.GetBytes("hello"));
        builder.Build(isoPath);
        await using var image = await DiscImages.OpenAsync(isoPath);
        Assert.Equal("iso9660", image.Identity.FormatId);
        Assert.Equal(0L, image.Identity.ProtectedCount);
    }

    [Fact]
    public async Task Xdvdfs_protected_system_count_matches_extract()
    {
        var files = new (string Path, byte[] Data)[]
        {
            ("default.xex", Encoding.ASCII.GetBytes("xex")),
            ("media/ICON.PNG", Encoding.ASCII.GetBytes("png")),
            ("EBOOT.BIN", Encoding.ASCII.GetBytes("boot")),
            ("$SystemUpdate/a.bin", Encoding.ASCII.GetBytes("a")),
            ("$SystemUpdate/nested/b.bin", Encoding.ASCII.GetBytes("bb")),
            ("$SystemUpdate/nested/c.bin", Encoding.ASCII.GetBytes("ccc"))
        };
        var partition = XdvdfsBuilder.BuildPartition(files);
        using var temp = new TempDir();
        var imagePath = XdvdfsBuilder.WriteImage(temp.Path, "xgd.iso", partition, 0);

        await using (var image = await DiscImages.OpenAsync(imagePath))
        {
            Assert.Equal(3L, image.Identity.FileCount);
            Assert.Equal(4L, image.Identity.ProtectedCount);
            Assert.DoesNotContain(Names(image.Root), name => name.Contains("$SystemUpdate", StringComparison.OrdinalIgnoreCase));
            Assert.Contains("/EBOOT.BIN", Names(image.Root));

            var dest = Path.Combine(temp.Path, "out");
            var extracted = await new Extractor().ExtractAsync(
                [image.Root],
                new DirectoryInfo(dest),
                new ExtractOptions(),
                null,
                CancellationToken.None);
            Assert.Equal(2, extracted.Extracted);
            Assert.Equal(0, extracted.Skipped);
            Assert.Equal((int)image.Identity.ProtectedCount, extracted.ProtectedSkipped);
            Assert.True(File.Exists(Path.Combine(dest, "default.xex")));
            Assert.True(File.Exists(Path.Combine(dest, "media", "ICON.PNG")));
            Assert.False(File.Exists(Path.Combine(dest, "EBOOT.BIN")));
            Assert.False(File.Exists(Path.Combine(dest, "$SystemUpdate", "a.bin")));
            Assert.False(File.Exists(Path.Combine(dest, "$SystemUpdate", "nested", "c.bin")));

            var forced = Path.Combine(temp.Path, "forced");
            var included = await new Extractor().ExtractAsync(
                [image.Root],
                new DirectoryInfo(forced),
                new ExtractOptions { IncludeProtected = true },
                null,
                CancellationToken.None);
            Assert.Equal(0, included.ProtectedSkipped);
            Assert.Equal(6, included.Extracted);
            Assert.Equal("boot", await File.ReadAllTextAsync(Path.Combine(forced, "EBOOT.BIN")));
            Assert.Equal("ccc", await File.ReadAllTextAsync(Path.Combine(forced, "$SystemUpdate", "nested", "c.bin")));
        }

        await using (var shown = await DiscImages.OpenAsync(imagePath, new OpenOptions { ShowSystemUpdate = true }))
        {
            Assert.Equal(6L, shown.Identity.FileCount);
            Assert.Equal(4L, shown.Identity.ProtectedCount);
            Assert.Contains(Names(shown.Root), name => name.Contains("$SystemUpdate", StringComparison.OrdinalIgnoreCase));
            var dest = Path.Combine(temp.Path, "shown");
            var extracted = await new Extractor().ExtractAsync(
                [shown.Root],
                new DirectoryInfo(dest),
                new ExtractOptions(),
                null,
                CancellationToken.None);
            Assert.Equal(4, extracted.ProtectedSkipped);
            Assert.Equal(2, extracted.Extracted);
        }
    }

    [Fact]
    public async Task Pattern_extract_keeps_only_matches()
    {
        var partition = XdvdfsBuilder.BuildPartition(
        [
            ("a.txt", Encoding.ASCII.GetBytes("a")),
            ("b.png", Encoding.ASCII.GetBytes("b"))
        ]);
        using var temp = new TempDir();
        var imagePath = XdvdfsBuilder.WriteImage(temp.Path, "files.iso", partition, 0);
        await using var image = await DiscImages.OpenAsync(imagePath);
        var dest = Path.Combine(temp.Path, "png");
        await new Extractor().ExtractAsync(
            [image.Root],
            new DirectoryInfo(dest),
            new ExtractOptions { Pattern = "*.png" },
            null,
            CancellationToken.None);
        Assert.True(File.Exists(Path.Combine(dest, "b.png")));
        Assert.False(File.Exists(Path.Combine(dest, "a.txt")));
    }

    private static List<string> Names(IDiscNode node)
    {
        var names = new List<string>();
        void Walk(IDiscNode current)
        {
            foreach (var child in current.Children)
            {
                names.Add(child.FullPath);
                if (child.IsDirectory)
                {
                    Walk(child);
                }
            }
        }

        Walk(node);
        return names;
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

    private static async Task<string> ReadAllAsync(IDiscNode node)
    {
        using var reader = new StreamReader(node.OpenRead(), Encoding.ASCII);
        return await reader.ReadToEndAsync();
    }

    private sealed class PhraseReader(string id, string phrase) : IImageFormat
    {
        public string Id => id;
        public string DisplayName => phrase;
        public string SupportPhrase => phrase;
        public bool CanOpen(Stream probe, ImageHints hints) => false;
        public Task<IDiscImage> OpenAsync(Stream source, OpenOptions options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "discfolio-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
