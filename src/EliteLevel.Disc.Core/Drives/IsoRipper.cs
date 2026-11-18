using System.Globalization;
using System.Security.Cryptography;

namespace EliteLevel.Disc.Core.Drives;

public sealed class RipOptions
{
    /// <summary>How many times each sector is read before it is reported as bad. The first try counts.</summary>
    public int Attempts { get; init; } = IsoRipper.DefaultAttempts;
}

public readonly record struct RipProgress(long CompletedSectors, long TotalSectors, long BadSectors, long BytesWritten);

public sealed record RipResult(
    string OutputPath,
    long BytesWritten,
    long SectorCount,
    IReadOnlyList<long> BadSectors,
    string Sha256,
    string Sha1,
    string Md5,
    string Sha256Path,
    string Sha1Path,
    string Md5Path,
    string? BadSectorPath);

/// <summary>
/// Copies a block device to a new ISO, one sector at a time.
/// A sector that still fails after the configured attempts is written as zeros and reported.
/// The device itself is only read.
/// </summary>
public static class IsoRipper
{
    public const int DefaultAttempts = 5;

    public static async Task<RipResult> RipAsync(
        IBlockDevice device,
        string outputPath,
        RipOptions? options = null,
        IProgress<RipProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        options ??= new RipOptions();
        if (options.Attempts < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Retries must be at least 1.");
        }

        if (string.IsNullOrWhiteSpace(outputPath))
        {
            throw new IOException("Rip needs an output file.");
        }

        if (OpticalDrives.IsDrivePath(outputPath))
        {
            throw new IOException("Refusing to write an image onto a drive.");
        }

        if (device.Length <= 0)
        {
            throw new IOException("The disc has no readable size.");
        }

        var full = Path.GetFullPath(outputPath);
        if (File.Exists(full))
        {
            throw new IOException("The output file already exists.");
        }

        var sha256Path = full + ".sha256";
        var sha1Path = full + ".sha1";
        var md5Path = full + ".md5";
        var badPath = full + ".badsectors";
        foreach (var sidecar in new[] { sha256Path, sha1Path, md5Path, badPath })
        {
            if (File.Exists(sidecar))
            {
                throw new IOException("A hash file next to the output already exists.");
            }
        }

        var directory = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var sectorSize = device.SectorSize > 0 ? device.SectorSize : OpticalDrives.SectorSize;
        var total = device.Length;
        var sectorCount = (total + sectorSize - 1) / sectorSize;
        var bad = new List<long>();
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        var created = false;
        try
        {
            await using var output = new FileStream(full, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            created = true;
            var buffer = new byte[sectorSize];
            var stride = Math.Max(1, sectorCount / 100);
            for (long sector = 0; sector < sectorCount; sector++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var offset = sector * sectorSize;
                var count = (int)Math.Min(sectorSize, total - offset);
                if (!TryRead(device, buffer, offset, count, sector, options.Attempts))
                {
                    Array.Clear(buffer, 0, count);
                    bad.Add(sector);
                }

                var span = buffer.AsSpan(0, count);
                sha256.AppendData(span);
                sha1.AppendData(span);
                md5.AppendData(span);
                await output.WriteAsync(buffer.AsMemory(0, count), CancellationToken.None).ConfigureAwait(false);
                if (sector == sectorCount - 1 || sector % stride == 0)
                {
                    progress?.Report(new RipProgress(sector + 1, sectorCount, bad.Count, offset + count));
                }
            }

            await output.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            if (created)
            {
                try
                {
                    File.Delete(full);
                }
                catch (IOException)
                {
                }
            }

            throw;
        }

        var sha256Hex = Convert.ToHexString(sha256.GetHashAndReset()).ToLowerInvariant();
        var sha1Hex = Convert.ToHexString(sha1.GetHashAndReset()).ToLowerInvariant();
        var md5Hex = Convert.ToHexString(md5.GetHashAndReset()).ToLowerInvariant();
        var name = Path.GetFileName(full);
        await File.WriteAllTextAsync(sha256Path, $"{sha256Hex}  {name}{Environment.NewLine}").ConfigureAwait(false);
        await File.WriteAllTextAsync(sha1Path, $"{sha1Hex}  {name}{Environment.NewLine}").ConfigureAwait(false);
        await File.WriteAllTextAsync(md5Path, $"{md5Hex}  {name}{Environment.NewLine}").ConfigureAwait(false);
        string? writtenBad = null;
        if (bad.Count > 0)
        {
            writtenBad = badPath;
            var lines = bad.Select(sector => sector.ToString(CultureInfo.InvariantCulture));
            await File.WriteAllLinesAsync(badPath, lines).ConfigureAwait(false);
        }

        return new RipResult(full, total, sectorCount, bad, sha256Hex, sha1Hex, md5Hex, sha256Path, sha1Path, md5Path, writtenBad);
    }

    private static bool TryRead(IBlockDevice device, byte[] buffer, long offset, int count, long sector, int attempts)
    {
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                var read = device.Read(offset, buffer, 0, count);
                if (read != count)
                {
                    throw new IOException($"Short read at sector {sector}.");
                }

                return true;
            }
            catch (IOException) when (attempt < attempts)
            {
                continue;
            }
            catch (IOException)
            {
                return false;
            }
        }

        return false;
    }
}
