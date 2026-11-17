using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using DiscUtils.Iso9660;
using EliteLevel.Disc.App.ViewModels;

namespace EliteLevel.Disc.App.Tests;

public class WindowTests
{
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
}
