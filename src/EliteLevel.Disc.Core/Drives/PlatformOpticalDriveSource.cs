using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.Text;
using System.Text.RegularExpressions;

namespace EliteLevel.Disc.Core.Drives;

/// <summary>
/// Optical drives on this machine. Every handle is opened read-only.
/// Windows uses the drive letter (<c>\\.\D:</c>) and DeviceIoControl for the size.
/// Linux uses <c>/dev/sr*</c> and <c>/dev/cdrom</c>. macOS checks <c>/dev/rdiskN</c> only when diskutil says it is optical.
/// </summary>
public sealed class PlatformOpticalDriveSource : IOpticalDriveSource
{
    public IReadOnlyList<OpticalDrive> List()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return ListWindows();
            }

            if (OperatingSystem.IsLinux())
            {
                return ListLinux();
            }

            if (OperatingSystem.IsMacOS())
            {
                return ListMac();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
        }

        return [];
    }

    public IBlockDevice Open(string id)
    {
        var match = Find(id);
        if (match is null)
        {
            if (OperatingSystem.IsWindows() && CdRomNode(id) is string node)
            {
                try
                {
                    return OpenPath(node, node, 0);
                }
                catch (UnauthorizedAccessException ex) when (ex is not DriveAccessException)
                {
                    throw new DriveAccessException(DriveAccessException.Explanation, ex);
                }
                catch (IOException)
                {
                    throw new DriveNotFoundException(id);
                }
            }

            throw new DriveNotFoundException(OpticalDrives.Canonical(id));
        }

        if (match.AccessDenied)
        {
            throw new DriveAccessException(DriveAccessException.Explanation);
        }

        try
        {
            return OpenPath(match.DevicePath, match.Id, match.MediaBytes);
        }
        catch (UnauthorizedAccessException ex) when (ex is not DriveAccessException)
        {
            throw new DriveAccessException(DriveAccessException.Explanation, ex);
        }
    }

    public DriveMedium Medium(string id)
    {
        var match = Find(id);
        if (match is null || match.AccessDenied)
        {
            return DriveMedium.Unknown;
        }

        try
        {
            using var stream = OpenRead(match.DevicePath);
            if (OperatingSystem.IsLinux())
            {
                return LinuxMedium(stream.SafeFileHandle);
            }

            if (OperatingSystem.IsWindows())
            {
                return WindowsMedium(stream.SafeFileHandle);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return DriveMedium.Unknown;
    }

    private OpticalDrive? Find(string id)
    {
        foreach (var drive in List())
        {
            if (OpticalDrives.SameId(drive.Id, id) || OpticalDrives.SameId(drive.DevicePath, id))
            {
                return drive;
            }
        }

        return null;
    }

    private static IBlockDevice OpenPath(string devicePath, string id, long knownLength)
    {
        var stream = OpenRead(devicePath);
        try
        {
            var length = QueryLength(stream);
            if (length <= 0)
            {
                length = knownLength;
            }

            var device = new FileBlockDevice(id, stream, length);
            stream = null;
            return device;
        }
        finally
        {
            stream?.Dispose();
        }
    }

    private static FileStream OpenRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

    private static List<OpticalDrive> ListWindows()
    {
        var result = new List<OpticalDrive>();
        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch (IOException)
        {
            return result;
        }

        foreach (var info in drives)
        {
            try
            {
                if (info.DriveType != DriveType.CDRom)
                {
                    continue;
                }

                var id = info.Name.TrimEnd('\\', '/');
                if (id.Length < 2 || id[1] != ':')
                {
                    continue;
                }

                var devicePath = @"\\.\" + id[..2];
                var present = false;
                var denied = false;
                string? label = null;
                long bytes = 0;
                try
                {
                    present = info.IsReady;
                    if (present)
                    {
                        label = string.IsNullOrWhiteSpace(info.VolumeLabel) ? null : info.VolumeLabel.Trim();
                        bytes = info.TotalSize;
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    denied = true;
                    present = true;
                }
                catch (IOException)
                {
                    present = false;
                }

                if (present)
                {
                    try
                    {
                        using var stream = OpenRead(devicePath);
                        var raw = QueryLength(stream);
                        if (raw > 0)
                        {
                            bytes = raw;
                        }

                        label ??= IsoLabel(stream, raw > 0 ? raw : bytes);
                    }
                    catch (UnauthorizedAccessException)
                    {
                        denied = true;
                    }
                    catch (IOException)
                    {
                    }
                }

                if (!present && !denied)
                {
                    continue;
                }

                result.Add(new OpticalDrive(id[..2].ToUpperInvariant(), devicePath, label, bytes, true, denied));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
            }
        }

        return result;
    }

    private static List<OpticalDrive> ListLinux()
    {
        var paths = new List<string>();
        if (Directory.Exists("/dev"))
        {
            try
            {
                paths.AddRange(Directory.EnumerateFiles("/dev", "sr*"));
                paths.AddRange(Directory.EnumerateFiles("/dev", "scd*"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        foreach (var link in new[] { "/dev/cdrom", "/dev/dvd", "/dev/cdrw", "/dev/dvdrw" })
        {
            if (File.Exists(link))
            {
                paths.Add(link);
            }
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<OpticalDrive>();
        foreach (var path in paths)
        {
            var full = Resolve(path);
            if (!seen.Add(full))
            {
                continue;
            }

            var drive = ProbeLinux(full);
            if (drive is not null)
            {
                result.Add(drive);
            }
        }

        return result;
    }

    private static OpticalDrive? ProbeLinux(string path)
    {
        try
        {
            using var stream = OpenRead(path);
            var status = LinuxDriveStatus(stream.SafeFileHandle);
            if (status is CdsNoDisc or CdsTrayOpen or CdsNotReady)
            {
                return null;
            }

            var length = QueryLength(stream);
            var present = status == CdsDiscOk || length > 0;
            if (!present)
            {
                return null;
            }

            var label = IsoLabel(stream, length);
            return new OpticalDrive(path, path, label, length, true, false);
        }
        catch (UnauthorizedAccessException)
        {
            return new OpticalDrive(path, path, null, 0, true, true);
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException)
        {
            return null;
        }
    }

    private static List<OpticalDrive> ListMac()
    {
        var listing = Run("diskutil", ["list"]);
        if (listing is null)
        {
            return [];
        }

        var result = new List<OpticalDrive>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(listing, @"/dev/disk\d+(?![\ds])"))
        {
            if (!seen.Add(match.Value) || seen.Count > 16)
            {
                continue;
            }

            var info = Run("diskutil", ["info", match.Value]);
            if (info is null || !IsOpticalInfo(info))
            {
                continue;
            }

            var bytes = DiskBytes(info);
            var label = DiskLabel(info);
            if (bytes <= 0 && label is null)
            {
                continue;
            }

            var raw = "/dev/r" + match.Value["/dev/".Length..];
            var node = File.Exists(raw) ? raw : match.Value;
            result.Add(new OpticalDrive(node, node, label, bytes, true, false));
        }

        return result;
    }

    private static bool IsOpticalInfo(string info)
    {
        var media = Line(info, "Optical Media Type") ?? Line(info, "Media Type") ?? "";
        if (media.Contains("CD", StringComparison.OrdinalIgnoreCase)
            || media.Contains("DVD", StringComparison.OrdinalIgnoreCase)
            || media.Contains("BD", StringComparison.OrdinalIgnoreCase)
            || media.Contains("Blu-ray", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return info.Contains("Optical Device: Yes", StringComparison.OrdinalIgnoreCase);
    }

    private static long DiskBytes(string info)
    {
        var size = Line(info, "Disk Size") ?? "";
        var match = Regex.Match(size, @"\((\d+) Bytes\)");
        return match.Success && long.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var bytes)
            ? bytes
            : 0;
    }

    private static string? DiskLabel(string info)
    {
        var name = Line(info, "Volume Name");
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        if (name.Equals("Not applicable", StringComparison.OrdinalIgnoreCase)
            || name.Equals("(null)", StringComparison.OrdinalIgnoreCase)
            || name.Equals("None", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return name.Trim();
    }

    private static string? Line(string info, string label)
    {
        foreach (var raw in info.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith(label, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var split = line.IndexOf(':');
            if (split < 0 || split + 1 >= line.Length)
            {
                return null;
            }

            return line[(split + 1)..].Trim();
        }

        return null;
    }

    private static string? Run(string file, IReadOnlyList<string> args)
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = file,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var arg in args)
            {
                process.StartInfo.ArgumentList.Add(arg);
            }

            if (!process.Start())
            {
                return null;
            }

            if (!process.WaitForExit(4000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
                {
                }

                return null;
            }

            return process.StandardOutput.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or PlatformNotSupportedException)
        {
            return null;
        }
    }

    private static long QueryLength(FileStream stream)
    {
        try
        {
            if (stream.Length > 0)
            {
                return stream.Length;
            }
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException)
        {
        }

        if (OperatingSystem.IsWindows())
        {
            var windows = WindowsLength(stream.SafeFileHandle);
            if (windows > 0)
            {
                return windows;
            }
        }

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            var size = BlkGetSize(stream.SafeFileHandle);
            if (size > 0 && size < long.MaxValue)
            {
                return (long)size;
            }
        }

        return 0;
    }

    private static string? IsoLabel(FileStream stream, long length)
    {
        if (length < 17L * OpticalDrives.SectorSize)
        {
            return null;
        }

        try
        {
            var sector = new byte[OpticalDrives.SectorSize];
            stream.Position = 16L * OpticalDrives.SectorSize;
            var read = stream.Read(sector, 0, sector.Length);
            stream.Position = 0;
            if (read < 64 || sector[0] != 1 || Encoding.ASCII.GetString(sector, 1, 5) != "CD001")
            {
                return null;
            }

            var label = Encoding.ASCII.GetString(sector, 40, 32).Trim().TrimEnd('\0', ' ');
            return label.Length == 0 ? null : label;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static string Resolve(string path)
    {
        try
        {
            var target = File.ResolveLinkTarget(path, returnFinalTarget: true);
            return target?.FullName ?? path;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException or UnauthorizedAccessException)
        {
            return path;
        }
    }

    private static string? CdRomNode(string id)
    {
        var trimmed = id.Trim();
        if (!Regex.IsMatch(trimmed, @"^\\\\\.[\\/]CdRom\d+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return null;
        }

        var split = trimmed.LastIndexOfAny(['\\', '/']);
        var node = split >= 0 ? trimmed[(split + 1)..] : trimmed;
        return @"\\.\" + node;
    }

    private static int LinuxDriveStatus(SafeFileHandle handle)
    {
        var status = WithFd(handle, fd => IoctlArg(fd, CdromDriveStatus, CdslCurrent));
        return status;
    }

    private static DriveMedium LinuxMedium(SafeFileHandle handle)
    {
        var status = WithFd(handle, fd => IoctlArg(fd, CdromDiscStatus, CdslCurrent));
        return status switch
        {
            CdsAudio => DriveMedium.Audio,
            CdsMixed => DriveMedium.Mixed,
            >= 101 and <= 104 => DriveMedium.Data,
            CdsNoDisc or CdsTrayOpen => DriveMedium.NoDisc,
            _ => DriveMedium.Unknown
        };
    }

    private static DriveMedium WindowsMedium(SafeFileHandle handle)
    {
        var toc = new byte[804];
        var pointer = Marshal.AllocHGlobal(toc.Length);
        try
        {
            if (!DeviceIoControl(handle, IoctlCdromReadToc, IntPtr.Zero, 0, pointer, (uint)toc.Length, out _, IntPtr.Zero))
            {
                return DriveMedium.Unknown;
            }

            Marshal.Copy(pointer, toc, 0, toc.Length);
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }

        var first = toc[2];
        var last = toc[3];
        if (last < first || last - first > 99)
        {
            return DriveMedium.Unknown;
        }

        var audio = false;
        var data = false;
        for (var track = first; track <= last; track++)
        {
            var offset = 4 + (track - first) * 8;
            if (offset + 1 >= toc.Length)
            {
                break;
            }

            var control = toc[offset + 1] & 0x0F;
            if ((control & 0x04) != 0)
            {
                data = true;
            }
            else
            {
                audio = true;
            }
        }

        if (data && audio)
        {
            return DriveMedium.Mixed;
        }

        if (data)
        {
            return DriveMedium.Data;
        }

        if (audio)
        {
            return DriveMedium.Audio;
        }

        return DriveMedium.Unknown;
    }

    private static long WindowsLength(SafeFileHandle handle)
    {
        var pointer = Marshal.AllocHGlobal(32);
        try
        {
            if (DeviceIoControl(handle, IoctlDiskGetLength, IntPtr.Zero, 0, pointer, 8, out var returned, IntPtr.Zero)
                && returned >= 8)
            {
                var length = Marshal.ReadInt64(pointer);
                if (length > 0)
                {
                    return length;
                }
            }

            if (!DeviceIoControl(handle, IoctlCdromGeometry, IntPtr.Zero, 0, pointer, 24, out returned, IntPtr.Zero) || returned < 24)
            {
                return 0;
            }

            var cylinders = Marshal.ReadInt64(pointer);
            var tracks = Marshal.ReadInt32(pointer, 12);
            var sectors = Marshal.ReadInt32(pointer, 16);
            var bytesPerSector = Marshal.ReadInt32(pointer, 20);
            if (cylinders <= 0 || tracks <= 0 || sectors <= 0 || bytesPerSector <= 0)
            {
                return 0;
            }

            return cylinders * tracks * sectors * bytesPerSector;
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    private static ulong BlkGetSize(SafeFileHandle handle)
    {
        try
        {
            return WithFd(handle, fd =>
            {
                var result = IoctlSize(fd, BlkGetSize64, out var size);
                return result < 0 ? 0UL : size;
            });
        }
        catch (DllNotFoundException)
        {
            return 0;
        }
    }

    private static T WithFd<T>(SafeFileHandle handle, Func<int, T> action)
    {
        var added = false;
        try
        {
            handle.DangerousAddRef(ref added);
            return action((int)handle.DangerousGetHandle());
        }
        finally
        {
            if (added)
            {
                handle.DangerousRelease();
            }
        }
    }

    private const uint IoctlCdromReadToc = 0x00024000;
    private const uint IoctlCdromGeometry = 0x0002404C;
    private const uint IoctlDiskGetLength = 0x0007405C;
    private const nuint CdromDriveStatus = 0x5326;
    private const nuint CdromDiscStatus = 0x5327;
    private const nuint BlkGetSize64 = 0x80081272;
    private const nint CdslCurrent = int.MaxValue;
    private const int CdsNoDisc = 1;
    private const int CdsTrayOpen = 2;
    private const int CdsNotReady = 3;
    private const int CdsDiscOk = 4;
    private const int CdsAudio = 100;
    private const int CdsMixed = 105;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device,
        uint controlCode,
        IntPtr inBuffer,
        uint inBufferSize,
        IntPtr outBuffer,
        uint outBufferSize,
        out uint bytesReturned,
        IntPtr overlapped);

    [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static extern int IoctlArg(int fd, nuint request, nint argument);

    [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static extern int IoctlSize(int fd, nuint request, out ulong size);
}

internal sealed class FileBlockDevice : IBlockDevice
{
    private readonly FileStream _stream;
    private readonly object _gate = new();
    private bool _disposed;

    public FileBlockDevice(string id, FileStream stream, long length)
    {
        Id = id;
        _stream = stream;
        Length = length < 0 ? 0 : length;
    }

    public string Id { get; }

    public long Length { get; }

    public int SectorSize => OpticalDrives.SectorSize;

    public int Read(long offset, byte[] buffer, int bufferOffset, int count)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(buffer);
        if (offset < 0 || bufferOffset < 0 || count < 0 || bufferOffset > buffer.Length - count)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        if (count == 0 || offset >= Length)
        {
            return 0;
        }

        var available = (int)Math.Min(count, Length - offset);
        lock (_gate)
        {
            _stream.Position = offset;
            var read = 0;
            while (read < available)
            {
                var n = _stream.Read(buffer, bufferOffset + read, available - read);
                if (n == 0)
                {
                    break;
                }

                read += n;
            }

            if (read < available)
            {
                throw new IOException($"Read error at offset {offset}.");
            }

            return read;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stream.Dispose();
    }
}
