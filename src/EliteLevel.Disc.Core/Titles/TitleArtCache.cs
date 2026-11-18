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

    public CachedTitle? TryGet(string imagePath, string? variant = null)
    {
        try
        {
            var key = Fingerprint(imagePath, variant);
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

    public CachedTitle Store(string imagePath, TitleMetadata metadata, string? variant = null)
    {
        var memory = MemoryCopy(metadata);
        try
        {
            var key = Fingerprint(imagePath, variant);
            if (key is null)
            {
                return memory;
            }

            var dir = Path.Combine(_directory, key);
            Directory.CreateDirectory(dir);
            string? icon = WriteArt(dir, "icon", metadata.Icon);
            string? banner = WriteArt(dir, "banner", metadata.Banner);
            var meta = MetaFrom(metadata, icon, banner);
            WriteAtomic(Path.Combine(dir, "meta.json"), Encoding.UTF8.GetBytes(JsonSerializer.Serialize(meta, Json)));
            Remember(imagePath, key);
            return ToCached(dir, meta);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return memory;
        }
    }

    /// <summary>
    /// Art for a drive, keyed by volume label, size and a hash of the first sectors.
    /// The key does not depend on a file path, so it still resolves after the disc is ejected.
    /// </summary>
    public CachedTitle? TryGetKey(string cacheKey)
    {
        if (!IsCacheKey(cacheKey))
        {
            return null;
        }

        try
        {
            var dir = Path.Combine(_directory, cacheKey);
            var metaPath = Path.Combine(dir, "meta.json");
            if (!File.Exists(metaPath))
            {
                return null;
            }

            var meta = JsonSerializer.Deserialize<CacheMeta>(File.ReadAllText(metaPath), Json);
            return meta is null ? null : ToCached(dir, meta);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    public CachedTitle StoreKey(string cacheKey, TitleMetadata metadata)
    {
        var memory = MemoryCopy(metadata);
        if (!IsCacheKey(cacheKey))
        {
            return memory;
        }

        try
        {
            var dir = Path.Combine(_directory, cacheKey);
            Directory.CreateDirectory(dir);
            string? icon = WriteArt(dir, "icon", metadata.Icon);
            string? banner = WriteArt(dir, "banner", metadata.Banner);
            var meta = MetaFrom(metadata, icon, banner);
            WriteAtomic(Path.Combine(dir, "meta.json"), Encoding.UTF8.GetBytes(JsonSerializer.Serialize(meta, Json)));
            return ToCached(dir, meta);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return memory;
        }
    }

    public void RemoveKey(string cacheKey)
    {
        if (!IsCacheKey(cacheKey))
        {
            return;
        }

        try
        {
            var index = ReadIndex();
            foreach (var pair in index.Where(pair => string.Equals(pair.Value, cacheKey, StringComparison.Ordinal)).ToArray())
            {
                index.Remove(pair.Key);
            }

            var dir = Path.Combine(_directory, cacheKey);
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }

            WriteIndex(index);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or JsonException)
        {
        }
    }

    /// <summary>
    /// Deletes the cached art and metadata for one image. The image file is not touched.
    /// </summary>
    public void Remove(string imagePath)
    {
        try
        {
            var index = ReadIndex();
            var name = EntryName(imagePath);
            string? key = null;
            if (name is not null && !index.Remove(name, out key))
            {
                var match = index.Keys.FirstOrDefault(existing => string.Equals(existing, name, StringComparison.OrdinalIgnoreCase));
                if (match is not null)
                {
                    key = index[match];
                    index.Remove(match);
                }
            }

            key ??= Fingerprint(imagePath);
            if (key is not null && IsCacheKey(key))
            {
                var dir = Path.Combine(_directory, key);
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }

            WriteIndex(index);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or JsonException)
        {
        }
    }

    private void Remember(string imagePath, string key)
    {
        var name = EntryName(imagePath);
        if (name is null || !IsCacheKey(key))
        {
            return;
        }

        var index = ReadIndex();
        index[name] = key;
        WriteIndex(index);
    }

    private Dictionary<string, string> ReadIndex()
    {
        try
        {
            var path = Path.Combine(_directory, "index.json");
            if (!File.Exists(path))
            {
                return new Dictionary<string, string>(PathComparer);
            }

            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path), Json);
            return map is null
                ? new Dictionary<string, string>(PathComparer)
                : new Dictionary<string, string>(map, PathComparer);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        {
            return new Dictionary<string, string>(PathComparer);
        }
    }

    private void WriteIndex(Dictionary<string, string> index)
    {
        Directory.CreateDirectory(_directory);
        WriteAtomic(Path.Combine(_directory, "index.json"), Encoding.UTF8.GetBytes(JsonSerializer.Serialize(index, Json)));
    }

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static bool IsCacheKey(string? key) =>
        key is { Length: 64 } && key.All(static c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static CachedTitle MemoryCopy(TitleMetadata metadata) => new()
    {
        Title = metadata.Title,
        TitleId = metadata.TitleId,
        Version = metadata.Version,
        Category = metadata.Category,
        ParentalLevel = metadata.ParentalLevel
    };

    private static CacheMeta MetaFrom(TitleMetadata metadata, string? icon, string? banner) => new()
    {
        Title = metadata.Title,
        TitleId = metadata.TitleId,
        Version = metadata.Version,
        Category = metadata.Category,
        ParentalLevel = metadata.ParentalLevel,
        Icon = icon,
        Banner = banner
    };

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

    private static string? EntryName(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                return new FileInfo(path).FullName;
            }

            if (Directory.Exists(path))
            {
                return new DirectoryInfo(path).FullName;
            }

            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? Fingerprint(string path, string? variant = null)
    {
        string? full;
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
        if (!string.IsNullOrEmpty(variant))
        {
            text += "\n" + variant;
        }

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
