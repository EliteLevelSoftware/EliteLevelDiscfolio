namespace EliteLevel.Disc.Core.Titles;

internal static class DiscRead
{
    public const int HeaderCap = 8 * 1024 * 1024;
    public const int ImageCap = 12 * 1024 * 1024;

    public static IDiscNode? Child(IDiscNode node, string name)
    {
        foreach (var child in node.Children)
        {
            if (child.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return child;
            }
        }

        return null;
    }

    public static IDiscNode? File(IDiscNode node, string name)
    {
        var child = Child(node, name);
        return child is { IsDirectory: false } ? child : null;
    }

    public static IDiscNode? Resolve(IDiscNode root, string path)
    {
        var current = root;
        foreach (var part in path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part is "." or "..")
            {
                return null;
            }

            var next = Child(current, part);
            if (next is null)
            {
                return null;
            }

            current = next;
        }

        return current.IsDirectory ? null : current;
    }

    public static IDiscNode? FindFile(IDiscNode node, string name)
    {
        var direct = File(node, name);
        if (direct is not null)
        {
            return direct;
        }

        foreach (var child in node.Children)
        {
            if (!child.IsDirectory)
            {
                continue;
            }

            var found = FindFile(child, name);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    public static byte[]? Bytes(IDiscNode? node, int cap)
    {
        if (node is null || node.IsDirectory || node.Size <= 0 || cap <= 0)
        {
            return null;
        }

        try
        {
            var length = (int)Math.Min(node.Size, cap);
            var buffer = new byte[length];
            using var stream = node.OpenRead();
            var read = 0;
            while (read < length)
            {
                var n = stream.Read(buffer, read, length - read);
                if (n == 0)
                {
                    break;
                }

                read += n;
            }

            return read == length ? buffer : buffer[..read];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ObjectDisposedException or ArgumentException)
        {
            return null;
        }
    }
}
