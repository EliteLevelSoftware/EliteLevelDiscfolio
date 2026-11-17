using EliteLevel.Disc.Core.Formats;
using EliteLevel.Disc.Core.IO;

namespace EliteLevel.Disc.Core.Search;

public static class NameSearch
{
    public static IReadOnlyList<IDiscNode> Search(IDiscNode root, string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return [];
        }

        var glob = pattern.Contains('*') || pattern.Contains('?');
        var matches = new List<IDiscNode>();
        var nodes = root is DiscNode concrete
            ? NodeWalk.Walk(concrete, includeSystem: false)
            : Walk(root);
        foreach (var node in nodes)
        {
            if (glob)
            {
                if (Glob.IsMatch(node.Name, pattern) || Glob.IsMatch(node.FullPath, pattern))
                {
                    matches.Add(node);
                }
            }
            else if (node.Name.Contains(pattern, StringComparison.OrdinalIgnoreCase)
                || node.FullPath.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add(node);
            }
        }

        return matches;
    }

    private static IEnumerable<IDiscNode> Walk(IDiscNode node)
    {
        foreach (var child in node.Children)
        {
            yield return child;
            if (child.IsDirectory)
            {
                foreach (var descendant in Walk(child))
                {
                    yield return descendant;
                }
            }
        }
    }
}
