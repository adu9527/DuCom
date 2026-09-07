using System.Text;
using DuCom.Core.Updates;

namespace DuCom.Core.Tests.Updates;

public sealed class PortableUpdateStagerTests : IDisposable
{
    private readonly string _directory;

    public PortableUpdateStagerTests() => _directory = Directory.CreateTempSubdirectory("ducom-stager-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void StagerUsesUpdatesFolderNextToTarget()
    {
        string target = Path.Combine(_directory, "DuCom.exe");
        PortableUpdateStager stager = new(target);

        Assert.Equal(Path.Combine(_directory, "Updates"), stager.StagingDirectory);
        Assert.Equal(Path.Combine(_directory, "Updates", "DuCom.exe.new"), stager.StagedFilePath);
        Assert.False(stager.HasStagedPackage);
    }

    [Fact]
    public void ApplyScriptIsAsciiAndReceivesPathsThroughEnvironment()
    {
        string script = PortableUpdateStager.BuildApplyScript();

        byte[] bytes = Encoding.ASCII.GetBytes(script);
        Assert.Equal(bytes.Length, script.Length);
        Assert.All(script, character => Assert.True(character <= 127));
        Assert.Contains("%DUCOM_UPDATE_TARGET%", script, StringComparison.Ordinal);
        Assert.Contains("%DUCOM_UPDATE_STAGED%", script, StringComparison.Ordinal);
        Assert.Contains("%DUCOM_UPDATE_PID%", script, StringComparison.Ordinal);
        Assert.DoesNotContain(_directory, script, StringComparison.Ordinal);
        Assert.Contains("tasklist", script, StringComparison.Ordinal);
        Assert.Contains("move /y", script, StringComparison.Ordinal);
        Assert.Contains("del \"%~f0\"", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyStagedUpdateRequiresStagedPackage()
    {
        PortableUpdateStager stager = new(Path.Combine(_directory, "DuCom.exe"));

        Assert.Throws<InvalidOperationException>(stager.ApplyStagedUpdate);
    }

    [Fact]
    public async Task StagePackageMovesPartFileIntoPlace()
    {
        string target = Path.Combine(_directory, "DuCom.exe");
        PortableUpdateStager stager = new(target);
        GitHubReleaseAsset asset = new("DuCom.exe", "https://example.com/DuCom.exe", 11);
        StubHttp http = new("0123456789A");
        using GitHubReleaseClient client = new(new HttpClient(http), "https://api.github.com/test");

        List<long> reports = [];
        await stager.StagePackageAsync(client, asset, new Progress<long>(reports.Add));

        Assert.True(stager.HasStagedPackage);
        Assert.Equal("0123456789A", File.ReadAllText(stager.StagedFilePath));
        Assert.False(File.Exists(stager.StagedFilePath + ".part"));
        Assert.Single(reports);
        Assert.Equal(11, reports[0]);
    }

    private sealed class StubHttp(string payload) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            HttpResponseMessage response = new(System.Net.HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.ASCII.GetBytes(payload)),
            };
            response.Content.Headers.Add("Content-Length", payload.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return Task.FromResult(response);
        }
    }
}
