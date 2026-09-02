using System.Net;
using System.Net.Sockets;
using System.Text;
using DuCom.Core.Storage;
using DuCom.Core.Telnet;
using Xunit;

namespace DuCom.Core.Tests.Telnet;

/// <summary>
/// Bridge-core lifecycle: client input framing into per-line serial sends, push of RX
/// display lines to clients, session-closed behavior, and disposal while work is in flight.
/// </summary>
public sealed class TelnetBridgeCoreTests
{
    private static int NextPort() => Random.Shared.Next(23_400, 24_000);

    [Fact]
    public async Task ClientLinesBecomeOneSerialSendEach()
    {
        int port = NextPort();
        await using BasicTelnetServer server = new();
        List<string> sent = [];
        using SemaphoreSlim sentGate = new(0);
        TelnetBridgeCore core = new(
            server,
            _ => Probe("COMX", send: command =>
            {
                lock (sent)
                {
                    sent.Add(command);
                }

                sentGate.Release();
                return Task.CompletedTask;
            }));
        try
        {
            server.Start(port);
            core.Bind("COMX");
            using TcpClient client = new();
            await client.ConnectAsync(IPAddress.Loopback, port);
            using NetworkStream stream = client.GetStream();
            _ = await stream.ReadAsync(new byte[256]); // welcome

            // Two commands in one chunk, split CRLF, plus a split UTF-8 char line.
            byte[] chunk = "hello\r\nping"u8.ToArray();
            await stream.WriteAsync(chunk);
            byte[] tail = "\r\n电压?\r\n"u8.ToArray();
            await stream.WriteAsync(tail.AsMemory(0, 4));
            await stream.WriteAsync(tail.AsMemory(4, tail.Length - 4));

            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
            for (int received = 0; received < 3; received++)
            {
                await sentGate.WaitAsync(timeout.Token);
            }

            lock (sent)
            {
                Assert.Equal(["hello", "ping", "电压?"], sent);
            }
        }
        finally
        {
            await core.DisposeAsync();
        }
    }

    [Fact]
    public async Task ClosedSessionIsNotSentToAndIsLogged()
    {
        int port = NextPort();
        await using BasicTelnetServer server = new();
        List<string> diagnostics = [];
        TelnetBridgeCore core = new(server, _ => null, message => diagnostics.Add(message));
        try
        {
            server.Start(port);
            core.Bind("COMX"); // bound, but the session lookup returns null (closed)
            using TcpClient client = new();
            await client.ConnectAsync(IPAddress.Loopback, port);
            using NetworkStream stream = client.GetStream();
            _ = await stream.ReadAsync(new byte[256]);

            await stream.WriteAsync("cmd\r\n"u8.ToArray());
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
            while (!diagnostics.Any(message => message.Contains("bound session is closed", StringComparison.Ordinal)))
            {
                await Task.Delay(20, timeout.Token);
            }
        }
        finally
        {
            await core.DisposeAsync();
        }
    }

    [Fact]
    public async Task DisposeDuringPushCancelsCleanly()
    {
        int port = NextPort();
        await using BasicTelnetServer server = new();
        // A pull delegate that keeps producing RX lines forever.
        long logicalId = 0;
        TelnetSessionProbe probe = new(
            "COMX",
            cursor =>
            {
                List<StoredLine> lines =
                [
                    new(++logicalId, 0, LineDirection.Rx, DateTimeOffset.UtcNow, $"line-{logicalId}", true),
                ];
                return new LineStoreSnapshot(1, logicalId, 0, lines);
            },
            (_, _) => Task.CompletedTask);
        TelnetBridgeCore core = new(server, _ => probe);
        server.Start(port);
        using TcpClient client = new();
        await client.ConnectAsync(IPAddress.Loopback, port);
        using NetworkStream stream = client.GetStream();
        _ = await stream.ReadAsync(new byte[256]);

        core.Bind("COMX");
        await Task.Delay(1_500); // let a couple of pushes run

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        await core.DisposeAsync().AsTask().WaitAsync(timeout.Token); // must not hang
    }

    [Fact]
    public async Task PushSkipsTxLinesAndStripsAnsiEscapes()
    {
        int port = NextPort();
        await using BasicTelnetServer server = new();
        long logicalId = 0;
        TelnetSessionProbe probe = new(
            "COMX",
            cursor =>
            {
                List<StoredLine> lines =
                [
                    new(++logicalId, 0, LineDirection.Tx, DateTimeOffset.UtcNow, "should-not-push", true),
                    new(++logicalId, 0, LineDirection.Rx, DateTimeOffset.UtcNow, "\u001B[31mred\u001B[0m", true),
                ];
                return new LineStoreSnapshot(1, logicalId, 0, lines);
            },
            (_, _) => Task.CompletedTask);
        TelnetBridgeCore core = new(server, _ => probe);
        try
        {
            server.Start(port);
            using TcpClient client = new();
            await client.ConnectAsync(IPAddress.Loopback, port);
            using NetworkStream stream = client.GetStream();
            byte[] buffer = new byte[4_096];
            _ = await stream.ReadAsync(buffer); // welcome

            core.Bind("COMX");
            StringBuilder received = new();
            using CancellationTokenSource overall = new(TimeSpan.FromSeconds(10));
            while (received.ToString().Contains("red", StringComparison.Ordinal) is false)
            {
                using CancellationTokenSource readTimeout = new(TimeSpan.FromSeconds(4));
                int read = await stream.ReadAsync(buffer, readTimeout.Token).AsTask().WaitAsync(overall.Token);
                received.Append(Encoding.UTF8.GetString(buffer, 0, read));
            }

            string text = received.ToString();
            Assert.Contains("red", text, StringComparison.Ordinal);
            Assert.DoesNotContain("should-not-push", text, StringComparison.Ordinal);
            Assert.DoesNotContain("\u001B", text, StringComparison.Ordinal);
        }
        finally
        {
            await core.DisposeAsync();
        }
    }

    [Fact]
    public async Task ShellCommandsAreHandledLocallyAndOrdinaryInputStillBridges()
    {
        int port = NextPort();
        await using BasicTelnetServer server = new();
        List<string> sent = [];
        using SemaphoreSlim serialSend = new(0);
        await using TelnetBridgeCore core = new(
            server,
            _ => Probe("COMX", command =>
            {
                lock (sent)
                {
                    sent.Add(command);
                }

                serialSend.Release();
                return Task.CompletedTask;
            }));
        server.Start(port);
        core.Bind("COMX");

        using TcpClient client = new();
        await client.ConnectAsync(IPAddress.Loopback, port);
        using NetworkStream stream = client.GetStream();
        _ = await ReadTextAsync(stream);
        await stream.WriteAsync("help\r\nclear\r\nsendtoall hello\r\nordinary\r\n"u8.ToArray());

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        await serialSend.WaitAsync(timeout.Token);
        lock (sent)
        {
            Assert.Equal(["ordinary"], sent);
        }
    }

    [Fact]
    public async Task AuthenticationGatesShellAndSerialInput()
    {
        int port = NextPort();
        await using BasicTelnetServer server = new();
        List<string> sent = [];
        using SemaphoreSlim serialSend = new(0);
        await using TelnetBridgeCore core = new(
            server,
            _ => Probe("COMX", command =>
            {
                sent.Add(command);
                serialSend.Release();
                return Task.CompletedTask;
            }));
        core.ConfigureAuthentication(new TelnetAuthenticationOptions(true, "operator", "runtime-secret"));
        server.Start(port);
        core.Bind("COMX");

        using TcpClient client = new();
        await client.ConnectAsync(IPAddress.Loopback, port);
        using NetworkStream stream = client.GetStream();
        _ = await ReadTextAsync(stream);
        await stream.WriteAsync("operator\r\nruntime-secret\r\nhelp\r\nserial\r\n"u8.ToArray());

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        await serialSend.WaitAsync(timeout.Token);
        Assert.Equal(["serial"], sent);
    }

    [Fact]
    public async Task TelnetNegotiationNeverBecomesSerialPayload()
    {
        int port = NextPort();
        await using BasicTelnetServer server = new();
        List<string> sent = [];
        using SemaphoreSlim serialSend = new(0);
        await using TelnetBridgeCore core = new(
            server,
            _ => Probe("COMX", command =>
            {
                sent.Add(command);
                serialSend.Release();
                return Task.CompletedTask;
            }));
        server.Start(port);
        core.Bind("COMX");

        using TcpClient client = new();
        await client.ConnectAsync(IPAddress.Loopback, port);
        using NetworkStream stream = client.GetStream();
        _ = await ReadTextAsync(stream);
        await stream.WriteAsync(new byte[] { 255, 253, 1, (byte)'c', (byte)'m', (byte)'d', 13, 10 });

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        await serialSend.WaitAsync(timeout.Token);
        Assert.Equal(["cmd"], sent);
    }

    [Fact]
    public async Task DisposingBridgeCoreDoesNotStopApplicationOwnedServer()
    {
        int port = NextPort();
        await using BasicTelnetServer server = new();
        TelnetBridgeCore core = new(server, _ => null);
        server.Start(port);

        await core.DisposeAsync();

        Assert.True(server.IsRunning);
        using TcpClient client = new();
        await client.ConnectAsync(IPAddress.Loopback, port);
    }

    private static async Task<string> ReadTextAsync(NetworkStream stream)
    {
        byte[] buffer = new byte[512];
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        int read = await stream.ReadAsync(buffer, timeout.Token);
        return Encoding.UTF8.GetString(buffer, 0, read);
    }

    private static TelnetSessionProbe Probe(string portName, Func<string, Task> send) => new(
        portName,
        _ => new LineStoreSnapshot(null, null, 0, []),
        (command, _) => send(command));
}
