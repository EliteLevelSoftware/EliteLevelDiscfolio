namespace EliteLevel.Disc.Core.Titles;

/// <summary>
/// Title and art read from a disc image or folder dump. Bytes are copies in memory.
/// The source image is never modified.
/// </summary>
public sealed class TitleMetadata
{
    public string? Title { get; init; }
    public string? TitleId { get; init; }
    public string? Version { get; init; }
    public string? Category { get; init; }
    public int? ParentalLevel { get; init; }
    public byte[]? Icon { get; init; }
    public byte[]? Banner { get; init; }

    /// <summary>File inside the image the icon was read from, such as ICON0.PNG.</summary>
    public string? IconName { get; init; }

    /// <summary>File inside the image the banner was read from, such as PIC1.PNG.</summary>
    public string? BannerName { get; init; }

    public bool HasIcon => Icon is { Length: > 0 };
    public bool HasBanner => Banner is { Length: > 0 };

    public static TitleMetadata Empty { get; } = new();
}
