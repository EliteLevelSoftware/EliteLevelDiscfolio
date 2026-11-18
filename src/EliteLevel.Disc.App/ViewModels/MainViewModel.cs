using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EliteLevel.Disc.App.Services;
using EliteLevel.Disc.Core;
using EliteLevel.Disc.Core.Drives;
using Velopack;
using Velopack.Exceptions;
using EliteLevel.Disc.Core.Extract;
using EliteLevel.Disc.Core.Hash;
using EliteLevel.Disc.Core.IO;
using EliteLevel.Disc.Core.Search;
using EliteLevel.Disc.Core.Titles;

namespace EliteLevel.Disc.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly TitleArtCache _cache;
    private readonly SynchronizationContext? _ui = SynchronizationContext.Current;
    private readonly object _updateSync = new();
    private UpdateManager? _updates;
    private VelopackAsset? _pendingUpdate;
    private CancellationTokenSource? _updateCancel;
    private int _updateBusy;
    private string? _openPath;
    private IDiscImage? _image;
    private readonly Dictionary<string, IDiscNode> _index = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _expanded = new(StringComparer.OrdinalIgnoreCase);
    private int _generation;
    private bool _extractAll;
    private CancellationTokenSource? _ripCancel;
    private DiscKey? _discKey;

    public MainViewModel()
    {
        _settings = SettingsStore.Load();
        ReduceEffects = _settings.ReduceEffects;
        ShowSystemUpdate = _settings.ShowSystemUpdate;
        HashSha1 = _settings.HashSha1;
        HashMd5 = _settings.HashMd5;
        CheckUpdatesOnStartup = _settings.CheckForUpdates;
        UpdateChannel = _settings.UpdateChannel == "prerelease" ? "prerelease" : "stable";
        UpdateSource = _settings.UpdateSource ?? "";
        SwitchKeys = _settings.SwitchKeys ?? "";
        ExtractDirectory = _settings.ExtractDirectory ?? "";
        SkipExisting = true;
        _cache = new TitleArtCache(SettingsStore.CacheDirectory);
        foreach (var recent in _settings.Recent)
        {
            var cached = string.IsNullOrEmpty(recent.CacheKey) ? _cache.TryGet(recent.Path, ArtVariant()) : _cache.TryGetKey(recent.CacheKey);
            var title = recent.Title;
            var titleId = recent.TitleId ?? "";
            string? icon = null;
            if (cached is not null)
            {
                if (!string.IsNullOrWhiteSpace(cached.Title))
                {
                    title = cached.Title;
                }

                if (!string.IsNullOrWhiteSpace(cached.TitleId))
                {
                    titleId = cached.TitleId;
                }

                icon = cached.IconPath;
            }

            Recent.Add(new RecentItem(recent.Path, title, recent.Meta, titleId, icon, recent.IsDrive, recent.CacheKey));
        }

        OpenCount = Recent.Count;
        RebuildVisibleRecent();
        VersionText = "Version " + ProductVersion();
    }

    public string VersionText { get; }

    public ObservableCollection<FileRow> Rows { get; } = [];
    public ObservableCollection<RecentItem> Recent { get; } = [];
    public ObservableCollection<RecentItem> VisibleRecent { get; } = [];

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
    [ObservableProperty] private bool decompressNcz;
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
    [ObservableProperty] private string? iconPath;
    [ObservableProperty] private string? bannerPath;
    [ObservableProperty] private string titleId = "";
    [ObservableProperty] private string platformTag = "";
    [ObservableProperty] private string sourceName = "";
    [ObservableProperty] private bool hasNotification;
    [ObservableProperty] private bool checkUpdatesOnStartup;
    [ObservableProperty] private string updateChannel = "stable";
    [ObservableProperty] private string updateSource = "";
    [ObservableProperty] private string switchKeys = "";
    [ObservableProperty] private string updateStatus = "";
    [ObservableProperty] private bool updateAvailable;
    [ObservableProperty] private bool updateReady;
    [ObservableProperty] private string updateMessage = "";
    [ObservableProperty] private string libraryMessage = "";
    [ObservableProperty] private bool isDriveSource;
    [ObservableProperty] private string driveNotice = "";
    [ObservableProperty] private string discKeyStatus = "";
    [ObservableProperty] private bool showDrivePicker;
    [ObservableProperty] private string drivePickerMessage = "";
    [ObservableProperty] private string? selectedDriveId;
    [ObservableProperty] private bool showRipDialog;
    [ObservableProperty] private bool isRipping;
    [ObservableProperty] private bool ripFinished;
    [ObservableProperty] private string ripOutput = "";
    [ObservableProperty] private string ripRetriesText = "5";
    [ObservableProperty] private string ripStatus = "";
    [ObservableProperty] private string ripSha256 = "";
    [ObservableProperty] private string ripSha1 = "";
    [ObservableProperty] private string ripMd5 = "";
    [ObservableProperty] private string ripBadSectors = "";
    [ObservableProperty] private string? rippedIsoPath;

    public ObservableCollection<DriveRow> Drives { get; } = [];

    public bool HasDriveNotice => !string.IsNullOrWhiteSpace(DriveNotice);

    public bool ShowDiscKey => IsDriveSource && PlatformTag == "PS3";

    public bool HasDiscKey => _discKey is not null;

    public int UpdateChannelIndex
    {
        get => UpdateChannel == "prerelease" ? 1 : 0;
        set => UpdateChannel = value == 1 ? "prerelease" : "stable";
    }

    public bool HasTitleId => !string.IsNullOrWhiteSpace(TitleId);

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

        if (_settings.CheckForUpdates)
        {
            _ = CheckForUpdatesAsync(startup: true);
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
            var next = await DiscImages.OpenAsync(path, new OpenOptions
            {
                ShowSystemUpdate = ShowSystemUpdate,
                KeysDirectory = CurrentSwitchKeys()
            }).ConfigureAwait(true);
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
            _openPath = path;
            _expanded.Clear();
            SelectedPath = null;
            HashFull = null;
            HashDisplay = "Not computed";
            Sha1Full = null;
            Md5Full = null;
            var metadata = TitleMetadataReader.Read(next);
            var cached = next.Identity.FromDrive && !string.IsNullOrEmpty(next.Identity.CacheKey)
                ? _cache.StoreKey(next.Identity.CacheKey, metadata)
                : _cache.Store(path, metadata, ArtVariant());
            ApplyIdentity(next.Identity, path, metadata, cached);
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
            ErrorMessage = OpticalDrives.IsDrivePath(path) ? ex.Message : "The image could not be opened.";
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
                    Decompress = DecompressNcz,
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or DiscFormatException)
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

    partial void OnDriveNoticeChanged(string value) => OnPropertyChanged(nameof(HasDriveNotice));

    partial void OnIsDriveSourceChanged(bool value) => OnPropertyChanged(nameof(ShowDiscKey));

    partial void OnPlatformTagChanged(string value) => OnPropertyChanged(nameof(ShowDiscKey));

    partial void OnSelectedDriveIdChanged(string? value)
    {
        foreach (var drive in Drives)
        {
            drive.IsSelected = drive.Id == value;
        }
    }

    partial void OnSearchTextChanged(string value)
    {
        RebuildRows();
        RebuildVisibleRecent();
    }

    partial void OnTitleIdChanged(string value) => OnPropertyChanged(nameof(HasTitleId));

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

    partial void OnCheckUpdatesOnStartupChanged(bool value)
    {
        _settings.CheckForUpdates = value;
        Persist();
    }

    partial void OnUpdateChannelChanged(string value)
    {
        _settings.UpdateChannel = value == "prerelease" ? "prerelease" : "stable";
        Persist();
        OnPropertyChanged(nameof(UpdateChannelIndex));
    }

    partial void OnUpdateSourceChanged(string value)
    {
        _settings.UpdateSource = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        Persist();
    }

    partial void OnSwitchKeysChanged(string value)
    {
        var trimmed = value.Trim();
        var stored = trimmed.Length == 0 ? null : trimmed;
        if (string.Equals(_settings.SwitchKeys, stored, StringComparison.Ordinal))
        {
            return;
        }

        _settings.SwitchKeys = stored;
        Persist();
        if (HasImage && !string.IsNullOrEmpty(_openPath) && (stored is null || File.Exists(stored) || Directory.Exists(stored)))
        {
            _ = OpenAsync(_openPath);
        }
    }

    partial void OnUpdateReadyChanged(bool value) => RestartToUpdateCommand.NotifyCanExecuteChanged();

    private void ApplyIdentity(ImageIdentity identity, string path, TitleMetadata metadata, CachedTitle cached)
    {
        var found = TitleMetadataReader.ReadTitle(metadata);
        if (found is not null)
        {
            VolumeTitle = found;
        }
        else
        {
            var fallback = string.IsNullOrWhiteSpace(identity.VolumeLabel)
                ? Path.GetFileNameWithoutExtension(path)
                : identity.VolumeLabel;
            VolumeTitle = fallback.Replace('_', ' ');
        }
        DetailTitle = VolumeTitle;
        TitleId = metadata.TitleId ?? "";
        IconPath = cached.IconPath;
        BannerPath = cached.BannerPath;
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
        var meta = new List<string>
        {
            TextFormat.Count(identity.FileCount) + " files",
            TextFormat.Count(identity.DirectoryCount) + " directories",
            "Opened " + DateTime.Now.ToString("t")
        };
        if (identity.ProtectedCount > 0)
        {
            meta.Insert(1, TextFormat.Count(identity.ProtectedCount) + " protected skipped");
        }

        MetaLine = string.Join('|', meta);
        DetailSubtitle = identity.Platform is null
            ? identity.FormatDisplayName
            : identity.Platform + "  ·  " + identity.FormatDisplayName;
        SizeText = ByteSize.Format(identity.TotalBytes);
        PlatformTag = identity.Platform switch
        {
            "PlayStation 3" => "PS3",
            "Xbox 360" => "360",
            "Xbox" => "Xbox",
            "PlayStation 2" => "PS2",
            "PlayStation 1" => "PS1",
            "Nintendo Switch" => "Switch",
            _ => ""
        };
        IsDriveSource = identity.FromDrive;
        DriveNotice = identity.Notice ?? "";
        _discKey = null;
        DiscKeyStatus = "";
        OnPropertyChanged(nameof(HasDiscKey));
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
            var query = SearchText.Trim();
            var matches = NameSearch.Search(_image.Root, query);
            if (matches.Count == 0 && QueryMatchesOpenTitle(query))
            {
                Add(_image.Root, 0);
                return;
            }

            foreach (var match in matches)
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
        var meta = (identity.FromDrive ? "Disc drive  ·  " : "")
            + (identity.Platform ?? identity.FormatDisplayName)
            + "  ·  "
            + ByteSize.Format(identity.TotalBytes);
        var full = FullPath(path);
        for (var i = Recent.Count - 1; i >= 0; i--)
        {
            if (string.Equals(FullPath(Recent[i].Path), full, StringComparison.OrdinalIgnoreCase))
            {
                Recent.RemoveAt(i);
            }
        }

        var icon = identity.FromDrive && !string.IsNullOrEmpty(identity.CacheKey)
            ? _cache.TryGetKey(identity.CacheKey)?.IconPath
            : _cache.TryGet(path, ArtVariant())?.IconPath;
        Recent.Insert(0, new RecentItem(path, title, meta, TitleId, icon, identity.FromDrive, identity.CacheKey));
        while (Recent.Count > 8)
        {
            Recent.RemoveAt(Recent.Count - 1);
        }

        SaveRecent();
    }

    [RelayCommand]
    private async Task RemoveFromLibrary(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var full = FullPath(path);
        if (_openPath is not null && string.Equals(FullPath(_openPath), full, StringComparison.OrdinalIgnoreCase))
        {
            await ClearImageAsync().ConfigureAwait(true);
        }

        string? cacheKey = null;
        for (var i = Recent.Count - 1; i >= 0; i--)
        {
            if (string.Equals(FullPath(Recent[i].Path), full, StringComparison.OrdinalIgnoreCase))
            {
                cacheKey ??= Recent[i].CacheKey;
                Recent.RemoveAt(i);
            }
        }

        if (!string.IsNullOrEmpty(cacheKey))
        {
            _cache.RemoveKey(cacheKey);
        }
        else
        {
            _cache.Remove(path);
        }

        LibraryMessage = "";
        SaveRecent();
    }

    [RelayCommand]
    private Task RemoveOpenFromLibrary() => RemoveFromLibrary(_openPath);

    [RelayCommand]
    private async Task OpenDrivePicker()
    {
        ShowDrivePicker = true;
        await RefreshDrives().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task RefreshDrives()
    {
        DrivePickerMessage = "Looking for drives…";
        SelectedDriveId = null;
        try
        {
            var listed = await Task.Run(() => OpticalDrives.List()).ConfigureAwait(true);
            Drives.Clear();
            if (listed.Count == 0)
            {
                DrivePickerMessage = "No optical drive with a disc was found.";
                return;
            }

            DrivePickerMessage = "Reading the disc…";
            foreach (var drive in listed)
            {
                var described = await Task.Run(() => OpticalDrives.DescribeAsync(drive)).ConfigureAwait(true);
                Drives.Add(new DriveRow
                {
                    Id = described.Id,
                    DevicePath = described.DevicePath,
                    VolumeLabel = string.IsNullOrWhiteSpace(described.VolumeLabel) ? "No label" : described.VolumeLabel!,
                    SizeText = ByteSize.Format(described.MediaBytes),
                    Platform = described.Error ?? described.Platform ?? described.FormatName ?? "Unknown",
                    Notice = described.Notice
                });
            }

            DrivePickerMessage = Drives.Count == 0 ? "No optical drive with a disc was found." : "";
            if (Drives.Count > 0)
            {
                SelectedDriveId = Drives[0].Id;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DrivePickerMessage = ex.Message;
        }
    }

    [RelayCommand]
    private async Task OpenSelectedDrive()
    {
        if (string.IsNullOrEmpty(SelectedDriveId))
        {
            DrivePickerMessage = "Choose a drive.";
            return;
        }

        var id = SelectedDriveId;
        ShowDrivePicker = false;
        await OpenAsync(id).ConfigureAwait(true);
    }

    [RelayCommand]
    private void CloseDrivePicker() => ShowDrivePicker = false;

    [RelayCommand]
    private void BeginRip()
    {
        if (!IsDriveSource || _openPath is null)
        {
            return;
        }

        RipFinished = false;
        RipStatus = "";
        RipSha256 = "";
        RipSha1 = "";
        RipMd5 = "";
        RipBadSectors = "";
        RippedIsoPath = null;
        if (string.IsNullOrWhiteSpace(RipOutput))
        {
            RipOutput = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                SanitizeFileName(VolumeTitle) + ".iso");
        }

        ShowRipDialog = true;
    }

    [RelayCommand]
    private async Task StartRip()
    {
        if (IsRipping || _openPath is null || !IsDriveSource)
        {
            return;
        }

        if (!int.TryParse(RipRetriesText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var attempts) || attempts is < 1 or > 20)
        {
            RipStatus = "Retries is a number from 1 to 20.";
            return;
        }

        if (string.IsNullOrWhiteSpace(RipOutput))
        {
            RipStatus = "Choose an ISO file.";
            return;
        }

        IBlockDevice device;
        try
        {
            device = OpticalDrives.Open(_openPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RipStatus = ex.Message;
            return;
        }

        _ripCancel?.Dispose();
        _ripCancel = new CancellationTokenSource();
        IsRipping = true;
        RipFinished = false;
        try
        {
            var progress = new Progress<RipProgress>(update =>
            {
                var percent = update.TotalSectors == 0
                    ? 100
                    : (int)(update.CompletedSectors * 100 / update.TotalSectors);
                RipStatus = string.Create(
                    CultureInfo.CurrentCulture,
                    $"Ripping {percent}%  ·  sector {update.CompletedSectors:N0} of {update.TotalSectors:N0}  ·  bad {update.BadSectors:N0}");
            });
            var result = await IsoRipper.RipAsync(
                device,
                RipOutput.Trim(),
                new RipOptions { Attempts = attempts },
                progress,
                _ripCancel.Token).ConfigureAwait(true);
            RipSha256 = result.Sha256;
            RipSha1 = result.Sha1;
            RipMd5 = result.Md5;
            RipBadSectors = result.BadSectors.Count == 0
                ? "Bad sectors: none"
                : "Bad sectors: " + string.Join(", ", result.BadSectors.Take(12).Select(sector => sector.ToString(CultureInfo.InvariantCulture)))
                    + (result.BadSectors.Count > 12 ? "…" : "");
            RipStatus = result.BadSectors.Count == 0
                ? "Rip finished. No bad sectors."
                : string.Create(CultureInfo.CurrentCulture, $"Rip finished. {result.BadSectors.Count:N0} bad sectors were stored as zeros.");
            RippedIsoPath = result.OutputPath;
            RipFinished = true;
            HasNotification = true;
        }
        catch (OperationCanceledException)
        {
            RipStatus = "Rip cancelled.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            RipStatus = ex.Message;
        }
        finally
        {
            device.Dispose();
            IsRipping = false;
        }
    }

    [RelayCommand]
    private void CancelRip() => _ripCancel?.Cancel();

    [RelayCommand]
    private void CloseRip()
    {
        if (IsRipping)
        {
            _ripCancel?.Cancel();
            return;
        }

        ShowRipDialog = false;
    }

    [RelayCommand]
    private async Task AddRipToLibrary()
    {
        if (string.IsNullOrEmpty(RippedIsoPath))
        {
            return;
        }

        var path = RippedIsoPath;
        ShowRipDialog = false;
        await OpenAsync(path).ConfigureAwait(true);
    }

    public void LoadDiscKey(string path)
    {
        if (!DiscKeyFile.TryLoad(path, out var key, out var error) || key is null)
        {
            _discKey = null;
            DiscKeyStatus = error ?? "That file is not a .ird or a 16-byte disc key.";
            OnPropertyChanged(nameof(HasDiscKey));
            return;
        }

        _discKey = key;
        DiscKeyStatus = "Disc key loaded. Game files stay encrypted in this version.";
        OnPropertyChanged(nameof(HasDiscKey));
    }

    private static string SanitizeFileName(string title)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(title.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray()).Trim().TrimEnd('.');
        return cleaned.Length == 0 ? "disc" : cleaned;
    }

    [RelayCommand]
    private async Task ClearMissing()
    {
        var missing = Recent.Where(item => !PathExists(item.Path)).Select(item => item.Path).ToList();
        foreach (var path in missing)
        {
            await RemoveFromLibrary(path).ConfigureAwait(true);
        }

        LibraryMessage = missing.Count == 0
            ? "No missing images."
            : "Removed " + missing.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + (missing.Count == 1 ? " missing image." : " missing images.");
    }

    [RelayCommand]
    private Task CheckForUpdatesNow() => CheckForUpdatesAsync(startup: false);

    private bool CanRestartToUpdate() => UpdateReady;

    [RelayCommand(CanExecute = nameof(CanRestartToUpdate))]
    private void RestartToUpdate()
    {
        UpdateManager? manager;
        VelopackAsset? asset;
        lock (_updateSync)
        {
            manager = _updates;
            asset = _pendingUpdate;
        }

        if (manager is null || asset is null || !UpdateReady)
        {
            return;
        }

        manager.ApplyUpdatesAndRestart(asset);
    }

    [RelayCommand]
    private void DismissUpdate()
    {
        lock (_updateSync)
        {
            _updateCancel?.Cancel();
        }

        UpdateAvailable = false;
    }

    private async Task CheckForUpdatesAsync(bool startup)
    {
        if (Interlocked.Exchange(ref _updateBusy, 1) == 1)
        {
            return;
        }

        var cancel = new CancellationTokenSource();
        lock (_updateSync)
        {
            _updateCancel?.Cancel();
            _updateCancel = cancel;
        }

        try
        {
            if (!startup)
            {
                UpdateStatus = "Checking for updates.";
            }

            UpdateManager manager;
            try
            {
                manager = UpdateFeed.CreateManager(_settings);
            }
            catch (InvalidOperationException)
            {
                if (!startup)
                {
                    UpdateStatus = "Updates apply to the installed app.";
                }

                return;
            }

            lock (_updateSync)
            {
                _updates = manager;
            }

            if (!manager.IsInstalled)
            {
                if (!startup)
                {
                    UpdateStatus = "Updates apply to the installed app.";
                }

                return;
            }

            var pending = manager.UpdatePendingRestart;
            var info = await manager.CheckForUpdatesAsync().ConfigureAwait(true);
            if (cancel.IsCancellationRequested)
            {
                return;
            }

            if (info is null)
            {
                if (pending is not null)
                {
                    ShowReady(pending);
                }
                else if (!startup)
                {
                    UpdateStatus = "Discfolio is up to date.";
                }

                return;
            }

            if (pending is not null && pending.Version == info.TargetFullRelease.Version)
            {
                ShowReady(pending);
                return;
            }

            var version = info.TargetFullRelease.Version.ToString();
            OnUi(() =>
            {
                UpdateAvailable = true;
                UpdateReady = false;
                UpdateMessage = "Version " + version + " is downloading.";
                UpdateStatus = "Update available.";
            });
            await manager.DownloadUpdatesAsync(
                info,
                progress => OnUi(() => UpdateMessage = "Version " + version + " is downloading (" + progress.ToString(System.Globalization.CultureInfo.InvariantCulture) + "%)."),
                cancel.Token).ConfigureAwait(true);
            if (cancel.IsCancellationRequested)
            {
                return;
            }

            ShowReady(info.TargetFullRelease);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is NotInstalledException)
        {
            if (!startup && !cancel.IsCancellationRequested)
            {
                OnUi(() => UpdateStatus = "Updates apply to the installed app.");
            }
        }
        catch (Exception ex)
        {
            if (!startup && !cancel.IsCancellationRequested)
            {
                var detail = UpdateFeed.PublicError(ex, UpdateFeed.ResolveToken(_settings.UpdateToken));
                OnUi(() => UpdateStatus = detail.Length == 0 ? "The update check failed." : "The update check failed. " + detail);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _updateBusy, 0);
        }
    }

    private void ShowReady(VelopackAsset asset)
    {
        lock (_updateSync)
        {
            _pendingUpdate = asset;
        }

        OnUi(() =>
        {
            UpdateAvailable = true;
            UpdateReady = true;
            UpdateMessage = "Version " + asset.Version + " is ready. Restart Discfolio to install it.";
            UpdateStatus = "Update available.";
        });
    }

    private void OnUi(Action action)
    {
        if (_ui is null || ReferenceEquals(SynchronizationContext.Current, _ui))
        {
            action();
            return;
        }

        _ui.Post(_ => action(), null);
    }

    private void SaveRecent()
    {
        OpenCount = Recent.Count;
        _settings.Recent = Recent.Select(item => new RecentRecord
        {
            Path = item.Path,
            Title = item.Title,
            Meta = item.Meta,
            TitleId = item.TitleId,
            IsDrive = item.IsDrive,
            CacheKey = item.CacheKey
        }).ToList();
        Persist();
        RebuildVisibleRecent();
    }

    private static bool PathExists(string path)
    {
        try
        {
            if (OpticalDrives.IsDrivePath(path))
            {
                return OpticalDrives.MediaPresent(path);
            }

            return File.Exists(path) || Directory.Exists(path);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    private static string FullPath(string path)
    {
        try
        {
            if (OpticalDrives.IsDrivePath(path))
            {
                return OpticalDrives.Canonical(path);
            }

            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            return path;
        }
    }

    private void RebuildVisibleRecent()
    {
        VisibleRecent.Clear();
        var query = SearchText.Trim();
        foreach (var item in Recent)
        {
            if (string.IsNullOrEmpty(query)
                || item.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                || item.TitleId.Contains(query, StringComparison.OrdinalIgnoreCase)
                || item.Meta.Contains(query, StringComparison.OrdinalIgnoreCase)
                || item.Path.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                VisibleRecent.Add(item);
            }
        }
    }

    private bool QueryMatchesOpenTitle(string query)
    {
        if (VolumeTitle.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return !string.IsNullOrEmpty(TitleId) && TitleId.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private string? CurrentSwitchKeys() => string.IsNullOrWhiteSpace(SwitchKeys) ? null : SwitchKeys.Trim();

    private string? ArtVariant()
    {
        var path = CurrentSwitchKeys();
        if (path is null)
        {
            return null;
        }

        long length = 0;
        long ticks = 0;
        try
        {
            if (File.Exists(path))
            {
                var info = new FileInfo(path);
                length = info.Length;
                ticks = info.LastWriteTimeUtc.Ticks;
            }
            else if (Directory.Exists(path))
            {
                ticks = new DirectoryInfo(path).LastWriteTimeUtc.Ticks;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return path;
        }

        return string.Create(CultureInfo.InvariantCulture, $"switch-keys\n{path}\n{length}\n{ticks}");
    }

    private static string ProductVersion()
    {
        var version = typeof(MainViewModel).Assembly.GetName().Version;
        if (version is null)
        {
            return "0.6.1";
        }

        return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{version.Major}.{version.Minor}.{Math.Max(version.Build, 0)}");
    }

    private async Task ClearImageAsync()
    {
        HasImage = false;
        _openPath = null;
        IconPath = null;
        BannerPath = null;
        IsDriveSource = false;
        DriveNotice = "";
        DiscKeyStatus = "";
        _discKey = null;
        OnPropertyChanged(nameof(HasDiscKey));
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

public sealed record RecentItem(
    string Path,
    string Title,
    string Meta,
    string TitleId = "",
    string? IconPath = null,
    bool IsDrive = false,
    string? CacheKey = null)
{
    public string Seed => Path;
}

public partial class DriveRow : ObservableObject
{
    public required string Id { get; init; }
    public required string DevicePath { get; init; }
    public required string VolumeLabel { get; init; }
    public required string SizeText { get; init; }
    public required string Platform { get; init; }
    public string? Notice { get; init; }
    [ObservableProperty] private bool isSelected;
    public string Title => Id + "  ·  " + VolumeLabel;
}
