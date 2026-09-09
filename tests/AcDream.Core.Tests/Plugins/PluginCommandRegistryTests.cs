using AcDream.Core.Plugins;
using AcDream.Plugin.Abstractions;

namespace AcDream.Core.Tests.Plugins;

public sealed class PluginCommandRegistryTests
{
    [Fact]
    public void RegisteredVerbHandlesBothPrefixesCaseInsensitively()
    {
        var registry = new PluginCommandRegistry();
        var seen = new List<PluginCommand>();
        using IDisposable lease = registry.Register("vt", seen.Add);

        Assert.True(registry.TryHandle("/VT start"));
        Assert.True(registry.TryHandle(" @vt   opt get EnableCombat "));
        Assert.Equal(2, seen.Count);
        Assert.Equal("start", seen[0].Arguments);
        Assert.Equal("opt get EnableCombat", seen[1].Arguments);
        Assert.Equal("/VT start", seen[0].RawText);
    }

    [Fact]
    public void ExactLeaseRemovalDoesNotConsumeUnknownServerCommand()
    {
        var registry = new PluginCommandRegistry();
        IDisposable lease = registry.Register("vt", static _ => { });
        lease.Dispose();

        Assert.False(registry.TryHandle("/vt start"));
        Assert.False(registry.TryHandle("hello"));
    }

    [Fact]
    public void DuplicateVerbIsRejectedWithoutReplacingOwner()
    {
        var registry = new PluginCommandRegistry();
        int calls = 0;
        using IDisposable lease = registry.Register("vt", _ => calls++);

        Assert.Throws<InvalidOperationException>(() =>
            registry.Register("VT", static _ => { }));
        Assert.True(registry.TryHandle("/vt"));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void HandlerFailureIsContainedAndReported()
    {
        Exception? failure = null;
        var registry = new PluginCommandRegistry((_, error) => failure = error);
        using IDisposable lease = registry.Register(
            "vt",
            static _ => throw new InvalidOperationException("broken"));

        Assert.True(registry.TryHandle("/vt start"));
        Assert.Equal("broken", failure?.Message);
    }
}
