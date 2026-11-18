using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EliteLevel.Disc.Core.Titles;

/// <summary>
/// Art and metadata for one image, stored under the Discfolio data folder.
/// The key is the reader schema, image path, length and last-write time. The image is not modified.
/// </summary>
public sealed class TitleArtCache
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    /// <summary>Bumped when title discovery changes, so an older cache entry is left unused.</summary>
    internal const int SchemaVersion = 2;

    private readonly string _directory;

    public TitleArtCache(string directory)
    {
        _directory = directory;
    }

    public CachedTitle? TryGet(string imagePath)
    {
        try
        {
            var key = Fingerprint(imagePath);
            if (key is null)
            {
                return null;
            }

            var dir = Path.Combine(_directory, key);
            var metaPath = Path.Combine(dir, "meta.json");
            if (!File.Exists(metaPath))
            {
                return null;
            }

            var meta = JsonSerializer.Deserialize<CacheMeta>(File.ReadAllText(metaPath), Json);
            if (meta is null)
            {
                return null;
            }

            return ToCached(dir, meta);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    public CachedTitle Store(string imagePath, TitleMetadata metadata)
    {
        var memory = new CachedTitle
        {
            Title = metadata.Title,
            TitleId = metadata.TitleId,
            Version = metadata.Version,
            Category = metadata.Category,
            ParentalLevel = metadata.ParentalLevel
        };
        try
        {
            var key = Fingerprint(imagePath);
            if (key is null)
            {
                return memory;
            }

            var dir = Path.Combine(_directory, key);
            Directory.CreateDirectory(dir);
            string? icon = WriteArt(dir, "icon", metadata.Icon);
            string? banner = WriteArt(dir, "banner", metadata.Banner);
            var meta = new CacheMeta
            {
                Title = metadata.Title,
                TitleId = metadata.TitleId,
                Version = metadata.Version,
                Category = metadata.Category,
                ParentalLevel = metadata.ParentalLevel,
                Icon = icon,
                Banner = banner
            };
            WriteAtomic(Path.Combine(dir, "meta.json"), Encoding.UTF8.GetBytes(JsonSerializer.Serialize(meta, Json)));
            return ToCached(dir, meta);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return memory;
        }
    }

    private static CachedTitle ToCached(string dir, CacheMeta meta) => new()
    {
        Title = meta.Title,
        TitleId = meta.TitleId,
        Version = meta.Version,
        Category = meta.Category,
        ParentalLevel = meta.ParentalLevel,
        IconPath = Existing(dir, meta.Icon),
        BannerPath = Existing(dir, meta.Banner)
    };

    private static string? Existing(string dir, string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        var path = Path.Combine(dir, name);
        return File.Exists(path) ? path : null;
    }

    private static string? WriteArt(string dir, string stem, byte[]? bytes)
    {
        foreach (var existing in Directory.EnumerateFiles(dir, stem + ".*").ToArray())
        {
            File.Delete(existing);
        }

        if (bytes is not { Length: > 0 })
        {
            return null;
        }

        var name = stem + ImageSniff.Extension(bytes);
        WriteAtomic(Path.Combine(dir, name), bytes);
        return name;
    }

    private static void WriteAtomic(string path, byte[] data)
    {
        var temporary = path + ".tmp";
        File.WriteAllBytes(temporary, data);
        File.Move(temporary, path, overwrite: true);
    }

    private static string? Fingerprint(string path)
    {
        string full;
        long length;
        long ticks;
        if (File.Exists(path))
        {
            var info = new FileInfo(path);
            full = info.FullName;
            length = info.Length;
            ticks = info.LastWriteTimeUtc.Ticks;
        }
        else if (Directory.Exists(path))
        {
            var info = new DirectoryInfo(path);
            full = info.FullName;
            length = 0;
            ticks = info.LastWriteTimeUtc.Ticks;
        }
        else
        {
            return null;
        }

        var text = string.Create(CultureInfo.InvariantCulture, $"{SchemaVersion}\n{full}\n{length}\n{ticks}");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    private sealed class CacheMeta
    {
        public string? Title { get; set; }
        public string? TitleId { get; set; }
        public string? Version { get; set; }
        public string? Category { get; set; }
        public int? ParentalLevel { get; set; }
        public string? Icon { get; set; }
        public string? Banner { get; set; }
    }
}

public sealed class CachedTitle
{
    public string? Title { get; init; }
    public string? TitleId { get; init; }
    public string? Version { get; init; }
    public string? Category { get; init; }
    public int? ParentalLevel { get; init; }
    public string? IconPath { get; init; }
    public string? BannerPath { get; init; }
}
