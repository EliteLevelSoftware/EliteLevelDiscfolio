using EliteLevel.Disc.Core.IO;
using EliteLevel.Disc.Core.Safety;

namespace EliteLevel.Disc.Core.Formats;

internal sealed class DiscNode : IDiscNode
{
    private readonly Func<Stream>? _open;
    private readonly long _length;
    private readonly bool _showSystem;
    private IReadOnlyList<DiscNode> _all = [];
    private long? _size;

    private DiscNode(string name, string fullPath, bool directory, long length, bool showSystem, Func<Stream>? open)
    {
        Name = name;
        FullPath = fullPath;
        IsDirectory = directory;
        _length = length;
        _showSystem = showSystem;
        _open = open;
        Kind = FileKinds.FromName(name, directory);
        IsProtected = SafetyPolicy.IsProtected(fullPath);
    }

    public string Name { get; }
    public string FullPath { get; }
    public bool IsDirectory { get; }
    public FileKind Kind { get; }
    public bool IsProtected { get; }

    public long Size
    {
        get
        {
            if (!IsDirectory)
            {
                return _length;
            }

            return _size ??= Visible().Sum(child => child.Size);
        }
    }

    public IReadOnlyList<IDiscNode> Children => Visible();

    internal IReadOnlyList<DiscNode> AllChildren => _all;

    public Stream OpenRead()
    {
        if (IsDirectory || _open is null)
        {
            throw new InvalidOperationException("Directories have no file stream.");
        }

        return _open();
    }

    internal void SetChildren(IReadOnlyList<DiscNode> children)
    {
        _all = children
            .OrderBy(child => child.IsDirectory ? 0 : 1)
            .ThenBy(child => child.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _size = null;
    }

    internal static DiscNode Directory(string name, string fullPath, bool showSystem) =>
        new(name, fullPath, true, 0, showSystem, null);

    internal static DiscNode File(string name, string fullPath, long length, bool showSystem, Func<Stream> open) =>
        new(name, fullPath, false, length, showSystem, open);

    private DiscNode[] Visible()
    {
        if (_showSystem)
        {
            return _all.ToArray();
        }

        return _all.Where(child => !SafetyPolicy.IsSystemUpdateName(child.Name)).ToArray();
    }
}

internal static class NodeWalk
{
    public static IEnumerable<DiscNode> Walk(DiscNode node, bool includeSystem)
    {
        var children = includeSystem ? node.AllChildren : node.Children.Cast<DiscNode>();
        foreach (var child in children)
        {
            yield return child;
            if (child.IsDirectory)
            {
                foreach (var descendant in Walk(child, includeSystem))
                {
                    yield return descendant;
                }
            }
        }
    }

    public static (long Files, long Directories, long Bytes) Measure(DiscNode root)
    {
        long files = 0;
        long directories = 0;
        long bytes = 0;
        foreach (var node in Walk(root, includeSystem: false))
        {
            if (node.IsDirectory)
            {
                directories++;
            }
            else
            {
                files++;
                bytes += node.Size;
            }
        }

        return (files, directories, bytes);
    }

    public static string? DetectPlatform(DiscNode root, string formatId)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { root.Name };
        foreach (var node in Walk(root, includeSystem: true))
        {
            names.Add(node.Name);
        }

        if (names.Contains("PS3_GAME") || names.Contains("PS3_UPDATE"))
        {
            return "PlayStation 3";
        }

        if (formatId == "xdvdfs")
        {
            if (names.Contains("default.xex") || names.Contains("$SystemUpdate"))
            {
                return "Xbox 360";
            }

            if (names.Contains("default.xbe"))
            {
                return "Xbox";
            }

            return "Xbox";
        }

        return null;
    }
}
