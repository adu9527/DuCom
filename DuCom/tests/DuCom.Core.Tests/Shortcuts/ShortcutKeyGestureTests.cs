namespace DuCom.Core.Tests.Shortcuts;

using DuCom.Services.Shortcuts;

public sealed class ShortcutKeyGestureTests
{
    [Theory]
    [InlineData("Ctrl+A", "Ctrl+A")]
    [InlineData("ctrl+a", "Ctrl+A")]
    [InlineData("Shift+Alt+F1", "Shift+Alt+F1")]
    [InlineData("Alt+Shift+F1", "Shift+Alt+F1")]
    [InlineData("Win+Control+B", "Ctrl+Win+B")]
    [InlineData(" F5 ", "F5")]
    public void Parse_NormalizesOrderAndCase(string input, string expected)
    {
        ShortcutKeyGesture? gesture = ShortcutKeyGesture.Parse(input);
        Assert.NotNull(gesture);
        Assert.Equal(expected, gesture.ToDisplayText());
    }

    [Theory]
    [InlineData("Ctrl")]
    [InlineData("Shift")]
    [InlineData("Alt")]
    [InlineData("Win")]
    [InlineData("Ctrl+Shift")]
    public void IsModifierOnly_DetectsModifierKeys(string input)
    {
        ShortcutKeyGesture? gesture = ShortcutKeyGesture.Parse(input);
        Assert.NotNull(gesture);
        Assert.True(gesture.IsModifierOnly);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("+")]
    public void Parse_InvalidInput_ReturnsNull(string? input)
    {
        ShortcutKeyGesture? gesture = ShortcutKeyGesture.Parse(input);
        Assert.Null(gesture);
    }

    [Fact]
    public void Matches_IgnoresKeyCaseAndOrder()
    {
        ShortcutKeyGesture first = ShortcutKeyGesture.Parse("ctrl+shift+a")!;
        ShortcutKeyGesture second = ShortcutKeyGesture.Parse("Shift+Ctrl+A")!;
        Assert.True(first.Matches(second));
    }

    [Fact]
    public void Matches_DistinguishesDifferentKeys()
    {
        ShortcutKeyGesture first = ShortcutKeyGesture.Parse("Ctrl+A")!;
        ShortcutKeyGesture second = ShortcutKeyGesture.Parse("Ctrl+B")!;
        Assert.False(first.Matches(second));
    }
}
