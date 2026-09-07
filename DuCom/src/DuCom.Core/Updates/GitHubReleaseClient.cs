using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace DuCom.Core.Updates;

/// <summary>Reads the latest release from the GitHub REST API and downloads release assets.</summary>
public sealed class GitHubReleaseClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly string _latestReleaseUrl;

    public GitHubReleaseClient(string repositoryUrl = "https://github.com/adu9527/DuCom")
    {
        if (TrySplitRepository(repositoryUrl, out string? owner, out string? name))
        {
            _latestReleaseUrl = $"https://api.github.com/repos/{owner}/{name}/releases/latest";
        }
        else
        {
            throw new ArgumentException($"'{repositoryUrl}' is not a GitHub repository URL.", nameof(repositoryUrl));
        }

        _httpClient = CreateHttpClient();
        _ownsHttpClient = true;
    }

    internal GitHubReleaseClient(HttpClient httpClient, string latestReleaseUrl)
    {
        _httpClient = httpClient;
        _latestReleaseUrl = latestReleaseUrl;
        _ownsHttpClient = false;
    }

    public async Task<GitHubRelease?> GetLatestReleaseAsync(CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await _httpClient.GetAsync(_latestReleaseUrl, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<GitHubRelease>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    public async Task<long> DownloadAssetAsync(
        GitHubReleaseAsset asset,
        string destinationPath,
        IProgress<long>? bytesProgress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(destinationPath);

        string? directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using HttpResponseMessage response = await _httpClient.GetAsync(
            asset.BrowserDownloadUrl,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        long totalBytes = response.Content.Headers.ContentLength ?? asset.Size;
        long receivedBytes = 0;
        byte[] buffer = new byte[81_920];
        await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using Stream target = File.Create(destinationPath);
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            receivedBytes += read;
            bytesProgress?.Report(receivedBytes);
        }

        if (totalBytes > 0 && receivedBytes != totalBytes)
        {
            throw new IOException($"Downloaded {receivedBytes} bytes but expected {totalBytes} bytes for '{asset.Name}'.");
        }

        return receivedBytes;
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private static HttpClient CreateHttpClient()
    {
        HttpClient client = new()
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DuCom-Updater");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    internal static bool TrySplitRepository(string repositoryUrl, out string? owner, out string? name)
    {
        owner = null;
        name = null;
        if (string.IsNullOrWhiteSpace(repositoryUrl))
        {
            return false;
        }

        string trimmed = repositoryUrl.Trim().TrimEnd('/');
        int marker = trimmed.IndexOf("github.com/", StringComparison.OrdinalIgnoreCase);
        if (marker < 0)
        {
            return false;
        }

        string remainder = trimmed[(marker + "github.com/".Length)..];
        string[] segments = remainder.Split('/');
        if (segments.Length != 2 || segments.Any(segment => string.IsNullOrWhiteSpace(segment)))
        {
            return false;
        }

        owner = segments[0];
        name = segments[1];
        return true;
    }
}
