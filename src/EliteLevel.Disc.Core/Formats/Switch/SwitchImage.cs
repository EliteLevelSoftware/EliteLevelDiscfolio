using System.Globalization;
using EliteLevel.Disc.Core;
using EliteLevel.Disc.Core.Formats;
using EliteLevel.Disc.Core.IO;

namespace EliteLevel.Disc.Core.Formats.Switch;

internal interface ISwitchTitle
{
    string? TitleId { get; }
}

/// <summary>
/// A Nintendo Switch NSP (PFS0) or XCI (game card plus root HFS0).
/// Container entries are listed without keys. An NCA becomes a folder of RomFS and ExeFS
/// only when the caller supplied keys that decrypt it.
/// </summary>
internal sealed class SwitchImage : IDiscImage, ISwitchTitle
{
    public const string KeysNeeded =
        "Nintendo Switch NCAs are encrypted with keys from your own console. Discfolio does not include keys. Set the prod.keys path in Settings, or pass --keys, to browse inside NCAs and read the title. Listing the container does not need keys.";

    public const string HeaderKeyMissing =
        "The keys file has no header_key, so NCA headers stay encrypted. Listing the container does not need keys.";

    public const string HeadersRejected =
        "Those keys did not decrypt the NCA headers. Check that prod.keys is from your own console. Listing the container does not need keys.";

    public const string SectionsLocked =
        "NCA headers were read, but the section keys are missing, so the files inside stay encrypted. Listing the container does not need keys.";

    public const string Ncz =
        "This image contains compressed NCZ entries. Decompression isn't supported yet.";

    private readonly Stream _source;
    private bool _disposed;

    private SwitchImage(Stream source, ImageIdentity identity, DiscNode root, string? titleId)
    {
        _source = source;
        Identity = identity;
        Root = root;
        TitleId = titleId;
    }

    public ImageIdentity Identity { get; }
    public IDiscNode Root { get; }
    public string? TitleId { get; }

    public static SwitchImage OpenPfs(Stream source, bool showSystem, SwitchKeys keys)
    {
        var gate = new object();
        var range = new StreamRange(source, gate, 0, source.Length);
        if (!PartitionFs.TryParse(range, 0, source.Length, hashed: false, out var entries, out _))
        {
            throw new DiscFormatException("This file looks like a Nintendo Switch NSP, but the PFS0 header is not valid.");
        }

        var scan = new Scan();
        var root = DiscNode.Directory(string.Empty, "/", showSystem);
        root.SetChildren(Files(range, entries, "/", showSystem, keys, scan));
        return Finish(source, root, "NSP", "PFS0", ["PFS0"], keys, scan);
    }

    public static SwitchImage OpenXci(Stream source, long rootOffset, bool showSystem, SwitchKeys keys)
    {
        var gate = new object();
        var range = new StreamRange(source, gate, 0, source.Length);
        if (!PartitionFs.TryParse(range, rootOffset, source.Length, hashed: true, out var entries, out _))
        {
            throw new DiscFormatException("This file looks like a Nintendo Switch game card, but it has no HFS0 partition table.");
        }

        var scan = new Scan();
        var partitions = new List<string>();
        var children = new List<DiscNode>();
        foreach (var entry in entries)
        {
            partitions.Add(entry.Name);
            var path = VirtualPath.Combine("/", entry.Name);
            var end = entry.Offset > long.MaxValue - entry.Size ? source.Length : Math.Min(source.Length, entry.Offset + entry.Size);
            if (entry.Size >= 0x10
                && PartitionFs.HasMagic(range, entry.Offset, "HFS0"u8)
                && PartitionFs.TryParse(range, entry.Offset, end, hashed: true, out var nested, out _))
            {
                var dir = DiscNode.Directory(entry.Name, path, showSystem);
                dir.SetChildren(Files(range, nested, path, showSystem, keys, scan));
                children.Add(dir);
                continue;
            }

            children.Add(Raw(range, entry, path, showSystem));
        }

        var root = DiscNode.Directory(string.Empty, "/", showSystem);
        root.SetChildren(children);
        var layout = partitions.Count == 0 ? "XCI" : string.Join(", ", partitions);
        return Finish(source, root, "XCI", layout, partitions, keys, scan);
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        _source.Dispose();
        return ValueTask.CompletedTask;
    }

    private static SwitchImage Finish(Stream source, DiscNode root, string display, string layout, IReadOnlyList<string> partitions, SwitchKeys keys, Scan scan)
    {
        var stats = NodeWalk.Measure(root);
        var identity = new ImageIdentity(
            "switch",
            display,
            null,
            "Nintendo Switch",
            layout,
            false,
            partitions,
            stats.Files,
            stats.Directories,
            stats.Bytes,
            stats.ProtectedFiles,
            "image",
            false,
            Notice(keys, scan));
        return new SwitchImage(source, identity, root, scan.BestTitleId);
    }

    private static string? Notice(SwitchKeys keys, Scan scan)
    {
        string? text = null;
        if (scan.NcaCount > 0 && scan.Expanded == 0)
        {
            if (!keys.Requested)
            {
                text = KeysNeeded;
            }
            else if (keys.Problem is not null && scan.Headers == 0)
            {
                text = keys.Problem;
            }
            else if (!keys.HasHeaderKey)
            {
                text = HeaderKeyMissing;
            }
            else if (scan.Headers == 0)
            {
                text = HeadersRejected;
            }
            else
            {
                text = SectionsLocked;
            }
        }

        if (scan.Ncz)
        {
            text = text is null ? Ncz : text + " " + Ncz;
        }

        return text;
    }

    private static List<DiscNode> Files(IByteSource source, IReadOnlyList<PartitionFs.Entry> entries, string parent, bool showSystem, SwitchKeys keys, Scan scan)
    {
        var children = new List<DiscNode>();
        foreach (var entry in entries)
        {
            var path = VirtualPath.Combine(parent, entry.Name);
            if (entry.Name.EndsWith(".ncz", StringComparison.OrdinalIgnoreCase))
            {
                scan.Ncz = true;
                children.Add(Raw(source, entry, path, showSystem));
                continue;
            }

            if (!entry.Name.EndsWith(".nca", StringComparison.OrdinalIgnoreCase))
            {
                children.Add(Raw(source, entry, path, showSystem));
                continue;
            }

            scan.NcaCount++;
            if (entry.Size < Nca.HeaderSize || entry.Offset < 0 || entry.Offset > source.Length || entry.Size > source.Length - entry.Offset)
            {
                children.Add(Raw(source, entry, path, showSystem));
                continue;
            }

            var nca = new SourceWindow(source, entry.Offset, entry.Size);
            if (!Nca.TryOpen(nca, keys, out var header))
            {
                children.Add(Raw(source, entry, path, showSystem));
                continue;
            }

            scan.Headers++;
            scan.Consider(Nca.ReadProgramId(header), header[Nca.ContentType]);
            var sections = Nca.Expand(nca, header, keys, path, showSystem);
            if (sections.Count == 0)
            {
                children.Add(Raw(source, entry, path, showSystem));
                continue;
            }

            scan.Expanded++;
            var folder = DiscNode.Directory(entry.Name, path, showSystem);
            folder.SetChildren(sections);
            children.Add(folder);
        }

        return children;
    }

    private static DiscNode Raw(IByteSource source, PartitionFs.Entry entry, string path, bool showSystem)
    {
        var start = entry.Offset;
        var size = entry.Size;
        return DiscNode.File(entry.Name, path, size, showSystem, () => new ByteSourceStream(source, start, size));
    }

    private sealed class Scan
    {
        private int _rank = -1;

        public int NcaCount { get; set; }
        public int Headers { get; set; }
        public int Expanded { get; set; }
        public bool Ncz { get; set; }
        public string? BestTitleId { get; private set; }

        public void Consider(ulong programId, byte contentType)
        {
            if (programId == 0 || programId == ulong.MaxValue)
            {
                return;
            }

            var rank = contentType switch
            {
                2 => 3,
                0 => 2,
                _ => 1
            };
            if (rank <= _rank)
            {
                return;
            }

            _rank = rank;
            BestTitleId = programId.ToString("X16", CultureInfo.InvariantCulture);
        }
    }
}
