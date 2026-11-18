using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DiscUtils.Iso9660;
using EliteLevel.Disc.Cli;
using EliteLevel.Disc.Core.Drives;
using EliteLevel.Disc.Core.Formats.Xdvdfs;
using EliteLevel.Disc.Core.Titles;

namespace EliteLevel.Disc.Core.Tests;

public class DriveTests
{
    [Fact]
    public async Task Drive_open_matches_the_image_and_uses_a_content_cache_key()
    {
        using var temp = new TempDir();
        var iso = await WriteIso(temp.Path, "NORTHWIND", "PS3_GAME/ICON0.PNG", "icon-bytes");
        var bytes = await File.ReadAllBytesAsync(iso);
        var source = new MemoryDriveSource();
        source.Add(Drive("D:"), new MemoryBlockDevice(bytes) { Id = "D:" });
        using (OpticalDrives.Override(source))
        {
            await using var fromFile = await DiscImages.OpenAsync(iso);
            await using var fromDrive = await DiscImages.OpenAsync("D:");
            Assert.True(fromDrive.Identity.FromDrive);
            Assert.Equal(fromFile.Identity.FormatId, fromDrive.Identity.FormatId);
            Assert.Equal("PlayStation 3", fromDrive.Identity.Platform);
            Assert.Equal("NORTHWIND", fromDrive.Identity.VolumeLabel);
            Assert.Contains("encrypted", fromDrive.Identity.Notice, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(64, fromDrive.Identity.CacheKey?.Length);
            var icon = fromDrive.Root.Children.Single(node => node.Name == "PS3_GAME").Children.Single();
            using var stream = icon.OpenRead();
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            Assert.Equal("icon-bytes"u8.ToArray(), memory.ToArray());

            var other = DriveCacheKey.Create("OTHER", bytes.LongLength, bytes.AsSpan(0, Math.Min(bytes.Length, 64)));
            Assert.NotEqual(fromDrive.Identity.CacheKey, other);
            Assert.NotEqual(
                DriveCacheKey.Create("NORTHWIND", bytes.LongLength, "a"u8),
                DriveCacheKey.Create("NORTHWIND", bytes.LongLength + 1, "a"u8));
            Assert.NotEqual(
                DriveCacheKey.Create("NORTHWIND", 10, "a"u8),
                DriveCacheKey.Create("NORTHWIND", 10, "b"u8));

            var cache = new TitleArtCache(Path.Combine(temp.Path, "cache"));
            var stored = cache.StoreKey(fromDrive.Identity.CacheKey!, new TitleMetadata
            {
                Title = "Northwind",
                Icon = SyntheticTitles.Png(1, 2, 3)
            });
            Assert.Equal("Northwind", cache.TryGetKey(fromDrive.Identity.CacheKey!)?.Title);
            Assert.Null(cache.TryGet("D:"));
            cache.RemoveKey(fromDrive.Identity.CacheKey!);
            Assert.Null(cache.TryGetKey(fromDrive.Identity.CacheKey!));
            Assert.NotNull(stored.IconPath);
            Assert.False(File.Exists(stored.IconPath));
        }
    }

    [Fact]
    public async Task Sector_stream_reads_match_the_backing_bytes()
    {
        var data = Enumerable.Range(0, 5000).Select(value => (byte)(value * 3)).ToArray();
        using var device = new MemoryBlockDevice(data);
        await using var stream = new SectorStream(device, leaveOpen: true);
        var copy = new byte[data.Length];
        var offset = 0;
        while (offset < copy.Length)
        {
            var read = stream.Read(copy, offset, Math.Min(100, copy.Length - offset));
            Assert.True(read > 0);
            offset += read;
        }

        Assert.Equal(data, copy);
        Assert.Throws<NotSupportedException>(() => stream.Write([1], 0, 1));
    }

    [Fact]
    public async Task Rip_retries_then_reports_a_bad_sector_without_changing_the_source()
    {
        using var temp = new TempDir();
        var iso = await WriteIso(temp.Path, "RIPME", "readme.txt", "hello");
        var bytes = await File.ReadAllBytesAsync(iso);
        var original = bytes.ToArray();
        var recovered = new MemoryBlockDevice(bytes, transientFailures: new Dictionary<long, int> { [3] = 2 });
        var output = Path.Combine(temp.Path, "recovered.iso");
        var recoveredResult = await IsoRipper.RipAsync(recovered, output, new RipOptions { Attempts = 5 });
        Assert.Empty(recoveredResult.BadSectors);
        Assert.Equal(original, await File.ReadAllBytesAsync(output));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(original)).ToLowerInvariant(), recoveredResult.Sha256);
        Assert.Contains(recoveredResult.Sha256, await File.ReadAllTextAsync(recoveredResult.Sha256Path));
        Assert.Contains(recoveredResult.Sha1, await File.ReadAllTextAsync(recoveredResult.Sha1Path));
        Assert.Contains(recoveredResult.Md5, await File.ReadAllTextAsync(recoveredResult.Md5Path));
        Assert.Equal(original, bytes);

        var broken = new MemoryBlockDevice(bytes, badSectors: [3]);
        var brokenOut = Path.Combine(temp.Path, "broken.iso");
        var brokenResult = await IsoRipper.RipAsync(broken, brokenOut, new RipOptions { Attempts = 3 });
        Assert.Equal(new long[] { 3 }, brokenResult.BadSectors.ToArray());
        var ripped = await File.ReadAllBytesAsync(brokenOut);
        var start = 3 * OpticalDrives.SectorSize;
        var end = Math.Min(bytes.Length, start + OpticalDrives.SectorSize);
        Assert.All(ripped.Skip(start).Take(end - start), value => Assert.Equal(0, value));
        Assert.Equal(bytes.AsSpan(0, start).ToArray(), ripped.AsSpan(0, start).ToArray());
        if (end < bytes.Length)
        {
            Assert.Equal(bytes.AsSpan(end).ToArray(), ripped.AsSpan(end).ToArray());
        }

        Assert.Equal(original, bytes);
        Assert.Contains("3", await File.ReadAllTextAsync(brokenResult.BadSectorPath!));

        var cancelOut = Path.Combine(temp.Path, "partial.iso");
        using var cancel = new CancellationTokenSource();
        IProgress<RipProgress> progress = new SyncProgress(update =>
        {
            if (update.CompletedSectors > 1)
            {
                cancel.Cancel();
            }
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            IsoRipper.RipAsync(new MemoryBlockDevice(bytes), cancelOut, new RipOptions { Attempts = 1 }, progress, cancel.Token));
        Assert.False(File.Exists(cancelOut));
        await Assert.ThrowsAsync<IOException>(() => IsoRipper.RipAsync(new MemoryBlockDevice(bytes), "D:", new RipOptions()));
    }

    [Fact]
    public async Task Cli_lists_reads_and_rips_a_fake_drive()
    {
        using var temp = new TempDir();
        var iso = await WriteIso(temp.Path, "NORTHWIND", "readme.txt", "hello");
        var bytes = await File.ReadAllBytesAsync(iso);
        var snapshot = bytes.ToArray();
        var source = new MemoryDriveSource();
        source.Add(Drive("D:"), new MemoryBlockDevice(bytes, badSectors: [4]) { Id = "D:" });
        using (OpticalDrives.Override(source))
        {
            var drives = await Run("drives", "--json");
            Assert.Equal(0, drives.Code);
            using (var doc = JsonDocument.Parse(drives.Stdout))
            {
                var row = doc.RootElement.GetProperty("drives")[0];
                Assert.Equal("D:", row.GetProperty("id").GetString());
                Assert.Equal("NORTHWIND", row.GetProperty("volumeLabel").GetString());
                Assert.Equal("PlayStation 3", row.GetProperty("platform").GetString());
            }

            var info = await Run("info", "D:", "--json");
            Assert.Equal(0, info.Code);
            using (var doc = JsonDocument.Parse(info.Stdout))
            {
                Assert.True(doc.RootElement.GetProperty("fromDrive").GetBoolean());
                Assert.Contains("encrypted", doc.RootElement.GetProperty("notice").GetString());
            }

            var list = await Run("ls", @"\\.\D:", "--long");
            Assert.Equal(0, list.Code);
            Assert.Contains("readme.txt", list.Stdout);

            var dest = Path.Combine(temp.Path, "out");
            var extract = await Run("extract", "D:", "--out", dest, "--json");
            Assert.Equal(0, extract.Code);
            Assert.Equal("hello"u8.ToArray(), await File.ReadAllBytesAsync(Path.Combine(dest, "readme.txt")));

            var ripped = Path.Combine(temp.Path, "copy.iso");
            var rip = await Run("rip", "D:", "--out", ripped, "--retries", "2", "--json");
            Assert.Equal(0, rip.Code);
            using (var doc = JsonDocument.Parse(rip.Stdout))
            {
                Assert.Equal(4, doc.RootElement.GetProperty("badSectors")[0].GetInt64());
                Assert.False(string.IsNullOrEmpty(doc.RootElement.GetProperty("sha256").GetString()));
                Assert.False(string.IsNullOrEmpty(doc.RootElement.GetProperty("md5").GetString()));
            }

            Assert.True(File.Exists(ripped + ".sha256"));
            Assert.Equal(snapshot, bytes);
        }
    }

    [Fact]
    public async Task Access_denied_and_audio_discs_are_explained()
    {
        using var temp = new TempDir();
        var source = new MemoryDriveSource();
        source.Add(Drive("D:", accessDenied: true), new MemoryBlockDevice(new byte[4096]));
        source.Add(
            new OpticalDrive("/dev/sr0", "/dev/sr0", null, 4096, true, false),
            new MemoryBlockDevice(new byte[64 * 2048]) { Id = "/dev/sr0" },
            DriveMedium.Audio);
        using (OpticalDrives.Override(source))
        {
            var denied = await Run("info", "D:");
            Assert.Equal(1, denied.Code);
            Assert.Contains("administrator", denied.Stderr, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("never writes", denied.Stderr, StringComparison.OrdinalIgnoreCase);

            var audio = await Run("info", "/dev/sr0", "--json");
            Assert.Equal(2, audio.Code);
            using var doc = JsonDocument.Parse(audio.Stdout);
            Assert.Contains("Audio", doc.RootElement.GetProperty("error").GetProperty("message").GetString());
            Assert.Contains("not supported", doc.RootElement.GetProperty("error").GetProperty("message").GetString());
        }
    }

    [Fact]
    public async Task Hidden_xbox_partition_is_explained_and_the_video_side_stays_readable()
    {
        using var temp = new TempDir();
        var iso = await WriteIso(temp.Path, "VIDEO", "readme.txt", "visible", includePs3: false);
        var bytes = await File.ReadAllBytesAsync(iso);
        var xgd3 = XdvdfsFormat.Layouts.Single(layout => layout.Layout == "XGD3").PartitionBase;
        var length = xgd3 + XdvdfsFormat.DescriptorOffset + OpticalDrives.SectorSize;
        var source = new MemoryDriveSource();
        source.Add(Drive("E:"), new MemoryBlockDevice(bytes, length, unreadableFrom: xgd3) { Id = "E:" });
        using (OpticalDrives.Override(source))
        {
            await using var image = await DiscImages.OpenAsync("E:");
            Assert.Equal("iso9660", image.Identity.FormatId);
            Assert.Contains("flashed firmware", image.Identity.Notice);
            var file = image.Root.Children.Single(node => node.Name == "readme.txt");
            using var stream = file.OpenRead();
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            Assert.Equal("visible"u8.ToArray(), memory.ToArray());
        }
    }

    [Fact]
    public async Task System_cnf_selects_the_same_platform_for_a_file_and_a_drive()
    {
        using var temp = new TempDir();
        var ps2 = await WriteIso(temp.Path, "SLUS_200", "SYSTEM.CNF", "BOOT2 = cdrom0:\\SLUS_200.01;1\nVER = 1.00\n");
        await using (var image = await DiscImages.OpenAsync(ps2))
        {
            Assert.Equal("PlayStation 2", image.Identity.Platform);
        }

        var ps1Path = Path.Combine(temp.Path, "ps1.iso");
        var builder = new CDBuilder { UseJoliet = true, VolumeIdentifier = "SLUS_001" };
        builder.AddFile("SYSTEM.CNF", Encoding.ASCII.GetBytes("BOOT = cdrom:\\SLUS_001.23;1\nTCB = 4\n"));
        builder.Build(ps1Path);
        var bytes = await File.ReadAllBytesAsync(ps1Path);
        var source = new MemoryDriveSource();
        source.Add(Drive("F:"), new MemoryBlockDevice(bytes) { Id = "F:" });
        using (OpticalDrives.Override(source))
        {
            await using var image = await DiscImages.OpenAsync("F:");
            Assert.Equal("PlayStation 1", image.Identity.Platform);
            Assert.Null(image.Identity.Notice);
        }
    }

    [Fact]
    public async Task Disc_key_files_load_without_echoing_the_key()
    {
        using var temp = new TempDir();
        var hex = Path.Combine(temp.Path, "key.txt");
        File.WriteAllText(hex, "00112233445566778899aabbccddeeff");
        Assert.True(DiscKeyFile.TryLoad(hex, out var fromHex, out var hexError));
        Assert.Null(hexError);
        Assert.Equal("00112233445566778899aabbccddeeff", Convert.ToHexString(fromHex!.Bytes).ToLowerInvariant());
        Assert.DoesNotContain("0011", fromHex.ToString(), StringComparison.OrdinalIgnoreCase);

        var raw = Path.Combine(temp.Path, "key.bin");
        File.WriteAllBytes(raw, fromHex.Bytes);
        Assert.True(DiscKeyFile.TryLoad(raw, out var fromRaw, out _));
        Assert.Equal(fromHex.Bytes, fromRaw!.Bytes);

        var ird = Path.Combine(temp.Path, "game.ird");
        File.WriteAllBytes(ird, SampleIrd(fromHex.Bytes));
        Assert.True(DiscKeyFile.TryLoad(ird, out var fromIrd, out var irdError));
        Assert.Null(irdError);
        Assert.Equal(fromHex.Bytes, fromIrd!.Bytes);

        var gzip = Path.Combine(temp.Path, "game.ird.gz");
        using (var output = File.Create(gzip))
        using (var zip = new GZipStream(output, CompressionLevel.Fastest))
        {
            var payload = SampleIrd(fromHex.Bytes);
            zip.Write(payload, 0, payload.Length);
        }

        Assert.True(DiscKeyFile.TryLoad(gzip, out var fromGzip, out _));
        Assert.Equal(fromHex.Bytes, fromGzip!.Bytes);

        var junk = Path.Combine(temp.Path, "notes.txt");
        File.WriteAllText(junk, "not a key");
        Assert.False(DiscKeyFile.TryLoad(junk, out _, out var error));
        Assert.Contains(".ird", error);
        Assert.DoesNotContain("0011", error, StringComparison.OrdinalIgnoreCase);

        var iso = await WriteIso(temp.Path, "KEYDISC", "readme.txt", "hello", includePs3: false);
        var info = await Run("info", iso, "--disc-key", hex);
        Assert.Equal(0, info.Code);
        Assert.Contains("Disc key", info.Stdout);
        Assert.Contains("loaded", info.Stdout);
        Assert.DoesNotContain("00112233", info.Stdout, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("00112233", info.Stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Real_drive_catalog_does_not_throw()
    {
        var drives = new PlatformOpticalDriveSource().List();
        Assert.NotNull(drives);
        var listed = await Run("drives", "--json");
        Assert.Equal(0, listed.Code);
        using var doc = JsonDocument.Parse(listed.Stdout);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.GetProperty("drives").ValueKind);
    }

    [Fact]
    public async Task Missing_drive_is_a_user_error()
    {
        using (OpticalDrives.Override(new MemoryDriveSource()))
        {
            var missing = await Run("info", "Z:");
            Assert.Equal(1, missing.Code);
            Assert.Contains("No optical drive", missing.Stderr);
        }
    }

    private static OpticalDrive Drive(string id, bool accessDenied = false) =>
        new(id, OperatingSystem.IsWindows() ? @"\\.\" + id : id, null, 0, true, accessDenied);

    private static async Task<string> WriteIso(string directory, string label, string name, string contents, bool includePs3 = true)
    {
        var path = Path.Combine(directory, label + ".iso");
        var builder = new CDBuilder { UseJoliet = true, VolumeIdentifier = label };
        builder.AddFile(name.Replace('/', Path.DirectorySeparatorChar), Encoding.ASCII.GetBytes(contents));
        if (includePs3
            && !name.Contains("PS3_GAME", StringComparison.OrdinalIgnoreCase)
            && !name.Contains("SYSTEM.CNF", StringComparison.OrdinalIgnoreCase))
        {
            builder.AddFile("PS3_GAME/PARAM.SFO", Encoding.ASCII.GetBytes("sfo"));
        }

        builder.Build(path);
        await Task.CompletedTask;
        return path;
    }

    private static byte[] SampleIrd(byte[] data1)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(Encoding.ASCII.GetBytes("3IRD"));
        writer.Write((byte)9);
        writer.Write(Encoding.ASCII.GetBytes("BLUS00001"));
        writer.Write((byte)9);
        writer.Write(Encoding.ASCII.GetBytes("Northwind"));
        writer.Write(Encoding.ASCII.GetBytes("4.41"));
        writer.Write(Encoding.ASCII.GetBytes("01.00"));
        writer.Write(Encoding.ASCII.GetBytes("01.00"));
        writer.Write(0);
        writer.Write(0);
        writer.Write((byte)0);
        writer.Write(0);
        writer.Write(0);
        writer.Write(new byte[115]);
        writer.Write(data1);
        writer.Write(new byte[16]);
        writer.Write(0);
        writer.Write(0);
        writer.Flush();
        return stream.ToArray();
    }

    private static async Task<(int Code, string Stdout, string Stderr)> Run(params string[] args)
    {
        await using var stdout = new StringWriter();
        await using var stderr = new StringWriter();
        var code = await CliHost.RunAsync(args, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    private sealed class SyncProgress(Action<RipProgress> action) : IProgress<RipProgress>
    {
        public void Report(RipProgress value) => action(value);
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
