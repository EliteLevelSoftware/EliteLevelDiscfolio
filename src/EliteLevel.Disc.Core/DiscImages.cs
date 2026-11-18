using System.Runtime.CompilerServices;
using EliteLevel.Disc.Core.Drives;
using EliteLevel.Disc.Core.Formats;
using EliteLevel.Disc.Core.Formats.Switch;
using EliteLevel.Disc.Core.Formats.Xdvdfs;

[assembly: InternalsVisibleTo("EliteLevel.Disc.Core.Tests")]
[assembly: InternalsVisibleTo("EliteLevel.Disc.App.Tests")]

namespace EliteLevel.Disc.Core;

/// <summary>Opens a disc image or a folder dump. The returned image owns its stream and is read-only.</summary>
public static class DiscImages
{
    private static readonly IImageFormat[] Formats = [new XdvdfsFormat(), new Iso9660Format(), new SwitchFormat()];

    public static IReadOnlyList<IImageFormat> RegisteredFormats => Formats;

    /// <summary>Shown when a file is not a registered reader and not a known unsupported container.</summary>
    internal static string UnsupportedFileMessage => DescribeUnsupportedFile(Formats.Append(new FolderFormat()));

    /// <summary>Builds the unsupported-file message from the readers that were passed in.</summary>
    internal static string DescribeUnsupportedFile(IEnumerable<IImageFormat> readers)
    {
        var phrases = readers
            .Where(reader => !string.IsNullOrWhiteSpace(reader.SupportPhrase))
            .OrderBy(reader => SupportOrder(reader.Id))
            .ThenBy(reader => reader.Id, StringComparer.Ordinal)
            .Select(reader => reader.SupportPhrase)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return "This file does not look like a supported disc image. Check that it is " + JoinPhrases(phrases) + ".";
    }

    public static Task<IDiscImage> OpenAsync(string path, OpenOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new OpenOptions();
        if (OpticalDrives.IsDrivePath(path))
        {
            return OpticalDrives.OpenImageAsync(path, options, cancellationToken);
        }

        if (Directory.Exists(path))
        {
            return new FolderFormat().OpenPathAsync(path, options, cancellationToken);
        }

        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Could not find the image.", path);
        }

        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return OpenOwnedAsync(stream, Path.GetFileName(path), options, cancellationToken);
    }

    public static Task<IDiscImage> OpenAsync(Stream source, string? displayName, OpenOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new OpenOptions();
        if (!source.CanRead || !source.CanSeek)
        {
            throw new DiscFormatException("The image stream must be readable and seekable.");
        }

        return OpenOwnedAsync(source, displayName ?? "image", options, cancellationToken);
    }

    internal static Task<IDiscImage> OpenStreamAsync(Stream source, string? displayName, OpenOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new OpenOptions();
        if (!source.CanRead || !source.CanSeek)
        {
            throw new DiscFormatException("The image stream must be readable and seekable.");
        }

        return OpenOwnedAsync(source, displayName ?? "image", options, cancellationToken);
    }

    private static async Task<IDiscImage> OpenOwnedAsync(Stream stream, string displayName, OpenOptions options, CancellationToken cancellationToken)
    {
        try
        {
            var hints = new ImageHints
            {
                FileName = displayName,
                Extension = Path.GetExtension(displayName)
            };
            var openOptions = new OpenOptions
            {
                ShowSystemUpdate = options.ShowSystemUpdate,
                MergeMultiExtent = options.MergeMultiExtent,
                KeysDirectory = options.KeysDirectory,
                DisplayName = displayName
            };
            foreach (var format in Formats)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!format.CanOpen(stream, hints))
                {
                    continue;
                }

                stream.Position = 0;
                var image = await format.OpenAsync(stream, openOptions, cancellationToken).ConfigureAwait(false);
                return new NamedImage(image, displayName);
            }

            throw new DiscFormatException(UnsupportedFileMessage);
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private sealed class NamedImage : IDiscImage, ISwitchTitle
    {
        private readonly IDiscImage _inner;

        public NamedImage(IDiscImage inner, string sourceName)
        {
            _inner = inner;
            var id = inner.Identity;
            Identity = id with { SourceName = sourceName };
        }

        public ImageIdentity Identity { get; }
        public IDiscNode Root => _inner.Root;
        public string? TitleId => (_inner as ISwitchTitle)?.TitleId;
        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }

    private static int SupportOrder(string id) => id switch
    {
        "iso9660" => 0,
        "xdvdfs" => 1,
        "switch" => 2,
        "folder" => 3,
        _ => 10
    };

    private static string JoinPhrases(IReadOnlyList<string> phrases)
    {
        return phrases.Count switch
        {
            0 => "a supported disc image",
            1 => phrases[0],
            2 => phrases[0] + " or " + phrases[1],
            _ => string.Join(", ", phrases.Take(phrases.Count - 1)) + ", or " + phrases[^1]
        };
    }
}
