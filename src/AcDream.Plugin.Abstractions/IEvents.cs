// src/AcDream.Plugin.Abstractions/IEvents.cs
namespace AcDream.Plugin.Abstractions;

/// <summary>
/// The client's notifications to plugins. Every handler runs on the
/// client's own update thread, and an exception thrown by one handler is
/// swallowed so it cannot take down the client or the other handlers.
/// </summary>
public interface IEvents
{
    /// <summary>
    /// Raised once for each new thing the client starts drawing in the
    /// world, carrying where it appeared.
    /// </summary>
    event Action<WorldEntitySnapshot> EntitySpawned;

    /// <summary>
    /// Raised at a fixed 15 ms -- about 66.7 times a second -- carrying
    /// exactly 0.015 seconds every time, on every client. It is not the
    /// client's own frame: a client that draws runs far faster than this and
    /// one that does not takes its own turns, and neither rate reaches a
    /// plugin, so the same plugin is paced the same way wherever it runs.
    /// A client that has fallen behind raises several in a row, and a stall
    /// longer than about a fifth of a second is dropped rather than replayed.
    /// This is the thread every other event here is raised on, and the thread
    /// a plugin should touch its own state on.
    /// </summary>
    event Action<double> Tick;

    /// <summary>
    /// Raised once each time the local player finishes entering the world,
    /// including after a reconnect. Plugins are not reloaded across a
    /// reconnect, so this fires again on the same instance.
    /// </summary>
    event Action LoginComplete
    {
        add { }
        remove { }
    }

    /// <summary>
    /// Raised when the in-world session ends, before the session is torn
    /// down, so a handler can still read gameplay state. The host raises it
    /// outside its session operation, so a handler may also issue commands,
    /// such as stopping the session, and they run rather than being deferred.
    /// The exception is a session ended from inside another event handler:
    /// Logoff is then raised within that call, and a session command issued
    /// from it is deferred or refused.
    /// </summary>
    event Action Logoff
    {
        add { }
        remove { }
    }

    /// <summary>
    /// Raised with the server's death message when the local player dies.
    /// </summary>
    event Action<string> LocalPlayerDied
    {
        add { }
        remove { }
    }

    /// <summary>
    /// Raised for every change to a world object the client is tracking:
    /// created, updated, appraisal data received, moved between cells, or
    /// released from the object table. Raised on the same thread as
    /// <see cref="Tick"/>, in the host's own delivery order. Handler
    /// exceptions are swallowed per handler, like every other event here.
    /// </summary>
    event Action<PluginObjectChange> ObjectChanged
    {
        add { }
        remove { }
    }

    /// <summary>
    /// Raised when the host observes a portal, recall, or other world
    /// transition. Notifications carry a generation and revision so a
    /// plugin can reject stale transition state.
    /// </summary>
    event Action<PluginPortalTransition> PortalTransition
    {
        add { }
        remove { }
    }

    /// <summary>Raised when a plugin-issued item use receives its server completion.</summary>
    event Action<PluginItemUseCompletion> ItemUseCompleted
    {
        add { }
        remove { }
    }

    /// <summary>
    /// Raised when a world-object activation (portal, door, NPC, vendor,
    /// container, or other interactable landscape object) completes, fails,
    /// or is interrupted. Correlates with a prior call to
    /// <see cref="IWorldObjectAutomation.Activate"/>.
    /// </summary>
    event Action<PluginActivationCompletion> ActivationCompleted
    {
        add { }
        remove { }
    }

    /// <summary>
    /// Raised when the host's current navigation walk report changes. Reports
    /// are delivered on the same thread as <see cref="Tick"/> and are emitted
    /// only when the report's sequence or state changes. A host that does not
    /// expose navigation leaves this event inert.
    /// </summary>
    event Action<PluginGoToReport> NavigationChanged
    {
        add { }
        remove { }
    }

    /// <summary>
    /// Raised when an external container (a corpse, a chest, a housing
    /// storage crate) becomes the client's open container. A vendor's shop
    /// pane is a separate surface and does not raise this.
    /// </summary>
    event Action<uint> ContainerOpened
    {
        add { }
        remove { }
    }

    /// <summary>
    /// Raised when the client's open external container closes. The payload
    /// is the container that was open, matching the id most recently raised
    /// by <see cref="ContainerOpened"/>.
    /// </summary>
    event Action<uint> ContainerClosed
    {
        add { }
        remove { }
    }

    /// <summary>
    /// Raised when the server asks the client to show a yes/no confirmation
    /// dialog. Answer it with <see cref="IAutomationSurface.Dialogs"/>.
    /// </summary>
    event Action<PluginConfirmation> ConfirmationRequested
    {
        add { }
        remove { }
    }
}
