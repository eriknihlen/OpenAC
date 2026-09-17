using System.Numerics;
using System.Reflection;
using AcDream.Core.Plugins;
using AcDream.Plugin.Abstractions;

namespace AcDream.Core.Tests.Plugins;

// ScopedPluginHost wraps the host's IEvents by hand-forwarding one add/remove
// pair per event so a disposed plugin scope stops receiving callbacks even
// though the host's own WorldEvents instance keeps running. IEvents members
// after EntitySpawned/Tick carry a default (no-op) implementation, so a
// forwarder that is never written compiles clean and silently drops that
// event for every plugin instead of delivering it. This test walks the
// interface by reflection so a future event cannot go unforwarded without a
// build-time-visible test failure, and proves the scope actually revokes on
// Dispose rather than just compiling.
public sealed class ScopedEventsTests
{
    [Fact]
    public void EveryEventIsForwardedThroughDisposeRevokesDelivery()
    {
        var inner = new WorldEvents();
        var host = new StubHost(inner);
        var scoped = new ScopedPluginHost(host, "example.plugin", "Example");

        EventInfo[] events = typeof(IEvents).GetEvents(
            BindingFlags.Public | BindingFlags.Instance);

        // Sanity check: if this drops, the interface shrank and the loop
        // below silently checks less than intended.
        Assert.True(
            events.Length >= 9,
            "IEvents should still have every event this test knows about.");

        var counters = new Dictionary<string, int[]>();
        int walked = 0;
        foreach (EventInfo eventInfo in events)
        {
            var counter = new int[1];
            counters[eventInfo.Name] = counter;
            Delegate handler = CreateHandler(eventInfo, counter);
            eventInfo.AddEventHandler(scoped.Events, handler);
            walked++;
        }

        // Every event IEvents declares today. If a new event is added
        // without a matching branch in CreateHandler/Fire below, those
        // helpers throw before this assertion is ever reached.
        Assert.Equal(9, walked);

        foreach (EventInfo eventInfo in events)
            Fire(inner, eventInfo.Name);

        foreach (EventInfo eventInfo in events)
        {
            Assert.True(
                counters[eventInfo.Name][0] > 0,
                "IEvents." + eventInfo.Name
                    + " was not delivered through ScopedEvents.");
        }

        scoped.Dispose();

        // Reset and fire again: a disposed scope must not still be wired to
        // the host's live WorldEvents instance.
        foreach (int[] counter in counters.Values)
            counter[0] = 0;

        foreach (EventInfo eventInfo in events)
            Fire(inner, eventInfo.Name);

        foreach (EventInfo eventInfo in events)
        {
            Assert.True(
                counters[eventInfo.Name][0] == 0,
                "IEvents." + eventInfo.Name
                    + " was still delivered after ScopedEvents was disposed.");
        }
    }

    private static Delegate CreateHandler(EventInfo eventInfo, int[] counter) =>
        eventInfo.Name switch
        {
            nameof(IEvents.Tick) =>
                new Action<double>(_ => counter[0]++),
            nameof(IEvents.EntitySpawned) =>
                new Action<WorldEntitySnapshot>(_ => counter[0]++),
            nameof(IEvents.LoginComplete) =>
                new Action(() => counter[0]++),
            nameof(IEvents.Logoff) =>
                new Action(() => counter[0]++),
            nameof(IEvents.LocalPlayerDied) =>
                new Action<string>(_ => counter[0]++),
            nameof(IEvents.ObjectChanged) =>
                new Action<PluginObjectChange>(_ => counter[0]++),
            nameof(IEvents.ContainerOpened) =>
                new Action<uint>(_ => counter[0]++),
            nameof(IEvents.ContainerClosed) =>
                new Action<uint>(_ => counter[0]++),
            nameof(IEvents.ConfirmationRequested) =>
                new Action<PluginConfirmation>(_ => counter[0]++),
            _ => throw new InvalidOperationException(
                "No handler mapping for IEvents." + eventInfo.Name
                    + " - add one here and to Fire() below."),
        };

    private static void Fire(WorldEvents inner, string eventName)
    {
        switch (eventName)
        {
            case nameof(IEvents.Tick):
                inner.FireTick(1.0);
                break;
            case nameof(IEvents.EntitySpawned):
                inner.FireEntitySpawned(
                    new WorldEntitySnapshot(1u, 1u, Vector3.Zero, Quaternion.Identity));
                break;
            case nameof(IEvents.LoginComplete):
                inner.FireLoginComplete();
                break;
            case nameof(IEvents.Logoff):
                inner.FireLogoff();
                break;
            case nameof(IEvents.LocalPlayerDied):
                inner.FireLocalPlayerDied("you have died");
                break;
            case nameof(IEvents.ObjectChanged):
                inner.FireObjectChanged(
                    new PluginObjectChange(1u, PluginObjectChangeKind.Created));
                break;
            case nameof(IEvents.ContainerOpened):
                inner.FireContainerOpened(1u);
                break;
            case nameof(IEvents.ContainerClosed):
                inner.FireContainerClosed(1u);
                break;
            case nameof(IEvents.ConfirmationRequested):
                inner.FireConfirmationRequested(
                    new PluginConfirmation(1u, 5, "continue?"));
                break;
            default:
                throw new InvalidOperationException(
                    "No fire mapping for IEvents." + eventName
                        + " - add one here and to CreateHandler() above.");
        }
    }

    private sealed class StubHost(IEvents events) : IPluginHost
    {
        public bool HasUi => false;
        public IPluginLogger Log { get; } = new SilentLogger();
        public IGameState State { get; } = new EmptyGameState();
        public IEvents Events { get; } = events;
        public ISelectionService Selection { get; } = new InertSelection();
        public IUiRegistry Ui { get; } = NoOpUiRegistry.Instance;
        public IAutomationSurface Automation { get; } = NoOpAutomationSurface.Instance;

        private sealed class SilentLogger : IPluginLogger
        {
            public void Info(string message) { }
            public void Warn(string message) { }
            public void Error(string message, Exception? error = null) { }
        }

        private sealed class EmptyGameState : IGameState
        {
            public IReadOnlyList<WorldEntitySnapshot> Entities { get; } = [];
        }

        private sealed class InertSelection : ISelectionService
        {
            public uint? SelectedObjectId => null;
            public uint? PreviousObjectId => null;
            public event Action<SelectionChangedEvent>? Changed;
            public bool Select(uint objectId)
            {
                Changed?.Invoke(default);
                return false;
            }
            public bool Clear() => false;
        }
    }
}
