using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using DuCom.Core.Updates;

namespace DuCom.Core.Tests.Updates;

public sealed class GitHubReleaseClientTests
{
    [Theory]
    [InlineData("https://github.com/adu9527/DuCom", "adu9527", "DuCom")]
    [InlineData("https://github.com/adu9527/DuCom/", "adu9527", "DuCom")]
    [InlineData("https://GitHub.com/SomeOwner/Some.Repo", "SomeOwner", "Some.Repo")]
    public void RepositoryUrlIsSplitIntoOwnerAndName(string repositoryUrl, string expectedOwner, string expectedName)
    {
        bool result = GitHubReleaseClient.TrySplitRepository(repositoryUrl, out string? owner, out string? name);

        Assert.True(result);
        Assert.Equal(expectedOwner, owner);
        Assert.Equal(expectedName, name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("https://gitlab.com/adu9527/DuCom")]
    [InlineData("https://github.com/adu9527")]
    [InlineData("https://github.com/adu9527/DuCom/extra")]
    public void InvalidRepositoryUrlsAreRejected(string repositoryUrl) =>
        Assert.False(GitHubReleaseClient.TrySplitRepository(repositoryUrl, out _, out _));

    [Fact]
    public void ConstructorRejectsInvalidRepositoryUrl() =>
        Assert.Throws<ArgumentException>(() => new GitHubReleaseClient("https://example.com/repo"));

    [Fact]
    public async Task GetLatestReleaseDeserializesReleasePayload()
    {
        string payload = """
        {
          "tag_name": "V0.0.0.4",
          "name": "V0.0.0.4",
          "body": "Release notes",
          "html_url": "https://github.com/adu9527/DuCom/releases/tag/V0.0.0.4",
          "draft": false,
          "prerelease": false,
          "assets": [
            { "name": "DuCom.exe", "browser_download_url": "https://example.com/DuCom.exe", "size": 12345 }
          ]
        }
        """;
        HttpClient http = new(new StubHandler(payload));
        using GitHubReleaseClient client = new(http, "https://api.github.com/test");

        GitHubRelease? release = await client.GetLatestReleaseAsync();

        Assert.NotNull(release);
        Assert.Equal("V0.0.0.4", release.TagName);
        Assert.Equal("Release notes", release.Body);
        Assert.NotNull(release.Assets);
        GitHubReleaseAsset asset = Assert.Single(release.Assets);
        Assert.Equal("DuCom.exe", asset.Name);
        Assert.Equal(12345, asset.Size);
    }

    [Fact]
    public async Task GetLatestReleaseReturnsNullWhenNoReleaseExists()
    {
        HttpClient http = new(new StubHandler(string.Empty, HttpStatusCode.NotFound));
        using GitHubReleaseClient client = new(http, "https://api.github.com/test");

        Assert.Null(await client.GetLatestReleaseAsync());
    }

    private sealed class StubHandler(string payload, HttpStatusCode statusCode = HttpStatusCode.OK) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(statusCode) { Content = new StringContent(payload) });
    }
}
