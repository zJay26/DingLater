using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DingLater.App.ViewModels;
using DingLater.Core.Models;
using DingLater.Core.Updates;

namespace DingLater.Tests;

[TestClass]
public sealed class UpdateTests
{
    private string _root = null!;

    [TestInitialize]
    public void SetUp() => _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "DingLater.UpdateTests", Guid.NewGuid().ToString("N"))).FullName;

    [TestCleanup]
    public void TearDown() => Directory.Delete(_root, recursive: true);

    [TestMethod]
    public void OldSettings_DefaultToOptionalSixHourlyChecks()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("{\"RetentionDays\":14}")!.Normalize();
        var now = DateTimeOffset.UtcNow;
        Assert.IsTrue(UpdatePolicy.IsDue(settings, now));
        Assert.IsFalse(UpdatePolicy.IsDue(settings with { AutomaticallyCheckUpdates = false }, now));
        Assert.IsFalse(UpdatePolicy.IsDue(settings with { LastUpdateCheckUtc = now.AddHours(-6).AddSeconds(1) }, now));
        Assert.IsTrue(UpdatePolicy.IsDue(settings with { LastUpdateCheckUtc = now.AddHours(-6) }, now));
        Assert.IsTrue(UpdatePolicy.IsDue(settings with { LastUpdateCheckUtc = now.AddDays(1) }, now));
        Assert.AreEqual(14, settings.RetentionDays);
    }

    [TestMethod]
    public void ReleaseSelection_UsesNumericStableVersionAndExactWindowsAsset()
    {
        Assert.IsNotNull(GitHubUpdateClient.ParseRelease(ReleaseJson("2.10.0"), new Version(2, 9, 0)));
        Assert.IsNull(GitHubUpdateClient.ParseRelease(ReleaseJson("2.4.0"), new Version(2, 4, 0)));
        Assert.IsNull(GitHubUpdateClient.ParseRelease(ReleaseJson("2.4.0"), new Version(3, 0, 0)));
        Assert.IsNull(GitHubUpdateClient.ParseRelease(ReleaseJson("2.4.0", prerelease: true), new Version(2, 3, 0)));
        Assert.IsNull(GitHubUpdateClient.ParseRelease(ReleaseJson("2.4.0", draft: true), new Version(2, 3, 0)));
        Assert.IsFalse(UpdatePolicy.TryParseVersion("v2.5.0-beta.1", out _));
        Assert.ThrowsExactly<InvalidDataException>(() => GitHubUpdateClient.ParseRelease(ReleaseJson("2.4.0", digest: "sha256:invalid"), new Version(2, 3, 0)));
        Assert.ThrowsExactly<InvalidDataException>(() => GitHubUpdateClient.ParseRelease(ReleaseJson("2.4.0").Replace("win-x64", "android", StringComparison.Ordinal), new Version(2, 3, 0)));
        Assert.ThrowsExactly<InvalidDataException>(() => GitHubUpdateClient.ParseRelease(ReleaseJson("2.4.0").Replace("github.com/zJay26", "example.com/zJay26", StringComparison.Ordinal), new Version(2, 3, 0)));
    }

    [TestMethod]
    public async Task AutomaticCheck_NeverDownloadsOrInstalls_ManualCheckRecoversSkippedVersion()
    {
        var settings = new AppSettings(AutomaticallyCheckUpdates: false);
        var client = new FakeClient();
        using var vm = CreateViewModel(client, () => settings, change => settings = change(settings));
        var installs = 0;
        vm.InstallRequested = _ => { installs++; return Task.CompletedTask; };
        await vm.CheckIfDueAsync();
        Assert.AreEqual(0, client.Checks);
        await vm.CheckAsync();
        Assert.AreEqual(1, client.Checks);
        Assert.IsTrue(vm.CanDownload);
        Assert.AreEqual(0, client.Downloads);
        Assert.AreEqual(0, installs);
        await vm.SkipAsync();
        Assert.AreEqual("2.4.0", settings.SkippedUpdateVersion);
        Assert.IsFalse(vm.ShowNotice);
        settings = settings with { AutomaticallyCheckUpdates = true, LastUpdateCheckUtc = null };
        await vm.CheckIfDueAsync();
        Assert.IsFalse(vm.ShowNotice);
        await vm.CheckAsync();
        Assert.IsTrue(vm.ShowNotice);
        Assert.AreEqual(0, client.Downloads);
        settings = settings with { UpdateDownloadDirectory = _root };
        await vm.DownloadAsync();
        Assert.AreEqual(_root, client.DownloadDirectory);
        Assert.IsTrue(vm.CanInstall);
        Assert.AreEqual(0, installs);
        await vm.InstallAsync();
        Assert.AreEqual(1, installs);
    }

    [TestMethod]
    public async Task FailedChecks_AreThrottledAcrossRestart_ButManualRetryIsAllowed()
    {
        var settings = new AppSettings();
        var client = new FakeClient { Failure = new IOException("offline") };
        using (var vm = CreateViewModel(client, () => settings, change => settings = change(settings)))
        {
            await vm.CheckIfDueAsync();
            Assert.IsFalse(vm.IsBusy);
            StringAssert.Contains(vm.Status, "offline");
        }

        using var restarted = CreateViewModel(client, () => settings, change => settings = change(settings));
        await restarted.CheckIfDueAsync();
        Assert.AreEqual(1, client.Checks);
        await restarted.CheckAsync();
        Assert.AreEqual(2, client.Checks);
    }

    [TestMethod]
    public async Task DisablingAutomaticChecks_CancelsInflightCheck_AndPreventsLateNotice()
    {
        var settings = new AppSettings();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new GitHubUpdateClient(new Handler(async (_, token) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));
        using var vm = CreateViewModel(client, () => settings, change => settings = change(settings));
        var check = vm.CheckIfDueAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await vm.CheckAsync(); // Concurrent requests are ignored.
        settings = settings with { AutomaticallyCheckUpdates = false };
        vm.SettingsChanged();
        await check.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsFalse(vm.ShowNotice);
        Assert.IsFalse(vm.IsBusy);
    }

    [TestMethod]
    public async Task NetworkClient_RejectsHttpOrForeignRedirects()
    {
        foreach (var target in new[] { "http://github.com/test", "https://example.com/test" })
        {
            using var client = new GitHubUpdateClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect)
            {
                Headers = { Location = new Uri(target) }
            })));
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => client.CheckAsync(new Version(2, 3, 0), CancellationToken.None));
        }
    }

    [TestMethod]
    public async Task Download_UsesChosenDirectory_VerifiesHashAndManifest()
    {
        var source = await MakePackageAsync("source", "2.4.0", ("DingLater.exe", "exe"), ("DingLater.dll", "dll"));
        var zip = Path.Combine(_root, "package.zip");
        ZipFile.CreateFromDirectory(source, zip);
        var bytes = await File.ReadAllBytesAsync(zip);
        using var client = BytesClient(bytes);
        var release = Release() with { Size = bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)) };
        var downloads = Path.Combine(_root, "下载 文件夹");
        var result = await client.DownloadAsync(release, downloads, new Progress<double>(), CancellationToken.None);
        Assert.IsTrue(result.ArchivePath.StartsWith(downloads + Path.DirectorySeparatorChar, StringComparison.Ordinal));
        Assert.AreEqual("exe", await File.ReadAllTextAsync(Path.Combine(result.Directory, "DingLater.exe")));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => client.DownloadAsync(release with { Sha256 = new string('0', 64) }, downloads, new Progress<double>(), CancellationToken.None));
        Assert.AreEqual(0, Directory.GetFiles(downloads, "*.partial", SearchOption.AllDirectories).Length);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => client.DownloadAsync(release with { Size = bytes.Length + 1 }, downloads, new Progress<double>(), CancellationToken.None));
    }

    [TestMethod]
    public async Task CancelledDownload_CleansPartialAndDoesNotInstall()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        using var client = BytesClient([1, 2, 3]);
        await Assert.ThrowsAsync<OperationCanceledException>(() => client.DownloadAsync(Release(), _root, new Progress<double>(), cancelled.Token));
        Assert.AreEqual(0, Directory.GetFiles(_root, "*.partial", SearchOption.AllDirectories).Length);
    }

    [TestMethod]
    public async Task Extraction_RejectsTraversalDuplicatesAndCorruption()
    {
        foreach (var relative in new[] { "../escape.txt", "DingLater.exe:stream", "CON", "folder./evil", "/absolute" })
        {
            var zip = Path.Combine(_root, Guid.NewGuid() + ".zip");
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                using var writer = new StreamWriter(archive.CreateEntry(relative).Open());
                writer.Write("invalid");
            }

            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => PortableUpdatePackage.ExtractAsync(zip, Path.Combine(_root, Guid.NewGuid().ToString()), new Version(2, 4, 0), CancellationToken.None));
        }

        var duplicate = Path.Combine(_root, "duplicate.zip");
        using (var archive = ZipFile.Open(duplicate, ZipArchiveMode.Create))
        {
            archive.CreateEntry("app.dll");
            archive.CreateEntry("APP.dll");
        }

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => PortableUpdatePackage.ExtractAsync(duplicate, Path.Combine(_root, "duplicate"), new Version(2, 4, 0), CancellationToken.None));
        var source = await MakePackageAsync("corrupt", "2.4.0", ("DingLater.exe", "exe"), ("DingLater.dll", "dll"));
        await File.WriteAllTextAsync(Path.Combine(source, "DingLater.dll"), "bad");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => PortableUpdatePackage.ValidateAsync(source, new Version(2, 4, 0), CancellationToken.None));
    }

    [TestMethod]
    public async Task Installer_ReplacesManagedFiles_PreservesUserFilesAndBackup()
    {
        var source = await MakePackageAsync("new", "2.4.0", ("DingLater.exe", "new exe"), ("DingLater.dll", "new dll"), ("new.dll", "new"));
        var target = await MakePackageAsync("old", "2.3.0", ("DingLater.exe", "old exe"), ("DingLater.dll", "old dll"), ("obsolete.dll", "old"));
        await File.WriteAllTextAsync(Path.Combine(target, "personal.txt"), "keep");
        var backup = await PortableUpdateInstaller.InstallAsync(source, target, new Version(2, 4, 0), CancellationToken.None);
        Assert.AreEqual("new exe", await File.ReadAllTextAsync(Path.Combine(target, "DingLater.exe")));
        Assert.AreEqual("old exe", await File.ReadAllTextAsync(Path.Combine(backup, "DingLater.exe")));
        Assert.AreEqual("keep", await File.ReadAllTextAsync(Path.Combine(target, "personal.txt")));
        Assert.IsFalse(File.Exists(Path.Combine(target, "obsolete.dll")));
        Assert.IsTrue(File.Exists(Path.Combine(target, "new.dll")));
        Assert.ThrowsExactly<IOException>(() => PortableUpdateInstaller.ValidateDirectories(target, Path.Combine(target, "child")));
    }

    [TestMethod]
    public async Task Installer_RollsBackAlreadyReplacedFiles_WhenLaterFileIsLocked()
    {
        var source = await MakePackageAsync("new", "2.4.0", ("DingLater.exe", "new exe"), ("DingLater.dll", "new dll"));
        var target = await MakePackageAsync("old", "2.3.0", ("DingLater.exe", "old exe"), ("DingLater.dll", "old dll"));
        using var locked = File.Open(Path.Combine(target, "DingLater.dll"), FileMode.Open, FileAccess.Read, FileShare.Read);
        await Assert.ThrowsExactlyAsync<IOException>(() => PortableUpdateInstaller.InstallAsync(source, target, new Version(2, 4, 0), CancellationToken.None));
        Assert.AreEqual("old exe", await File.ReadAllTextAsync(Path.Combine(target, "DingLater.exe")));
        Assert.AreEqual("old dll", await File.ReadAllTextAsync(Path.Combine(target, "DingLater.dll")));
        await PortableUpdatePackage.ValidateAsync(target, new Version(2, 3, 0), CancellationToken.None);
    }

    private async Task<string> MakePackageAsync(string name, string version, params (string Name, string Content)[] files)
    {
        var directory = Directory.CreateDirectory(Path.Combine(_root, name)).FullName;
        var manifest = new List<PortableUpdatePackage.PackageFile>();
        foreach (var (file, content) in files)
        {
            var bytes = Encoding.UTF8.GetBytes(content);
            await File.WriteAllBytesAsync(Path.Combine(directory, file), bytes);
            manifest.Add(new(file, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes))));
        }

        await File.WriteAllTextAsync(Path.Combine(directory, "package-files.json"), JsonSerializer.Serialize(new PortableUpdatePackage.Manifest(1, version, manifest.ToArray())));
        return directory;
    }

    private static UpdateViewModel CreateViewModel(IUpdateClient client, Func<AppSettings> get, Action<Func<AppSettings, AppSettings>> save) =>
        new(client, new Version(2, 3, 0), get, change => { save(change); return Task.FromResult(true); });

    private static string ReleaseJson(string version, bool prerelease = false, bool draft = false, string? digest = null) => JsonSerializer.Serialize(new
    {
        tag_name = $"v{version}",
        prerelease,
        draft,
        assets = new[] { new { name = $"DingLater-{version}-win-x64-portable.zip", size = 3, digest = digest ?? "sha256:" + new string('a', 64), browser_download_url = $"{GitHubUpdateClient.RepositoryUrl}/releases/download/v{version}/DingLater-{version}-win-x64-portable.zip" } }
    });

    private static UpdateRelease Release() => GitHubUpdateClient.ParseRelease(ReleaseJson("2.4.0"), new Version(2, 3, 0))!;

    private static GitHubUpdateClient BytesClient(byte[] bytes) => new(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) })));

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private sealed class FakeClient : IUpdateClient
    {
        public int Checks { get; private set; }
        public int Downloads { get; private set; }
        public string? DownloadDirectory { get; private set; }
        public Exception? Failure { get; init; }
        public Task<UpdateRelease?> CheckAsync(Version currentVersion, CancellationToken cancellationToken)
        {
            Checks++;
            return Failure is null ? Task.FromResult<UpdateRelease?>(Release()) : Task.FromException<UpdateRelease?>(Failure);
        }

        public Task<PreparedUpdate> DownloadAsync(UpdateRelease release, string directory, IProgress<double> progress, CancellationToken cancellationToken)
        {
            Downloads++;
            DownloadDirectory = directory;
            return Task.FromResult(new PreparedUpdate(release, Path.Combine(directory, release.FileName), Path.Combine(directory, "app")));
        }
    }
}
