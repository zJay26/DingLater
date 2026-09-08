using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DingLater.Core.Updates;

// The only network boundary in DingLater. It has no access to messages, keys or account data.
public sealed class GitHubUpdateClient : IUpdateClient, IDisposable
{
    public const string RepositoryUrl = "https://github.com/zJay26/DingLater";
    private const string LatestUrl = "https://api.github.com/repos/zJay26/DingLater/releases/latest";
    private const long MaximumPackageSize = 512L * 1024 * 1024;
    private readonly HttpClient _http;

    public GitHubUpdateClient(HttpMessageHandler? handler = null)
    {
        _http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("DingLater-Updater/1.0");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    public async Task<UpdateRelease?> CheckAsync(Version currentVersion, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var response = await GetAsync(new Uri(LatestUrl), timeout.Token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        var json = await ReadLimitedAsync(response.Content, 2 * 1024 * 1024, timeout.Token).ConfigureAwait(false);
        return ParseRelease(json, currentVersion);
    }

    public static UpdateRelease? ParseRelease(string json, Version currentVersion)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean()
            || !UpdatePolicy.TryParseVersion(root.GetProperty("tag_name").GetString(), out var version)
            || version <= currentVersion)
        {
            return null;
        }

        var tag = root.GetProperty("tag_name").GetString()!;
        var name = $"DingLater-{version}-win-x64-portable.zip";
        var assets = root.GetProperty("assets").EnumerateArray().Where(asset => asset.GetProperty("name").GetString() == name).ToArray();
        if (assets.Length != 1)
        {
            throw new InvalidDataException("新版尚未提供完整的 Windows x64 更新包，请稍后重试。");
        }

        var asset = assets[0];
        var uri = new Uri(asset.GetProperty("browser_download_url").GetString()!);
        var expected = $"{RepositoryUrl}/releases/download/{tag}/{name}";
        var digest = asset.TryGetProperty("digest", out var value) ? value.GetString() : null;
        var size = asset.GetProperty("size").GetInt64();
        if (uri.AbsoluteUri != expected || size <= 0 || size > MaximumPackageSize
            || digest is null || !digest.StartsWith("sha256:", StringComparison.Ordinal)
            || !PortableUpdatePackage.IsSha256(digest[7..]))
        {
            throw new InvalidDataException("更新包地址、大小或 SHA-256 校验信息无效。");
        }

        return new UpdateRelease(version, new Uri($"{RepositoryUrl}/releases/tag/{tag}"), uri, name, size, digest[7..]);
    }

    public async Task<PreparedUpdate> DownloadAsync(UpdateRelease release, string directory, IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (!Path.IsPathFullyQualified(directory))
        {
            throw new ArgumentException("请选择完整的下载目录路径。");
        }

        // A private, unique subdirectory never overwrites a user's existing download.
        PortableUpdatePackage.RejectReparsePoints(directory);
        var work = Path.Combine(Path.GetFullPath(directory), $"{release.Version}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(work);
        var archive = Path.Combine(work, release.FileName);
        var partial = archive + ".partial";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(30));
        try
        {
            using var response = await GetAsync(release.DownloadUri, timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is { } length && length != release.Size)
            {
                throw new InvalidDataException("更新包下载大小与发布信息不符。");
            }

            await using (var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false))
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[81920];
                long received = 0;
                int count;
                while ((count = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
                {
                    received += count;
                    if (received > release.Size || received > MaximumPackageSize)
                    {
                        throw new InvalidDataException("更新包超出预期大小。");
                    }

                    hash.AppendData(buffer, 0, count);
                    await output.WriteAsync(buffer.AsMemory(0, count), timeout.Token).ConfigureAwait(false);
                    progress.Report(95d * received / release.Size);
                }

                if (received != release.Size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("更新包 SHA-256 校验失败，请重新下载。");
                }
            }

            File.Move(partial, archive);
            var extracted = Path.Combine(work, "app");
            await PortableUpdatePackage.ExtractAsync(archive, extracted, release.Version, timeout.Token).ConfigureAwait(false);
            progress.Report(100);
            return new PreparedUpdate(release, archive, extracted);
        }
        finally
        {
            if (File.Exists(partial))
            {
                File.Delete(partial);
            }
        }
    }

    private async Task<HttpResponseMessage> GetAsync(Uri uri, CancellationToken cancellationToken)
    {
        for (var redirects = 0; redirects < 6; redirects++)
        {
            if (uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo)
                || uri.Host is not ("api.github.com" or "github.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com"))
            {
                throw new InvalidDataException("拒绝不受支持的更新下载地址。");
            }

            var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308))
            {
                return response;
            }

            var location = response.Headers.Location;
            response.Dispose();
            uri = location is null ? throw new InvalidDataException("更新下载跳转缺少地址。") : new Uri(uri, location);
        }

        throw new InvalidDataException("更新下载跳转次数过多。");
    }

    private static async Task<string> ReadLimitedAsync(HttpContent content, int maximum, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var result = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (result.Length + count > maximum)
            {
                throw new InvalidDataException("更新信息超出预期大小。");
            }

            result.Write(buffer, 0, count);
        }

        return Encoding.UTF8.GetString(result.ToArray());
    }

    public void Dispose() => _http.Dispose();
}
