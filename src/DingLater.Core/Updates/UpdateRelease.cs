using DingLater.Core.Models;

namespace DingLater.Core.Updates;

public sealed record UpdateRelease(Version Version, Uri ReleaseUri, Uri DownloadUri, string FileName, long Size, string Sha256);

public sealed record PreparedUpdate(UpdateRelease Release, string ArchivePath, string Directory);

public interface IUpdateClient
{
    Task<UpdateRelease?> CheckAsync(Version currentVersion, CancellationToken cancellationToken);
    Task<PreparedUpdate> DownloadAsync(UpdateRelease release, string directory, IProgress<double> progress, CancellationToken cancellationToken);
}

public static class UpdatePolicy
{
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

    public static bool IsDue(AppSettings settings, DateTimeOffset now) =>
        settings.AutomaticallyCheckUpdates &&
        (settings.LastUpdateCheckUtc is not { } last || last > now || now - last >= CheckInterval);

    public static string DownloadDirectory(AppSettings settings) =>
        string.IsNullOrWhiteSpace(settings.UpdateDownloadDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "DingLater")
            : Path.GetFullPath(settings.UpdateDownloadDirectory);

    public static bool TryParseVersion(string? tag, out Version version)
    {
        version = new Version(0, 0, 0);
        var value = tag?.TrimStart('v');
        if (value is null || value.Split('.').Length != 3 || !Version.TryParse(value, out var parsed))
        {
            return false;
        }

        version = parsed;
        return true;
    }
}
