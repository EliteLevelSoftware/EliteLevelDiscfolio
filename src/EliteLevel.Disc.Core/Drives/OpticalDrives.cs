using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using EliteLevel.Disc.Core.Formats.Xdvdfs;

namespace EliteLevel.Disc.Core.Drives;

public sealed class DriveAccessException : UnauthorizedAccessException
{
    public const string Explanation =
        "Discfolio could not read this drive. On Windows, reading the disc directly needs an administrator. On Linux, your account needs permission to read the optical device, usually the cdrom group. Discfolio never writes to the drive.";

    public DriveAccessException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

public sealed class DriveNotFoundException : IOException
{
    public DriveNotFoundException(string id)
        : base($"No optical drive \"{id}\" with a disc was found.")
    {
    }
}

public enum DriveMedium
{
    Unknown = 0,
    NoDisc,
    Data,
    Audio,
    Mixed
}

public sealed record OpticalDrive(
    string Id,
    string DevicePath,
    string? VolumeLabel,
    long MediaBytes,
    bool MediaPresent,
    bool AccessDenied);

public sealed record DriveDescription(
    string Id,
    string DevicePath,
    string? VolumeLabel,
    long MediaBytes,
    string? Platform,
    string? FormatName,
    string? Notice,
    string? Error);

public interface IOpticalDriveSource
{
    IReadOnlyList<OpticalDrive> List();

    IBlockDevice Open(string id);

    DriveMedium Medium(string id);
}

public static class DriveNotices
{
    public const string PlayStation3 =
        "PlayStation 3 game data on a disc is encrypted. A normal drive can list the file system. Reading the game files needs a compatible drive and a disc key from a .ird file you supply. Load a .ird or a 16-byte key to attach it. This version does not decrypt the files.";

    public const string Xbox =
        "Xbox and Xbox 360 game partitions need a drive with flashed firmware. Discfolio is showing the video partition and anything else this drive can read.";

    public const string Audio =
        "This is an audio CD. Audio tracks are not supported yet.";

    public const string Unrecognized =
        "This disc does not look like a supported data disc. Audio CD tracks are not supported. Discfolio reads Mode 1 data tracks with 2048-byte sectors, ISO 9660, and the Xbox file system when the drive can see it.";

    public static string? ForImage(ImageIdentity identity, IBlockDevice device)
    {
        if (identity.Platform == "PlayStation 3")
        {
            return PlayStation3;
        }

        if (identity.FormatId == "iso9660" && GamePartitionHidden(device))
        {
            return Xbox;
        }

        return null;
    }

    /// <summary>
    /// A normal drive often errors when asked for the Xbox game partition and still returns the video partition.
    /// </summary>
    public static bool GamePartitionHidden(IBlockDevice device)
    {
        var sector = device.SectorSize > 0 ? device.SectorSize : OpticalDrives.SectorSize;
        foreach (var (_, partitionBase) in XdvdfsFormat.Layouts)
        {
            if (partitionBase <= 0)
            {
                continue;
            }

            var descriptor = partitionBase + XdvdfsFormat.DescriptorOffset;
            if (descriptor < 0 || descriptor + sector > device.Length)
            {
                continue;
            }

            try
            {
                var buffer = new byte[sector];
                if (device.Read(descriptor, buffer, 0, sector) != sector)
                {
                    return true;
                }
            }
            catch (IOException)
            {
                return true;
            }
        }

        return false;
    }
}

public static class DriveCacheKey
{
    public const int PrefixSectors = 32;

    public static string Create(string? volumeLabel, long size, ReadOnlySpan<byte> prefix)
    {
        var prefixHash = Convert.ToHexString(SHA256.HashData(prefix)).ToLowerInvariant();
        var label = (volumeLabel ?? "").Trim();
        var text = string.Create(CultureInfo.InvariantCulture, $"drive\n{label}\n{size}\n{prefixHash}");
        return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }
}

/// <summary>
/// Drive catalog used by tests. Each open returns a fresh reader over the same bytes.
/// </summary>
public sealed class MemoryDriveSource : IOpticalDriveSource
{
    private readonly object _gate = new();
    private readonly List<Entry> _entries = [];

    public void Add(OpticalDrive drive, MemoryBlockDevice device, DriveMedium medium = DriveMedium.Data)
    {
        ArgumentNullException.ThrowIfNull(drive);
        ArgumentNullException.ThrowIfNull(device);
        lock (_gate)
        {
            _entries.Add(new Entry(drive, device, medium));
        }
    }

    public void SetMediaPresent(string id, bool present)
    {
        lock (_gate)
        {
            var index = _entries.FindIndex(entry => OpticalDrives.SameId(entry.Drive.Id, id));
            if (index < 0)
            {
                return;
            }

            var entry = _entries[index];
            _entries[index] = entry with { Drive = entry.Drive with { MediaPresent = present } };
        }
    }

    public IReadOnlyList<OpticalDrive> List()
    {
        lock (_gate)
        {
            return _entries
                .Select(entry => entry.Drive)
                .Where(drive => drive.MediaPresent || drive.AccessDenied)
                .ToArray();
        }
    }

    public IBlockDevice Open(string id)
    {
        lock (_gate)
        {
            var entry = Find(id);
            if (entry is null || !entry.Drive.MediaPresent)
            {
                throw new DriveNotFoundException(OpticalDrives.Canonical(id));
            }

            if (entry.Drive.AccessDenied)
            {
                throw new DriveAccessException(DriveAccessException.Explanation);
            }

            return entry.Device.Clone();
        }
    }

    public DriveMedium Medium(string id)
    {
        lock (_gate)
        {
            var entry = Find(id);
            if (entry is null || !entry.Drive.MediaPresent)
            {
                return DriveMedium.NoDisc;
            }

            return entry.Medium;
        }
    }

    private Entry? Find(string id)
    {
        foreach (var entry in _entries)
        {
            if (OpticalDrives.SameId(entry.Drive.Id, id) || OpticalDrives.SameId(entry.Drive.DevicePath, id))
            {
                return entry;
            }
        }

        return null;
    }

    private sealed record Entry(OpticalDrive Drive, MemoryBlockDevice Device, DriveMedium Medium);
}

/// <summary>Lists optical drives and opens one as a read-only disc image.</summary>
public static class OpticalDrives
{
    public const int SectorSize = 2048;

    private static readonly object Gate = new();
    private static readonly SemaphoreSlim OverrideLock = new(1, 1);
    private static IOpticalDriveSource _source = new PlatformOpticalDriveSource();

    private static readonly Regex UnixDevice = new(
        @"^/dev/(sr\d+|scd\d+|cdrom|dvd|cdrw|dvdrw)$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex MacDisk = new(
        @"^/dev/r?disk\d+$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex CdRomNode = new(
        @"^\\\\\.[\\/]CdRom\d+$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    internal static IDisposable Override(IOpticalDriveSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        OverrideLock.Wait();
        IOpticalDriveSource previous;
        lock (Gate)
        {
            previous = _source;
            _source = source;
        }

        return new Restore(previous);
    }

    public static IReadOnlyList<OpticalDrive> List()
    {
        lock (Gate)
        {
            return Source.List();
        }
    }

    public static IBlockDevice Open(string id)
    {
        try
        {
            lock (Gate)
            {
                return Source.Open(id);
            }
        }
        catch (UnauthorizedAccessException ex) when (ex is not DriveAccessException)
        {
            throw new DriveAccessException(DriveAccessException.Explanation, ex);
        }
    }

    public static bool MediaPresent(string path)
    {
        if (!IsDrivePath(path))
        {
            return false;
        }

        try
        {
            var id = Canonical(path);
            return List().Any(drive =>
                (SameId(drive.Id, id) || SameId(drive.DevicePath, path))
                && (drive.MediaPresent || drive.AccessDenied));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static bool IsDrivePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        if (IsExplicit(path))
        {
            return true;
        }

        if (!MacDisk.IsMatch(path.Trim()))
        {
            return false;
        }

        try
        {
            return List().Any(drive => SameId(drive.Id, path) || SameId(drive.DevicePath, path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static string Canonical(string path)
    {
        var trimmed = path.Trim();
        if (TryWindowsLetter(trimmed, out var letter))
        {
            return letter;
        }

        if (CdRomNode.IsMatch(trimmed))
        {
            var split = trimmed.LastIndexOfAny(['\\', '/']);
            var node = split >= 0 ? trimmed[(split + 1)..] : trimmed;
            return @"\\.\" + node;
        }

        return trimmed;
    }

    public static bool SameId(string left, string right)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(Canonical(left), Canonical(right), comparison))
        {
            return true;
        }

        return string.Equals(ResolveDevice(left), ResolveDevice(right), comparison);
    }

    public static async Task<DriveDescription> DescribeAsync(OpticalDrive drive, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(drive);
        if (drive.AccessDenied)
        {
            return new DriveDescription(
                drive.Id,
                drive.DevicePath,
                drive.VolumeLabel,
                drive.MediaBytes,
                null,
                null,
                null,
                DriveAccessException.Explanation);
        }

        try
        {
            var image = await OpenImageAsync(drive.Id, new OpenOptions(), cancellationToken).ConfigureAwait(false);
            try
            {
                var identity = image.Identity;
                var bytes = drive.MediaBytes > 0 ? drive.MediaBytes : identity.TotalBytes;
                return new DriveDescription(
                    drive.Id,
                    drive.DevicePath,
                    identity.VolumeLabel ?? drive.VolumeLabel,
                    bytes,
                    identity.Platform,
                    identity.FormatDisplayName,
                    identity.Notice,
                    null);
            }
            finally
            {
                await image.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (DiscFormatException ex)
        {
            return new DriveDescription(
                drive.Id,
                drive.DevicePath,
                drive.VolumeLabel,
                drive.MediaBytes,
                null,
                null,
                ex.Message,
                null);
        }
        catch (DriveAccessException ex)
        {
            return new DriveDescription(drive.Id, drive.DevicePath, drive.VolumeLabel, drive.MediaBytes, null, null, null, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new DriveDescription(drive.Id, drive.DevicePath, drive.VolumeLabel, drive.MediaBytes, null, null, null, ex.Message);
        }
    }

    public static async Task<IDiscImage> OpenImageAsync(string path, OpenOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new OpenOptions();
        var id = Canonical(path);
        IBlockDevice device;
        try
        {
            device = Open(id);
        }
        catch (DriveNotFoundException)
        {
            throw;
        }
        catch (DriveAccessException)
        {
            throw;
        }

        IDiscImage? image = null;
        try
        {
            var stream = new SectorStream(device, leaveOpen: true);
            try
            {
                image = await DiscImages.OpenStreamAsync(stream, id, options, cancellationToken).ConfigureAwait(false);
            }
            catch (DiscFormatException ex)
            {
                throw ExplainFormat(id, ex);
            }
            catch (IOException ex) when (SafeMedium(id) == DriveMedium.Audio)
            {
                throw new DiscFormatException(DriveNotices.Audio, ex);
            }

            byte[] prefix;
            try
            {
                prefix = ReadPrefix(device);
            }
            catch (IOException)
            {
                prefix = [];
            }

            string? notice;
            try
            {
                notice = DriveNotices.ForImage(image.Identity, device);
            }
            catch (IOException)
            {
                notice = null;
            }

            var cacheKey = DriveCacheKey.Create(image.Identity.VolumeLabel, device.Length, prefix);
            var wrapped = new DriveImage(image, device, notice, cacheKey);
            image = null;
            return wrapped;
        }
        catch
        {
            if (image is not null)
            {
                await image.DisposeAsync().ConfigureAwait(false);
            }

            device.Dispose();
            throw;
        }
    }

    internal static Exception ExplainFormat(string id, DiscFormatException ex)
    {
        if (ex.Message == DiscFormatException.SwitchCompressed
            || ex.Message.StartsWith("This file looks like a Nintendo Switch", StringComparison.Ordinal)
            || ex.Message.StartsWith("This file has a Switch extension", StringComparison.Ordinal))
        {
            return ex;
        }

        if (SafeMedium(id) == DriveMedium.Audio)
        {
            return new DiscFormatException(DriveNotices.Audio, ex);
        }

        return new DiscFormatException(DriveNotices.Unrecognized, ex);
    }

    private static IOpticalDriveSource Source => _source;

    private static DriveMedium SafeMedium(string id)
    {
        try
        {
            lock (Gate)
            {
                return Source.Medium(id);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DriveMedium.Unknown;
        }
    }

    private static bool IsExplicit(string path)
    {
        var trimmed = path.Trim();
        return TryWindowsLetter(trimmed, out _) || UnixDevice.IsMatch(trimmed) || CdRomNode.IsMatch(trimmed);
    }

    private static bool TryWindowsLetter(string path, out string letter)
    {
        letter = "";
        var text = path.Trim().TrimEnd('\\', '/');
        if (text.StartsWith(@"\\.\", StringComparison.Ordinal) || text.StartsWith("//./", StringComparison.Ordinal))
        {
            text = text[4..];
        }

        if (text.Length == 2 && text[1] == ':' && char.IsAsciiLetter(text[0]))
        {
            letter = char.ToUpperInvariant(text[0]) + ":";
            return true;
        }

        return false;
    }

    private static string ResolveDevice(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var target = File.ResolveLinkTarget(full, returnFinalTarget: true);
            return target?.FullName ?? full;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException or UnauthorizedAccessException)
        {
            return path;
        }
    }

    private static byte[] ReadPrefix(IBlockDevice device)
    {
        var sector = Math.Max(1, device.SectorSize);
        var bytes = (int)Math.Min(device.Length, (long)DriveCacheKey.PrefixSectors * sector);
        if (bytes <= 0)
        {
            return [];
        }

        var buffer = new byte[bytes];
        var offset = 0;
        while (offset < bytes)
        {
            var count = Math.Min(sector, bytes - offset);
            var read = device.Read(offset, buffer, offset, count);
            if (read <= 0)
            {
                break;
            }

            offset += read;
        }

        return offset == bytes ? buffer : buffer[..offset];
    }

    private sealed class Restore : IDisposable
    {
        private IOpticalDriveSource? _previous;

        public Restore(IOpticalDriveSource previous) => _previous = previous;

        public void Dispose()
        {
            if (_previous is null)
            {
                return;
            }

            lock (Gate)
            {
                _source = _previous;
            }

            _previous = null;
            OverrideLock.Release();
        }
    }

    private sealed class DriveImage : IDiscImage
    {
        private readonly IDiscImage _inner;
        private readonly IBlockDevice _device;
        private bool _disposed;

        public DriveImage(IDiscImage inner, IBlockDevice device, string? notice, string cacheKey)
        {
            _inner = inner;
            _device = device;
            Identity = inner.Identity with
            {
                FromDrive = true,
                Notice = notice,
                CacheKey = cacheKey
            };
        }

        public ImageIdentity Identity { get; }

        public IDiscNode Root => _inner.Root;

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            await _inner.DisposeAsync().ConfigureAwait(false);
            _device.Dispose();
        }
    }
}
