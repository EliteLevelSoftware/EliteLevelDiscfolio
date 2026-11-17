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
        }

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

        var usage = await Run("extract", iso);
        Assert.Equal(1, usage.Code);
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
