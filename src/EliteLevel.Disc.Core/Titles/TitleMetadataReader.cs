using System.Text;

namespace EliteLevel.Disc.Core.Titles;

/// <summary>
/// Reads a display title and its art from an open image. Each platform stays in its
/// own reader. A failure falls back to the volume label and, for a plain ISO, a
/// simple autorun.inf icon. Nothing is written next to the image.
/// </summary>
public static class TitleMetadataReader
{
    public static TitleMetadata Read(IDiscImage image)
    {
        try
        {
            var root = image.Root;
            if (TryPlayStation3(root, out var ps3))
            {
                return ps3;
            }

            if (TryXbox360(root, out var xbox360))
            {
                return xbox360;
            }

            if (TryOriginalXbox(root, out var xbox))
            {
                return xbox;
            }

            return IsoFallback(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            return TitleMetadata.Empty;
        }
    }

    /// <summary>Title taken from the image, or null when the caller should use the volume label.</summary>
    public static string? ReadTitle(TitleMetadata metadata) =>
        string.IsNullOrWhiteSpace(metadata.Title) ? null : metadata.Title.Trim();

    public static string ArtExtension(ReadOnlySpan<byte> image) => ImageSniff.Extension(image);

    private static bool TryPlayStation3(IDiscNode root, out TitleMetadata metadata)
    {
        metadata = TitleMetadata.Empty;
        var home = DiscRead.Shallowest(root, 2, IsPlayStationRoot);
        if (home is null)
        {
            return false;
        }

        var game = DiscRead.Child(home, "PS3_GAME");
        if (game is not { IsDirectory: true })
        {
            game = DiscRead.File(home, "PARAM.SFO") is not null ? home : null;
        }

        if (game is null)
        {
            return false;
        }

        string? title = null;
        string? titleId = null;
        string? version = null;
        string? category = null;
        int? parental = null;
        var sfo = DiscRead.Bytes(DiscRead.File(game, "PARAM.SFO"), 1024 * 1024);
        if (sfo is not null && ParamSfo.TryRead(sfo, out var file))
        {
            title = file.Text("TITLE");
            titleId = file.Text("TITLE_ID");
            version = file.Text("APP_VER") ?? file.Text("VERSION");
            category = file.Text("CATEGORY");
            parental = file.UInt32("PARENTAL_LEVEL");
        }

        var icon = ImageFile(game, "ICON0.PNG", out var iconName);
        var banner = ImageFile(game, "PIC1.PNG", out var bannerName);
        if (banner is null)
        {
            banner = ImageFile(game, "PIC0.PNG", out bannerName);
        }

        if (title is null && titleId is null && version is null && icon is null && banner is null)
        {
            return false;
        }

        metadata = new TitleMetadata
        {
            Title = title,
            TitleId = titleId,
            Version = version,
            Category = category,
            ParentalLevel = parental,
            Icon = icon,
            Banner = banner,
            IconName = iconName,
            BannerName = bannerName
        };
        return true;
    }

    /// <summary>A directory that holds PS3_DISC.SFB, PS3_GAME/PARAM.SFO, or PARAM.SFO itself.</summary>
    private static bool IsPlayStationRoot(IDiscNode directory)
    {
        if (DiscRead.File(directory, "PS3_DISC.SFB") is not null)
        {
            return true;
        }

        var game = DiscRead.Child(directory, "PS3_GAME");
        if (game is { IsDirectory: true } && DiscRead.File(game, "PARAM.SFO") is not null)
        {
            return true;
        }

        return DiscRead.File(directory, "PARAM.SFO") is not null;
    }

    private static bool TryXbox360(IDiscNode root, out TitleMetadata metadata)
    {
        metadata = TitleMetadata.Empty;
        var home = DiscRead.Shallowest(root, 2, directory => DiscRead.File(directory, "default.xex") is not null);
        if (home is null)
        {
            return false;
        }

        var xex = DiscRead.File(home, "default.xex");
        if (xex is null)
        {
            return false;
        }

        string? title = null;
        string? titleId = null;
        string? version = null;
        byte[]? icon = null;
        var bytes = DiscRead.Bytes(xex, DiscRead.HeaderCap);
        if (bytes is not null)
        {
            XexTitle.TryRead(bytes, out title, out titleId, out version, out icon);
        }

        string? iconName = icon is null ? null : "default.xex";
        byte[]? banner = null;
        string? bannerName = null;
        var nxeNode = DiscRead.File(home, "nxeart") ?? DiscRead.FindFile(home, "nxeart");
        var nxeRaw = DiscRead.Bytes(nxeNode, DiscRead.ImageCap);
        var nxe = nxeRaw is null ? null : ImageSniff.AsImage(nxeRaw);
        if (nxe is not null)
        {
            banner = nxe;
            bannerName = "nxeart";
            if (icon is null)
            {
                icon = nxe;
                iconName = "nxeart";
            }
        }

        if (title is null && titleId is null && version is null && icon is null)
        {
            return false;
        }

        metadata = new TitleMetadata
        {
            Title = title,
            TitleId = titleId,
            Version = version,
            Icon = icon,
            Banner = banner,
            IconName = iconName,
            BannerName = bannerName
        };
        return true;
    }

    private static bool TryOriginalXbox(IDiscNode root, out TitleMetadata metadata)
    {
        metadata = TitleMetadata.Empty;
        var home = DiscRead.Shallowest(root, 2, directory => DiscRead.File(directory, "default.xbe") is not null);
        if (home is null)
        {
            return false;
        }

        var xbe = DiscRead.File(home, "default.xbe");
        if (xbe is null)
        {
            return false;
        }

        var bytes = DiscRead.Bytes(xbe, DiscRead.HeaderCap);
        if (bytes is null || !XbeTitle.TryRead(bytes, out var title, out var titleId, out var version, out var icon))
        {
            return false;
        }

        metadata = new TitleMetadata
        {
            Title = title,
            TitleId = titleId,
            Version = version,
            Icon = icon,
            IconName = icon is null ? null : "default.xbe"
        };
        return title is not null || titleId is not null || icon is not null;
    }

    private static TitleMetadata IsoFallback(IDiscNode root)
    {
        var autorun = DiscRead.Bytes(DiscRead.File(root, "autorun.inf"), 64 * 1024);
        if (autorun is null)
        {
            return TitleMetadata.Empty;
        }

        var text = Encoding.UTF8.GetString(autorun);
        if (!TrySimpleIcon(text, out var relative))
        {
            return TitleMetadata.Empty;
        }

        var file = DiscRead.Resolve(root, relative);
        var bytes = DiscRead.Bytes(file, DiscRead.ImageCap);
        var image = bytes is null ? null : ImageSniff.AsImage(bytes);
        if (image is null || file is null)
        {
            return TitleMetadata.Empty;
        }

        return new TitleMetadata
        {
            Icon = image,
            IconName = file.Name
        };
    }

    private static byte[]? ImageFile(IDiscNode? directory, string name, out string? foundName)
    {
        foundName = null;
        if (directory is not { IsDirectory: true })
        {
            return null;
        }

        var node = DiscRead.File(directory, name);
        var bytes = DiscRead.Bytes(node, DiscRead.ImageCap);
        var image = bytes is null ? null : ImageSniff.AsImage(bytes);
        if (image is null || node is null)
        {
            return null;
        }

        foundName = node.Name;
        return image;
    }

    /// <summary>
    /// An autorun icon is simple when it points at a file in the image.
    /// A resource index such as <c>setup.exe,0</c> is left alone.
    /// </summary>
    internal static bool TrySimpleIcon(string text, out string relative)
    {
        relative = string.Empty;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().TrimStart('\uFEFF');
            if (line.Length == 0 || line[0] is ';' or '#')
            {
                continue;
            }

            var split = line.IndexOf('=');
            if (split <= 0)
            {
                continue;
            }

            var key = line[..split].Trim();
            if (!key.Equals("icon", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = line[(split + 1)..].Trim().Trim('"');
            if (value.Length == 0 || value.Contains(',') || value.Contains(':') || value.Contains("..", StringComparison.Ordinal))
            {
                continue;
            }

            relative = value.Replace('\\', '/').TrimStart('/');
            return relative.Length > 0 && !relative.Contains("//", StringComparison.Ordinal);
        }

        return false;
    }
}
