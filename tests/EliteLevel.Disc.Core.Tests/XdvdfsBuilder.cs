using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace EliteLevel.Disc.Core.Tests;

/// <summary>
/// Builds a minimal XDVDFS game partition for tests. The on-disk layout follows the public
/// volume-descriptor and directory-entry rules; it is not a copy of another project's writer.
/// </summary>
internal static class XdvdfsBuilder
{
    public const int SectorSize = 2048;

    public static byte[] BuildPartition(IReadOnlyList<(string Path, byte[] Data)> files)
    {
        var root = new Dir("");
        foreach (var (path, data) in files)
        {
            Add(root, path.Split('/', StringSplitOptions.RemoveEmptyEntries), data);
        }

        var nextSector = 33;
        Allocate(root, ref nextSector);
        var image = new byte[nextSector * SectorSize];
        WriteDirectory(image, root);
        WriteDescriptor(image, root);
        return image;
    }

    public static string WriteImage(string directory, string name, byte[] partition, long partitionBase, byte[]? prefix = null)
    {
        var path = Path.Combine(directory, name);
        using var stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
        TryMakeSparse(stream);
        stream.SetLength(partitionBase + partition.Length);
        if (prefix is { Length: > 0 })
        {
            stream.Position = 0;
            stream.Write(prefix);
        }

        stream.Position = partitionBase;
        stream.Write(partition);
        return path;
    }

    private static void Add(Dir dir, string[] parts, byte[] data)
    {
        if (parts.Length == 1)
        {
            dir.Files.Add(new FileEntry(parts[0], data));
            return;
        }

        var next = dir.Dirs.FirstOrDefault(item => item.Name.Equals(parts[0], StringComparison.OrdinalIgnoreCase));
        if (next is null)
        {
            next = new Dir(parts[0]);
            dir.Dirs.Add(next);
        }

        Add(next, parts[1..], data);
    }

    private static void Allocate(Dir dir, ref int nextSector)
    {
        foreach (var child in dir.Dirs)
        {
            Allocate(child, ref nextSector);
        }

        foreach (var file in dir.Files)
        {
            if (file.Data.Length == 0)
            {
                file.Sector = 0;
                continue;
            }

            file.Sector = (uint)nextSector;
            nextSector += Math.Max(1, (file.Data.Length + SectorSize - 1) / SectorSize);
        }

        if (dir.Dirs.Count == 0 && dir.Files.Count == 0)
        {
            dir.Sector = 0;
            dir.Table = [];
            return;
        }

        dir.Table = BuildTable(dir);
        dir.Sector = (uint)nextSector;
        nextSector += dir.Table.Length / SectorSize;
    }

    private static void WriteDirectory(byte[] image, Dir dir)
    {
        if (dir.Table.Length > 0)
        {
            dir.Table.CopyTo(image.AsSpan((int)dir.Sector * SectorSize));
        }

        foreach (var child in dir.Dirs)
        {
            WriteDirectory(image, child);
        }

        foreach (var file in dir.Files)
        {
            if (file.Data.Length == 0)
            {
                continue;
            }

            file.Data.CopyTo(image.AsSpan((int)file.Sector * SectorSize));
        }
    }

    private static void WriteDescriptor(byte[] image, Dir root)
    {
        var sector = image.AsSpan(32 * SectorSize, SectorSize);
        "MICROSOFT*XBOX*MEDIA"u8.CopyTo(sector);
        BinaryPrimitives.WriteUInt32LittleEndian(sector[0x14..], root.Sector);
        BinaryPrimitives.WriteUInt32LittleEndian(sector[0x18..], (uint)root.Table.Length);
        "MICROSOFT*XBOX*MEDIA"u8.CopyTo(sector[0x7EC..]);
    }

    private static byte[] BuildTable(Dir dir)
    {
        var nodes = new List<Node>();
        foreach (var child in dir.Dirs)
        {
            nodes.Add(new Node(child.Name, child.Sector, (uint)child.Table.Length, true));
        }

        foreach (var file in dir.Files)
        {
            nodes.Add(new Node(file.Name, file.Sector, (uint)file.Data.Length, false));
        }

        nodes.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        var root = MakeTree(nodes, 0, nodes.Count);
        if (root is null)
        {
            return [];
        }

        var cursor = 0;
        Place(root, ref cursor);
        var size = ((cursor + SectorSize - 1) / SectorSize) * SectorSize;
        var buffer = new byte[size];
        Array.Fill(buffer, (byte)0xFF);
        WriteNode(root, buffer);
        return buffer;
    }

    private static Node? MakeTree(List<Node> nodes, int start, int count)
    {
        if (count == 0)
        {
            return null;
        }

        var mid = count / 2;
        var node = nodes[start + mid];
        node.Left = MakeTree(nodes, start, mid);
        node.Right = MakeTree(nodes, start + mid + 1, count - mid - 1);
        return node;
    }

    private static void Place(Node node, ref int cursor)
    {
        var need = EntrySize(node.Name);
        var at = cursor % SectorSize;
        if (at != 0 && at + need > SectorSize)
        {
            cursor += SectorSize - at;
        }

        node.Offset = cursor;
        cursor += need;
        if (node.Left is not null)
        {
            Place(node.Left, ref cursor);
        }

        if (node.Right is not null)
        {
            Place(node.Right, ref cursor);
        }
    }

    private static void WriteNode(Node node, byte[] buffer)
    {
        var offset = node.Offset;
        var left = node.Left is null ? (ushort)0 : checked((ushort)(node.Left.Offset / 4));
        var right = node.Right is null ? (ushort)0 : checked((ushort)(node.Right.Offset / 4));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset), left);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset + 2), right);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset + 4), node.Sector);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset + 8), node.Size);
        buffer[offset + 12] = node.IsDirectory ? (byte)0x10 : (byte)0x01;
        buffer[offset + 13] = checked((byte)node.Name.Length);
        Encoding.Latin1.GetBytes(node.Name).CopyTo(buffer.AsSpan(offset + 14));
        if (node.Left is not null)
        {
            WriteNode(node.Left, buffer);
        }

        if (node.Right is not null)
        {
            WriteNode(node.Right, buffer);
        }
    }

    private static int EntrySize(string name) => (14 + name.Length + 3) & ~3;

    private static void TryMakeSparse(FileStream stream)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            _ = DeviceIoControl(stream.SafeFileHandle, 0x000900C4, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
        }
        catch (DllNotFoundException)
        {
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        Microsoft.Win32.SafeHandles.SafeFileHandle handle,
        uint controlCode,
        IntPtr inBuffer,
        uint inBufferSize,
        IntPtr outBuffer,
        uint outBufferSize,
        out uint bytesReturned,
        IntPtr overlapped);

    private sealed class Dir(string name)
    {
        public string Name { get; } = name;
        public List<Dir> Dirs { get; } = [];
        public List<FileEntry> Files { get; } = [];
        public uint Sector { get; set; }
        public byte[] Table { get; set; } = [];
    }

    private sealed class FileEntry(string name, byte[] data)
    {
        public string Name { get; } = name;
        public byte[] Data { get; } = data;
        public uint Sector { get; set; }
    }

    private sealed class Node(string name, uint sector, uint size, bool isDirectory)
    {
        public string Name { get; } = name;
        public uint Sector { get; } = sector;
        public uint Size { get; } = size;
        public bool IsDirectory { get; } = isDirectory;
        public Node? Left { get; set; }
        public Node? Right { get; set; }
        public int Offset { get; set; }
    }
}
