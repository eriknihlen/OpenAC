using System.Numerics;
using AcDream.App.Composition;
using AcDream.Core.Physics;
using DatReaderWriter.Types;

namespace AcDream.App.Tests.Composition;

public sealed class AnimationHookRegistrationSetTests
{
    [Fact]
    public void CleanupUnregistersInReverseAndNeverReplaysSuccessfulEdges()
    {
        var calls = new List<string>();
        var first = new Sink("first");
        var second = new Sink("second");
        var third = new Sink("third");
        bool failSecond = true;
        var registrations = new AnimationHookRegistrationSet(
            sink => calls.Add($"add:{((Sink)sink).Name}"),
            sink =>
            {
                string name = ((Sink)sink).Name;
                calls.Add($"remove:{name}");
                if (name == "second" && failSecond)
                    throw new InvalidOperationException("synthetic removal failure");
            });
        registrations.Register(first);
        registrations.Register(second);
        registrations.Register(third);

        Assert.Throws<AggregateException>(registrations.Dispose);
        Assert.False(registrations.IsCleanupComplete);
        failSecond = false;
        registrations.Dispose();
        registrations.Dispose();

        Assert.True(registrations.IsCleanupComplete);
        Assert.Equal(
            [
                "add:first", "add:second", "add:third",
                "remove:third", "remove:second", "remove:first",
                "remove:second",
            ],
            calls);
    }

    [Fact]
    public void DuplicateRegistrationIsIdempotentAndClosedSetRejectsNewEdges()
    {
        int adds = 0;
        int removes = 0;
        var sink = new Sink("only");
        var registrations = new AnimationHookRegistrationSet(
            _ => adds++,
            _ => removes++);

        registrations.Register(sink);
        registrations.Register(sink);
        registrations.Dispose();

        Assert.Equal(1, adds);
        Assert.Equal(1, removes);
        Assert.Throws<ObjectDisposedException>(() =>
            registrations.Register(new Sink("late")));
    }

    [Fact]
    public void OwnedRegistrationReleasesExactlyAndRetriesItsOwnFailure()
    {
        int adds = 0;
        int removes = 0;
        bool fail = true;
        var sink = new Sink("owned");
        var registrations = new AnimationHookRegistrationSet(
            _ => adds++,
            _ =>
            {
                removes++;
                if (fail)
                    throw new InvalidOperationException("retry");
            });

        IDisposable binding = registrations.RegisterOwned(sink);
        Assert.Throws<InvalidOperationException>(() =>
            registrations.RegisterOwned(sink));
        Assert.Throws<InvalidOperationException>(binding.Dispose);
        fail = false;
        binding.Dispose();
        binding.Dispose();
        registrations.Dispose();

        Assert.Equal(1, adds);
        Assert.Equal(2, removes);
        Assert.True(registrations.IsCleanupComplete);
    }

    private sealed class Sink(string name) : IAnimationHookSink
    {
        public string Name { get; } = name;
        public void OnHook(
            uint entityId,
            Vector3 entityWorldPosition,
            AnimationHook hook)
        {
        }
    }
}
