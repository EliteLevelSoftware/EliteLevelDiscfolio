using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;
using EliteLevel.Disc.Core;
using EliteLevel.Disc.Core.Extract;
using EliteLevel.Disc.Core.Hash;
using EliteLevel.Disc.Core.IO;
using EliteLevel.Disc.Core.Search;

namespace EliteLevel.Disc.Cli;

public static class CliHost
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
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
        Console.SetOut(stdout);
        Console.SetError(stderr);
        try
        {
            return await Build().Parse(args).InvokeAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Gate.Release();
        }
    }

    private static RootCommand Build()
    {
        var image = new Argument<string>("image") { Description = "Disc image or folder dump" };
        var json = new Option<bool>("--json") { Description = "Print JSON" };
        var path = new Option<string>("--path") { Description = "Directory or file inside the image" };
        var showSystem = new Option<bool>("--show-system") { Description = "Show system-update folders" };
        var longer = new Option<bool>("--long", "-l") { Description = "Print size and kind" };
        var output = new Option<string>("--out") { Description = "Destination directory" };
        var pattern = new Option<string>("--pattern") { Description = "Only extract names matching this glob" };
        var exclude = new Option<string>("--exclude") { Description = "Skip names matching this glob" };
        var skipExisting = new Option<bool>("--skip-existing") { Description = "Keep a destination file when its size matches" };
        var includeProtected = new Option<bool>("--include-protected") { Description = "Also copy protected paths, still encrypted and unchanged" };
        var algo = new Option<string>("--algo") { Description = "Comma-separated hashes: sha256, sha1, md5", DefaultValueFactory = _ => "sha256" };
        var manifest = new Option<string>("--manifest") { Description = "Write a hash manifest to this file" };
        var searchPattern = new Argument<string>("pattern") { Description = "Name or glob to find" };

        var info = new Command("info", "Describe an image");
        info.Arguments.Add(image);
        info.Options.Add(json);
        info.Options.Add(showSystem);
        info.SetAction((result, token) => InfoAsync(result.GetValue(image)!, result.GetValue(json), result.GetValue(showSystem), token));

        var list = new Command("list", "List one directory");
        list.Arguments.Add(image);
        list.Options.Add(path);
        list.Options.Add(json);
        list.Options.Add(longer);
        list.Options.Add(showSystem);
        list.SetAction((result, token) => ListAsync(
            result.GetValue(image)!,
            result.GetValue(path),
            result.GetValue(json),
            result.GetValue(longer),
            result.GetValue(showSystem),
            token));

        var tree = new Command("tree", "Print the directory tree");
        tree.Arguments.Add(image);
        tree.Options.Add(path);
        tree.Options.Add(showSystem);
        tree.SetAction((result, token) => TreeAsync(result.GetValue(image)!, result.GetValue(path), result.GetValue(showSystem), token));

        var extract = new Command("extract", "Copy files out of an image");
        extract.Arguments.Add(image);
        extract.Options.Add(output);
        extract.Options.Add(path);
        extract.Options.Add(pattern);
        extract.Options.Add(exclude);
        extract.Options.Add(skipExisting);
        extract.Options.Add(includeProtected);
        extract.Options.Add(showSystem);
        extract.Options.Add(json);
        extract.SetAction((result, token) => ExtractAsync(
            result.GetValue(image)!,
            result.GetValue(output),
            result.GetValue(path),
            result.GetValue(pattern),
            result.GetValue(exclude),
            result.GetValue(skipExisting),
            result.GetValue(includeProtected),
            result.GetValue(showSystem),
            result.GetValue(json),
            token));

        var hash = new Command("hash", "Compute SHA-256, SHA-1 or MD5");
        hash.Arguments.Add(image);
        hash.Options.Add(path);
        hash.Options.Add(algo);
        hash.Options.Add(manifest);
        hash.Options.Add(json);
        hash.Options.Add(showSystem);
        hash.SetAction((result, token) => HashAsync(
            result.GetValue(image)!,
            result.GetValue(path),
            result.GetValue(algo),
            result.GetValue(manifest),
            result.GetValue(json),
            result.GetValue(showSystem),
            token));

        var search = new Command("search", "Find entries by name");
        search.Arguments.Add(image);
        search.Arguments.Add(searchPattern);
        search.Options.Add(json);
        search.Options.Add(showSystem);
        search.SetAction((result, token) => SearchAsync(
            result.GetValue(image)!,
            result.GetValue(searchPattern)!,
            result.GetValue(json),
            result.GetValue(showSystem),
            token));

        var detect = new Command("detect", "Print the detected format");
        detect.Arguments.Add(image);
        detect.Options.Add(json);
        detect.SetAction((result, token) => DetectAsync(result.GetValue(image)!, result.GetValue(json), token));

        var root = new RootCommand("EliteLevel Discfolio. Read-only explorer for disc images and folder dumps.");
        root.Subcommands.Add(info);
        root.Subcommands.Add(list);
        root.Subcommands.Add(tree);
        root.Subcommands.Add(extract);
        root.Subcommands.Add(hash);
        root.Subcommands.Add(search);
        root.Subcommands.Add(detect);
        return root;
    }

    private static async Task<int> InfoAsync(string image, bool json, bool showSystem, CancellationToken cancellationToken)
    {
        return await WithImageAsync(image, showSystem, async (disc, _) =>
        {
            var id = disc.Identity;
            if (json)
            {
                WriteJson(new
                {
                    format = id.FormatId,
                    formatName = id.FormatDisplayName,
                    volumeLabel = id.VolumeLabel,
                    platform = id.Platform,
                    layout = id.Layout,
                    videoPartition = id.VideoPartition,
                    partitions = id.Partitions,
                    files = id.FileCount,
                    directories = id.DirectoryCount,
                    bytes = id.TotalBytes,
                    source = id.SourceName
                });
            }
            else
            {
                Console.WriteLine(id.VolumeLabel ?? id.SourceName);
                Line("Format", id.FormatDisplayName);
                Line("Platform", id.Platform ?? "Unknown");
                Line("Layout", id.Layout ?? "Single partition");
                Line("Files", TextFormat.Count(id.FileCount));
                Line("Directories", TextFormat.Count(id.DirectoryCount));
                Line("Size", ByteSize.Format(id.TotalBytes));
                Line("Source", id.SourceName);
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
                Console.Error.WriteLine("The path is a file. Pass the directory that contains it.");
                return 1;
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
        bool showSystem,
        bool json,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            Console.Error.WriteLine("Extract needs --out.");
            return 1;
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
            Console.Error.WriteLine("Unknown hash in --algo. Use sha256, sha1, md5.");
            return 1;
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

    private static async Task<int> WithImageAsync(string image, bool showSystem, Func<IDiscImage, CancellationToken, Task<int>> action, CancellationToken cancellationToken)
    {
        try
        {
            await using var disc = await DiscImages.OpenAsync(image, new OpenOptions { ShowSystemUpdate = showSystem }, cancellationToken).ConfigureAwait(false);
            return await action(disc, cancellationToken).ConfigureAwait(false);
        }
        catch (FileNotFoundException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
        catch (DirectoryNotFoundException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
        catch (DiscFormatException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
        catch (OperationCanceledException)
        {
            return 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
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
