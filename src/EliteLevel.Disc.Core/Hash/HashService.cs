using System.Security.Cryptography;
using EliteLevel.Disc.Core.Formats;
using EliteLevel.Disc.Core.IO;

namespace EliteLevel.Disc.Core.Hash;

public sealed class HashService : IHashService
{
    public async Task<HashSetResult> ComputeAsync(IDiscNode node, HashAlgorithmKind kinds, CancellationToken cancellationToken)
    {
        if (node.IsDirectory)
        {
            throw new InvalidOperationException("Hash a file. A directory hash is a manifest of its files.");
        }

        if (kinds == HashAlgorithmKind.None)
        {
            kinds = HashAlgorithmKind.Sha256;
        }

        await using var stream = node.OpenRead();
        return await ComputeAsync(stream, kinds, cancellationToken).ConfigureAwait(false);
    }

    public async Task<HashSetResult> ComputeAsync(Stream stream, HashAlgorithmKind kinds, CancellationToken cancellationToken)
    {
        if (kinds == HashAlgorithmKind.None)
        {
            kinds = HashAlgorithmKind.Sha256;
        }

        using var sha256 = kinds.HasFlag(HashAlgorithmKind.Sha256) ? SHA256.Create() : null;
        using var sha1 = kinds.HasFlag(HashAlgorithmKind.Sha1) ? SHA1.Create() : null;
        using var md5 = kinds.HasFlag(HashAlgorithmKind.Md5) ? MD5.Create() : null;
        var buffer = new byte[81920];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            sha256?.TransformBlock(buffer, 0, read, null, 0);
            sha1?.TransformBlock(buffer, 0, read, null, 0);
            md5?.TransformBlock(buffer, 0, read, null, 0);
        }

        return new HashSetResult(Finish(sha256), Finish(sha1), Finish(md5));
    }

    public async Task<IReadOnlyList<HashEntry>> HashTreeAsync(IDiscNode root, HashAlgorithmKind kinds, CancellationToken cancellationToken)
    {
        var results = new List<HashEntry>();
        var files = root is DiscNode concrete
            ? NodeWalk.Walk(concrete, includeSystem: true).Where(node => !node.IsDirectory)
            : WalkFiles(root);
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(new HashEntry(file.FullPath, await ComputeAsync(file, kinds, cancellationToken).ConfigureAwait(false)));
        }

        return results;
    }

    private static IEnumerable<IDiscNode> WalkFiles(IDiscNode node)
    {
        if (!node.IsDirectory)
        {
            yield return node;
            yield break;
        }

        foreach (var child in node.Children)
        {
            foreach (var file in WalkFiles(child))
            {
                yield return file;
            }
        }
    }

    private static string? Finish(HashAlgorithm? algorithm)
    {
        if (algorithm is null)
        {
            return null;
        }

        algorithm.TransformFinalBlock([], 0, 0);
        return Hex.Format(algorithm.Hash ?? []);
    }
}
