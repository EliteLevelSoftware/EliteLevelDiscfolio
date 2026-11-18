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
/// Options for opening an image. A keys path is not itself a secret.
/// The file it names is read only to decrypt an NCA the caller already has, and it is never logged.
/// </summary>
public sealed class OpenOptions
{
    /// <summary>When false, <c>$SystemUpdate</c> is omitted from the visible tree.</summary>
    public bool ShowSystemUpdate { get; init; }

    /// <summary>ISO 9660 multi-extent files are presented as one stream.</summary>
    public bool MergeMultiExtent { get; init; } = true;

    /// <summary>
    /// Path to a prod.keys file, or a folder that contains prod.keys and title.keys.
    /// Discfolio does not include console keys.
    /// </summary>
    public string? KeysDirectory { get; init; }

    /// <summary>File name used when a container is identified by its extension. Not a full path.</summary>
    public string? DisplayName { get; init; }
}

public sealed class ExtractOptions
{
    public bool SkipExisting { get; init; }
    public bool IncludeProtected { get; init; }
    public bool VerifyHash { get; init; }
    public string? Pattern { get; init; }
    public string? Exclude { get; init; }

    /// <summary>Write an NCZ entry as one decompressed NCA instead of the files inside it.</summary>
    public bool Decompress { get; init; }
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
    /// <summary>Files the default extract skips, including files under a hidden system-update folder.</summary>
    long ProtectedCount,
    string SourceName,
    bool FromDrive = false,
    string? Notice = null,
    string? CacheKey = null);

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

    /// <summary>Phrase used when listing formats a file might be. Kept on the reader so the unsupported-file message stays current.</summary>
    string SupportPhrase { get; }

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
    /// <summary>A file that matched none of the registered readers.</summary>
    public static string Unrecognized => DiscImages.UnsupportedFileMessage;

    /// <summary>A file named like NSZ or XCZ that is not a PFS0 or XCI of NCZ entries.</summary>
    public const string SwitchCompressed =
        "This file is not an NSZ or XCZ container. An NSZ is a PFS0 of NCZ files and an XCZ is an XCI of NCZ files.";

    public DiscFormatException(string message) : base(message)
    {
    }

    public DiscFormatException(string message, Exception inner) : base(message, inner)
    {
    }
}
