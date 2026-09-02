using System.Net;
using System.Net.Sockets;
using System.Text;
using DuCom.Core.Telnet;
using Xunit;

namespace DuCom.Core.Tests.Telnet;

public sealed class TelnetListenOptionsTests
{
    [Fact]
    public void DefaultIsLoopbackOnly()
    {
        TelnetListenOptions options = new(23_230);

        Assert.Equal(IPAddress.Loopback, options.BindAddress);
        Assert.False(options.AllowRemote);
    }

    [Fact]
    public void RemoteRequiresExplicitOptIn()
    {
        TelnetListenOptions options = new(23_230, AllowRemote: true, AuthenticationEnabled: true);

        Assert.Equal(IPAddress.Any, options.BindAddress);
    }

    [Fact]
    public void RemoteWithoutAuthenticationIsRejected()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new TelnetListenOptions(23_230, AllowRemote: true).Validate());
    }

    [Fact]
    public void PortBoundsAreValidated()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TelnetListenOptions(0).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new TelnetListenOptions(65_536).Validate());
    }
}

public sealed class TelnetNegotiationFilterTests
{
    [Fact]
    public void NegotiationSplitAcrossChunksIsRemoved()
    {
        TelnetNegotiationFilter filter = new();

        Assert.Equal("he"u8.ToArray(), filter.Filter([255, 253, 1, (byte)'h', (byte)'e', 255]));
        Assert.Equal("lp\r\n"u8.ToArray(), filter.Filter([251, 3, (byte)'l', (byte)'p', 13, 10]));
    }

    [Fact]
    public void SubnegotiationAndEscapedIacAreHandled()
    {
        TelnetNegotiationFilter filter = new();

        byte[] result = filter.Filter([255, 250, 31, 0, 80, 0, 24, 255, 240, (byte)'x', 255, 255]);

        Assert.Equal([(byte)'x', (byte)255], result);
    }
}

public sealed class IncrementalUtf8LineFramerTests
{
    [Fact]
    public void MultibyteCharacterSplitAcrossChunksIsNotCorrupted()
    {
        IncrementalUtf8LineFramer framer = new();
        // "娓╁害=25.5\n" 鈥?the 3-byte 娓?is split after its first byte.
        byte[] payload = "娓╁害=25.5\n"u8.ToArray();

        Assert.Empty(framer.Append(payload.AsMemory(0, 1).Span));
        Assert.Empty(framer.Append(payload.AsMemory(1, 1).Span));
        IReadOnlyList<string> lines = framer.Append(payload.AsMemory(2, payload.Length - 2).Span);

        Assert.Equal(["娓╁害=25.5"], lines);
    }

    [Fact]
    public void CrlfSplitAcrossChunksYieldsExactlyOneLine()
    {
        IncrementalUtf8LineFramer framer = new();

        Assert.Equal(["cmd1"], framer.Append("cmd1\r"u8.ToArray()));
        IReadOnlyList<string> rest = framer.Append("\n"u8.ToArray());

        Assert.Empty(rest); // the LF after CR is a second terminator with an empty pending line
    }

    [Fact]
    public void CrLfAndLfAndLoneCrAllTerminateLines()
    {
        IncrementalUtf8LineFramer framer = new();

        IReadOnlyList<string> lines = framer.Append("a\r\nb\nc\rd"u8.ToArray());

        Assert.Equal(["a", "b", "c"], lines);
        Assert.Equal("d", framer.Flush());
    }

    [Fact]
    public void MultipleLinesInOneChunkAreSplitInOrder()
    {
        IncrementalUtf8LineFramer framer = new();

        IReadOnlyList<string> lines = framer.Append("one\r\ntwo\r\nthree\r\n"u8.ToArray());

        Assert.Equal(["one", "two", "three"], lines);
    }

    [Fact]
    public void TrailingPartialLineStaysBufferedUntilFlush()
    {
        IncrementalUtf8LineFramer framer = new();

        Assert.Equal(["done"], framer.Append("done\npar"u8.ToArray()));
        Assert.Equal("par", framer.Flush());
        Assert.Null(framer.Flush());
    }

    [Fact]
    public void EmptyLinesAreNotEmitted()
    {
        IncrementalUtf8LineFramer framer = new();

        IReadOnlyList<string> lines = framer.Append("\r\n\r\nx\r\n"u8.ToArray());

        Assert.Equal(["x"], lines);
    }

    [Fact]
    public void ResetClearsPendingAndDecoderState()
    {
        IncrementalUtf8LineFramer framer = new();
        framer.Append("ab"u8.ToArray());
        framer.Reset();

        Assert.Null(framer.Flush());
        Assert.Equal(["c"], framer.Append("c\n"u8.ToArray()));
    }
}

public sealed class BasicTelnetServerTests
{
    [Fact]
    public async Task StartBindsLoopbackByDefaultAndAcceptsLocalClient()
    {
        BasicTelnetServer server = new();
        try
        {
            server.Start(0 is var _ ? 23_231 : 23_231);

            using TcpClient client = new();
            await client.ConnectAsync(IPAddress.Loopback, 23_231);
            using NetworkStream stream = client.GetStream();
            byte[] buffer = new byte[256];
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
            int read = await stream.ReadAsync(buffer, timeout.Token);
            string welcome = Encoding.UTF8.GetString(buffer, 0, read);

            Assert.Contains("DuCom Telnet bridge ready.", welcome);
            Assert.Contains("127.0.0.1", server.LocalEndPoint, StringComparison.Ordinal);
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task RemoteConnectIsRefusedWithoutAllowRemote()
    {
        BasicTelnetServer server = new();
        try
        {
            server.Start(23_232); // loopback only

            IPAddress? lanAddress = Dns.GetHostAddresses(Dns.GetHostName())
                .FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address));
            if (lanAddress is null)
            {
                // No LAN address on this machine; the loopback-bind policy is still
                // asserted through BindAddress in TelnetListenOptionsTests.
                return;
            }

            using TcpClient client = new();
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(3));
            // The loopback-bound listener must not accept a connection addressed to the
            // machine's non-loopback address.
            await Assert.ThrowsAnyAsync<Exception>(() =>
                client.ConnectAsync(lanAddress, 23_232).WaitAsync(timeout.Token));
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task ClientDisconnectRemovesClientFromList()
    {
        BasicTelnetServer server = new();
        try
        {
            server.Start(23_233);
            TcpClient client = new();
            await client.ConnectAsync(IPAddress.Loopback, 23_233);
            _ = await client.GetStream().ReadAsync(new byte[256]);

            client.Close();
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
            while (server.ClientCount > 0)
            {
                await Task.Delay(20, timeout.Token);
            }

            Assert.Equal(0, server.ClientCount);
        }
        finally
        {
            await server.DisposeAsync();
        }
    }
}
