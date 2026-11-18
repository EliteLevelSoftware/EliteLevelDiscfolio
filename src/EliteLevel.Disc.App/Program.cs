using Avalonia;
using Avalonia.Fonts.Inter;
using Velopack;

namespace EliteLevel.Disc.App;

internal static class Program
{
    public static string[] StartupArgs { get; private set; } = [];

    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build().SetAutoApplyOnStartup(false).Run();
        StartupArgs = args;
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
