using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using DiscUtils.Iso9660;
using EliteLevel.Disc.App.Services;
using EliteLevel.Disc.App.ViewModels;
using EliteLevel.Disc.Core.Drives;
using EliteLevel.Disc.Core.Tests;

namespace EliteLevel.Disc.App.Tests;

public class WindowTests
{
    [AvaloniaFact]
    public void Extract_toggle_decompresses_ncz_entries()
    {
        var window = new MainWindow();
        window.Show();
        var model = Assert.IsType<MainViewModel>(window.DataContext);
        Assert.False(model.DecompressNcz);
        model.Page = "extract";
        var toggle = window.GetVisualDescendants()
            .OfType<CheckBox>()
            .FirstOrDefault(box => box.Content as string == "Decompress NCZ entries to NCA");
        Assert.NotNull(toggle);
        toggle.IsChecked = true;
        Assert.True(model.DecompressNcz);
        window.Close();
    }

    [AvaloniaFact]
    public void Window_shows_one_primary_and_the_credit()
    {
        var window = new MainWindow();
        window.Show();
        var model = Assert.IsType<MainViewModel>(window.DataContext);
        model.DismissWelcomeCommand.Execute(null);
        Assert.False(model.ShowWelcome);
        Assert.Equal("Discfolio " + model.VersionLabel + " · by Elite Level Software", model.CreditLine);
        Assert.StartsWith("v", model.VersionLabel);
        Assert.DoesNotContain("0.0.0", model.VersionLabel);

        var text = window.GetVisualDescendants().OfType<TextBlock>().Select(block => block.Text).ToList();
        Assert.Contains("Discfolio", text);
        Assert.Contains(model.VersionLabel, text);
        Assert.Contains("·", text);
        Assert.Contains("by Elite Level Software", text);

        var primaries = window.GetVisualDescendants().OfType<Button>()
            .Where(button => button.Classes.Contains("el-primary") && button.IsEffectivelyVisible)
            .Select(button => button.Content as string)
            .ToList();
        Assert.Equal(new[] { "Open image" }, primaries);

        model.OpenAboutCommand.Execute(null);
        window.UpdateLayout();
        Assert.True(model.ShowAbout);
        text = window.GetVisualDescendants().OfType<TextBlock>().Select(block => block.Text).ToList();
        Assert.Contains("by Elite Level Software", text);
        Assert.Contains("A product of Elite Level", text);
        Assert.Contains("Plus Jakarta Sans and DM Mono are included under the SIL Open Font License. Both notices ship with Discfolio.", text);
        window.Close();
    }

    [AvaloniaFact]
    public void Window_opens_on_the_empty_state()
    {
        var window = new MainWindow();
        window.Show();
        var model = Assert.IsType<MainViewModel>(window.DataContext);
        Assert.True(window.IsVisible);
        Assert.True(model.ShowEmpty);
        Assert.Equal("Read-only library", "Read-only library");
        window.Close();
    }

    [AvaloniaFact]
    public async Task Window_loads_a_synthetic_iso()
    {
        var directory = Path.Combine(Path.GetTempPath(), "discfolio-ui", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var iso = Path.Combine(directory, "northwind.iso");
        try
        {
            var builder = new CDBuilder { UseJoliet = true, VolumeIdentifier = "NORTHWIND" };
            builder.AddFile(@"PS3_GAME\ICON0.PNG", Encoding.ASCII.GetBytes("icon"));
            builder.AddFile(@"PS3_GAME\USRDIR\DATA.BIN", Encoding.ASCII.GetBytes("hello"));
            builder.Build(iso);

            var accountSettings = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "EliteLevel",
                "Discfolio",
                "settings.json");
            var before = File.Exists(accountSettings) ? await File.ReadAllBytesAsync(accountSettings) : null;

            var window = new MainWindow();
            window.Show();
            var model = Assert.IsType<MainViewModel>(window.DataContext);
            await model.OpenAsync(iso);
            Assert.True(model.HasImage);
            Assert.True(model.ShowBrowser);
            Assert.Equal("NORTHWIND", model.VolumeTitle);
            Assert.Contains("PlayStation 3", model.ChipLine);
            Assert.Contains(model.Rows, row => row.Name == "PS3_GAME");
            model.SelectRowCommand.Execute("/PS3_GAME");
            model.ToggleRowCommand.Execute("/PS3_GAME");
            Assert.Contains(model.Rows, row => row.Name == "ICON0.PNG");

            Assert.Equal(IsolatedData.Root, SettingsStore.DirectoryPath);
            Assert.StartsWith(Path.GetTempPath(), SettingsStore.FilePath, StringComparison.Ordinal);
            Assert.Contains("NORTHWIND", await File.ReadAllTextAsync(SettingsStore.FilePath));
            var after = File.Exists(accountSettings) ? await File.ReadAllBytesAsync(accountSettings) : null;
            Assert.Equal(before, after);
            Assert.Equal(new[] { "northwind.iso" }, Directory.GetFiles(directory).Select(Path.GetFileName).ToArray());

            model.SearchText = "NORTHWIND";
            Assert.Contains(model.Rows, row => row.Name == "PS3_GAME");
            Assert.Contains(model.VisibleRecent, item => item.Title == "NORTHWIND");
            model.SearchText = "not-in-this-image";
            Assert.Empty(model.Rows);
            Assert.Empty(model.VisibleRecent);
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
    public async Task Window_shows_sfo_title_art_and_title_id()
    {
        var directory = Path.Combine(Path.GetTempPath(), "discfolio-ui", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var iso = Path.Combine(directory, "traders.iso");
        try
        {
            var icon = SyntheticTitles.Png(12, 34, 56);
            var banner = SyntheticTitles.Png(90, 10, 20);
            var builder = new CDBuilder { UseJoliet = true, VolumeIdentifier = "POTCAWE" };
            builder.AddFile(@"PS3_GAME\PARAM.SFO", SyntheticTitles.Sfo("Northwind Traders", "BLUS00001", "01.02", "DG", 5));
            builder.AddFile(@"PS3_GAME\ICON0.PNG", icon);
            builder.AddFile(@"PS3_GAME\PIC1.PNG", banner);
            builder.Build(iso);

            var window = new MainWindow();
            window.Show();
            var model = Assert.IsType<MainViewModel>(window.DataContext);
            await model.OpenAsync(iso);
            Assert.Equal("Northwind Traders", model.VolumeTitle);
            Assert.Equal("Northwind Traders", model.DetailTitle);
            Assert.Equal("BLUS00001", model.TitleId);
            Assert.True(model.HasTitleId);
            Assert.NotNull(model.IconPath);
            Assert.NotNull(model.BannerPath);
            Assert.StartsWith(Path.GetFullPath(SettingsStore.CacheDirectory), Path.GetFullPath(model.IconPath));
            Assert.Equal(icon, await File.ReadAllBytesAsync(model.IconPath));
            Assert.Equal(banner, await File.ReadAllBytesAsync(model.BannerPath));
            using (var decoded = new Bitmap(model.IconPath))
            {
                Assert.Equal(new global::Avalonia.PixelSize(1, 1), decoded.PixelSize);
            }

            var bannerArt = EliteLevel.Avalonia.Controls.TitleArt.LoadBanner(model.BannerPath, model.IconPath, 64, 40);
            var softened = EliteLevel.Avalonia.Controls.TitleArt.LoadBanner(null, model.IconPath, 64, 40);
            Assert.Equal(new global::Avalonia.PixelSize(1, 1), bannerArt!.PixelSize);
            Assert.Equal(new global::Avalonia.PixelSize(64, 40), softened!.PixelSize);

            model.SearchText = "BLUS00001";
            Assert.Contains(model.Rows, row => row.Name == "PS3_GAME");
            Assert.Contains(model.VisibleRecent, item => item.TitleId == "BLUS00001");
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
    public async Task Window_opens_a_drive_and_rips_it()
    {
        var directory = Path.Combine(Path.GetTempPath(), "discfolio-ui", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var iso = Path.Combine(directory, "drive.iso");
        try
        {
            var builder = new CDBuilder { UseJoliet = true, VolumeIdentifier = "NORTHWIND" };
            builder.AddFile("readme.txt", Encoding.ASCII.GetBytes("hello"));
            builder.Build(iso);
            var bytes = await File.ReadAllBytesAsync(iso);
            var source = new MemoryDriveSource();
            source.Add(new OpticalDrive("D:", @"\\.\D:", "NORTHWIND", bytes.LongLength, true, false), new MemoryBlockDevice(bytes) { Id = "D:" });
            using (OpticalDrives.Override(source))
            {
                var window = new MainWindow();
                window.Show();
                var labels = window.GetVisualDescendants().OfType<Button>().Select(button => button.Content as string).ToList();
                Assert.Contains("Open disc drive", labels);
                var model = Assert.IsType<MainViewModel>(window.DataContext);
                await model.OpenAsync("D:");
                Assert.True(model.HasImage);
                Assert.True(model.IsDriveSource);
                Assert.Equal("NORTHWIND", model.VolumeTitle);
                Assert.Contains(model.Recent, item => item.IsDrive);
                Assert.Contains("Disc drive", model.Recent[0].Meta);

                model.RipOutput = Path.Combine(directory, "ripped.iso");
                model.RipRetriesText = "4";
                await model.StartRipCommand.ExecuteAsync(null);
                Assert.True(model.RipFinished);
                Assert.Contains("No bad sectors", model.RipStatus);
                Assert.Equal(64, model.RipSha256.Length);
                Assert.Equal(bytes, await File.ReadAllBytesAsync(model.RipOutput));
                Assert.True(File.Exists(model.RipOutput + ".sha256"));
                Assert.True(File.Exists(model.RipOutput + ".sha1"));
                Assert.True(File.Exists(model.RipOutput + ".md5"));

                await model.AddRipToLibraryCommand.ExecuteAsync(null);
                Assert.Contains(model.Recent, item => item.Path == model.RippedIsoPath && !item.IsDrive);
                Assert.Equal(bytes, await File.ReadAllBytesAsync(iso));
                window.Close();
            }
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
    public async Task Window_lists_a_switch_nsp_and_asks_for_keys()
    {
        var directory = Path.Combine(Path.GetTempPath(), "discfolio-ui", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var nsp = Path.Combine(directory, "northwind.nsp");
        try
        {
            await File.WriteAllBytesAsync(nsp, TinyPfs0(("note.txt", "hello"u8.ToArray()), ("control.nca", new byte[32])));
            var window = new MainWindow();
            window.Show();
            var model = Assert.IsType<MainViewModel>(window.DataContext);
            await model.OpenAsync(nsp);
            Assert.True(model.HasImage);
            Assert.Contains("Nintendo Switch", model.ChipLine);
            Assert.Equal("Switch", model.PlatformTag);
            Assert.Contains("does not include keys", model.DriveNotice);
            Assert.Contains(model.Rows, row => row.Name == "note.txt");
            model.SelectRowCommand.Execute("/note.txt");
            Assert.Equal("/note.txt", model.SelectedPath);
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

    private static byte[] TinyPfs0(params (string Name, byte[] Data)[] files)
    {
        var names = files.Select(file => System.Text.Encoding.UTF8.GetBytes(file.Name + "\0")).ToArray();
        var strings = names.Sum(name => name.Length);
        var header = 0x10 + (files.Length * 0x18) + strings;
        var size = header + files.Sum(file => file.Data.Length);
        var result = new byte[size];
        "PFS0"u8.CopyTo(result);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(4), files.Length);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(8), strings);
        var nameAt = 0;
        var dataAt = 0;
        for (var i = 0; i < files.Length; i++)
        {
            var at = 0x10 + (i * 0x18);
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(result.AsSpan(at), dataAt);
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(result.AsSpan(at + 8), files[i].Data.Length);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(at + 0x10), nameAt);
            names[i].CopyTo(result.AsSpan(0x10 + (files.Length * 0x18) + nameAt));
            files[i].Data.CopyTo(result.AsSpan(header + dataAt));
            nameAt += names[i].Length;
            dataAt += files[i].Data.Length;
        }

        return result;
    }
}
