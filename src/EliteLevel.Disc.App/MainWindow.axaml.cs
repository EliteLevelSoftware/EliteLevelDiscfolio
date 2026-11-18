using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using EliteLevel.Disc.App.ViewModels;

namespace EliteLevel.Disc.App;

public partial class MainWindow : EliteLevel.Avalonia.Controls.EliteWindow
{
    public MainWindow()
    {
        InitializeComponent();
        DragDrop.AddDropHandler(this, OnDrop);
        DragDrop.AddDragOverHandler(this, OnDragOver);
        Opened += (_, _) =>
        {
            if (DataContext is MainViewModel viewModel)
            {
                viewModel.OnFirstShown();
                viewModel.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName is nameof(MainViewModel.Page) or nameof(MainViewModel.IsLibrary))
                    {
                        MoveMarker();
                    }
                };
            }

            MoveMarker();
            var startup = Program.StartupArgs.FirstOrDefault(arg => !arg.StartsWith('-'));
            if (!string.IsNullOrEmpty(startup) && DataContext is MainViewModel vm)
            {
                _ = vm.OpenAsync(startup);
            }

            try
            {
                Icon = new WindowIcon(global::Avalonia.Platform.AssetLoader.Open(new Uri("avares://EliteLevel.Disc.App/Assets/icon.png")));
            }
            catch (Exception)
            {
            }
        };
    }

    private MainViewModel? Model => DataContext as MainViewModel;

    private async void OnOpenFile(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open disc image",
            AllowMultiple = false
        });
        var path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (path is not null && Model is not null)
        {
            await Model.OpenAsync(path);
        }
    }

    private async void OnOpenFolder(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Open folder dump",
            AllowMultiple = false
        });
        var path = folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
        if (path is not null && Model is not null)
        {
            await Model.OpenAsync(path);
        }
    }

    private async void OnBrowseExtract(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Extract to",
            AllowMultiple = false
        });
        var path = folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
        if (path is not null && Model is not null)
        {
            Model.ExtractDirectory = path;
        }
    }

    private void OnRow(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string path })
        {
            Model?.SelectRowCommand.Execute(path);
        }
    }

    private void OnRowExpand(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string path })
        {
            Model?.ToggleRowCommand.Execute(path);
        }
    }

    private void OnRecent(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string path })
        {
            _ = Model?.OpenAsync(path);
        }
    }

    private void OnRemoveFromLibrary(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item)
        {
            return;
        }

        var path = item.Tag as string;
        if (string.IsNullOrEmpty(path) && item.Parent is ContextMenu { PlacementTarget: Control target })
        {
            path = target.Tag as string;
        }

        if (!string.IsNullOrEmpty(path))
        {
            _ = Model?.RemoveFromLibraryCommand.ExecuteAsync(path);
        }
    }

    private async void OnCopyHash(object? sender, RoutedEventArgs e)
    {
        if (Model?.HashFull is null)
        {
            return;
        }

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null)
        {
            await clipboard.SetTextAsync(Model.HashFull);
            Model.ExtractStatus = "Copied the hash.";
            Model.HasNotification = true;
        }
    }

    private async void OnCopyDetail(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(Model?.ErrorDetail) && string.IsNullOrEmpty(Model?.ErrorMessage))
        {
            return;
        }

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null && Model is not null)
        {
            await clipboard.SetTextAsync(Model.ErrorDetail ?? Model.ErrorMessage);
        }
    }

    private async void OnSaveManifest(object? sender, RoutedEventArgs e)
    {
        if (Model is null)
        {
            return;
        }

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save manifest",
            DefaultExtension = "sha256",
            SuggestedFileName = "manifest.sha256"
        });
        var path = file?.TryGetLocalPath();
        if (path is not null)
        {
            await Model.SaveManifestAsync(path);
        }
    }

    private void OnDrag(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is global::Avalonia.Visual visual && visual.FindAncestorOfType<Button>() is not null)
        {
            return;
        }

        if (e.Source is global::Avalonia.Visual text && text.FindAncestorOfType<TextBox>() is not null)
        {
            return;
        }

        DragMove(e);
    }

    private void OnMinimize(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximize(object? sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        if (WindowState == WindowState.Normal)
        {
            CloseButton.Classes.Add("round");
        }
        else
        {
            CloseButton.Classes.Remove("round");
        }
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private void OnResizeTop(object? sender, PointerPressedEventArgs e) => BeginResizeDrag(WindowEdge.North, e);
    private void OnResizeBottom(object? sender, PointerPressedEventArgs e) => BeginResizeDrag(WindowEdge.South, e);
    private void OnResizeLeft(object? sender, PointerPressedEventArgs e) => BeginResizeDrag(WindowEdge.West, e);
    private void OnResizeRight(object? sender, PointerPressedEventArgs e) => BeginResizeDrag(WindowEdge.East, e);

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.O)
        {
            OnOpenFile(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.K)
        {
            SearchBox.Focus();
            e.Handled = true;
        }
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.F)
        {
            SearchBox.Focus();
            e.Handled = true;
        }
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.E && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            Model?.BeginExtractSelectedCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.KeyModifiers.HasFlag(KeyModifiers.Shift) && e.Key == Key.E)
        {
            Model?.BeginExtractAllCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.H)
        {
            _ = Model?.HashSelectionAsync();
            e.Handled = true;
        }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = DragDropEffects.Copy;
        e.Handled = true;
    }

#pragma warning disable CS0618
    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (!e.Data.Contains(DataFormats.Files) || Model is null)
        {
            return;
        }

        var path = e.Data.GetFiles()?.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrEmpty(path))
        {
            await Model.OpenAsync(path);
        }
    }
#pragma warning restore CS0618

    private void MoveMarker()
    {
        Control target = Model?.Page switch
        {
            "open" => NavOpen,
            "extract" => NavExtract,
            "verify" => NavVerify,
            "settings" => NavSettings,
            "help" => NavHelp,
            _ => NavLibrary
        };
        var point = global::Avalonia.VisualExtensions.TranslatePoint(target, new global::Avalonia.Point(0, Math.Max(0, (target.Bounds.Height - 24) / 2)), NavColumn);
        if (point is not null && Marker.RenderTransform is TranslateTransform shift)
        {
            shift.Y = point.Value.Y;
        }
    }
}
