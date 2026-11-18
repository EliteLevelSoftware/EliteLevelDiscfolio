using System.Runtime.CompilerServices;
using EliteLevel.Disc.App.Services;

namespace EliteLevel.Disc.App.Tests;

/// <summary>
/// UI tests open images through the real window, which remembers them.
/// The data folder is a temp directory for the whole test run so that
/// a synthetic library entry is never written into the account's Discfolio settings.
/// </summary>
internal static class IsolatedData
{
    internal static string Root { get; } = Path.Combine(Path.GetTempPath(), "discfolio-app-data", Guid.NewGuid().ToString("N"));

    [ModuleInitializer]
    internal static void Init()
    {
        Directory.CreateDirectory(Root);
        SettingsStore.UseDirectory(Root);
    }
}
