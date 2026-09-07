using System.Text.Json.Serialization;

namespace DuCom.Core.Updates;

public sealed record GitHubReleaseAsset(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("browser_download_url")] string BrowserDownloadUrl,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("state")] string? State = null,
    [property: JsonPropertyName("content_type")] string? ContentType = null);

public sealed record GitHubRelease(
    [property: JsonPropertyName("tag_name")] string TagName,
    [property: JsonPropertyName("name")] string? Name = null,
    [property: JsonPropertyName("body")] string? Body = null,
    [property: JsonPropertyName("html_url")] string? HtmlUrl = null,
    [property: JsonPropertyName("draft")] bool Draft = false,
    [property: JsonPropertyName("prerelease")] bool Prerelease = false,
    [property: JsonPropertyName("published_at")] DateTimeOffset? PublishedAt = null,
    [property: JsonPropertyName("assets")] List<GitHubReleaseAsset>? Assets = null);
