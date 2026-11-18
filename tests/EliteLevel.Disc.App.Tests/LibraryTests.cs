using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DiscUtils.Iso9660;
using EliteLevel.Disc.App.Services;
using EliteLevel.Disc.App.ViewModels;
using EliteLevel.Disc.Core.Tests;
using Velopack.Sources;

namespace EliteLevel.Disc.App.Tests;

public class LibraryTests
{
    [Fact]
    public void Public_releases_need_no_token()
    {
        Assert.Equal("https://github.com/EliteLevelSoftware/EliteLevelDiscfolio", UpdateFeed.DefaultRepository);
        Assert.Equal(UpdateFeed.DefaultRepository, UpdateFeed.RepositoryUrl(null));
        Assert.Equal(UpdateFeed.DefaultRepository, UpdateFeed.RepositoryUrl("  "));
        Assert.True(UpdateFeed.IsGitHubRepository(UpdateFeed.RepositoryUrl(null)));
        Assert.IsType<GithubSource>(UpdateFeed.CreateSource(null, null, false));
        Assert.IsType<SimpleWebSource>(UpdateFeed.CreateSource("https://example.com/discfolio", null, false));
        Assert.Equal("https://github.com/Other/Repo", UpdateFeed.RepositoryUrl("https://github.com/Other/Repo.git"));
        Assert.Equal(UpdateFeed.RuntimeId() + "-stable", UpdateFeed.ChannelName("stable"));
        Assert.Equal(UpdateFeed.RuntimeId() + "-prerelease", UpdateFeed.ChannelName("prerelease"));
        Assert.Equal(UpdateFeed.RuntimeId() + "-stable", UpdateFeed.ChannelName("other"));
    }

    [Fact]
    public void Token_is_an_optional_local_override()
    {
        var previous = Environment.GetEnvironmentVariable(UpdateFeed.TokenEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(UpdateFeed.TokenEnvironmentVariable, null);
            Assert.Null(UpdateFeed.ResolveToken(null));
            Assert.Null(UpdateFeed.ResolveToken("  "));
            Assert.Equal("local-token", UpdateFeed.ResolveToken(" local-token "));

            Environment.SetEnvironmentVariable(UpdateFeed.TokenEnvironmentVariable, " env-token ");
            Assert.Equal("env-token", UpdateFeed.ResolveToken("local-token"));
            var sanitized = UpdateFeed.PublicError(new InvalidOperationException("rejected env-token"), "env-token");
            Assert.DoesNotContain("env-token", sanitized);

            using var scope = new SettingsScope();
            SettingsStore.Save(new AppSettings { UpdateToken = "local-token" });
            var text = File.ReadAllText(SettingsStore.FilePath);
            Assert.Contains("local-token", text);
            Assert.DoesNotContain("env-token", text);
        }
        finally
        {
            Environment.SetEnvironmentVariable(UpdateFeed.TokenEnvironmentVariable, previous);
        }
    }

    [Fact]
    public async Task Check_now_does_not_download_when_the_app_is_not_installed()
    {
        using var scope = new SettingsScope();
        var model = new MainViewModel();
        Assert.True(model.CheckUpdatesOnStartup);
        Assert.Equal("stable", model.UpdateChannel);
        Assert.Equal("", model.UpdateSource);

        await model.CheckForUpdatesNowCommand.ExecuteAsync(null);

        Assert.False(model.UpdateAvailable);
        Assert.False(model.UpdateReady);
        Assert.Equal("Updates apply to the installed app.", model.UpdateStatus);
    }

    [Fact]
    public async Task Remove_and_clear_missing_leave_the_image_file()
    {
        using var scope = new SettingsScope();
        var keep = Path.Combine(scope.Root, "keep.iso");
        var gone = Path.Combine(scope.Root, "gone.iso");
        var keepBytes = Encoding.ASCII.GetBytes("keep-image");
        await File.WriteAllBytesAsync(keep, keepBytes);
        await File.WriteAllBytesAsync(gone, "gone-image"u8.ToArray());
        var cache = new EliteLevel.Disc.Core.Titles.TitleArtCache(SettingsStore.CacheDirectory);
        var kept = cache.Store(keep, new EliteLevel.Disc.Core.Titles.TitleMetadata
        {
            Title = "Keep",
            Icon = SyntheticTitles.Png(3, 4, 5)
        });
        var removed = cache.Store(gone, new EliteLevel.Disc.Core.Titles.TitleMetadata
        {
            Title = "Gone",
            Icon = SyntheticTitles.Png(6, 7, 8)
        });
        SettingsStore.Save(new AppSettings
        {
            Recent =
            [
                new RecentRecord { Path = keep, Title = "Keep", Meta = "ISO" },
                new RecentRecord { Path = gone, Title = "Gone", Meta = "ISO" }
            ]
        });
        File.Delete(gone);

        var model = new MainViewModel();
        Assert.Equal(2, model.Recent.Count);
        await model.ClearMissingCommand.ExecuteAsync(null);

        Assert.Equal("Removed 1 missing image.", model.LibraryMessage);
        Assert.Equal(new[] { keep }, model.Recent.Select(item => item.Path).ToArray());
        Assert.Equal(keepBytes, await File.ReadAllBytesAsync(keep));
        Assert.False(File.Exists(gone));
        Assert.NotNull(kept.IconPath);
        Assert.True(File.Exists(kept.IconPath));
        Assert.NotNull(removed.IconPath);
        Assert.False(File.Exists(removed.IconPath));
        Assert.Contains("Keep", await File.ReadAllTextAsync(SettingsStore.FilePath));
        Assert.DoesNotContain("Gone", await File.ReadAllTextAsync(SettingsStore.FilePath));
    }

    [AvaloniaFact]
    public async Task Window_wraps_a_long_title_and_can_remove_it_from_the_library()
    {
        var directory = Path.Combine(Path.GetTempPath(), "discfolio-ui", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var iso = Path.Combine(directory, "pirates.iso");
        const string title = "Pirates of the Caribbean: At World's End";
        try
        {
            var builder = new CDBuilder { UseJoliet = true, VolumeIdentifier = "POTCAWE" };
            builder.AddFile(@"PS3_GAME\PARAM.SFO", SyntheticTitles.Sfo(title, "BLUS31147", "01.00", "DG", 5));
            builder.AddFile(@"PS3_GAME\ICON0.PNG", SyntheticTitles.Png(12, 34, 56));
            builder.Build(iso);
            var before = await File.ReadAllBytesAsync(iso);

            var window = new MainWindow();
            window.Show();
            var model = Assert.IsType<MainViewModel>(window.DataContext);
            await model.OpenAsync(iso);
            Assert.Equal(title, model.DetailTitle);
            Assert.True(model.ShowBrowser);
            window.UpdateLayout();

            var side = window.GetVisualDescendants().OfType<TextBlock>().First(block => block.Text == title && block.FontSize == 22);
            Assert.Equal(TextWrapping.Wrap, side.TextWrapping);
            Assert.Equal(2, side.MaxLines);
            Assert.Equal(TextTrimming.CharacterEllipsis, side.TextTrimming);
            Assert.Equal(title, ToolTip.GetTip(side));

            var hero = window.GetVisualDescendants().OfType<TextBlock>().First(block => block.Text == title && block.FontSize != 22);
            Assert.Equal(TextWrapping.Wrap, hero.TextWrapping);
            Assert.Equal(2, hero.MaxLines);
            Assert.Equal(TextTrimming.CharacterEllipsis, hero.TextTrimming);
            Assert.Equal(title, ToolTip.GetTip(hero));
            Assert.True(hero.FontSize < 48);

            var tile = window.GetVisualDescendants().OfType<Button>().First(button => button.Classes.Contains("el-tile") && button.Tag as string == iso);
            Assert.Equal(title, ToolTip.GetTip(tile));
            var tileTitle = tile.GetVisualDescendants().OfType<TextBlock>().First(block => block.Text == title);
            Assert.Equal(2, tileTitle.MaxLines);
            Assert.Equal(TextWrapping.Wrap, tileTitle.TextWrapping);
            tile.ContextMenu!.Open(tile);
            var menuItem = Assert.IsAssignableFrom<MenuItem>(Assert.Single(tile.ContextMenu.Items));
            Assert.Equal("Remove from library", menuItem.Header);
            Assert.Equal(iso, tile.Tag);
            Assert.Contains(window.GetVisualDescendants().OfType<Button>(), button => button.Content as string == "Remove from library");
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), block => block.Text == "Clear missing");

            await model.RemoveOpenFromLibraryCommand.ExecuteAsync(null);
            Assert.Equal(before, await File.ReadAllBytesAsync(iso));
            Assert.False(model.HasImage);
            Assert.DoesNotContain(model.Recent, item => item.Path == iso);
            window.Close();
        }
        finally
        {
            try
            {
                Directory.Delete(directory, true);
            }
            catch (IOException)
            {
            }
        }
    }

    [AvaloniaFact]
    public async Task Context_menu_removes_a_library_entry_and_persists_it()
    {
        var directory = Path.Combine(Path.GetTempPath(), "discfolio-ui", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var listed = BuildImage(directory, "listed.iso", "Listed Harbor", "BLUS30011");
        var open = BuildImage(directory, "open.iso", "Open Harbor", "BLUS30012");
        try
        {
            var listedBytes = await File.ReadAllBytesAsync(listed);
            var openBytes = await File.ReadAllBytesAsync(open);
            var window = new MainWindow();
            window.Show();
            var model = Assert.IsType<MainViewModel>(window.DataContext);
            model.DismissWelcomeCommand.Execute(null);
            await model.OpenAsync(listed);
            await model.OpenAsync(open);
            window.UpdateLayout();
            var listedIcon = model.Recent.Single(item => item.Path == listed).IconPath;
            Assert.False(string.IsNullOrEmpty(listedIcon));
            Assert.True(File.Exists(listedIcon));
            Assert.Contains(model.VisibleRecent, item => item.Path == listed);
            Assert.Contains(model.VisibleRecent, item => item.Path == open);

            await ActivateLibraryMenuAsync(window, listed);

            Assert.True(model.HasImage);
            Assert.Contains(model.Recent, item => item.Path == open);
            Assert.DoesNotContain(model.Recent, item => item.Path == listed);
            Assert.DoesNotContain(model.VisibleRecent, item => item.Path == listed);
            Assert.Contains(model.VisibleRecent, item => item.Path == open);
            window.UpdateLayout();
            Assert.DoesNotContain(
                window.GetVisualDescendants().OfType<Button>(),
                button => button.IsEffectivelyVisible && button.Classes.Contains("el-tile") && button.Tag as string == listed);
            Assert.Equal(listedBytes, await File.ReadAllBytesAsync(listed));
            Assert.False(File.Exists(listedIcon));
            Assert.DoesNotContain(SettingsStore.Load().Recent, item => item.Path == listed);
            Assert.Contains(SettingsStore.Load().Recent, item => item.Path == open);

            model.ShowPageCommand.Execute("open");
            window.UpdateLayout();
            await ActivateLibraryMenuAsync(window, open);

            Assert.False(model.HasImage);
            Assert.DoesNotContain(model.Recent, item => item.Path == open);
            Assert.DoesNotContain(model.VisibleRecent, item => item.Path == open);
            Assert.Equal(openBytes, await File.ReadAllBytesAsync(open));
            var saved = SettingsStore.Load().Recent;
            Assert.DoesNotContain(saved, item => item.Path == listed || item.Path == open);
            window.Close();

            var again = new MainWindow();
            again.Show();
            var restored = Assert.IsType<MainViewModel>(again.DataContext);
            Assert.DoesNotContain(restored.Recent, item => item.Path == listed || item.Path == open);
            Assert.DoesNotContain(restored.VisibleRecent, item => item.Path == listed || item.Path == open);
            again.Close();
        }
        finally
        {
            TryDelete(directory);
        }
    }

    [AvaloniaFact]
    public async Task Clear_missing_and_open_panel_remove_buttons_update_the_library()
    {
        var directory = Path.Combine(Path.GetTempPath(), "discfolio-ui", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var keep = BuildImage(directory, "keep.iso", "Kept Harbor", "BLUS30021");
        var missing = BuildImage(directory, "missing.iso", "Missing Harbor", "BLUS30022");
        var open = BuildImage(directory, "panel.iso", "Panel Harbor", "BLUS30023");
        try
        {
            var keepBytes = await File.ReadAllBytesAsync(keep);
            var openBytes = await File.ReadAllBytesAsync(open);
            var window = new MainWindow();
            window.Show();
            var model = Assert.IsType<MainViewModel>(window.DataContext);
            model.DismissWelcomeCommand.Execute(null);
            await model.OpenAsync(keep);
            await model.OpenAsync(missing);
            await model.OpenAsync(keep);
            File.Delete(missing);
            var missingIcon = model.Recent.Single(item => item.Path == missing).IconPath;
            var keepIcon = model.Recent.Single(item => item.Path == keep).IconPath;
            Assert.False(string.IsNullOrEmpty(missingIcon));
            Assert.False(string.IsNullOrEmpty(keepIcon));
            Assert.True(File.Exists(missingIcon));
            Assert.True(File.Exists(keepIcon));
            Assert.False(EntryExists(missing));
            window.UpdateLayout();
            var before = model.Recent.Select(item => item.Path).ToList();

            await ClickAsync(window, FindButton(window, "Clear missing"));

            var removed = before.Where(path => model.Recent.All(item => item.Path != path)).ToList();
            Assert.Contains(missing, removed);
            Assert.DoesNotContain(keep, removed);
            Assert.Equal(MissingMessage(removed.Count), model.LibraryMessage);
            Assert.DoesNotContain(model.Recent, item => item.Path == missing);
            Assert.DoesNotContain(model.VisibleRecent, item => item.Path == missing);
            Assert.Contains(model.Recent, item => item.Path == keep);
            Assert.Contains(model.VisibleRecent, item => item.Path == keep);
            Assert.Equal(keepBytes, await File.ReadAllBytesAsync(keep));
            Assert.False(File.Exists(missing));
            Assert.False(File.Exists(missingIcon));
            Assert.True(File.Exists(keepIcon));
            var afterClear = SettingsStore.Load().Recent;
            Assert.DoesNotContain(afterClear, item => item.Path == missing);
            Assert.Contains(afterClear, item => item.Path == keep);

            await model.OpenAsync(open);
            window.UpdateLayout();
            var openIcon = model.IconPath;
            Assert.False(string.IsNullOrEmpty(openIcon));
            Assert.True(File.Exists(openIcon));
            await ClickAsync(window, FindButton(window, "Remove from library"));

            Assert.False(model.HasImage);
            Assert.DoesNotContain(model.Recent, item => item.Path == open);
            Assert.DoesNotContain(model.VisibleRecent, item => item.Path == open);
            Assert.Contains(model.VisibleRecent, item => item.Path == keep);
            Assert.Equal(openBytes, await File.ReadAllBytesAsync(open));
            Assert.False(File.Exists(openIcon));
            Assert.True(File.Exists(keepIcon));
            var saved = SettingsStore.Load().Recent;
            Assert.DoesNotContain(saved, item => item.Path == open || item.Path == missing);
            Assert.Contains(saved, item => item.Path == keep);
            window.Close();

            var again = new MainWindow();
            again.Show();
            var restored = Assert.IsType<MainViewModel>(again.DataContext);
            Assert.DoesNotContain(restored.Recent, item => item.Path == open || item.Path == missing);
            Assert.Contains(restored.Recent, item => item.Path == keep);
            Assert.Contains(restored.VisibleRecent, item => item.Path == keep);
            again.Close();
        }
        finally
        {
            TryDelete(directory);
        }
    }

    private static async Task ActivateLibraryMenuAsync(Window window, string path)
    {
        window.UpdateLayout();
        var host = window.GetVisualDescendants().OfType<Button>().First(button =>
            button.IsEffectivelyVisible
            && button.Tag as string == path
            && button.ContextMenu is not null);
        var menu = host.ContextMenu!;
        menu.Open(host);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        var item = Assert.IsAssignableFrom<MenuItem>(Assert.Single(menu.Items));
        Assert.Equal("Remove from library", item.Header);
        Assert.Equal(path, Assert.IsType<RecentItem>(item.DataContext).Path);
        await ClickAsync(window, item);
    }

    private static async Task ClickAsync(Window window, Control control)
    {
        window.UpdateLayout();
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window);
        Assert.NotNull(point);
        window.MouseMove(point.Value);
        window.MouseDown(point.Value, MouseButton.Left);
        window.MouseUp(point.Value, MouseButton.Left);
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
    }

    private static Button FindButton(Window window, string label)
    {
        return window.GetVisualDescendants().OfType<Button>().First(button =>
            button.IsEffectivelyVisible
            && button.Bounds.Width > 1
            && button.Bounds.Height > 1
            && (button.Content as string == label
                || button.GetVisualDescendants().OfType<TextBlock>().Any(block => block.Text == label)));
    }

    private static string BuildImage(string directory, string name, string title, string titleId)
    {
        var iso = Path.Combine(directory, name);
        var builder = new CDBuilder { UseJoliet = true, VolumeIdentifier = "HARBOR" };
        builder.AddFile(@"PS3_GAME\PARAM.SFO", SyntheticTitles.Sfo(title, titleId, "01.00", "DG", 5));
        builder.AddFile(@"PS3_GAME\ICON0.PNG", SyntheticTitles.Png(12, 34, 56));
        builder.Build(iso);
        return iso;
    }

    private static bool EntryExists(string path)
    {
        try
        {
            return File.Exists(path) || Directory.Exists(path);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    private static string MissingMessage(int count) =>
        "Removed " + count.ToString(System.Globalization.CultureInfo.InvariantCulture) + (count == 1 ? " missing image." : " missing images.");

    private static void TryDelete(string directory)
    {
        try
        {
            Directory.Delete(directory, true);
        }
        catch (IOException)
        {
        }
    }

    private sealed class SettingsScope : IDisposable
    {
        private readonly string _previous = SettingsStore.DirectoryPath;

        public SettingsScope()
        {
            Root = Path.Combine(Path.GetTempPath(), "discfolio-settings", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            SettingsStore.UseDirectory(Root);
        }

        public string Root { get; }

        public void Dispose()
        {
            SettingsStore.UseDirectory(_previous);
            try
            {
                Directory.Delete(Root, true);
            }
            catch (IOException)
            {
            }
        }
    }
}
