using EliteLevel.Disc.Core.IO;

namespace EliteLevel.Disc.Core.Formats;

public sealed class FolderFormat : IImageFormat
{
    public string Id => "folder";
    public string DisplayName => "Folder dump";

    public bool CanOpen(Stream probe, ImageHints hints) => false;

    public Task<IDiscImage> OpenAsync(Stream source, OpenOptions options, CancellationToken cancellationToken) =>
        throw new DiscFormatException("A folder dump is opened from a directory path.");

    public Task<IDiscImage> OpenPathAsync(string path, OpenOptions options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var rootInfo = new DirectoryInfo(path);
        if (!rootInfo.Exists)
        {
            throw new DirectoryNotFoundException(path);
        }

        var fullRoot = rootInfo.FullName;
        var root = DiscNode.Directory(rootInfo.Name, "/", options.ShowSystemUpdate);
        root.SetChildren(ReadChildren(rootInfo, "/", fullRoot, options.ShowSystemUpdate));
        var stats = NodeWalk.Measure(root);
        var platform = NodeWalk.DetectPlatform(root, Id);
        var identity = new ImageIdentity(
            Id,
            DisplayName,
            rootInfo.Name,
            platform,
            "folder",
            false,
            ["Folder"],
            stats.Files,
            stats.Directories,
            stats.Bytes,
            rootInfo.Name);
        return Task.FromResult<IDiscImage>(new FolderImage(identity, root));
    }

    private static List<DiscNode> ReadChildren(DirectoryInfo directory, string virtualPath, string rootFull, bool showSystem)
    {
        var children = new List<DiscNode>();
        IEnumerable<FileSystemInfo> entries;
        try
        {
            entries = directory.EnumerateFileSystemInfos();
        }
        catch (IOException)
        {
            return children;
        }
        catch (UnauthorizedAccessException)
        {
            return children;
        }

        foreach (var entry in entries)
        {
            if (!VirtualPath.IsInside(rootFull, entry.FullName))
            {
                continue;
            }

            var childPath = VirtualPath.Combine(virtualPath, entry.Name);
            if (entry is DirectoryInfo sub)
            {
                var dir = DiscNode.Directory(entry.Name, childPath, showSystem);
                dir.SetChildren(ReadChildren(sub, childPath, rootFull, showSystem));
                children.Add(dir);
            }
            else if (entry is FileInfo file)
            {
                var physical = file.FullName;
                var length = file.Length;
                children.Add(DiscNode.File(file.Name, childPath, length, showSystem, () =>
                    new FileStream(physical, FileMode.Open, FileAccess.Read, FileShare.Read)));
            }
        }

        return children;
    }

    private sealed class FolderImage : IDiscImage
    {
        public FolderImage(ImageIdentity identity, DiscNode root)
        {
            Identity = identity;
            Root = root;
        }

        public ImageIdentity Identity { get; }
        public IDiscNode Root { get; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
