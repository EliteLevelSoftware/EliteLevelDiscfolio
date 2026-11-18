using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;
using EliteLevel.Disc.Core;
using EliteLevel.Disc.Core.Drives;
using EliteLevel.Disc.Core.Extract;
using EliteLevel.Disc.Core.Hash;
using EliteLevel.Disc.Core.IO;
using EliteLevel.Disc.Core.Search;
using EliteLevel.Disc.Core.Formats.Switch;
using EliteLevel.Disc.Core.Titles;

namespace EliteLevel.Disc.Cli;

public static class CliHost
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly AsyncLocal<bool> JsonOutput = new();
    private static readonly AsyncLocal<DiscKey?> LoadedKey = new();
    private static readonly AsyncLocal<string?> KeysPath = new();
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    public static async Task<int> RunAsync(string[] args, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var previousOut = Console.Out;
        var previousError = Console.Error;
        var previousJson = JsonOutput.Value;
        Console.SetOut(stdout);
        Console.SetError(stderr);
        JsonOutput.Value = HasJsonFlag(args);
        try
        {
            var parsed = Build().Parse(args);
            if (JsonOutput.Value && parsed.Errors.Count > 0)
            {
                var message = string.Join(Environment.NewLine, parsed.Errors.Select(error => error.Message));
                return WriteError(1, "user", message);
            }

            return await parsed.InvokeAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            JsonOutput.Value = previousJson;
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Gate.Release();
        }
    }

    private static RootCommand Build()
    {
        var image = new Argument<string>("image") { Description = "Disc image, folder dump, or optical drive (D: or /dev/sr0)" };
        var json = new Option<bool>("--json") { Description = "Print JSON" };
        var path = new Option<string>("--path") { Description = "Directory or file inside the image" };
        var showSystem = new Option<bool>("--show-system") { Description = "Show system-update folders" };
        var longer = new Option<bool>("--long", "-l") { Description = "Print size and kind" };
        var output = new Option<string>("--out") { Description = "Destination directory" };
        var pattern = new Option<string>("--pattern") { Description = "Only extract names matching this glob" };
        var exclude = new Option<string>("--exclude") { Description = "Skip names matching this glob" };
        var skipExisting = new Option<bool>("--skip-existing") { Description = "Keep a destination file when its size matches" };
        var includeProtected = new Option<bool>("--include-protected") { Description = "Also copy protected paths, still encrypted and unchanged" };
        var decompress = new Option<bool>("--decompress") { Description = "Write an NCZ entry as the decompressed NCA" };
        var algo = new Option<string>("--algo") { Description = "Comma-separated hashes: sha256, sha1, md5", DefaultValueFactory = _ => "sha256" };
        var manifest = new Option<string>("--manifest") { Description = "Write a hash manifest to this file" };
        var searchPattern = new Argument<string>("pattern") { Description = "Name or glob to find" };
        var discKey = new Option<string>("--disc-key") { Description = "A .ird file or a 16-byte disc key. The key is not printed." };
        var keys = new Option<string>("--keys") { Description = "Path to prod.keys, or a folder containing it. title.keys beside it is used too. Keys are not printed." };

        var info = new Command("info", "Describe an image");
        info.Arguments.Add(image);
        info.Options.Add(json);
        info.Options.Add(showSystem);
        info.Options.Add(discKey);
        info.Options.Add(keys);
        info.SetAction((result, token) => WithDiscKeyAsync(
            result.GetValue(discKey),
            () => WithKeysAsync(
                result.GetValue(keys),
                () => InfoAsync(result.GetValue(image)!, result.GetValue(json), result.GetValue(showSystem), token))));

        var list = new Command("list", "List one directory");
        list.Arguments.Add(image);
        list.Options.Add(path);
        list.Options.Add(json);
        list.Options.Add(longer);
        list.Options.Add(showSystem);
        list.Options.Add(keys);
        list.Aliases.Add("ls");
        list.SetAction((result, token) => WithKeysAsync(
            result.GetValue(keys),
            () => ListAsync(
            result.GetValue(image)!,
            result.GetValue(path),
            result.GetValue(json),
            result.GetValue(longer),
            result.GetValue(showSystem),
            token)));

        var tree = new Command("tree", "Print the directory tree");
        tree.Arguments.Add(image);
        tree.Options.Add(path);
        tree.Options.Add(showSystem);
        tree.Options.Add(keys);
        tree.SetAction((result, token) => WithKeysAsync(
            result.GetValue(keys),
            () => TreeAsync(result.GetValue(image)!, result.GetValue(path), result.GetValue(showSystem), token)));

        var extract = new Command("extract", "Copy files out of an image");
        extract.Arguments.Add(image);
        extract.Options.Add(output);
        extract.Options.Add(path);
        extract.Options.Add(pattern);
        extract.Options.Add(exclude);
        extract.Options.Add(skipExisting);
        extract.Options.Add(includeProtected);
        extract.Options.Add(decompress);
        extract.Options.Add(showSystem);
        extract.Options.Add(json);
        extract.Options.Add(discKey);
        extract.Options.Add(keys);
        extract.SetAction((result, token) => WithDiscKeyAsync(
            result.GetValue(discKey),
            () => WithKeysAsync(
                result.GetValue(keys),
                () => ExtractAsync(
            result.GetValue(image)!,
            result.GetValue(output),
            result.GetValue(path),
            result.GetValue(pattern),
            result.GetValue(exclude),
            result.GetValue(skipExisting),
            result.GetValue(includeProtected),
            result.GetValue(decompress),
            result.GetValue(showSystem),
            result.GetValue(json),
            token))));

        var hash = new Command("hash", "Compute SHA-256, SHA-1 or MD5");
        hash.Arguments.Add(image);
        hash.Options.Add(path);
        hash.Options.Add(algo);
        hash.Options.Add(manifest);
        hash.Options.Add(json);
        hash.Options.Add(showSystem);
        hash.Options.Add(keys);
        hash.SetAction((result, token) => WithKeysAsync(
            result.GetValue(keys),
            () => HashAsync(
            result.GetValue(image)!,
            result.GetValue(path),
            result.GetValue(algo),
            result.GetValue(manifest),
            result.GetValue(json),
            result.GetValue(showSystem),
            token)));

        var search = new Command("search", "Find entries by name");
        search.Arguments.Add(image);
        search.Arguments.Add(searchPattern);
        search.Options.Add(json);
        search.Options.Add(showSystem);
        search.Options.Add(keys);
        search.SetAction((result, token) => WithKeysAsync(
            result.GetValue(keys),
            () => SearchAsync(
            result.GetValue(image)!,
            result.GetValue(searchPattern)!,
            result.GetValue(json),
            result.GetValue(showSystem),
            token)));

        var detect = new Command("detect", "Print the detected format");
        detect.Arguments.Add(image);
        detect.Options.Add(json);
        detect.Options.Add(keys);
        detect.SetAction((result, token) => WithKeysAsync(
            result.GetValue(keys),
            () => DetectAsync(result.GetValue(image)!, result.GetValue(json), token)));

        var artOut = new Option<string>("--out") { Description = "Folder to write the icon and banner into" };
        var art = new Command("art", "Export the title icon and banner");
        art.Arguments.Add(image);
        art.Options.Add(artOut);
        art.Options.Add(json);
        art.Options.Add(discKey);
        art.Options.Add(keys);
        art.SetAction((result, token) => WithDiscKeyAsync(
            result.GetValue(discKey),
            () => WithKeysAsync(
                result.GetValue(keys),
                () => ArtAsync(result.GetValue(image)!, result.GetValue(artOut), result.GetValue(json), token))));

        var drives = new Command("drives", "List optical drives that have a disc");
        drives.Options.Add(json);
        drives.SetAction((result, token) => DrivesAsync(result.GetValue(json), token));

        var drive = new Argument<string>("drive") { Description = "Optical drive, such as D: or /dev/sr0" };
        var ripOut = new Option<string>("--out") { Description = "ISO file to write" };
        var retries = new Option<int>("--retries")
        {
            Description = "Read attempts per sector before it is reported as bad",
            DefaultValueFactory = _ => IsoRipper.DefaultAttempts
        };
        var rip = new Command("rip", "Copy a disc to an ISO and write SHA-256, SHA-1 and MD5");
        rip.Arguments.Add(drive);
        rip.Options.Add(ripOut);
        rip.Options.Add(retries);
        rip.Options.Add(json);
        rip.SetAction((result, token) => RipAsync(
            result.GetValue(drive)!,
            result.GetValue(ripOut),
            result.GetValue(retries),
            result.GetValue(json),
            token));

        var convertOut = new Option<string>("--out") { Description = "Decompressed NSP or XCI to write" };
        var convert = new Command("convert", "Write an NSZ or XCZ as a decompressed NSP or XCI");
        convert.Arguments.Add(image);
        convert.Options.Add(convertOut);
        convert.Options.Add(json);
        convert.Options.Add(keys);
        convert.SetAction((result, token) => WithKeysAsync(
            result.GetValue(keys),
            () => ConvertAsync(result.GetValue(image)!, result.GetValue(convertOut), result.GetValue(json), token)));

        var root = new RootCommand("EliteLevel Discfolio. Read-only explorer for disc images, folder dumps, and optical drives.");
        root.Subcommands.Add(info);
        root.Subcommands.Add(list);
        root.Subcommands.Add(tree);
        root.Subcommands.Add(extract);
        root.Subcommands.Add(hash);
        root.Subcommands.Add(search);
        root.Subcommands.Add(detect);
        root.Subcommands.Add(art);
        root.Subcommands.Add(drives);
        root.Subcommands.Add(rip);
        root.Subcommands.Add(convert);
        return root;
    }

    private static async Task<int> InfoAsync(string image, bool json, bool showSystem, CancellationToken cancellationToken)
    {
        return await WithImageAsync(image, showSystem, async (disc, _) =>
        {
            var id = disc.Identity;
            var meta = TitleMetadataReader.Read(disc);
            var title = TitleMetadataReader.ReadTitle(meta) ?? id.VolumeLabel ?? id.SourceName;
            if (json)
            {
                WriteJson(new
                {
                    format = id.FormatId,
                    formatName = id.FormatDisplayName,
                    volumeLabel = id.VolumeLabel,
                    title,
                    titleId = meta.TitleId,
                    version = meta.Version,
                    category = meta.Category,
                    parentalLevel = meta.ParentalLevel,
                    platform = id.Platform,
                    layout = id.Layout,
                    videoPartition = id.VideoPartition,
                    partitions = id.Partitions,
                    files = id.FileCount,
                    @protected = id.ProtectedCount,
                    directories = id.DirectoryCount,
                    bytes = id.TotalBytes,
                    source = id.SourceName,
                    icon = meta.IconName,
                    banner = meta.BannerName,
                    fromDrive = id.FromDrive ? (bool?)true : null,
                    notice = id.Notice,
                    discKeyLoaded = LoadedKey.Value is null ? null : (bool?)true,
                    keysLoaded = KeysPath.Value is null ? null : (bool?)true
                });
            }
            else
            {
                Console.WriteLine(title);
                Line("Format", id.FormatDisplayName);
                Line("Platform", id.Platform ?? "Unknown");
                if (!string.IsNullOrWhiteSpace(id.VolumeLabel) && !id.VolumeLabel.Equals(title, StringComparison.Ordinal))
                {
                    Line("Volume", id.VolumeLabel);
                }

                if (!string.IsNullOrWhiteSpace(meta.TitleId))
                {
                    Line("Title ID", meta.TitleId);
                }

                if (!string.IsNullOrWhiteSpace(meta.Version))
                {
                    Line("Version", meta.Version);
                }

                if (!string.IsNullOrWhiteSpace(meta.Category))
                {
                    Line("Category", meta.Category);
                }

                if (meta.ParentalLevel is int parental)
                {
                    Line("Parental", parental.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }

                Line("Art", ArtSummary(meta));
                Line("Layout", id.Layout ?? "Single partition");
                Line("Files", TextFormat.FileSummary(id.FileCount, id.ProtectedCount));
                Line("Directories", TextFormat.Count(id.DirectoryCount));
                Line("Size", ByteSize.Format(id.TotalBytes));
                Line("Source", id.SourceName);
                if (id.FromDrive)
                {
                    Line("Drive", "Optical drive");
                }

                if (LoadedKey.Value is not null)
                {
                    Line("Disc key", "loaded");
                }

                if (KeysPath.Value is not null)
                {
                    Line("Keys", "loaded");
                }

                if (!string.IsNullOrWhiteSpace(id.Notice))
                {
                    Console.WriteLine(id.Notice);
                }
            }

            return 0;
        }, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> ListAsync(string image, string? path, bool json, bool longer, bool showSystem, CancellationToken cancellationToken)
    {
        return await WithImageAsync(image, showSystem, async (disc, _) =>
        {
            var node = Resolve(disc.Root, path);
            if (!node.IsDirectory)
            {
                return WriteError(1, "user", "The path is a file. Pass the directory that contains it.");
            }

            if (json)
            {
                WriteJson(new
                {
                    path = node.FullPath,
                    entries = node.Children.Select(Entry).ToArray()
                });
            }
            else
            {
                foreach (var child in node.Children)
                {
                    if (longer)
                    {
                        var kind = child.IsDirectory ? "directory" : child.Kind.ToString().ToLowerInvariant();
                        var mark = child.IsProtected ? "  skipped on extract" : string.Empty;
                        Console.WriteLine($"{ByteSize.Format(child.Size),10}  {kind,-12}  {child.Name}{mark}");
                    }
                    else
                    {
                        Console.WriteLine(child.IsDirectory ? child.Name + "/" : child.Name);
                    }
                }
            }

            return 0;
        }, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> TreeAsync(string image, string? path, bool showSystem, CancellationToken cancellationToken)
    {
        return await WithImageAsync(image, showSystem, async (disc, _) =>
        {
            var node = Resolve(disc.Root, path);
            Console.WriteLine(node.FullPath);
            PrintTree(node, "");
            return 0;
        }, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> ExtractAsync(
        string image,
        string? output,
        string? path,
        string? pattern,
        string? exclude,
        bool skipExisting,
        bool includeProtected,
        bool decompress,
        bool showSystem,
        bool json,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return WriteError(1, "user", "Extract needs --out.");
        }

        return await WithImageAsync(image, showSystem, async (disc, _) =>
        {
            var node = Resolve(disc.Root, path);
            var result = await new Extractor().ExtractAsync(
                [node],
                new DirectoryInfo(output),
                new ExtractOptions
                {
                    SkipExisting = skipExisting,
                    IncludeProtected = includeProtected,
                    Decompress = decompress,
                    Pattern = pattern,
                    Exclude = exclude
                },
                null,
                cancellationToken).ConfigureAwait(false);
            if (json)
            {
                WriteJson(new
                {
                    extracted = result.Extracted,
                    skipped = result.Skipped,
                    @protected = result.ProtectedSkipped,
                    bytes = result.BytesWritten,
                    destination = Path.GetFullPath(output)
                });
            }
            else
            {
                Console.WriteLine($"Extracted {result.Extracted:N0}, skipped {result.Skipped:N0}, protected {result.ProtectedSkipped:N0}");
                Console.WriteLine("Destination " + Path.GetFullPath(output));
            }

            return 0;
        }, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> HashAsync(
        string image,
        string? path,
        string? algo,
        string? manifest,
        bool json,
        bool showSystem,
        CancellationToken cancellationToken)
    {
        if (!TryParseAlgos(algo, out var kinds))
        {
            return WriteError(1, "user", "Unknown hash in --algo. Use sha256, sha1, md5.");
        }

        return await WithImageAsync(image, showSystem, async (disc, _) =>
        {
            var hasher = new HashService();
            IReadOnlyList<HashEntry> entries;
            if (string.IsNullOrWhiteSpace(path))
            {
                entries = await hasher.HashTreeAsync(disc.Root, kinds, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var node = Resolve(disc.Root, path);
                if (node.IsDirectory)
                {
                    entries = await hasher.HashTreeAsync(node, kinds, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    entries = [new HashEntry(node.FullPath, await hasher.ComputeAsync(node, kinds, cancellationToken).ConfigureAwait(false))];
                }
            }

            if (!string.IsNullOrWhiteSpace(manifest))
            {
                var lines = new List<string>();
                foreach (var entry in entries)
                {
                    var relative = entry.Path.TrimStart('/');
                    if (entry.Hashes.Sha256 is not null)
                    {
                        lines.Add($"{entry.Hashes.Sha256}  {relative}");
                    }

                    if (entry.Hashes.Sha1 is not null)
                    {
                        lines.Add($"{entry.Hashes.Sha1}  {relative}  sha1");
                    }

                    if (entry.Hashes.Md5 is not null)
                    {
                        lines.Add($"{entry.Hashes.Md5}  {relative}  md5");
                    }
                }

                await File.WriteAllLinesAsync(manifest, lines, cancellationToken).ConfigureAwait(false);
            }

            if (json)
            {
                WriteJson(new
                {
                    entries = entries.Select(entry => new
                    {
                        path = entry.Path,
                        sha256 = entry.Hashes.Sha256,
                        sha1 = entry.Hashes.Sha1,
                        md5 = entry.Hashes.Md5
                    })
                });
            }
            else
            {
                foreach (var entry in entries)
                {
                    WriteHash("SHA-256", entry.Hashes.Sha256, entry.Path);
                    WriteHash("SHA-1", entry.Hashes.Sha1, entry.Path);
                    WriteHash("MD5", entry.Hashes.Md5, entry.Path);
                }
            }

            return 0;
        }, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> SearchAsync(string image, string pattern, bool json, bool showSystem, CancellationToken cancellationToken)
    {
        return await WithImageAsync(image, showSystem, async (disc, _) =>
        {
            var matches = NameSearch.Search(disc.Root, pattern);
            if (json)
            {
                WriteJson(new { pattern, matches = matches.Select(Entry) });
            }
            else
            {
                foreach (var match in matches)
                {
                    Console.WriteLine(match.FullPath);
                }
            }

            return 0;
        }, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> ArtAsync(string image, string? output, bool json, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return WriteError(1, "user", "art needs --out.");
        }

        return await WithImageAsync(image, false, async (disc, _) =>
        {
            var meta = TitleMetadataReader.Read(disc);
            if (!meta.HasIcon && !meta.HasBanner)
            {
                return WriteError(1, "user", "This image has no title art.");
            }

            Directory.CreateDirectory(output);
            string? icon = null;
            string? banner = null;
            if (meta.Icon is { Length: > 0 } iconBytes)
            {
                icon = "icon" + TitleMetadataReader.ArtExtension(iconBytes);
                await File.WriteAllBytesAsync(Path.Combine(output, icon), iconBytes, cancellationToken).ConfigureAwait(false);
            }

            if (meta.Banner is { Length: > 0 } bannerBytes)
            {
                banner = "banner" + TitleMetadataReader.ArtExtension(bannerBytes);
                await File.WriteAllBytesAsync(Path.Combine(output, banner), bannerBytes, cancellationToken).ConfigureAwait(false);
            }

            var destination = Path.GetFullPath(output);
            if (json)
            {
                WriteJson(new { destination, icon, banner });
            }
            else
            {
                if (icon is not null)
                {
                    Console.WriteLine("Icon   " + Path.Combine(destination, icon));
                }

                if (banner is not null)
                {
                    Console.WriteLine("Banner " + Path.Combine(destination, banner));
                }
            }

            return 0;
        }, cancellationToken).ConfigureAwait(false);
    }

    private static string ArtSummary(TitleMetadata meta)
    {
        if (meta.IconName is null && meta.BannerName is null)
        {
            return "none";
        }

        if (meta.IconName is not null && meta.BannerName is not null)
        {
            return meta.IconName + ", " + meta.BannerName;
        }

        return meta.IconName ?? meta.BannerName!;
    }

    private static Task<int> DetectAsync(string image, bool json, CancellationToken cancellationToken)
    {
        return WithImageAsync(image, false, async (disc, _) =>
        {
            if (json)
            {
                WriteJson(new
                {
                    format = disc.Identity.FormatId,
                    formatName = disc.Identity.FormatDisplayName,
                    layout = disc.Identity.Layout,
                    platform = disc.Identity.Platform
                });
            }
            else
            {
                Console.WriteLine($"{disc.Identity.FormatId}\t{disc.Identity.Layout ?? disc.Identity.FormatDisplayName}\t{disc.Identity.SourceName}");
            }

            return 0;
        }, cancellationToken);
    }

    private static async Task<int> WithDiscKeyAsync(string? path, Func<Task<int>> action)
    {
        LoadedKey.Value = null;
        if (!string.IsNullOrWhiteSpace(path))
        {
            if (!DiscKeyFile.TryLoad(path, out var key, out var error))
            {
                return WriteError(1, "user", error ?? "That file is not a .ird or a 16-byte disc key.");
            }

            LoadedKey.Value = key;
        }

        try
        {
            return await action().ConfigureAwait(false);
        }
        finally
        {
            LoadedKey.Value = null;
        }
    }

    private static async Task<int> WithKeysAsync(string? path, Func<Task<int>> action)
    {
        KeysPath.Value = null;
        if (!string.IsNullOrWhiteSpace(path) && !File.Exists(path) && !Directory.Exists(path))
        {
            return WriteError(1, "user", "The keys path does not exist.");
        }

        KeysPath.Value = string.IsNullOrWhiteSpace(path) ? null : path;
        try
        {
            return await action().ConfigureAwait(false);
        }
        finally
        {
            KeysPath.Value = null;
        }
    }

    private static async Task<int> DrivesAsync(bool json, CancellationToken cancellationToken)
    {
        try
        {
            var listed = OpticalDrives.List();
            var rows = new List<DriveJson>();
            foreach (var drive in listed)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var described = await OpticalDrives.DescribeAsync(drive, cancellationToken).ConfigureAwait(false);
                rows.Add(new DriveJson(
                    described.Id,
                    described.DevicePath,
                    described.VolumeLabel,
                    described.MediaBytes,
                    described.Platform,
                    described.FormatName,
                    described.Notice,
                    described.Error));
            }

            if (json)
            {
                WriteJson(new { drives = rows });
            }
            else if (rows.Count == 0)
            {
                Console.WriteLine("No optical drive with a disc was found.");
            }
            else
            {
                foreach (var row in rows)
                {
                    var detail = row.Error ?? row.Platform ?? row.Format ?? "";
                    Console.WriteLine($"{row.Id}\t{row.VolumeLabel}\t{ByteSize.Format(row.Bytes)}\t{detail}");
                    if (!string.IsNullOrWhiteSpace(row.Notice) && row.Error is null)
                    {
                        Console.WriteLine(row.Notice);
                    }
                }
            }

            return 0;
        }
        catch (OperationCanceledException)
        {
            return WriteError(1, "user", "Cancelled.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return WriteError(1, "user", ex.Message);
        }
    }

    private static async Task<int> ConvertAsync(string image, string? output, bool json, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return WriteError(1, "user", "Convert needs --out.");
        }

        if (OpticalDrives.IsDrivePath(image) || Directory.Exists(image))
        {
            return WriteError(1, "user", "Convert reads an NSZ or XCZ file.");
        }

        var lastPercent = -1;
        var progress = new Progress<SwitchConvert.ConvertProgress>(update =>
        {
            if (json || update.TotalBytes <= 0)
            {
                return;
            }

            var percent = (int)(update.BytesWritten * 100 / update.TotalBytes);
            if (percent == lastPercent)
            {
                return;
            }

            lastPercent = percent;
            Console.Error.WriteLine($"Converting {percent}% {update.Current}");
        });

        try
        {
            var result = await SwitchConvert.ConvertAsync(
                image,
                output,
                new OpenOptions { KeysDirectory = KeysPath.Value },
                progress,
                cancellationToken).ConfigureAwait(false);
            if (json)
            {
                WriteJson(new
                {
                    output = result.OutputPath,
                    bytes = result.BytesWritten,
                    sha256 = result.Sha256
                });
            }
            else
            {
                Console.WriteLine("Wrote " + result.OutputPath);
                Console.WriteLine("SHA-256 " + result.Sha256);
            }

            return 0;
        }
        catch (OperationCanceledException)
        {
            return WriteError(1, "user", "Cancelled.");
        }
        catch (DiscFormatException ex)
        {
            return WriteError(2, "unsupported", ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or FileNotFoundException)
        {
            return WriteError(1, "user", ex.Message);
        }
    }

    private static async Task<int> RipAsync(string drive, string? output, int attempts, bool json, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return WriteError(1, "user", "Rip needs --out.");
        }

        if (attempts < 1)
        {
            return WriteError(1, "user", "Retries must be at least 1.");
        }

        if (!OpticalDrives.IsDrivePath(drive))
        {
            return WriteError(1, "user", "Rip reads an optical drive such as D: or /dev/sr0.");
        }

        IBlockDevice device;
        try
        {
            device = OpticalDrives.Open(drive);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return WriteError(1, "user", ex.Message);
        }

        var lastPercent = -1;
        var progress = new Progress<RipProgress>(update =>
        {
            if (json)
            {
                return;
            }

            var percent = update.TotalSectors == 0 ? 100 : (int)(update.CompletedSectors * 100 / update.TotalSectors);
            if (percent == lastPercent)
            {
                return;
            }

            lastPercent = percent;
            Console.Error.WriteLine($"Ripping {percent}% ({update.CompletedSectors}/{update.TotalSectors}, bad {update.BadSectors})");
        });

        try
        {
            var result = await IsoRipper.RipAsync(device, output, new RipOptions { Attempts = attempts }, progress, cancellationToken).ConfigureAwait(false);
            if (json)
            {
                WriteJson(new
                {
                    output = result.OutputPath,
                    bytes = result.BytesWritten,
                    sectors = result.SectorCount,
                    badSectors = result.BadSectors,
                    sha256 = result.Sha256,
                    sha1 = result.Sha1,
                    md5 = result.Md5
                });
            }
            else
            {
                Console.WriteLine("Wrote " + result.OutputPath);
                Console.WriteLine("Sectors " + result.SectorCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
                Console.WriteLine(result.BadSectors.Count == 0
                    ? "Bad sectors none"
                    : "Bad sectors " + string.Join(", ", result.BadSectors.Select(sector => sector.ToString(System.Globalization.CultureInfo.InvariantCulture))));
                Console.WriteLine("SHA-256 " + result.Sha256);
                Console.WriteLine("SHA-1 " + result.Sha1);
                Console.WriteLine("MD5 " + result.Md5);
            }

            return 0;
        }
        catch (OperationCanceledException)
        {
            return WriteError(1, "user", "Cancelled.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            return WriteError(1, "user", ex.Message);
        }
        finally
        {
            device.Dispose();
        }
    }

    private sealed record DriveJson(
        string Id,
        string DevicePath,
        string? VolumeLabel,
        long Bytes,
        string? Platform,
        string? Format,
        string? Notice,
        string? Error);

    private static async Task<int> WithImageAsync(string image, bool showSystem, Func<IDiscImage, CancellationToken, Task<int>> action, CancellationToken cancellationToken)
    {
        try
        {
            await using var disc = await DiscImages.OpenAsync(image, new OpenOptions { ShowSystemUpdate = showSystem, KeysDirectory = KeysPath.Value }, cancellationToken).ConfigureAwait(false);
            return await action(disc, cancellationToken).ConfigureAwait(false);
        }
        catch (FileNotFoundException ex)
        {
            return WriteError(1, "user", ex.Message);
        }
        catch (DirectoryNotFoundException ex)
        {
            return WriteError(1, "user", ex.Message);
        }
        catch (DiscFormatException ex)
        {
            return WriteError(2, "unsupported", ex.Message);
        }
        catch (OperationCanceledException)
        {
            return WriteError(1, "user", "Cancelled.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            return WriteError(1, "user", ex.Message);
        }
    }

    private static bool HasJsonFlag(string[] args)
    {
        foreach (var arg in args)
        {
            if (arg.Equals("--json", StringComparison.OrdinalIgnoreCase)
                || arg.StartsWith("--json=", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Exit codes: 0 success, 1 user, 2 unsupported, 3 reserved for needs-keys.</summary>
    private static int WriteError(int code, string kind, string message)
    {
        if (JsonOutput.Value)
        {
            WriteJson(new
            {
                error = new
                {
                    code,
                    kind,
                    message
                }
            });
        }
        else if (!string.IsNullOrEmpty(message))
        {
            Console.Error.WriteLine(message);
        }

        return code;
    }

    private static IDiscNode Resolve(IDiscNode root, string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path == "/" || path == "\\")
        {
            return root;
        }

        var current = root;
        foreach (var part in path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var next = current.Children.FirstOrDefault(child => child.Name.Equals(part, StringComparison.OrdinalIgnoreCase));
            if (next is null)
            {
                throw new FileNotFoundException("That path is not in the image.", path);
            }

            current = next;
        }

        return current;
    }

    private static void PrintTree(IDiscNode node, string prefix)
    {
        var children = node.Children;
        for (var i = 0; i < children.Count; i++)
        {
            var child = children[i];
            var last = i == children.Count - 1;
            Console.WriteLine(prefix + (last ? "└── " : "├── ") + child.Name + (child.IsDirectory ? "/" : string.Empty));
            if (child.IsDirectory)
            {
                PrintTree(child, prefix + (last ? "    " : "│   "));
            }
        }
    }

    private static object Entry(IDiscNode node) => new
    {
        name = node.Name,
        path = node.FullPath,
        directory = node.IsDirectory,
        size = node.Size,
        kind = node.Kind.ToString().ToLowerInvariant(),
        @protected = node.IsProtected
    };

    private static bool TryParseAlgos(string? text, out HashAlgorithmKind kinds)
    {
        kinds = HashAlgorithmKind.None;
        foreach (var part in (text ?? "sha256").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "sha256":
                    kinds |= HashAlgorithmKind.Sha256;
                    break;
                case "sha1":
                    kinds |= HashAlgorithmKind.Sha1;
                    break;
                case "md5":
                    kinds |= HashAlgorithmKind.Md5;
                    break;
                default:
                    return false;
            }
        }

        if (kinds == HashAlgorithmKind.None)
        {
            kinds = HashAlgorithmKind.Sha256;
        }

        return true;
    }

    private static void WriteHash(string label, string? value, string path)
    {
        if (value is not null)
        {
            Console.WriteLine($"{label,-8} {value}  {path}");
        }
    }

    private static void Line(string label, string value) => Console.WriteLine($"{label,-12} {value}");

    private static void WriteJson<T>(T value) => Console.WriteLine(JsonSerializer.Serialize(value, Json));
}
