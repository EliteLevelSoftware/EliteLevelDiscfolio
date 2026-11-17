using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EliteLevel.Disc.App.Services;
using EliteLevel.Disc.Core;
using EliteLevel.Disc.Core.Extract;
using EliteLevel.Disc.Core.Hash;
using EliteLevel.Disc.Core.IO;
using EliteLevel.Disc.Core.Search;

namespace EliteLevel.Disc.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private IDiscImage? _image;
    private readonly Dictionary<string, IDiscNode> _index = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _expanded = new(StringComparer.OrdinalIgnoreCase);
    private int _generation;
    private bool _extractAll;

    public MainViewModel()
    {
        _settings = SettingsStore.Load();
        ReduceEffects = _settings.ReduceEffects;
        ShowSystemUpdate = _settings.ShowSystemUpdate;
        HashSha1 = _settings.HashSha1;
        HashMd5 = _settings.HashMd5;
        ExtractDirectory = _settings.ExtractDirectory ?? "";
        SkipExisting = true;
        foreach (var recent in _settings.Recent)
        {
            Recent.Add(new RecentItem(recent.Path, recent.Title, recent.Meta));
        }

        OpenCount = Recent.Count;
    }

    public ObservableCollection<FileRow> Rows { get; } = [];
    public ObservableCollection<RecentItem> Recent { get; } = [];

    [ObservableProperty] private string page = "library";
    [ObservableProperty] private bool isLoading;
    [ObservableProperty] private bool hasImage;
    [ObservableProperty] private string? errorMessage;
    [ObservableProperty] private string? errorDetail;
    [ObservableProperty] private string searchText = "";
    [ObservableProperty] private string volumeTitle = "";
    [ObservableProperty] private string eyebrow = "Currently open";
    [ObservableProperty] private string chipLine = "";
    [ObservableProperty] private string metaLine = "";
    [ObservableProperty] private string detailTitle = "";
    [ObservableProperty] private string detailSubtitle = "";
    [ObservableProperty] private string sizeText = "";
    [ObservableProperty] private string hashDisplay = "Not computed";
    [ObservableProperty] private string? hashFull;
    [ObservableProperty] private string? sha1Full;
    [ObservableProperty] private string? md5Full;
    [ObservableProperty] private string? selectedPath;
    [ObservableProperty] private string breadcrumb = "";
    [ObservableProperty] private string locator = "";
    [ObservableProperty] private bool skipExisting;
    [ObservableProperty] private bool includeProtected;
    [ObservableProperty] private string? pattern;
    [ObservableProperty] private string extractDirectory = "";
    [ObservableProperty] private string extractStatus = "";
    [ObservableProperty] private bool isExtracting;
    [ObservableProperty] private bool showExtractDialog;
    [ObservableProperty] private bool showWelcome;
    [ObservableProperty] private string protectedSummary = "Default extract skips system-update folders and protected paths such as EBOOT.BIN, PS3_UPDATE and LICDIR.";
    [ObservableProperty] private bool reduceEffects;
    [ObservableProperty] private bool showSystemUpdate;
    [ObservableProperty] private bool hashSha1;
    [ObservableProperty] private bool hashMd5;
    [ObservableProperty] private int libraryCount;
    [ObservableProperty] private int openCount;
    [ObservableProperty] private int extractCount;
    [ObservableProperty] private int verifyCount;
    [ObservableProperty] private string artSeed = "discfolio";
    [ObservableProperty] private string platformTag = "";
    [ObservableProperty] private string sourceName = "";
    [ObservableProperty] private bool hasNotification;

    public bool IsLibrary => Page == "library";
    public bool IsOpenImages => Page == "open";
    public bool IsExtract => Page == "extract";
    public bool IsVerify => Page == "verify";
    public bool IsSettings => Page == "settings";
    public bool IsHelp => Page == "help";
    public bool ShowEmpty => IsLibrary && !IsLoading && !HasImage && string.IsNullOrEmpty(ErrorMessage);
    public bool ShowError => !string.IsNullOrEmpty(ErrorMessage) && !IsLoading;
    public bool ShowBrowser => IsLibrary && HasImage && !IsLoading && string.IsNullOrEmpty(ErrorMessage);

    public event EventHandler? ImageChanged;

    public void OnFirstShown()
    {
        if (!_settings.WelcomeSeen)
        {
            ShowWelcome = true;
        }
    }

    [RelayCommand]
    private void ShowPage(string? page)
    {
        Page = string.IsNullOrEmpty(page) ? "library" : page;
    }

    [RelayCommand]
    private void DismissWelcome()
    {
        ShowWelcome = false;
        _settings.WelcomeSeen = true;
        Persist();
    }

    [RelayCommand]
    private void BeginExtractAll()
    {
        _extractAll = true;
        ShowExtractDialog = HasImage;
    }

    [RelayCommand]
    private void BeginExtractSelected()
    {
        _extractAll = false;
        ShowExtractDialog = HasImage;
    }

    [RelayCommand]
    private void CancelExtract() => ShowExtractDialog = false;

    [RelayCommand]
    private async Task ConfirmExtractAsync()
    {
        ShowExtractDialog = false;
        await ExtractAsync(_extractAll).ConfigureAwait(true);
    }

    [RelayCommand]
    private Task VerifyAsync() => HashSelectionAsync();

    [RelayCommand]
    private void SelectRow(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        SelectedPath = path;
        foreach (var row in Rows)
        {
            row.IsSelected = row.Path.Equals(path, StringComparison.OrdinalIgnoreCase);
        }

        Breadcrumb = path;
        if (_index.TryGetValue(path, out var node))
        {
            Locator = node.IsDirectory
                ? node.Name
                : node.Name + "  ·  " + ByteSize.Format(node.Size);
        }
    }

    [RelayCommand]
    private void ToggleRow(string? path)
    {
        if (string.IsNullOrEmpty(path) || !_index.TryGetValue(path, out var node) || !node.IsDirectory)
        {
            return;
        }

        if (!_expanded.Add(path))
        {
            _expanded.Remove(path);
        }

        RebuildRows();
    }

    [RelayCommand]
    private Task OpenRecent(string? path) => string.IsNullOrEmpty(path) ? Task.CompletedTask : OpenAsync(path);

    public async Task OpenAsync(string path)
    {
        var generation = ++_generation;
        IsLoading = true;
        ErrorMessage = null;
        ErrorDetail = null;
        Page = "library";
        NotifyVisibility();
        try
        {
            var next = await DiscImages.OpenAsync(path, new OpenOptions { ShowSystemUpdate = ShowSystemUpdate }).ConfigureAwait(true);
            if (generation != _generation)
            {
                await next.DisposeAsync().ConfigureAwait(true);
                return;
            }

            if (_image is not null)
            {
                await _image.DisposeAsync().ConfigureAwait(true);
            }

            _image = next;
            _expanded.Clear();
            SelectedPath = null;
            HashFull = null;
            HashDisplay = "Not computed";
            Sha1Full = null;
            Md5Full = null;
            ApplyIdentity(next.Identity, path);
            RebuildRows();
            Remember(path, next.Identity);
            HasImage = true;
            ImageChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (DiscFormatException ex)
        {
            await ClearImageAsync().ConfigureAwait(true);
            ErrorMessage = ex.Message;
            ErrorDetail = ex.InnerException?.Message;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            await ClearImageAsync().ConfigureAwait(true);
            ErrorMessage = "The image could not be opened.";
            ErrorDetail = ex.Message;
        }
        finally
        {
            if (generation == _generation)
            {
                IsLoading = false;
                NotifyVisibility();
            }
        }
    }

    public async Task ExtractAsync(bool all)
    {
        if (_image is null || string.IsNullOrWhiteSpace(ExtractDirectory))
        {
            ExtractStatus = "Choose a destination folder.";
            return;
        }

        IDiscNode node = _image.Root;
        if (!all && SelectedPath is not null && _index.TryGetValue(SelectedPath, out var selected))
        {
            node = selected;
        }

        IsExtracting = true;
        try
        {
            var result = await new Extractor().ExtractAsync(
                [node],
                new DirectoryInfo(ExtractDirectory),
                new ExtractOptions
                {
                    SkipExisting = SkipExisting,
                    IncludeProtected = IncludeProtected,
                    Pattern = string.IsNullOrWhiteSpace(Pattern) ? null : Pattern.Trim()
                },
                new Progress<ExtractProgress>(progress => ExtractStatus = progress.CurrentPath),
                CancellationToken.None).ConfigureAwait(true);
            ExtractStatus = $"Extracted {result.Extracted:N0}, skipped {result.Skipped:N0}, protected {result.ProtectedSkipped:N0}. {ByteSize.Format(result.BytesWritten)} written.";
            ExtractCount++;
            HasNotification = true;
            _settings.ExtractDirectory = ExtractDirectory;
            Persist();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ExtractStatus = ex.Message;
        }
        finally
        {
            IsExtracting = false;
        }
    }

    public async Task HashSelectionAsync()
    {
        if (SelectedPath is null || !_index.TryGetValue(SelectedPath, out var node) || node.IsDirectory)
        {
            HashDisplay = "Select a file";
            HashFull = null;
            Page = "verify";
            NotifyVisibility();
            return;
        }

        var kinds = HashAlgorithmKind.Sha256;
        if (HashSha1)
        {
            kinds |= HashAlgorithmKind.Sha1;
        }

        if (HashMd5)
        {
            kinds |= HashAlgorithmKind.Md5;
        }

        var result = await new HashService().ComputeAsync(node, kinds, CancellationToken.None).ConfigureAwait(true);
        HashFull = result.Sha256;
        Sha1Full = result.Sha1;
        Md5Full = result.Md5;
        HashDisplay = TextFormat.MiddleEllipsis(result.Sha256);
        VerifyCount = 1;
        Page = "verify";
        NotifyVisibility();
    }

    public async Task SaveManifestAsync(string path)
    {
        if (_image is null)
        {
            return;
        }

        var entries = await new HashService().HashTreeAsync(_image.Root, HashAlgorithmKind.Sha256, CancellationToken.None).ConfigureAwait(true);
        var lines = entries.Select(entry => $"{entry.Hashes.Sha256}  {entry.Path.TrimStart('/')}");
        await File.WriteAllLinesAsync(path, lines).ConfigureAwait(true);
        ExtractStatus = "Saved the manifest.";
        HasNotification = true;
    }

    public byte[]? ReadPreview(string fileName)
    {
        if (_image is null)
        {
            return null;
        }

        var match = _index.Values.FirstOrDefault(node =>
            !node.IsDirectory
            && node.Name.Equals(fileName, StringComparison.OrdinalIgnoreCase)
            && node.Size > 0
            && node.Size < 12 * 1024 * 1024);
        if (match is null)
        {
            return null;
        }

        using var stream = match.OpenRead();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    partial void OnPageChanged(string value) => NotifyVisibility();

    partial void OnSearchTextChanged(string value) => RebuildRows();

    partial void OnReduceEffectsChanged(bool value)
    {
        _settings.ReduceEffects = value;
        Persist();
    }

    partial void OnShowSystemUpdateChanged(bool value)
    {
        _settings.ShowSystemUpdate = value;
        Persist();
        if (HasImage && SourceName.Length > 0)
        {
            var recent = Recent.FirstOrDefault();
            if (recent is not null)
            {
                _ = OpenAsync(recent.Path);
            }
        }
    }

    partial void OnHashSha1Changed(bool value)
    {
        _settings.HashSha1 = value;
        Persist();
    }

    partial void OnHashMd5Changed(bool value)
    {
        _settings.HashMd5 = value;
        Persist();
    }

    private void ApplyIdentity(ImageIdentity identity, string path)
    {
        var title = string.IsNullOrWhiteSpace(identity.VolumeLabel)
            ? Path.GetFileNameWithoutExtension(path)
            : identity.VolumeLabel;
        VolumeTitle = title.Replace('_', ' ');
        DetailTitle = VolumeTitle;
        SourceName = identity.SourceName;
        ArtSeed = identity.FormatId + ":" + identity.SourceName + ":" + (identity.VolumeLabel ?? "");
        var chips = new List<string>();
        if (!string.IsNullOrEmpty(identity.Platform))
        {
            chips.Add(identity.Platform);
        }

        chips.Add(identity.FormatDisplayName);
        if (!string.IsNullOrEmpty(identity.Layout))
        {
            chips.Add(identity.Layout);
        }

        ChipLine = string.Join('|', chips);
        MetaLine = string.Join('|',
        [
            TextFormat.Count(identity.FileCount) + " files",
            TextFormat.Count(identity.DirectoryCount) + " directories",
            "Opened " + DateTime.Now.ToString("t")
        ]);
        DetailSubtitle = identity.Platform is null
            ? identity.FormatDisplayName
            : identity.Platform + "  ·  " + identity.FormatDisplayName;
        SizeText = ByteSize.Format(identity.TotalBytes);
        PlatformTag = identity.Platform switch
        {
            "PlayStation 3" => "PS3",
            "Xbox 360" => "360",
            "Xbox" => "Xbox",
            _ => ""
        };
        LibraryCount = (int)Math.Min(identity.FileCount, int.MaxValue);
        Locator = identity.FormatDisplayName;
        Breadcrumb = "/";
    }

    private void RebuildRows()
    {
        Rows.Clear();
        _index.Clear();
        if (_image is null)
        {
            return;
        }

        Index(_image.Root);
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            foreach (var match in NameSearch.Search(_image.Root, SearchText.Trim()))
            {
                Rows.Add(MakeRow(match, 0, false));
            }

            return;
        }

        void Add(IDiscNode node, int depth)
        {
            foreach (var child in node.Children)
            {
                var expanded = child.IsDirectory && _expanded.Contains(child.FullPath);
                Rows.Add(MakeRow(child, depth, expanded));
                if (expanded)
                {
                    Add(child, depth + 1);
                }
            }
        }

        Add(_image.Root, 0);
        if (SelectedPath is not null)
        {
            SelectRow(SelectedPath);
        }
    }

    private void Index(IDiscNode node)
    {
        if (node.FullPath.Length > 1)
        {
            _index[node.FullPath] = node;
        }

        foreach (var child in node.Children)
        {
            Index(child);
        }
    }

    private FileRow MakeRow(IDiscNode node, int depth, bool expanded) => new()
    {
        Name = node.Name,
        Path = node.FullPath,
        SizeText = node.IsDirectory ? "" : ByteSize.Format(node.Size),
        Depth = depth,
        IsDirectory = node.IsDirectory,
        IsProtected = node.IsProtected,
        HasChildren = node.IsDirectory && node.Children.Count > 0,
        Expanded = expanded,
        IsSelected = node.FullPath.Equals(SelectedPath, StringComparison.OrdinalIgnoreCase)
    };

    private void Remember(string path, ImageIdentity identity)
    {
        var title = VolumeTitle;
        var meta = (identity.Platform ?? identity.FormatDisplayName) + "  ·  " + ByteSize.Format(identity.TotalBytes);
        for (var i = Recent.Count - 1; i >= 0; i--)
        {
            if (string.Equals(Recent[i].Path, path, StringComparison.OrdinalIgnoreCase))
            {
                Recent.RemoveAt(i);
            }
        }

        Recent.Insert(0, new RecentItem(path, title, meta));
        while (Recent.Count > 8)
        {
            Recent.RemoveAt(Recent.Count - 1);
        }

        OpenCount = Recent.Count;
        _settings.Recent = Recent.Select(item => new RecentRecord { Path = item.Path, Title = item.Title, Meta = item.Meta }).ToList();
        Persist();
    }

    private async Task ClearImageAsync()
    {
        HasImage = false;
        if (_image is not null)
        {
            await _image.DisposeAsync().ConfigureAwait(true);
            _image = null;
        }

        Rows.Clear();
        LibraryCount = 0;
    }

    private void Persist() => SettingsStore.Save(_settings);

    private void NotifyVisibility()
    {
        OnPropertyChanged(nameof(IsLibrary));
        OnPropertyChanged(nameof(IsOpenImages));
        OnPropertyChanged(nameof(IsExtract));
        OnPropertyChanged(nameof(IsVerify));
        OnPropertyChanged(nameof(IsSettings));
        OnPropertyChanged(nameof(IsHelp));
        OnPropertyChanged(nameof(ShowEmpty));
        OnPropertyChanged(nameof(ShowError));
        OnPropertyChanged(nameof(ShowBrowser));
    }
}

public partial class FileRow : ObservableObject
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public required string SizeText { get; init; }
    public int Depth { get; init; }
    public bool IsDirectory { get; init; }
    public bool IsProtected { get; init; }
    public bool HasChildren { get; init; }
    public bool Expanded { get; init; }
    [ObservableProperty] private bool isSelected;
    public global::Avalonia.Thickness Indent => new(8 + Depth * 20, 0, 8, 0);
}

public sealed record RecentItem(string Path, string Title, string Meta)
{
    public string Seed => Path;
}
