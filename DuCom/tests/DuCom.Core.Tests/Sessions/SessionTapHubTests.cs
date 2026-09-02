using System.Text;
using DuCom.Core.Parsing;
using DuCom.Core.Sending;
using DuCom.Core.Sessions;

namespace DuCom.Core.Tests.Sessions;

public sealed class SessionTapHubTests
{
    private static readonly DateTimeOffset FirstReceivedAt = new(2026, 8, 26, 1, 2, 3, 456, TimeSpan.Zero);
    private static readonly DateTimeOffset SecondReceivedAt = FirstReceivedAt.AddSeconds(1);

    private static ReceiveFormattingProfile CreateProfile(ReceiveDisplayMode mode) => new(
        Version: 0,
        EncodingName: "utf-8",
        DisplayMode: mode,
        TimestampEnabled: true);

    [Fact]
    public void StrTapPublishesTimestampedTextAndJoinsSoftWrappedContinuations()
    {
        SessionTapHub hub = new();
        List<string> published = [];
        hub.Register(new SessionDisplayTap
        {
            Id = "float",
            FormatSelector = () => SessionTapDisplayFormat.Str,
            Publish = published.Add,
        });

        hub.PublishReceive("hello"u8, FirstReceivedAt, CreateProfile(ReceiveDisplayMode.Str));
        hub.PublishReceive(" world"u8, SecondReceivedAt, CreateProfile(ReceiveDisplayMode.Str));
        hub.PublishReceive("\nnext"u8, SecondReceivedAt, CreateProfile(ReceiveDisplayMode.Str));

        string text = string.Concat(published);
        Assert.Contains($"[{FirstReceivedAt.ToLocalTime():HH:mm:ss.fff}] hello world", text, StringComparison.Ordinal);
        Assert.Contains($"\r\n[{SecondReceivedAt.ToLocalTime():HH:mm:ss.fff}] next", text, StringComparison.Ordinal);
    }

    [Fact]
    public void HexTapFormatsBytesAsSpacedHex()
    {
        SessionTapHub hub = new();
        List<string> published = [];
        hub.Register(new SessionDisplayTap
        {
            Id = "float",
            FormatSelector = () => SessionTapDisplayFormat.Hex,
            Publish = published.Add,
        });

        hub.PublishReceive(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, FirstReceivedAt, CreateProfile(ReceiveDisplayMode.Str));

        string text = string.Concat(published);
        Assert.Contains("DE AD BE EF", text, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatSwitchRestartsFormatterAndSkipsStalePartialLine()
    {
        SessionTapHub hub = new();
        List<string> published = [];
        SessionTapDisplayFormat format = SessionTapDisplayFormat.Str;
        hub.Register(new SessionDisplayTap
        {
            Id = "float",
            FormatSelector = () => format,
            Publish = published.Add,
        });

        hub.PublishReceive("par"u8, FirstReceivedAt, CreateProfile(ReceiveDisplayMode.Str));
        format = SessionTapDisplayFormat.Hex;
        hub.PublishReceive(new byte[] { 0x01 }, SecondReceivedAt, CreateProfile(ReceiveDisplayMode.Str));

        // SuperCom behavior: the format switch applies to new data only; the stale partial
        // text stays in the surface buffer and the fresh formatter opens its own line.
        string text = string.Concat(published);
        Assert.EndsWith("01", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TransmitIsPublishedToEveryTap()
    {
        SessionTapHub hub = new();
        List<string> first = [];
        List<string> second = [];
        hub.Register(new SessionDisplayTap
        {
            Id = "float",
            FormatSelector = () => SessionTapDisplayFormat.Str,
            Publish = first.Add,
        });
        hub.Register(new SessionDisplayTap
        {
            Id = "filter",
            FormatSelector = () => SessionTapDisplayFormat.Str,
            Publish = second.Add,
        });

        hub.PublishTransmit("TX > AT");

        string payload = "TX > AT\r\n";
        Assert.Equal([payload], first);
        Assert.Equal([payload], second);
    }

    [Fact]
    public void ReplyWindowReflectsLastSendModeAndExpires()
    {
        SessionTapHub hub = new();

        Assert.Null(hub.ResolveReplyWindowFormat(2_000));

        hub.NotifySent(SendMode.Hex);
        Assert.Equal(SendMode.Hex, hub.ResolveReplyWindowFormat(2_000));
        Assert.Equal(SendMode.Hex, hub.ResolveReplyWindowFormat(60_000));
        Assert.Null(hub.ResolveReplyWindowFormat(0));
    }

    [Fact]
    public void UnregisterStopsPublishingAndDuplicateRegistrationReplaces()
    {
        SessionTapHub hub = new();
        List<string> published = [];
        hub.Register(new SessionDisplayTap
        {
            Id = "float",
            FormatSelector = () => SessionTapDisplayFormat.Str,
            Publish = published.Add,
        });
        hub.Register(new SessionDisplayTap
        {
            Id = "float",
            FormatSelector = () => SessionTapDisplayFormat.Str,
            Publish = _ => Assert.Fail("replaced tap must not be invoked"),
        });

        Assert.True(hub.Unregister("FLOAT"));
        Assert.False(hub.Unregister("float"));
        Assert.Equal(0, hub.TapCount);

        hub.PublishTransmit("TX > idle");
        Assert.Empty(published);
    }

    [Fact]
    public void Utf8CharacterSplitAcrossBlocksIsReassembled()
    {
        SessionTapHub hub = new();
        List<string> published = [];
        hub.Register(new SessionDisplayTap
        {
            Id = "float",
            FormatSelector = () => SessionTapDisplayFormat.Str,
            Publish = published.Add,
        });

        byte[] bytes = Encoding.UTF8.GetBytes("中");
        hub.PublishReceive(bytes.AsSpan(0, 2), FirstReceivedAt, CreateProfile(ReceiveDisplayMode.Str));
        hub.PublishReceive(bytes.AsSpan(2), SecondReceivedAt, CreateProfile(ReceiveDisplayMode.Str));

        Assert.Contains("中", string.Concat(published), StringComparison.Ordinal);
    }

    [Fact]
    public void ThrowingFormatSelectorIsRemovedAndHealthyTapContinues()
    {
        SessionTapHub hub = new();
        List<string> healthyPublished = [];
        int selectorCalls = 0;
        hub.Register(new SessionDisplayTap
        {
            Id = "faulty",
            FormatSelector = () =>
            {
                selectorCalls++;
                throw new InvalidOperationException("selector failed");
            },
            Publish = _ => Assert.Fail("faulty tap must not publish"),
        });
        hub.Register(new SessionDisplayTap
        {
            Id = "healthy",
            FormatSelector = () => SessionTapDisplayFormat.Str,
            Publish = healthyPublished.Add,
        });

        hub.PublishReceive("first\n"u8, FirstReceivedAt, CreateProfile(ReceiveDisplayMode.Str));
        hub.PublishReceive("second\n"u8, SecondReceivedAt, CreateProfile(ReceiveDisplayMode.Str));

        Assert.Equal(1, selectorCalls);
        Assert.Equal(1, hub.TapCount);
        Assert.Contains("first", string.Concat(healthyPublished), StringComparison.Ordinal);
        Assert.Contains("second", string.Concat(healthyPublished), StringComparison.Ordinal);
    }

    [Fact]
    public void ThrowingPublishIsRemovedAndHealthyTapContinues()
    {
        SessionTapHub hub = new();
        List<string> healthyPublished = [];
        int faultyPublishCalls = 0;
        hub.Register(new SessionDisplayTap
        {
            Id = "faulty",
            FormatSelector = () => SessionTapDisplayFormat.Str,
            Publish = _ =>
            {
                faultyPublishCalls++;
                throw new InvalidOperationException("publish failed");
            },
        });
        hub.Register(new SessionDisplayTap
        {
            Id = "healthy",
            FormatSelector = () => SessionTapDisplayFormat.Str,
            Publish = healthyPublished.Add,
        });

        hub.PublishReceive("first\n"u8, FirstReceivedAt, CreateProfile(ReceiveDisplayMode.Str));
        hub.PublishReceive("second\n"u8, SecondReceivedAt, CreateProfile(ReceiveDisplayMode.Str));

        Assert.Equal(1, faultyPublishCalls);
        Assert.Equal(1, hub.TapCount);
        Assert.Contains("first", string.Concat(healthyPublished), StringComparison.Ordinal);
        Assert.Contains("second", string.Concat(healthyPublished), StringComparison.Ordinal);
    }
}
