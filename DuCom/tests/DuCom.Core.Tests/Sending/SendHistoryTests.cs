using DuCom.Core.Sending;
using Xunit;

namespace DuCom.Core.Tests.Sending;

public class SendHistoryTests
{
    [Fact]
    public void Record_IgnoresEmptyAndWhitespace()
    {
        SendHistory history = new();

        Assert.False(history.Record(null));
        Assert.False(history.Record(string.Empty));
        Assert.False(history.Record("   \r\n"));
        Assert.Equal(0, history.Count);
    }

    [Fact]
    public void Record_SuppressesImmediateDuplicate()
    {
        SendHistory history = new();
        history.Record("ATZ");

        Assert.False(history.Record("ATZ"));
        Assert.Single(history.Entries);
    }

    [Fact]
    public void Record_OlderDuplicateMovesToNewestPosition()
    {
        SendHistory history = new();
        history.Record("A");
        history.Record("B");
        history.Record("A");

        Assert.Equal(2, history.Count);
        Assert.Equal(["B", "A"], history.Entries);
    }

    [Fact]
    public void Record_TrimsTrailingNewlinesOnly()
    {
        SendHistory history = new();
        history.Record("payload \r\n");

        // The stored entry stays faithful to what was on the wire except for line breaks.
        Assert.Equal("payload ", history.Entries[0]);
    }

    [Fact]
    public void Record_CapacityEvictsOldest()
    {
        SendHistory history = new(3);
        history.Record("1");
        history.Record("2");
        history.Record("3");
        history.Record("4");

        Assert.Equal(3, history.Count);
        Assert.Equal(["2", "3", "4"], history.Entries);
    }

    [Fact]
    public void Replace_RestoresEntriesWithNormalization()
    {
        SendHistory history = new(3);
        history.Replace(["x", "y", "y", "z"]);

        Assert.Equal(3, history.Count);
        Assert.Equal(["x", "y", "z"], history.Entries);
    }

    [Fact]
    public void Clear_RemovesEverything()
    {
        SendHistory history = new();
        history.Record("a");
        history.Clear();

        Assert.Equal(0, history.Count);
    }

    [Theory]
    [InlineData(null, new[] { "AT+RESET", "version", "AT+INFO" })]
    [InlineData("at+", new[] { "AT+RESET", "AT+INFO" })]
    [InlineData("SION", new[] { "version" })]
    [InlineData("missing", new string[0])]
    public void Search_FiltersCaseInsensitivelyNewestFirst(string? query, string[] expected)
    {
        SendHistory history = new();
        history.Replace(["AT+INFO", "version", "AT+RESET"]);

        Assert.Equal(expected, history.Search(query));
        Assert.Equal(["AT+INFO", "version", "AT+RESET"], history.Entries);
    }

    [Fact]
    public void Constructor_RejectsInvalidCapacity()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SendHistory(0));
    }
}

public class SendHistoryNavigatorTests
{
    private static SendHistory CreateHistory()
    {
        SendHistory history = new();
        history.Record("first");
        history.Record("second");
        return history;
    }

    [Fact]
    public void MovePrevious_EmptyHistory_ReturnsNull()
    {
        SendHistoryNavigator navigator = new(new SendHistory());

        Assert.Null(navigator.MovePrevious("draft"));
        Assert.False(navigator.IsBrowsing);
    }

    [Fact]
    public void MovePrevious_SavesDraftThenWalksBackward()
    {
        SendHistoryNavigator navigator = new(CreateHistory());

        Assert.Equal("second", navigator.MovePrevious("live edit"));
        Assert.True(navigator.IsBrowsing);
        Assert.Equal("first", navigator.MovePrevious(string.Empty));
        Assert.Null(navigator.MovePrevious(string.Empty)); // already at oldest
    }

    [Fact]
    public void MoveNext_BeyondNewestRestoresDraft()
    {
        SendHistoryNavigator navigator = new(CreateHistory());
        navigator.MovePrevious("my draft");
        navigator.MovePrevious(string.Empty);

        Assert.Equal("second", navigator.MoveNext());
        Assert.Equal("my draft", navigator.MoveNext());
        Assert.False(navigator.IsBrowsing);
        Assert.Null(navigator.MoveNext());
    }

    [Fact]
    public void MoveNext_WithoutBrowsing_ReturnsNull()
    {
        SendHistoryNavigator navigator = new(CreateHistory());

        Assert.Null(navigator.MoveNext());
    }

    [Fact]
    public void Reset_ReturnsToDraftState()
    {
        SendHistoryNavigator navigator = new(CreateHistory());
        navigator.MovePrevious("d");

        navigator.Reset();

        Assert.False(navigator.IsBrowsing);
    }
}
