using System.Text.Json;

namespace EliteLevel.Disc.App.Services;

public sealed class AppSettings
{
    public bool ReduceEffects { get; set; }
    public bool ShowSystemUpdate { get; set; }
    public bool WelcomeSeen { get; set; }
    public bool HashSha1 { get; set; }
    public bool HashMd5 { get; set; }
    public string? ExtractDirectory { get; set; }
    public List<RecentRecord> Recent { get; set; } = [];
}

public sealed class RecentRecord
{
    public string Path { get; set; } = "";
    public string Title { get; set; } = "";
    public string Meta { get; set; } = "";
    public string TitleId { get; set; } = "";
}

public static class SettingsStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static string _directory = DefaultDirectory();

    /// <summary>Discfolio data folder. Tests point this at a temporary directory.</summary>
    public static string DirectoryPath => _directory;

    public static string FilePath => Path.Combine(_directory, "settings.json");

    public static string CacheDirectory => Path.Combine(_directory, "cache");

    public static void UseDirectory(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Directory.CreateDirectory(directory);
        _directory = directory;
    }

    public static string DefaultDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "EliteLevel",
        "Discfolio");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return new AppSettings();
            }

            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
