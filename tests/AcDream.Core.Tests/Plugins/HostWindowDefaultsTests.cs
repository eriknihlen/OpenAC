using AcDream.Plugin.Abstractions;

namespace AcDream.Core.Tests.Plugins;

// IHostWindow's own default interface implementations, and IPluginHost's
// default Window property -- proven the same way IPluginClipboard's and
// IHotkeyRegistry's no-op defaults are documented: a host that never
// implements the member still answers every call, all as "unavailable",
// rather than throwing.
public sealed class HostWindowDefaultsTests
{
    private sealed class MinimalHostWindow : IHostWindow;

    [Fact]
    public void DefaultMembersReportUnavailableAndNotMinimized()
    {
        IHostWindow window = new MinimalHostWindow();

        Assert.False(window.IsMinimized);
        Assert.Equal(HostWindowStatus.Unavailable, window.Minimize().Status);
        Assert.Equal(HostWindowStatus.Unavailable, window.Restore().Status);
        Assert.Equal(HostWindowStatus.Unavailable, window.RequestClose().Status);
    }

    [Fact]
    public void NoOpHostWindowIsTheSharedInstanceAndBehavesLikeTheDefault()
    {
        Assert.Same(NoOpHostWindow.Instance, NoOpHostWindow.Instance);

        IHostWindow window = NoOpHostWindow.Instance;
        Assert.False(window.IsMinimized);
        Assert.False(window.Minimize().Succeeded);
        Assert.False(window.Restore().Succeeded);
        Assert.False(window.RequestClose().Succeeded);
    }

    private sealed class MinimalPluginHost : IPluginHost
    {
        public bool HasUi => false;
        public IPluginLogger Log => throw new NotSupportedException();
        public IGameState State => throw new NotSupportedException();
        public IEvents Events => throw new NotSupportedException();
        public ISelectionService Selection => throw new NotSupportedException();
        public IUiRegistry Ui => throw new NotSupportedException();
        public IAutomationSurface Automation => throw new NotSupportedException();
    }

    [Fact]
    public void AHostThatNeverImplementsWindowExposesTheSharedNoOp()
    {
        IPluginHost host = new MinimalPluginHost();

        Assert.Same(NoOpHostWindow.Instance, host.Window);
    }
}
