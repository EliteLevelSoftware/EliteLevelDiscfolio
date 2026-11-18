using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
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
