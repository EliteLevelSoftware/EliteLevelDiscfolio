using DiscUtils;
using DiscUtils.Iso9660;
using EliteLevel.Disc.Core.IO;

namespace EliteLevel.Disc.Core.Formats;

public sealed class Iso9660Format : IImageFormat
{
    public string Id => "iso9660";
    public string DisplayName => "ISO 9660";

    public bool CanOpen(Stream probe, ImageHints hints)
    {
        if (!probe.CanSeek || probe.Length < 0x8000 + 2048)
        {
            return false;
        }

        var position = probe.Position;
        try
        {
            return CDReader.Detect(probe);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException)
        {
            return false;
        }
        finally
        {
            probe.Position = position;
        }
    }

    public Task<IDiscImage> OpenAsync(Stream source, OpenOptions options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var reader = new CDReader(source, joliet: true);
        var gate = new object();
        var variant = Describe(reader);
        var root = ReadDirectory(reader, reader.Root, "/", options.ShowSystemUpdate, gate);
        var stats = NodeWalk.Measure(root);
        var identity = new ImageIdentity(
            Id,
            variant,
            CleanLabel(reader.VolumeLabel),
            NodeWalk.DetectPlatform(root, Id),
            null,
            false,
            [variant],
            stats.Files,
            stats.Directories,
            stats.Bytes,
            "image");
        return Task.FromResult<IDiscImage>(new IsoImage(reader, source, identity, root));
    }

    private static string? CleanLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return null;
        }

        // Joliet identifiers are UCS-2. A space-padded ISO field can show up as U+2020.
        var text = label.Trim().TrimEnd('\0', ' ', '\u2020').Trim();
        return text.Length == 0 ? null : text;
    }

    private static string Describe(CDReader reader)
    {
        var name = reader.ActiveVariant.ToString();
        if (name.Contains("Joliet", StringComparison.OrdinalIgnoreCase))
        {
            return "ISO 9660 / Joliet";
        }

        return "ISO 9660";
    }

    private static DiscNode ReadDirectory(CDReader reader, DiscDirectoryInfo directory, string path, bool showSystem, object gate)
    {
        var node = DiscNode.Directory(directory.Name, path, showSystem);
        var children = new List<DiscNode>();
        foreach (var sub in directory.GetDirectories())
        {
            var childPath = VirtualPath.Combine(path, sub.Name);
            children.Add(ReadDirectory(reader, sub, childPath, showSystem, gate));
        }

        foreach (var file in directory.GetFiles())
        {
            var childPath = VirtualPath.Combine(path, file.Name);
            var length = file.Length;
            var fullName = file.FullName;
            children.Add(DiscNode.File(file.Name, childPath, length, showSystem, () =>
            {
                var inner = reader.OpenFile(fullName, FileMode.Open);
                return new SyncedStream(inner, gate, length);
            }));
        }

        node.SetChildren(children);
        return node;
    }

    private sealed class IsoImage : IDiscImage
    {
        private readonly CDReader _reader;
        private readonly Stream _source;
        private bool _disposed;

        public IsoImage(CDReader reader, Stream source, ImageIdentity identity, DiscNode root)
        {
            _reader = reader;
            _source = source;
            Identity = identity;
            Root = root;
        }

        public ImageIdentity Identity { get; }
        public IDiscNode Root { get; }

        public ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return ValueTask.CompletedTask;
            }

            _disposed = true;
            _reader.Dispose();
            _source.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
