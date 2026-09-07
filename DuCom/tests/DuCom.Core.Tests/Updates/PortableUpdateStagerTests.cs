using System.Security.Cryptography;
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
    public void ApplyScriptBacksUpTargetBeforeSwapping()
    {
        string script = PortableUpdateStager.BuildApplyScript();

        Assert.Contains("%DUCOM_UPDATE_BACKUP%", script, StringComparison.Ordinal);
        int backup = script.IndexOf("copy /y \"%TARGET%\" \"%BACKUP%\"", StringComparison.Ordinal);
        int swap = script.IndexOf("move /y \"%STAGED%\" \"%TARGET%\"", StringComparison.Ordinal);
        Assert.True(backup >= 0 && swap >= 0 && backup < swap, "The apply script must back up the target before the first move.");
    }

    [Fact]
    public void ApplyScriptGivesUpWaitingAfterBoundedTimeout()
    {
        string script = PortableUpdateStager.BuildApplyScript();

        Assert.Contains("GEQ 120", script, StringComparison.Ordinal);
        Assert.Contains("wait_timeout", script, StringComparison.Ordinal);
        Assert.Contains(".apply-timeout", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyScriptMatchesPidWithSurroundingSpaces()
    {
        string script = PortableUpdateStager.BuildApplyScript();

        Assert.Contains("find \" %PID% \"", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyScriptWritesFailureMarkerWhenSwapFails()
    {
        string script = PortableUpdateStager.BuildApplyScript();

        Assert.Contains(".apply-failed", script, StringComparison.Ordinal);
        Assert.Contains("del /q \"%STAGED%.apply-failed\"", script, StringComparison.Ordinal);
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
        GitHubReleaseAsset asset = new("DuCom.exe", "https://example.com/DuCom.exe", 12);
        StubHttp http = new("MZ0123456789");
        using GitHubReleaseClient client = new(new HttpClient(http), "https://api.github.com/test");

        List<long> reports = [];
        await stager.StagePackageAsync(client, asset, new Progress<long>(reports.Add));

        Assert.True(stager.HasStagedPackage);
        Assert.Equal("MZ0123456789", File.ReadAllText(stager.StagedFilePath));
        Assert.False(File.Exists(stager.StagedFilePath + ".part"));
        Assert.Single(reports);
        Assert.Equal(12, reports[0]);
    }

    [Fact]
    public async Task StagePackageRejectsPayloadWithoutExecutableHeader()
    {
        string target = Path.Combine(_directory, "DuCom.exe");
        PortableUpdateStager stager = new(target);
        GitHubReleaseAsset asset = new("DuCom.exe", "https://example.com/DuCom.exe", 11);
        StubHttp http = new("not-an-exe");
        using GitHubReleaseClient client = new(new HttpClient(http), "https://api.github.com/test");

        await Assert.ThrowsAsync<IOException>(() => stager.StagePackageAsync(client, asset));

        Assert.False(stager.HasStagedPackage);
        Assert.False(File.Exists(stager.StagedFilePath + ".part"));
    }

    [Fact]
    public async Task StagePackageAcceptsMatchingSha256Digest()
    {
        string target = Path.Combine(_directory, "DuCom.exe");
        PortableUpdateStager stager = new(target);
        byte[] payload = Encoding.ASCII.GetBytes("MZ-digest-ok");
        GitHubReleaseAsset asset = new("DuCom.exe", "https://example.com/DuCom.exe", payload.Length, Digest: $"sha256:{Convert.ToHexString(SHA256.HashData(payload))}");
        StubHttp http = new(Encoding.ASCII.GetString(payload));
        using GitHubReleaseClient client = new(new HttpClient(http), "https://api.github.com/test");

        await stager.StagePackageAsync(client, asset);

        Assert.True(stager.HasStagedPackage);
    }

    [Fact]
    public async Task StagePackageRejectsMismatchingSha256Digest()
    {
        string target = Path.Combine(_directory, "DuCom.exe");
        PortableUpdateStager stager = new(target);
        byte[] payload = Encoding.ASCII.GetBytes("MZ-digest-bad");
        byte[] other = Encoding.ASCII.GetBytes("different bytes");
        GitHubReleaseAsset asset = new("DuCom.exe", "https://example.com/DuCom.exe", payload.Length, Digest: $"sha256:{Convert.ToHexString(SHA256.HashData(other))}");
        StubHttp http = new(Encoding.ASCII.GetString(payload));
        using GitHubReleaseClient client = new(new HttpClient(http), "https://api.github.com/test");

        IOException exception = await Assert.ThrowsAsync<IOException>(() => stager.StagePackageAsync(client, asset));

        Assert.Contains("SHA-256", exception.Message);
        Assert.False(stager.HasStagedPackage);
        Assert.False(File.Exists(stager.StagedFilePath + ".part"));
    }

    [Fact]
    public async Task DiscardStagedPackageRemovesStagedFiles()
    {
        string target = Path.Combine(_directory, "DuCom.exe");
        PortableUpdateStager stager = new(target);
        GitHubReleaseAsset asset = new("DuCom.exe", "https://example.com/DuCom.exe", 12);
        StubHttp http = new("MZ0123456789");
        using GitHubReleaseClient client = new(new HttpClient(http), "https://api.github.com/test");
        await stager.StagePackageAsync(client, asset);
        stager.WriteStagedVersionMarker("V0.0.0.9");
        Assert.True(File.Exists(stager.StagedVersionMarkerPath));

        stager.DiscardStagedPackage();

        Assert.False(stager.HasStagedPackage);
        Assert.False(File.Exists(stager.StagedFilePath + ".part"));
        Assert.False(File.Exists(stager.StagedVersionMarkerPath));
    }

    [Fact]
    public void ConsumeApplyMarkersReportsAndDeletesMarkers()
    {
        string target = Path.Combine(_directory, "DuCom.exe");
        PortableUpdateStager stager = new(target);
        Directory.CreateDirectory(stager.StagingDirectory);
        File.WriteAllText(stager.ApplyFailedMarkerPath, "failed");
        File.WriteAllText(stager.ApplyTimeoutMarkerPath, "timeout");

        List<string> markers = stager.ConsumeApplyMarkers();

        Assert.Equal(["failed", "timeout"], markers);
        Assert.False(File.Exists(stager.ApplyFailedMarkerPath));
        Assert.False(File.Exists(stager.ApplyTimeoutMarkerPath));
        Assert.Empty(stager.ConsumeApplyMarkers());
    }

    [Fact]
    public void DiscardStagedPackageRemovesApplyMarkers()
    {
        string target = Path.Combine(_directory, "DuCom.exe");
        PortableUpdateStager stager = new(target);
        Directory.CreateDirectory(stager.StagingDirectory);
        File.WriteAllText(stager.ApplyFailedMarkerPath, "failed");
        File.WriteAllText(stager.ApplyTimeoutMarkerPath, "timeout");

        stager.DiscardStagedPackage();

        Assert.False(File.Exists(stager.ApplyFailedMarkerPath));
        Assert.False(File.Exists(stager.ApplyTimeoutMarkerPath));
    }

    [Fact]
    public void ApplyRollbackRequiresBackupFile()
    {
        string target = Path.Combine(_directory, "DuCom.exe");
        PortableUpdateStager stager = new(target);

        Assert.False(stager.HasRollbackBackup);
        Assert.Throws<InvalidOperationException>(stager.ApplyRollback);
    }

    [Fact]
    public void ApplyRollbackRejectsBackupWithoutExecutableHeader()
    {
        string target = Path.Combine(_directory, "DuCom.exe");
        PortableUpdateStager stager = new(target);
        File.WriteAllText(target + ".bak", "not an executable");

        Assert.True(stager.HasRollbackBackup);
        Assert.Throws<IOException>(stager.ApplyRollback);
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
