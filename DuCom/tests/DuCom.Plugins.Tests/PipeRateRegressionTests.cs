using System.Buffers.Binary;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Text.Json;
using DuCom.Plugin;
using DuCom.PluginHost.Transport;
using Xunit;

namespace DuCom.Plugins.Tests;

public sealed class PipeRateRegressionTests
{
    private const long ByteLimit = 32L * 1024 * 1024;
    private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;

    [Fact]
    public async Task AccumulatesBytesAndAllowsExactlyTheLimit()
    {
        await using PluginPipeServer server = PluginPipeServer.Create(Guid.NewGuid().ToString("N"), string.Empty);
        for (int i = 0; i < 32; i++)
        {
            Count(server, 1024 * 1024, 100_000);
        }

        Assert.Equal(ByteLimit, WindowBytes(server));
        Assert.Throws<PipeRateViolationException>(() => Count(server, 1, 100_000));
    }

    [Fact]
    public async Task EvictsOnlyEntriesOlderThanTenSeconds()
    {
        await using PluginPipeServer server = PluginPipeServer.Create(Guid.NewGuid().ToString("N"), string.Empty);
        Count(server, 100, 100_000);
        Count(server, 200, 105_000);
        Count(server, 0, 110_000);
        Assert.Equal(300, WindowBytes(server));
        Count(server, 7, 110_001);
        Assert.Equal(207, WindowBytes(server));
        Count(server, ByteLimit - 7, 115_001);
        Assert.Equal(ByteLimit, WindowBytes(server));
        Assert.Throws<PipeRateViolationException>(() => Count(server, 1, 115_001));
    }

    [Fact]
    public async Task MessageLimitStillAppliesAndExpires()
    {
        await using PluginPipeServer server = PluginPipeServer.Create(Guid.NewGuid().ToString("N"), string.Empty);
        for (int i = 0; i < 4000; i++)
        {
            Count(server, 4, 100_000);
        }

        Assert.Throws<PipeRateViolationException>(() => Count(server, 4, 110_000));
        Count(server, 4, 110_001);
        Assert.Equal(8, WindowBytes(server));
    }

    [Fact]
    public async Task CountsOriginalJsonIncludingWhitespaceUnknownFieldsAndHeader()
    {
        var (server, client) = await ConnectAsync();
        await using (server)
        await using (client)
        using (CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5)))
        {
            byte[] frame = Frame("{\"k\":\"req\",\"ignored\":\"" + new string('x', 4096) + "\"}   ");
            Task<PluginWireMessage?> read = ReadOne(server, timeout.Token);
            await client.WriteAsync(frame, timeout.Token);
            PluginWireMessage? message = await read;
            Assert.NotNull(message);
            Assert.True(PluginWire.Encode(message).Length < frame.Length);
            Assert.Equal(frame.Length, WindowBytes(server));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadLoopRejectsRawByteExcessBeforeDispatchOrJsonParsing(bool malformed)
    {
        var (server, client) = await ConnectAsync();
        await using (server)
        await using (client)
        using (CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5)))
        {
            byte[] frame = Frame(malformed ? "{" : "{\"k\":\"req\"}   ");
            // Payload alone fits; the original header pushes the window over its limit.
            Count(server, ByteLimit - frame.Length + 1, Environment.TickCount64);
            int dispatched = 0;
            Exception? failure = null;
            server.MessageReceived += _ => dispatched++;
            server.ConnectionClosed += error => failure = error;
            Task loop = (Task)typeof(PluginPipeServer).GetMethod("ReadLoopAsync", PrivateInstance)!.Invoke(server, null)!;
            await client.WriteAsync(frame, timeout.Token);
            await loop.WaitAsync(timeout.Token);
            Assert.IsType<PipeRateViolationException>(failure);
            Assert.Equal(0, dispatched);
            Assert.Equal(ByteLimit + 1, WindowBytes(server));
        }
    }

    [Fact]
    public async Task MalformedJsonIsChargedBeforeParsingFails()
    {
        var (server, client) = await ConnectAsync();
        await using (server)
        await using (client)
        using (CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5)))
        {
            byte[] frame = Frame("{");
            Task<PluginWireMessage?> read = ReadOne(server, timeout.Token);
            await client.WriteAsync(frame, timeout.Token);
            await Assert.ThrowsAsync<JsonException>(() => read);
            Assert.Equal(frame.Length, WindowBytes(server));
        }
    }

    [Theory]
    [InlineData((uint)PluginWire.MaximumFrameBytes + 1)]
    [InlineData(uint.MaxValue)]
    public async Task OversizedFrameIsRejectedFromHeaderWithoutReadingPayload(uint length)
    {
        var (server, client) = await ConnectAsync();
        await using (server)
        await using (client)
        using (CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5)))
        {
            byte[] header = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(header, length);
            Task<PluginWireMessage?> read = ReadOne(server, timeout.Token);
            await client.WriteAsync(header, timeout.Token);
            await Assert.ThrowsAsync<PipeProtocolException>(() => read);
        }
    }

    private static async Task<(PluginPipeServer Server, NamedPipeClientStream Client)> ConnectAsync()
    {
        string name = Guid.NewGuid().ToString("N");
        NamedPipeServerStream pipe = new(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        PluginPipeServer server = (PluginPipeServer)Activator.CreateInstance(
            typeof(PluginPipeServer), PrivateInstance, null, [pipe], null)!;
        NamedPipeClientStream client = new(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
            Task connection = pipe.WaitForConnectionAsync(timeout.Token);
            await client.ConnectAsync(timeout.Token);
            await connection;
            return (server, client);
        }
        catch
        {
            await client.DisposeAsync();
            await server.DisposeAsync();
            throw;
        }
    }

    private static byte[] Frame(string json)
    {
        byte[] payload = Encoding.UTF8.GetBytes(json);
        byte[] frame = new byte[4 + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)payload.Length);
        payload.CopyTo(frame, 4);
        return frame;
    }

    private static Task<PluginWireMessage?> ReadOne(PluginPipeServer server, CancellationToken token) =>
        (Task<PluginWireMessage?>)typeof(PluginPipeServer).GetMethod("ReadOneAsync", PrivateInstance)!.Invoke(server, [token])!;

    private static long WindowBytes(PluginPipeServer server) =>
        (long)typeof(PluginPipeServer).GetField("_windowBytes", PrivateInstance)!.GetValue(server)!;

    private static void Count(PluginPipeServer server, long bytes, long now)
    {
        try
        {
            typeof(PluginPipeServer).GetMethod("CountRate", PrivateInstance)!.Invoke(server, [bytes, now]);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is PipeRateViolationException violation)
        {
            throw violation;
        }
    }
}
