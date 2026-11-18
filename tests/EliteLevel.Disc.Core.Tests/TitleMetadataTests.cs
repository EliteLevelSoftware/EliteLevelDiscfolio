using System.Text;
using System.Text.Json;
using DiscUtils.Iso9660;
using EliteLevel.Disc.Cli;
using EliteLevel.Disc.Core.Titles;

namespace EliteLevel.Disc.Core.Tests;

public class TitleMetadataTests
{
    [Fact]
    public async Task Ps3_folder_dump_reads_sfo_icon_and_banner()
    {
        using var temp = new TempDir();
        var icon = SyntheticTitles.Png(10, 20, 30);
        var banner = SyntheticTitles.Png(40, 50, 60);
        var game = Path.Combine(temp.Path, "PS3_GAME");
        Directory.CreateDirectory(game);
        await File.WriteAllBytesAsync(Path.Combine(game, "PARAM.SFO"), SyntheticTitles.Sfo("Northwind Traders", "BLUS00001", "01.02", "DG", 5));
        await File.WriteAllBytesAsync(Path.Combine(game, "ICON0.PNG"), icon);
        await File.WriteAllBytesAsync(Path.Combine(game, "PIC1.PNG"), banner);

        await using var image = await DiscImages.OpenAsync(temp.Path);
        var meta = TitleMetadataReader.Read(image);
        Assert.Equal("Northwind Traders", meta.Title);
        Assert.Equal("BLUS00001", meta.TitleId);
        Assert.Equal("01.02", meta.Version);
        Assert.Equal("DG", meta.Category);
        Assert.Equal(5, meta.ParentalLevel);
        Assert.Equal(icon, meta.Icon);
        Assert.Equal(banner, meta.Banner);
        Assert.Equal("ICON0.PNG", meta.IconName);
        Assert.Equal("PIC1.PNG", meta.BannerName);
        Assert.Equal(new[] { "ICON0.PNG", "PARAM.SFO", "PIC1.PNG" }, Directory.GetFiles(game).Select(Path.GetFileName).OrderBy(name => name).ToArray());
    }

    [Fact]
    public async Task Ps3_iso_uses_pic0_when_pic1_is_missing()
    {
        using var temp = new TempDir();
        var icon = SyntheticTitles.Png(1, 2, 3);
        var pic0 = SyntheticTitles.Png(4, 5, 6);
        var iso = Path.Combine(temp.Path, "game.iso");
        var builder = new CDBuilder { UseJoliet = true, VolumeIdentifier = "POTCAWE" };
        builder.AddFile(@"PS3_GAME\PARAM.SFO", SyntheticTitles.Sfo("Northwind", "BLES00002", "01.00", "DG", 3));
        builder.AddFile(@"PS3_GAME\ICON0.PNG", icon);
        builder.AddFile(@"PS3_GAME\PIC0.PNG", pic0);
        builder.Build(iso);
        var before = Directory.GetFiles(temp.Path).OrderBy(path => path).ToArray();

        await using var image = await DiscImages.OpenAsync(iso);
        var meta = TitleMetadataReader.Read(image);
        Assert.Equal("Northwind", TitleMetadataReader.ReadTitle(meta));
        Assert.Equal("BLES00002", meta.TitleId);
        Assert.Equal("PIC0.PNG", meta.BannerName);
        Assert.Equal(pic0, meta.Banner);
        Assert.Equal(before, Directory.GetFiles(temp.Path).OrderBy(path => path).ToArray());

        var info = await Run("info", iso, "--json");
        Assert.Equal(0, info.Code);
        using (var doc = JsonDocument.Parse(info.Stdout))
        {
            Assert.Equal("POTCAWE", doc.RootElement.GetProperty("volumeLabel").GetString());
            Assert.Equal("Northwind", doc.RootElement.GetProperty("title").GetString());
            Assert.Equal("BLES00002", doc.RootElement.GetProperty("titleId").GetString());
            Assert.Equal("01.00", doc.RootElement.GetProperty("version").GetString());
            Assert.Equal("ICON0.PNG", doc.RootElement.GetProperty("icon").GetString());
            Assert.Equal("PIC0.PNG", doc.RootElement.GetProperty("banner").GetString());
        }

        var text = await Run("info", iso);
        Assert.Contains("Northwind", text.Stdout);
        Assert.Contains("Title ID     BLES00002", text.Stdout);
        Assert.Contains("Version      01.00", text.Stdout);
        Assert.Contains("Art          ICON0.PNG, PIC0.PNG", text.Stdout);

        var exported = Path.Combine(temp.Path, "art");
        var art = await Run("art", iso, "--out", exported, "--json");
        Assert.Equal(0, art.Code);
        Assert.Equal(icon, await File.ReadAllBytesAsync(Path.Combine(exported, "icon.png")));
        Assert.Equal(pic0, await File.ReadAllBytesAsync(Path.Combine(exported, "banner.png")));
        Assert.False(File.Exists(iso + ".png"));
    }

    [Fact]
    public async Task Xbox_360_reads_xex_and_nxeart_from_a_disc()
    {
        using var temp = new TempDir();
        var icon = SyntheticTitles.Png(7, 8, 9);
        var nxe = SyntheticTitles.Png(11, 12, 13);
        var version = 1u | (2u << 4) | (3u << 8);
        var xex = SyntheticTitles.Xex(0x4D5307E6, version, "Contoso Rally", icon, resourceInFile: true);
        var partition = XdvdfsBuilder.BuildPartition(
        [
            ("default.xex", xex),
            ("nxeart", nxe)
        ]);
        var imagePath = XdvdfsBuilder.WriteImage(temp.Path, "game.iso", partition, 0);

        await using var image = await DiscImages.OpenAsync(imagePath);
        var meta = TitleMetadataReader.Read(image);
        Assert.Equal("Contoso Rally", meta.Title);
        Assert.Equal("4D5307E6", meta.TitleId);
        Assert.Equal("1.2.3", meta.Version);
        Assert.Equal(icon, meta.Icon);
        Assert.Equal(nxe, meta.Banner);
        Assert.Equal("default.xex", meta.IconName);
        Assert.Equal("nxeart", meta.BannerName);
    }

    [Fact]
    public async Task Xbox_360_finds_xdbf_when_the_resource_address_is_not_a_file_offset()
    {
        using var temp = new TempDir();
        var root = Path.Combine(temp.Path, "dump");
        Directory.CreateDirectory(root);
        var xex = SyntheticTitles.Xex(0x1234ABCD, 1u | (0u << 4) | (9u << 8), "Offset Rally", SyntheticTitles.Png(1, 1, 1), resourceInFile: false);
        await File.WriteAllBytesAsync(Path.Combine(root, "default.xex"), xex);

        await using var image = await DiscImages.OpenAsync(root);
        var meta = TitleMetadataReader.Read(image);
        Assert.Equal("Offset Rally", meta.Title);
        Assert.Equal("1234ABCD", meta.TitleId);
        Assert.Equal("1.0.9", meta.Version);
        Assert.NotNull(meta.Icon);
    }

    [Fact]
    public async Task Broken_xex_falls_back_without_throwing()
    {
        using var temp = new TempDir();
        var root = Path.Combine(temp.Path, "POTCAWE");
        Directory.CreateDirectory(root);
        await File.WriteAllBytesAsync(Path.Combine(root, "default.xex"), Encoding.ASCII.GetBytes("xex"));

        await using var image = await DiscImages.OpenAsync(root);
        var meta = TitleMetadataReader.Read(image);
        Assert.Null(meta.Title);
        Assert.Null(meta.TitleId);
        Assert.Null(meta.Icon);

        var info = await Run("info", root);
        Assert.Equal(0, info.Code);
        Assert.Contains("POTCAWE", info.Stdout);
        Assert.Contains("Art          none", info.Stdout);

        var art = await Run("art", root, "--out", Path.Combine(temp.Path, "out"));
        Assert.Equal(1, art.Code);
        Assert.Contains("no title art", art.Stderr);
    }

    [Fact]
    public async Task Original_xbox_reads_the_certificate_and_title_image()
    {
        using var temp = new TempDir();
        var root = Path.Combine(temp.Path, "dump");
        Directory.CreateDirectory(root);
        var icon = SyntheticTitles.Png(21, 22, 23);
        await File.WriteAllBytesAsync(Path.Combine(root, "default.xbe"), SyntheticTitles.Xbe(0x4D53000A, "Fabrikam", icon, 7));

        await using var image = await DiscImages.OpenAsync(root);
        var meta = TitleMetadataReader.Read(image);
        Assert.Equal("Fabrikam", meta.Title);
        Assert.Equal("4D53000A", meta.TitleId);
        Assert.Equal("7", meta.Version);
        Assert.Equal(icon, meta.Icon);
        Assert.Equal("default.xbe", meta.IconName);
    }

    [Fact]
    public async Task Autorun_icon_is_used_only_when_it_points_at_a_file()
    {
        using var temp = new TempDir();
        var cover = SyntheticTitles.Png(3, 3, 3);
        var root = Path.Combine(temp.Path, "pc");
        Directory.CreateDirectory(root);
        await File.WriteAllBytesAsync(Path.Combine(root, "cover.png"), cover);
        await File.WriteAllTextAsync(Path.Combine(root, "autorun.inf"), "[autorun]\r\nicon=setup.exe,0\r\nicon=cover.png\r\n");

        await using var image = await DiscImages.OpenAsync(root);
        var meta = TitleMetadataReader.Read(image);
        Assert.Null(meta.Title);
        Assert.Equal(cover, meta.Icon);
        Assert.Equal("cover.png", meta.IconName);

        await File.WriteAllTextAsync(Path.Combine(root, "autorun.inf"), "[autorun]\r\nicon=setup.exe,0\r\n");
        await using var indexed = await DiscImages.OpenAsync(root);
        Assert.Null(TitleMetadataReader.Read(indexed).Icon);
    }

    [Fact]
    public async Task Cache_is_keyed_in_the_data_folder_and_not_beside_the_image()
    {
        using var temp = new TempDir();
        var cache = Path.Combine(temp.Path, "cache");
        var root = Path.Combine(temp.Path, "disc");
        Directory.CreateDirectory(root);
        var icon = SyntheticTitles.Png(9, 9, 9);
        await File.WriteAllBytesAsync(Path.Combine(root, "PARAM.SFO"), SyntheticTitles.Sfo("Cached", "BLUS00009", "01.00", "DG", 0));
        await File.WriteAllBytesAsync(Path.Combine(root, "ICON0.PNG"), icon);
        var imageFiles = Directory.GetFiles(root).Select(Path.GetFileName).OrderBy(name => name, StringComparer.Ordinal).ToArray();

        TitleMetadata meta;
        await using (var image = await DiscImages.OpenAsync(root))
        {
            meta = TitleMetadataReader.Read(image);
        }

        var store = new TitleArtCache(cache);
        var cached = store.Store(root, meta);
        Assert.Equal("Cached", cached.Title);
        Assert.Equal("BLUS00009", cached.TitleId);
        Assert.NotNull(cached.IconPath);
        var cacheRoot = Path.GetFullPath(cache).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var iconFull = Path.GetFullPath(cached.IconPath!);
        Assert.StartsWith(cacheRoot, iconFull, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        Assert.Equal(icon, await File.ReadAllBytesAsync(iconFull));
        Assert.Equal(imageFiles, Directory.GetFiles(root).Select(Path.GetFileName).OrderBy(name => name, StringComparer.Ordinal).ToArray());

        var again = store.TryGet(root);
        Assert.Equal("Cached", again?.Title);
        Assert.Equal(cached.IconPath, again?.IconPath);

        Directory.SetLastWriteTimeUtc(root, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.Null(store.TryGet(root));
    }

    [Fact]
    public async Task Art_without_a_destination_is_a_user_error()
    {
        using var temp = new TempDir();
        var iso = Path.Combine(temp.Path, "game.iso");
        var builder = new CDBuilder { UseJoliet = true, VolumeIdentifier = "PLAIN" };
        builder.AddFile("readme.txt", Encoding.ASCII.GetBytes("hello"));
        builder.Build(iso);

        var missing = await Run("art", iso, "--json");
        Assert.Equal(1, missing.Code);
        using var doc = JsonDocument.Parse(missing.Stdout);
        Assert.Equal("user", doc.RootElement.GetProperty("error").GetProperty("kind").GetString());
        Assert.Equal("art needs --out.", doc.RootElement.GetProperty("error").GetProperty("message").GetString());
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
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "discfolio-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }
        public void Dispose() => Directory.Delete(Path, true);
    }
}
