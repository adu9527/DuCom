using DuCom.PluginHost.Core;

if (args.Length != 3) return 2;
string root = args[0];
string hostRunId = args[1];
string readyPath = args[2];
HostTempDiskBudget budget = new(1024 * 1024);
using HostTempResourceLedger ledger = new(root, hostRunId, budget);
string resourcePath = Path.Combine(root, "HostOutput", hostRunId, "interrupted.part");
string resourceId = Guid.NewGuid().ToString("N");
ledger.RegisterIntent(new HostTempResourceRecord
{
    ResourceId = resourceId,
    PluginId = "org.example.recovery",
    ActivationId = hostRunId,
    Kind = HostTempResourceKind.OutputFile,
    Path = resourcePath,
    PluginLimitBytes = 1024 * 1024,
});
ledger.TryReserve(resourceId, 4096);
Directory.CreateDirectory(Path.GetDirectoryName(resourcePath)!);
await using FileStream stream = new(resourcePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
await stream.WriteAsync(new byte[4096]);
await stream.FlushAsync();
File.WriteAllText(readyPath, resourcePath);
await Task.Delay(Timeout.InfiniteTimeSpan);
return 0;
