using System.Net.Sockets;
using System.Text.Json;
using DuCom.Plugin;

namespace DuCom.PluginTestBed;

public sealed class BedOkPlugin : DuComPlugin
{
    public static async Task Main() => await Task.CompletedTask;

    public override async Task InitializeAsync(CancellationToken cancellationToken)
    {
        string? stored = await Api.Storage.ReadAsync(cancellationToken);
        if (string.IsNullOrEmpty(stored))
        {
            await Api.Storage.WriteAsync(JsonSerializer.Serialize(new { marker = "ok" }), cancellationToken);
        }
    }

    public override Task<PluginActivation> ActivateAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new PluginActivation
        {
            Menus =
            [
                new MenuContribution { ContributionId = "open", Label = "OK bed", CommandId = "open", PageId = "page" },
            ],
            ToolPages =
            [
                new ToolPageContribution
                {
                    ContributionId = "page",
                    Title = "OK bed",
                    Nodes = [new UiLabelNode { Text = "ready" }],
                },
            ],
        });
}

public sealed class BedCrashPlugin : DuComPlugin
{
    public override Task InitializeAsync(CancellationToken cancellationToken)
    {
        Environment.FailFast("bed-crash");
        return Task.CompletedTask;
    }
}

public sealed class BedConstructorCrashPlugin : DuComPlugin
{
    public BedConstructorCrashPlugin() => throw new InvalidOperationException("bed constructor crash");
}

public sealed class BedHangPlugin : DuComPlugin
{
    public override Task<PluginActivation> ActivateAsync(CancellationToken cancellationToken)
    {
        Thread.Sleep(Timeout.Infinite);
        return Task.FromResult(PluginActivation.Empty);
    }
}

public sealed class BedFloodPlugin : DuComPlugin
{
    public override async Task<PluginActivation> ActivateAsync(CancellationToken cancellationToken)
    {
        _ = Task.Run(async () =>
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                Api.Diagnostics.Info(new string('x', 2000));
            }
        });
        return PluginActivation.Empty;
    }
}

/// <summary>
/// Attempts everything the sandbox should refuse: reading a host user file, writing to the
/// user's temp root, opening a serial device, and an outbound TCP connection. Each probe
/// result is recorded in private storage so the test can assert every denial.
/// </summary>
public sealed class BedUnauthorizedPlugin : DuComPlugin
{
    public override async Task<PluginActivation> ActivateAsync(CancellationToken cancellationToken)
    {
        Dictionary<string, string> results = new();

        try
        {
            using System.Security.Principal.WindowsIdentity identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            string? appContainerGroup = identity.Groups?
                .Select(group => group.Value)
                .FirstOrDefault(value => value.StartsWith("S-1-15-2-", StringComparison.Ordinal));
            results["token_appcontainer_sid"] = appContainerGroup ?? "none";
            results["identity_owner"] = identity.Owner?.Value ?? "null";
        }
        catch (Exception exception)
        {
            results["identity_owner"] = exception.GetType().Name;
        }

        string secretPath = Environment.GetEnvironmentVariable("DUCOM_BED_SECRET") ?? Path.Combine(Path.GetTempPath(), "ducom-bed-missing-secret.txt");
        results["probe_path"] = secretPath;
        try
        {
            _ = File.ReadAllText(secretPath);
            results["read_host_file"] = "ESCAPED";
        }
        catch (Exception exception)
        {
            results["read_host_file"] = exception.GetType().Name;
        }

        results["temp_path"] = Path.GetTempPath();
        try
        {
            string escapePath = Path.Combine(Path.GetDirectoryName(secretPath)!, "ducom-bed-escape.txt");
            File.WriteAllText(escapePath, "x");
            results["write_temp"] = "ESCAPED";
        }
        catch (Exception exception)
        {
            results["write_temp"] = exception.GetType().Name;
        }

        try
        {
            using FileStream stream = File.OpenRead("\\\\.\\COM1");
            results["serial_open"] = "ESCAPED";
        }
        catch (Exception exception)
        {
            results["serial_open"] = exception.GetType().Name;
        }

        try
        {
            using TcpClient client = new();
            int port = int.TryParse(Environment.GetEnvironmentVariable("DUCOM_BED_TCP_PORT"), out int parsed) ? parsed : 1;
            client.ConnectAsync(System.Net.IPAddress.Loopback, port, cancellationToken).AsTask().Wait(3000);
            results["tcp_connect"] = "CONNECTED";
        }
        catch (Exception exception)
        {
            results["tcp_connect"] = exception.GetType().Name;
        }

        try
        {
            string? result = await Api.Storage.ReadAsync(cancellationToken);
            results["storage_read"] = result is null ? "null" : "granted";
        }
        catch (PluginHostException exception)
        {
            results["storage_read"] = exception.Code;
        }

        await Api.Storage.WriteAsync(JsonSerializer.Serialize(results), cancellationToken);
        return PluginActivation.Empty;
    }
}

public sealed class BedLeakPlugin : DuComPlugin
{
    public override Task<PluginActivation> ActivateAsync(CancellationToken cancellationToken)
    {
        new Thread(() =>
        {
            List<byte[]> chunks = [];
            while (true)
            {
                chunks.Add(new byte[16 * 1024 * 1024]);
            }
        })
        {
            IsBackground = true,
            Name = "bed-leak",
        }.Start();
        return Task.FromResult(PluginActivation.Empty);
    }
}

public sealed class BedSlowConsumerPlugin : DuComPlugin
{
    public override async Task<PluginActivation> ActivateAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<DuCom.Plugin.Dto.SerialSessionInfo> sessions = await Api.Serial.ListSessionsAsync(cancellationToken);
        foreach (DuCom.Plugin.Dto.SerialSessionInfo session in sessions)
        {
            if (await Api.Serial.SubscribeAsync(session.SessionId, cancellationToken))
            {
                break;
            }
        }

        return PluginActivation.Empty;
    }

    public override void OnSerialData(SerialDataEventArgs eventArgs) => Thread.Sleep(4_000);
}

public sealed class BedLateSubmitPlugin : DuComPlugin
{
    public override Task<CommandInvokeOutcome> OnCommandAsync(string commandId, string? arg, IReadOnlyDictionary<string, string> formValues, CancellationToken cancellationToken) =>
        Task.FromResult(CommandInvokeOutcome.Complete("late"));
}

public sealed class BedHostOnlyProbePlugin : DuComPlugin
{
    public override Task<PluginActivation> ActivateAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new PluginActivation
        {
            Menus = [new MenuContribution { ContributionId = "probe", Label = "Probe", CommandId = "probe" }],
        });

    public override async Task<CommandInvokeOutcome> OnCommandAsync(string commandId, string? arg, IReadOnlyDictionary<string, string> formValues, CancellationToken cancellationToken)
    {
        Dictionary<string, string> results = new();
        foreach (string kind in new[] { "output", "snapshot" })
        {
            string directory = formValues[kind + "Directory"];
            string file = formValues[kind + "File"];
            results[kind + "_read"] = Probe(() => File.ReadAllText(file));
            results[kind + "_create"] = Probe(() => File.WriteAllText(Path.Combine(directory, "worker-created.txt"), "bad"));
            results[kind + "_modify"] = Probe(() => File.WriteAllText(file, "changed"));
            results[kind + "_delete"] = Probe(() => File.Delete(file));
            results[kind + "_rename"] = Probe(() => Directory.Move(directory, directory + "-worker"));
        }

        try
        {
            OutputHandle output = await Api.Output.BeginAsync(cancellationToken);
            await Api.Output.WriteAsync(output.Token, 0, "broker-ok"u8.ToArray(), cancellationToken);
            await Api.Output.DiscardAsync(output.Token, cancellationToken);
            results["broker_output"] = "ok";
        }
        catch (Exception exception)
        {
            results["broker_output"] = exception.GetType().Name;
        }

        await Api.Storage.WriteAsync(JsonSerializer.Serialize(results), cancellationToken);
        return CommandInvokeOutcome.Complete("probed");
    }

    private static string Probe(Action action)
    {
        try { action(); return "ESCAPED"; }
        catch (Exception exception) { return exception.GetType().Name; }
    }
}

public sealed class BedOutputCrashPlugin : DuComPlugin
{
    public override Task<PluginActivation> ActivateAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new PluginActivation
        {
            Menus = [new MenuContribution { ContributionId = "crash", Label = "Crash output", CommandId = "crash" }],
        });

    public override async Task<CommandInvokeOutcome> OnCommandAsync(string commandId, string? arg, IReadOnlyDictionary<string, string> formValues, CancellationToken cancellationToken)
    {
        OutputHandle output = await Api.Output.BeginAsync(cancellationToken);
        await Api.Output.WriteAsync(output.Token, 0, new byte[256 * 1024], cancellationToken);
        Environment.FailFast("output crash probe");
        return CommandInvokeOutcome.Complete();
    }
}
