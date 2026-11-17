using EliteLevel.Disc.Core.Formats;
using EliteLevel.Disc.Core.Formats.Xdvdfs;

namespace EliteLevel.Disc.Core;

/// <summary>Opens a disc image or a folder dump. The returned image owns its stream and is read-only.</summary>
public static class DiscImages
{
    private static readonly IImageFormat[] Formats = [new XdvdfsFormat(), new Iso9660Format()];

    public static IReadOnlyList<IImageFormat> RegisteredFormats => Formats;

    public static Task<IDiscImage> OpenAsync(string path, OpenOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new OpenOptions();
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

    private static async Task<IDiscImage> OpenOwnedAsync(Stream stream, string displayName, OpenOptions options, CancellationToken cancellationToken)
    {
        try
        {
            var hints = new ImageHints
            {
                FileName = displayName,
                Extension = Path.GetExtension(displayName)
            };
            foreach (var format in Formats)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!format.CanOpen(stream, hints))
                {
                    continue;
                }

                stream.Position = 0;
                var image = await format.OpenAsync(stream, options, cancellationToken).ConfigureAwait(false);
                return new NamedImage(image, displayName);
            }

            throw new DiscFormatException(DiscFormatException.Unrecognized);
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private sealed class NamedImage : IDiscImage
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
        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }
}
