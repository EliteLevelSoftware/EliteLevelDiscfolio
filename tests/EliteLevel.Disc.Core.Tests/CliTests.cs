using System.Text;
using System.Text.Json;
using DiscUtils.Iso9660;
using EliteLevel.Disc.Cli;

namespace EliteLevel.Disc.Core.Tests;

public class CliTests
{
    [Fact]
    public async Task Json_commands_match_the_fixture()
    {
        using var temp = new TempDir();
        var iso = Path.Combine(temp.Path, "game.iso");
        var builder = new CDBuilder { UseJoliet = true, VolumeIdentifier = "NORTHWIND" };
        builder.AddFile("readme.txt", Encoding.ASCII.GetBytes("hello"));
        builder.AddFile(@"PS3_GAME\ICON0.PNG", Encoding.ASCII.GetBytes("icon"));
        builder.Build(iso);

        var info = await Run("info", iso, "--json");
        Assert.Equal(0, info.Code);
        using (var doc = JsonDocument.Parse(info.Stdout))
        {
            Assert.Equal("iso9660", doc.RootElement.GetProperty("format").GetString());
            Assert.Equal("NORTHWIND", doc.RootElement.GetProperty("volumeLabel").GetString());
            Assert.Equal("PlayStation 3", doc.RootElement.GetProperty("platform").GetString());
            Assert.Equal(2, doc.RootElement.GetProperty("files").GetInt32());
            Assert.Equal(0, doc.RootElement.GetProperty("protected").GetInt32());
        }

        var plainInfo = await Run("info", iso);
        Assert.Equal(0, plainInfo.Code);
        Assert.DoesNotContain("protected system entries", plainInfo.Stdout);

        var list = await Run("list", iso, "--path", "/PS3_GAME", "--json", "--long");
        Assert.Equal(0, list.Code);
        using (var doc = JsonDocument.Parse(list.Stdout))
        {
            Assert.Equal("/PS3_GAME", doc.RootElement.GetProperty("path").GetString());
            Assert.Contains("ICON0.PNG", list.Stdout);
        }

        var search = await Run("search", iso, "ICON0", "--json");
        Assert.Equal(0, search.Code);
        Assert.Contains("/PS3_GAME/ICON0.PNG", search.Stdout);

        var dest = Path.Combine(temp.Path, "out");
        var extract = await Run("extract", iso, "--out", dest, "--pattern", "*.png", "--skip-existing", "--json");
        Assert.Equal(0, extract.Code);
        Assert.True(File.Exists(Path.Combine(dest, "PS3_GAME", "ICON0.PNG")));
        Assert.False(File.Exists(Path.Combine(dest, "readme.txt")));
        using (var doc = JsonDocument.Parse(extract.Stdout))
        {
            Assert.Equal(1, doc.RootElement.GetProperty("extracted").GetInt32());
        }

        var again = await Run("extract", iso, "--out", dest, "--pattern", "*.png", "--skip-existing", "--json");
        Assert.Equal(0, again.Code);
        using (var doc = JsonDocument.Parse(again.Stdout))
        {
            Assert.Equal(1, doc.RootElement.GetProperty("skipped").GetInt32());
            Assert.Equal(0, doc.RootElement.GetProperty("extracted").GetInt32());
        }

        var manifest = Path.Combine(temp.Path, "game.sha256");
        var hash = await Run("hash", iso, "--path", "/readme.txt", "--algo", "sha256,sha1,md5", "--manifest", manifest, "--json");
        Assert.Equal(0, hash.Code);
        using (var doc = JsonDocument.Parse(hash.Stdout))
        {
            var entry = doc.RootElement.GetProperty("entries")[0];
            Assert.Equal("2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824", entry.GetProperty("sha256").GetString());
            Assert.Equal("aaf4c61ddcc5e8a2dabede0f3b482cd9aea9434d", entry.GetProperty("sha1").GetString());
            Assert.Equal("5d41402abc4b2a76b9719d911017c592", entry.GetProperty("md5").GetString());
        }

        var manifestText = await File.ReadAllTextAsync(manifest);
        Assert.Contains("2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824  readme.txt", manifestText);

        var detect = await Run("detect", iso, "--json");
        Assert.Equal(0, detect.Code);
        Assert.Contains("iso9660", detect.Stdout);

        var missing = await Run("info", Path.Combine(temp.Path, "missing.iso"));
        Assert.Equal(1, missing.Code);

        var garbage = Path.Combine(temp.Path, "garbage.bin");
        await File.WriteAllBytesAsync(garbage, new byte[4096]);
        var bad = await Run("info", garbage);
        Assert.Equal(2, bad.Code);
        Assert.Contains("does not look like a supported disc image", bad.Stderr);
        Assert.Contains("ISO 9660/Joliet (including PS3)", bad.Stderr);
        Assert.Contains("Xbox and Xbox 360 XDVDFS", bad.Stderr);
        Assert.Contains("folder dumps", bad.Stderr);
        Assert.Contains("Nintendo Switch NSP, XCI, NSZ and XCZ", bad.Stderr);
        Assert.Equal(string.Empty, bad.Stdout);

        var usage = await Run("extract", iso);
        Assert.Equal(1, usage.Code);
        Assert.Contains("Extract needs --out.", usage.Stderr);
    }

    [Fact]
    public async Task Json_errors_keep_the_exit_code()
    {
        using var temp = new TempDir();
        var iso = Path.Combine(temp.Path, "game.iso");
        var builder = new CDBuilder { UseJoliet = true, VolumeIdentifier = "NORTHWIND" };
        builder.AddFile("readme.txt", Encoding.ASCII.GetBytes("hello"));
        builder.Build(iso);

        var garbage = Path.Combine(temp.Path, "garbage.bin");
        await File.WriteAllBytesAsync(garbage, new byte[4096]);
        var text = await Run("info", garbage);
        var unsupported = await Run("info", garbage, "--json");
        Assert.Equal(2, unsupported.Code);
        Assert.Equal(string.Empty, unsupported.Stderr);
        using (var doc = JsonDocument.Parse(unsupported.Stdout))
        {
            var error = doc.RootElement.GetProperty("error");
            Assert.Equal(2, error.GetProperty("code").GetInt32());
            Assert.Equal("unsupported", error.GetProperty("kind").GetString());
            Assert.Equal(text.Stderr.Trim(), error.GetProperty("message").GetString());
            Assert.Contains("ISO 9660/Joliet (including PS3)", error.GetProperty("message").GetString());
            Assert.Contains("Nintendo Switch NSP, XCI, NSZ and XCZ", error.GetProperty("message").GetString());
        }

        var nsp = Path.Combine(temp.Path, "game.nsp");
        await File.WriteAllBytesAsync(nsp, "PFS0"u8.ToArray().Concat(new byte[24]).ToArray());
        var switchImage = await Run("detect", nsp, "--json");
        Assert.Equal(0, switchImage.Code);
        using (var doc = JsonDocument.Parse(switchImage.Stdout))
        {
            Assert.Equal("switch", doc.RootElement.GetProperty("format").GetString());
            Assert.Equal("Nintendo Switch", doc.RootElement.GetProperty("platform").GetString());
        }

        var nsz = Path.Combine(temp.Path, "game.nsz");
        await File.WriteAllBytesAsync(nsz, [0x28, 0xB5, 0x2F, 0xFD, 0, 0, 0, 0]);
        var switchText = await Run("info", nsz);
        Assert.Equal(2, switchText.Code);
        Assert.Equal(DiscFormatException.SwitchCompressed, switchText.Stderr.Trim());
        Assert.Equal(string.Empty, switchText.Stdout);

        var missing = await Run("info", Path.Combine(temp.Path, "missing.iso"), "--json");
        Assert.Equal(1, missing.Code);
        Assert.Equal(string.Empty, missing.Stderr);
        using (var doc = JsonDocument.Parse(missing.Stdout))
        {
            var error = doc.RootElement.GetProperty("error");
            Assert.Equal(1, error.GetProperty("code").GetInt32());
            Assert.Equal("user", error.GetProperty("kind").GetString());
            Assert.False(string.IsNullOrWhiteSpace(error.GetProperty("message").GetString()));
        }

        var usage = await Run("extract", iso, "--json");
        Assert.Equal(1, usage.Code);
        using (var doc = JsonDocument.Parse(usage.Stdout))
        {
            var error = doc.RootElement.GetProperty("error");
            Assert.Equal(1, error.GetProperty("code").GetInt32());
            Assert.Equal("user", error.GetProperty("kind").GetString());
            Assert.Equal("Extract needs --out.", error.GetProperty("message").GetString());
        }

        var algo = await Run("hash", iso, "--algo", "crc32", "--json");
        Assert.Equal(1, algo.Code);
        using (var doc = JsonDocument.Parse(algo.Stdout))
        {
            Assert.Equal("user", doc.RootElement.GetProperty("error").GetProperty("kind").GetString());
            Assert.Contains("--algo", doc.RootElement.GetProperty("error").GetProperty("message").GetString());
        }

        var parsed = await Run("info", "--json");
        Assert.Equal(1, parsed.Code);
        Assert.Equal(string.Empty, parsed.Stderr);
        using (var doc = JsonDocument.Parse(parsed.Stdout))
        {
            var error = doc.RootElement.GetProperty("error");
            Assert.Equal(1, error.GetProperty("code").GetInt32());
            Assert.Equal("user", error.GetProperty("kind").GetString());
            Assert.False(string.IsNullOrWhiteSpace(error.GetProperty("message").GetString()));
        }
    }

    [Fact]
    public async Task Info_and_extract_share_the_protected_system_count()
    {
        using var temp = new TempDir();
        var partition = XdvdfsBuilder.BuildPartition(
        [
            ("default.xex", Encoding.ASCII.GetBytes("xex")),
            ("media/ICON.PNG", Encoding.ASCII.GetBytes("png")),
            ("$SystemUpdate/a.bin", Encoding.ASCII.GetBytes("a")),
            ("$SystemUpdate/nested/b.bin", Encoding.ASCII.GetBytes("b"))
        ]);
        var image = XdvdfsBuilder.WriteImage(temp.Path, "game.iso", partition, 0);

        var info = await Run("info", image);
        Assert.Equal(0, info.Code);
        Assert.Contains("2 files, plus 2 protected system entries skipped on extract", info.Stdout);

        var infoJson = await Run("info", image, "--json");
        Assert.Equal(0, infoJson.Code);
        using (var doc = JsonDocument.Parse(infoJson.Stdout))
        {
            Assert.Equal(2, doc.RootElement.GetProperty("files").GetInt32());
            Assert.Equal(2, doc.RootElement.GetProperty("protected").GetInt32());
        }

        var shown = await Run("info", image, "--show-system");
        Assert.Contains("4 files, plus 2 protected system entries skipped on extract", shown.Stdout);

        var dest = Path.Combine(temp.Path, "out");
        var extract = await Run("extract", image, "--out", dest);
        Assert.Equal(0, extract.Code);
        Assert.Contains("Extracted 2, skipped 0, protected 2", extract.Stdout);
        Assert.True(File.Exists(Path.Combine(dest, "default.xex")));
        Assert.False(File.Exists(Path.Combine(dest, "$SystemUpdate", "a.bin")));

        var extractJson = await Run("extract", image, "--out", Path.Combine(temp.Path, "out-json"), "--json");
        Assert.Equal(0, extractJson.Code);
        using (var doc = JsonDocument.Parse(extractJson.Stdout))
        {
            Assert.Equal(2, doc.RootElement.GetProperty("extracted").GetInt32());
            Assert.Equal(2, doc.RootElement.GetProperty("protected").GetInt32());
        }
    }

    [Fact]
    public async Task Repository_rejects_keys_and_committed_images()
    {
        var root = FindRepoRoot();
        var forbidden = new List<string>();
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file);
            if (relative.StartsWith(".git" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || relative.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || relative.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || relative.StartsWith("artifacts" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                continue;
            }

            var name = Path.GetFileName(file);
            if (name.Equals("prod.keys", StringComparison.OrdinalIgnoreCase)
                || name.Equals("title.keys", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".tik", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".iso", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".xiso", StringComparison.OrdinalIgnoreCase))
            {
                forbidden.Add(relative);
            }

            if (new FileInfo(file).Length > 8 * 1024 * 1024)
            {
                forbidden.Add(relative + " (larger than 8 MB)");
            }
        }

        Assert.True(forbidden.Count == 0, "Refusing secrets or disc images in the tree: " + string.Join(", ", forbidden));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "EliteLevelDiscfolio.sln")) || File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
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
