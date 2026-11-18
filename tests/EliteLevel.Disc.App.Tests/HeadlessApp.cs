using Avalonia;
using Avalonia.Fonts.Inter;
using Avalonia.Headless;
using EliteLevel.Disc.App;

[assembly: AvaloniaTestApplication(typeof(EliteLevel.Disc.App.Tests.HeadlessApp))]

namespace EliteLevel.Disc.App.Tests;

public class HeadlessApp
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
        .WithInterFont();
}
