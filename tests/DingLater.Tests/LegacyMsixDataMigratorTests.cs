using DingLater.App.Services;
using Microsoft.Data.Sqlite;

namespace DingLater.Tests;

[TestClass]
public sealed class LegacyMsixDataMigratorTests
{
    [TestMethod]
    public async Task Migration_UsesSqliteBackupAndLeavesSourceUntouched()
    {
        var root = Path.Combine(Path.GetTempPath(), "DingLater.MigrationTests", Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source");
        var target = Path.Combine(root, "target");
        Directory.CreateDirectory(Path.Combine(source, "keys"));
        var key = Enumerable.Range(0, 64).Select(value => (byte)value).ToArray();
        await File.WriteAllBytesAsync(Path.Combine(source, "keys", "master.key"), key);
        var sourceDatabase = Path.Combine(source, "dinglater.db");
        await using (var connection = new SqliteConnection($"Data Source={sourceDatabase}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE proof(value TEXT NOT NULL); INSERT INTO proof(value) VALUES('migrated');";
            await command.ExecuteNonQueryAsync();
        }

        await LegacyMsixDataMigrator.MigrateFromAsync(source, target);

        CollectionAssert.AreEqual(key, await File.ReadAllBytesAsync(Path.Combine(target, "keys", "master.key")));
        await using (var migrated = new SqliteConnection($"Data Source={Path.Combine(target, "dinglater.db")};Mode=ReadOnly"))
        {
            await migrated.OpenAsync();
            await using var command = migrated.CreateCommand();
            command.CommandText = "SELECT value FROM proof;";
            Assert.AreEqual("migrated", await command.ExecuteScalarAsync());
        }

        Assert.IsTrue(File.Exists(sourceDatabase));
        Assert.IsTrue(File.Exists(Path.Combine(source, "keys", "master.key")));
        SqliteConnection.ClearAllPools();
        Directory.Delete(root, recursive: true);
    }
}
