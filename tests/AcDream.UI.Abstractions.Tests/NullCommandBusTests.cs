namespace AcDream.UI.Abstractions.Tests;

public sealed class NullCommandBusTests
{
    private sealed record FakeCmd(int Value);

    [Fact]
    public void Publish_DoesNotThrow_OnAnyRecordType()
    {
        var bus = NullCommandBus.Instance;

        bus.Publish(new FakeCmd(42));
        bus.Publish("a string command");
        bus.Publish(12345);
    }

    [Fact]
    public void Instance_IsSingleton()
    {
        Assert.Same(NullCommandBus.Instance, NullCommandBus.Instance);
    }
}
