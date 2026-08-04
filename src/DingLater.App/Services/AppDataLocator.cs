using System.IO;

namespace DingLater.App.Services;

internal static class AppDataLocator
{
    internal static string Resolve()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DingLater");
}
