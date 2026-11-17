namespace EliteLevel.Disc.Core;

/// <summary>How a file should be treated by a later preview pane.</summary>
public enum FileKind
{
    Unknown = 0,
    Directory,
    Image,
    Text,
    Audio,
    Binary,
    ParamSfo
}

[Flags]
public enum HashAlgorithmKind
{
    None = 0,
    Sha256 = 1,
    Sha1 = 2,
    Md5 = 4
}

/// <summary>Hints that came from the file name. Detection itself is by content.</summary>
public sealed class ImageHints
{
    public string? FileName { get; init; }
    public string? Extension { get; init; }
}

/// <summary>
/// Options for opening an image. Key material is never stored here in this milestone;
/// the property exists so a later Switch backend can use the same options object.
/// </summary>
public sealed class OpenOptions
{
    /// <summary>When false, <c>$SystemUpdate</c> is omitted from the visible tree.</summary>
    public bool ShowSystemUpdate { get; init; }

    /// <summary>ISO 9660 multi-extent files are presented as one stream.</summary>
    public bool MergeMultiExtent { get; init; } = true;

    /// <summary>Reserved for a later keys folder. Never logged.</summary>
    public string? KeysDirectory { get; init; }
}

public sealed class ExtractOptions
{
    public bool SkipExisting { get; init; }
    public bool IncludeProtected { get; init; }
    public bool VerifyHash { get; init; }
    public string? Pattern { get; init; }
    public string? Exclude { get; init; }
}

public sealed record ImageIdentity(
    string FormatId,
    string FormatDisplayName,
    string? VolumeLabel,
    string? Platform,
    string? Layout,
    bool VideoPartition,
    IReadOnlyList<string> Partitions,
    long FileCount,
    long DirectoryCount,
    long TotalBytes,
    string SourceName);

public readonly record struct ExtractProgress(int Completed, int Total, string CurrentPath, long BytesWritten);

public readonly record struct ExtractResult(int Extracted, int Skipped, int ProtectedSkipped, long BytesWritten);

public sealed record HashSetResult(string? Sha256, string? Sha1, string? Md5)
{
    public string? Primary => Sha256 ?? Sha1 ?? Md5;
}

public sealed record HashEntry(string Path, HashSetResult Hashes);

public interface IImageFormat
{
    string Id { get; }
    string DisplayName { get; }
    bool CanOpen(Stream probe, ImageHints hints);
    Task<IDiscImage> OpenAsync(Stream source, OpenOptions options, CancellationToken cancellationToken);
}

public interface IDiscImage : IAsyncDisposable
{
    ImageIdentity Identity { get; }
    IDiscNode Root { get; }
}

public interface IDiscNode
{
    string Name { get; }
    string FullPath { get; }
    bool IsDirectory { get; }
    long Size { get; }
    IReadOnlyList<IDiscNode> Children { get; }
    Stream OpenRead();
    FileKind Kind { get; }

    /// <summary>Skipped by the default extract policy. Still readable.</summary>
    bool IsProtected { get; }
}

public interface IExtractor
{
    Task<ExtractResult> ExtractAsync(
        IEnumerable<IDiscNode> nodes,
        DirectoryInfo destination,
        ExtractOptions options,
        IProgress<ExtractProgress>? progress,
        CancellationToken cancellationToken);
}

public interface IHashService
{
    Task<HashSetResult> ComputeAsync(IDiscNode node, HashAlgorithmKind kinds, CancellationToken cancellationToken);
}

/// <summary>The image is not a format this build can read.</summary>
public sealed class DiscFormatException : Exception
{
    public const string Unrecognized =
        "This file does not look like a supported disc image. Check that it is an ISO, XISO, NSP, XCI, or a folder dump.";

    public DiscFormatException(string message) : base(message)
    {
    }

    public DiscFormatException(string message, Exception inner) : base(message, inner)
    {
    }
}
