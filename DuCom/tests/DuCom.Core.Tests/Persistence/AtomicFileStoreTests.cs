using DuCom.Core.Persistence;

namespace DuCom.Core.Tests.Persistence;

public sealed class AtomicFileStoreTests : IDisposable
{
    private readonly string _directory;

    public AtomicFileStoreTests() => _directory = Directory.CreateTempSubdirectory("ducom-atomic-store-").FullName;

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
    public void CommitWritesAllFilesAtomically()
    {
        string first = Path.Combine(_directory, "one.json");
        string second = Path.Combine(_directory, "sub", "two.json");

        AtomicFileStore.CommitAll(
        [
            new AtomicFileWrite(first, AtomicFileStore.EncodeUtf8("{\"a\":1}")),
            new AtomicFileWrite(second, AtomicFileStore.EncodeUtf8("{\"b\":2}")),
        ]);

        Assert.Equal("{\"a\":1}", File.ReadAllText(first));
        Assert.Equal("{\"b\":2}", File.ReadAllText(second));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(_directory, "*.migrate-bak", SearchOption.AllDirectories));
    }

    [Fact]
    public void FailureDuringReplaceRollsBackPreviousFiles()
    {
        string first = Path.Combine(_directory, "one.json");
        string second = Path.Combine(_directory, "two.json");
        File.WriteAllText(first, "old-one");
        File.WriteAllText(second, "old-two");

        // Lock the second destination so its File.Replace fails after the first succeeded.
        using (FileStream lockStream = new(second, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            AggregateException failure = Assert.Throws<AggregateException>(() => AtomicFileStore.CommitAll(
            [
                new AtomicFileWrite(first, AtomicFileStore.EncodeUtf8("new-one")),
                new AtomicFileWrite(second, AtomicFileStore.EncodeUtf8("new-two")),
            ]));
            Assert.Contains(failure.InnerExceptions, exception => exception is IOException);
            Assert.Equal("old-one", File.ReadAllText(first));
        }

        Assert.Equal("old-two", File.ReadAllText(second));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
        // The rollback succeeded and consumed the backup (its content IS the restored
        // file), so no backup files remain.
        Assert.Empty(Directory.GetFiles(_directory, "*.migrate-bak"));
    }

    [Fact]
    public void NonIoCommitFailureStillRollsBackAndAggregates()
    {
        string first = Path.Combine(_directory, "one.json");
        string second = Path.Combine(_directory, "two.json");
        File.WriteAllText(first, "old-one");
        File.WriteAllText(second, "old-two");
        // A read-only destination makes the second replace fail with a non-IO exception
        // (UnauthorizedAccessException): the rollback must still run for the first file.
        File.SetAttributes(second, FileAttributes.ReadOnly);
        try
        {
            AggregateException failure = Assert.Throws<AggregateException>(() => AtomicFileStore.CommitAll(
            [
                new AtomicFileWrite(first, AtomicFileStore.EncodeUtf8("new-one")),
                new AtomicFileWrite(second, AtomicFileStore.EncodeUtf8("new-two")),
            ]));

            Assert.Contains(failure.InnerExceptions, exception => exception is UnauthorizedAccessException);
            Assert.Equal("old-one", File.ReadAllText(first));
            Assert.Equal("old-two", File.ReadAllText(second));
        }
        finally
        {
            File.SetAttributes(second, FileAttributes.Normal);
        }
    }

    [Fact]
    public void RollbackFailureKeepsBackupAndAggregatesBothFailures()
    {
        string first = Path.Combine(_directory, "one.json");
        string second = Path.Combine(_directory, "two.json");
        File.WriteAllText(first, "old-one");
        File.WriteAllText(second, "old-two");
        // The commit itself must fail (read-only second destination) so the rollback runs;
        // the injected fault then breaks the rollback of the already-replaced first file.
        File.SetAttributes(second, FileAttributes.ReadOnly);
        try
        {
            AtomicFileStore.RollbackFault = destination => destination == first
                ? new UnauthorizedAccessException("rollback denied")
                : null;
            AggregateException failure = Assert.Throws<AggregateException>(() => AtomicFileStore.CommitAll(
            [
                new AtomicFileWrite(first, AtomicFileStore.EncodeUtf8("new-one")),
                new AtomicFileWrite(second, AtomicFileStore.EncodeUtf8("new-two")),
            ]));

            Assert.Contains(failure.InnerExceptions, exception => exception.Message.Contains("rollback denied", StringComparison.Ordinal));
            Assert.True(failure.Message.Contains("backup", StringComparison.Ordinal), "the aggregate must name the kept backups");
            // The first file stays at its rolled-forward content, but its backup survives
            // for manual recovery.
            Assert.Single(Directory.GetFiles(_directory, "*.migrate-bak"));
        }
        finally
        {
            AtomicFileStore.RollbackFault = null;
            File.SetAttributes(second, FileAttributes.Normal);
        }
    }

    [Fact]
    public void EncodingIsUtf8WithoutBom()
    {
        byte[] content = AtomicFileStore.EncodeUtf8("数据");

        Assert.Equal(new byte[] { 0xE6, 0x95, 0xB0, 0xE6, 0x8D, 0xAE }, content);
    }
}
