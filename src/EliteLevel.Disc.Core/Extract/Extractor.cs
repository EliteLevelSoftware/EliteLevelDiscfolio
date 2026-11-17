using EliteLevel.Disc.Core.Formats;
using EliteLevel.Disc.Core.Hash;
using EliteLevel.Disc.Core.IO;

namespace EliteLevel.Disc.Core.Extract;

public sealed class Extractor : IExtractor
{
    public async Task<ExtractResult> ExtractAsync(
        IEnumerable<IDiscNode> nodes,
        DirectoryInfo destination,
        ExtractOptions options,
        IProgress<ExtractProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(destination);
        options ??= new ExtractOptions();

        var files = new List<IDiscNode>();
        foreach (var node in nodes)
        {
            Collect(node, files);
        }

        destination.Create();
        var destinationFull = destination.FullName;
        var extracted = 0;
        var skipped = 0;
        var protectedSkipped = 0;
        long bytes = 0;
        var completed = 0;

        foreach (var node in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (NodeWalk.IsProtectedFile(node) && !options.IncludeProtected)
            {
                protectedSkipped++;
                continue;
            }

            if (!Included(node, options))
            {
                continue;
            }

            var relative = VirtualPath.ToRelative(node.FullPath);
            var target = Path.GetFullPath(Path.Combine(destinationFull, relative));
            if (!VirtualPath.IsInside(destinationFull, target))
            {
                throw new InvalidOperationException("Extract refused to write outside the destination.");
            }

            var parent = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            if (options.SkipExisting && File.Exists(target) && new FileInfo(target).Length == node.Size)
            {
                if (!options.VerifyHash || await SameHashAsync(node, target, cancellationToken).ConfigureAwait(false))
                {
                    skipped++;
                    completed++;
                    progress?.Report(new ExtractProgress(completed, files.Count, node.FullPath, bytes));
                    continue;
                }
            }

            await using (var input = node.OpenRead())
            await using (var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            }

            extracted++;
            bytes += node.Size;
            completed++;
            progress?.Report(new ExtractProgress(completed, files.Count, node.FullPath, bytes));
        }

        return new ExtractResult(extracted, skipped, protectedSkipped, bytes);
    }

    private static void Collect(IDiscNode node, List<IDiscNode> files)
    {
        if (!node.IsDirectory)
        {
            files.Add(node);
            return;
        }

        if (node is not DiscNode concrete)
        {
            foreach (var child in node.Children)
            {
                Collect(child, files);
            }

            return;
        }

        // Same file set info counts as protected: hidden system-update files included.
        foreach (var child in NodeWalk.FilesIncludingSystem(concrete))
        {
            files.Add(child);
        }
    }

    private static bool Included(IDiscNode node, ExtractOptions options)
    {
        if (!string.IsNullOrEmpty(options.Exclude)
            && (Glob.IsMatch(node.Name, options.Exclude) || Glob.IsMatch(node.FullPath, options.Exclude)))
        {
            return false;
        }

        if (string.IsNullOrEmpty(options.Pattern))
        {
            return true;
        }

        return Glob.IsMatch(node.Name, options.Pattern) || Glob.IsMatch(node.FullPath, options.Pattern);
    }

    private static async Task<bool> SameHashAsync(IDiscNode node, string path, CancellationToken cancellationToken)
    {
        var hasher = new HashService();
        var fromImage = await hasher.ComputeAsync(node, HashAlgorithmKind.Sha256, cancellationToken).ConfigureAwait(false);
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var fromDisk = await hasher.ComputeAsync(file, HashAlgorithmKind.Sha256, cancellationToken).ConfigureAwait(false);
        return string.Equals(fromImage.Sha256, fromDisk.Sha256, StringComparison.OrdinalIgnoreCase);
    }
}
