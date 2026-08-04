using System.IO;
using Microsoft.Data.Sqlite;

namespace DingLater.App.Services;

internal static class LegacyMsixDataMigrator
{
    internal static async Task<bool> MigrateIfNeededAsync(
        string targetRoot,
        CancellationToken cancellationToken = default)
    {
        var targetDatabase = Path.Combine(targetRoot, "dinglater.db");
        if (File.Exists(targetDatabase))
        {
            return false;
        }

        var packagesRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Packages");
        if (!Directory.Exists(packagesRoot))
        {
            return false;
        }

        var source = Directory.EnumerateDirectories(packagesRoot, "DingLater.Local_*", SearchOption.TopDirectoryOnly)
            .Select(path => Path.Combine(path, "LocalState"))
            .Where(path => File.Exists(Path.Combine(path, "dinglater.db")))
            .Where(path => File.Exists(Path.Combine(path, "keys", "master.key")))
            .OrderByDescending(path => File.GetLastWriteTimeUtc(Path.Combine(path, "dinglater.db")))
            .FirstOrDefault();
        if (source is null)
        {
            return false;
        }

        await MigrateFromAsync(source, targetRoot, cancellationToken).ConfigureAwait(false);
        return true;
    }

    internal static async Task MigrateFromAsync(
        string source,
        string targetRoot,
        CancellationToken cancellationToken = default)
    {
        var targetDatabase = Path.Combine(targetRoot, "dinglater.db");
        if (!File.Exists(Path.Combine(source, "dinglater.db"))
            || !File.Exists(Path.Combine(source, "keys", "master.key")))
        {
            throw new InvalidOperationException("旧版 DingLater 数据不完整，未执行迁移。");
        }

        Directory.CreateDirectory(targetRoot);
        var migrationRoot = Path.Combine(targetRoot, ".migration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(migrationRoot);
        try
        {
            var temporaryDatabase = Path.Combine(migrationRoot, "dinglater.db");
            var temporaryKey = Path.Combine(migrationRoot, "master.key");
            File.Copy(Path.Combine(source, "keys", "master.key"), temporaryKey, overwrite: false);

            await using (var sourceConnection = new SqliteConnection(
                             new SqliteConnectionStringBuilder
                             {
                                 DataSource = Path.Combine(source, "dinglater.db"),
                                 Mode = SqliteOpenMode.ReadOnly,
                                 Pooling = false
                             }.ConnectionString))
            await using (var targetConnection = new SqliteConnection(
                             new SqliteConnectionStringBuilder
                             {
                                 DataSource = temporaryDatabase,
                                 Mode = SqliteOpenMode.ReadWriteCreate,
                                 Pooling = false
                             }.ConnectionString))
            {
                await sourceConnection.OpenAsync(cancellationToken);
                await targetConnection.OpenAsync(cancellationToken);
                sourceConnection.BackupDatabase(targetConnection);
            }

            Directory.CreateDirectory(Path.Combine(targetRoot, "keys"));
            File.Move(temporaryKey, Path.Combine(targetRoot, "keys", "master.key"), overwrite: true);
            File.Move(temporaryDatabase, targetDatabase, overwrite: false);
        }
        finally
        {
            if (Directory.Exists(migrationRoot))
            {
                Directory.Delete(migrationRoot, recursive: true);
            }
        }
    }
}
