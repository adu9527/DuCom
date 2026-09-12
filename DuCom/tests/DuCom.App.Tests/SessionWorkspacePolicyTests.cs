using DuCom.ViewModels;
using Xunit;

namespace DuCom.App.Tests;

public sealed class SessionWorkspacePolicyTests
{
    [Fact]
    public void ExplicitOpenPortsWinAndAreNormalized()
    {
        string[] result = SessionWorkspacePolicy.ResolveOpenPorts(
            ["COM2", "com2", " ", "COM4"], ["COM8"], ["COM1"]);

        Assert.Equal(["COM2", "COM4"], result);
    }

    [Fact]
    public void LegacyLayoutRestoresRightPortsAndFirstLeftPort()
    {
        string[] result = SessionWorkspacePolicy.ResolveOpenPorts(
            [], ["COM8", "COM9"], ["COM9", "COM3", "COM4"]);

        Assert.Equal(["COM8", "COM9", "COM3"], result);
    }

    [Theory]
    [InlineData(-3, 4, 0)]
    [InlineData(2, 4, 2)]
    [InlineData(99, 4, 3)]
    [InlineData(5, 0, 0)]
    public void MoveIndexIsBounded(int requested, int count, int expected) =>
        Assert.Equal(expected, SessionWorkspacePolicy.BoundMoveIndex(requested, count));

    [Fact]
    public void ActiveSessionFallsBackWhenRememberedSessionWasRemoved()
    {
        object removed = new();
        object selected = new();

        object? active = SessionWorkspacePolicy.ResolveActive(removed, selected, null, [selected]);

        Assert.Same(selected, active);
        Assert.Null(SessionWorkspacePolicy.ResolveActive(removed, removed, null, Array.Empty<object>()));
    }
}
