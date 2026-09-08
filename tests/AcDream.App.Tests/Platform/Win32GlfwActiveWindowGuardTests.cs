using AcDream.App.Platform;

namespace AcDream.App.Tests.Platform;

public sealed class Win32GlfwActiveWindowGuardTests
{
    [Fact]
    public void CurrentProcessWindowRemainsVisibleToGlfw()
    {
        nint window = (nint)0x1234;

        Assert.Equal(
            window,
            Win32GlfwActiveWindowGuard.AcceptWindow(
                window,
                ownerProcessId: 47,
                currentProcessId: 47));
    }

    [Fact]
    public void ForeignProcessWindowBecomesGlfwsExistingNoWindowPath()
    {
        Assert.Equal(
            0,
            Win32GlfwActiveWindowGuard.AcceptWindow(
                (nint)0x1234,
                ownerProcessId: 48,
                currentProcessId: 47));
    }

    [Theory]
    [InlineData(0, 47, 47)]
    [InlineData(0x1234, 0, 47)]
    public void MissingOrUnownedWindowIsRejected(
        long window,
        uint ownerProcessId,
        uint currentProcessId)
    {
        Assert.Equal(
            0,
            Win32GlfwActiveWindowGuard.AcceptWindow(
                (nint)window,
                ownerProcessId,
                currentProcessId));
    }
}
