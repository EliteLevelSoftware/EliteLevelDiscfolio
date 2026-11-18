using System.Runtime.InteropServices;
using Velopack;
using Velopack.Sources;

namespace EliteLevel.Disc.App.Services;

/// <summary>
/// The public GitHub releases for EliteLevelSoftware/EliteLevelDiscfolio.
/// A token is not required. <see cref="TokenEnvironmentVariable"/> or a saved
/// <see cref="AppSettings.UpdateToken"/> is only an override for a private feed.
/// </summary>
public static class UpdateFeed
{
    public const string DefaultRepository = "https://github.com/EliteLevelSoftware/EliteLevelDiscfolio";
    public const string TokenEnvironmentVariable = "DISCFOLIO_UPDATE_TOKEN";

    public static string RuntimeId()
    {
        if (OperatingSystem.IsWindows())
        {
            return "win-x64";
        }

        if (OperatingSystem.IsMacOS())
        {
            return RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "osx-arm64" : "osx-x64";
        }

        return RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "linux-arm64" : "linux-x64";
    }

    public static string ChannelName(string? track) =>
        RuntimeId() + "-" + (track == "prerelease" ? "prerelease" : "stable");

    public static string RepositoryUrl(string? configured)
    {
        var value = configured?.Trim() ?? "";
        if (value.Length == 0)
        {
            return DefaultRepository;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return value;
        }

        if (!uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            return value;
        }

        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return value;
        }

        var repo = parts[1];
        if (repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            repo = repo[..^4];
        }

        return "https://github.com/" + parts[0] + "/" + repo;
    }

    public static bool IsGitHubRepository(string url)
    {
        const string prefix = "https://github.com/";
        if (!url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var rest = url[prefix.Length..];
        var slash = rest.IndexOf('/');
        if (slash <= 0 || slash >= rest.Length - 1)
        {
            return false;
        }

        var repo = rest[(slash + 1)..];
        return repo.Length > 0 && !repo.Contains('/');
    }

    public static string? ResolveToken(string? settingsToken)
    {
        var env = Environment.GetEnvironmentVariable(TokenEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(env))
        {
            return env.Trim();
        }

        if (!string.IsNullOrWhiteSpace(settingsToken))
        {
            return settingsToken.Trim();
        }

        return null;
    }

    public static IUpdateSource CreateSource(string? configured, string? token, bool prerelease)
    {
        var url = RepositoryUrl(configured);
        if (IsGitHubRepository(url))
        {
            return new GithubSource(url, string.IsNullOrWhiteSpace(token) ? null : token, prerelease);
        }

        return new SimpleWebSource(url);
    }

    public static UpdateManager CreateManager(AppSettings settings)
    {
        var track = settings.UpdateChannel == "prerelease" ? "prerelease" : "stable";
        var token = ResolveToken(settings.UpdateToken);
        return new UpdateManager(CreateSource(settings.UpdateSource, token, track == "prerelease"), new UpdateOptions
        {
            ExplicitChannel = ChannelName(track),
            AllowVersionDowngrade = true
        });
    }

    public static string PublicError(Exception exception, string? token)
    {
        var message = exception.Message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (!string.IsNullOrEmpty(token))
        {
            message = message.Replace(token, "", StringComparison.Ordinal);
        }

        return message.Length > 180 ? message[..180] : message;
    }
}
