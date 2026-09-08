using System;
using System.Collections.Generic;

namespace AcDream.Core.Net.Messages;

public sealed class GameEventDispatcher
{
    public delegate void EventHandler(GameEventEnvelope envelope);

    private sealed class RegistrationNode(
        GameEventType type,
        EventHandler handler,
        RegistrationNode? previous)
    {
        public GameEventType Type { get; } = type;
        public EventHandler Handler { get; } = handler;
        public RegistrationNode? Previous { get; } = previous;
        public bool Retired { get; set; }
    }

    private sealed class OwnedRegistration(
        GameEventDispatcher owner,
        RegistrationNode node) : IDisposable
    {
        private Action? _retire = () => owner.Retire(node);

        public void Dispose() => Interlocked.Exchange(ref _retire, null)?.Invoke();
    }

    private readonly Dictionary<GameEventType, RegistrationNode> _handlers = new();
    private readonly Dictionary<GameEventType, int> _unhandledCounts = new();

    /// <summary>
    /// Register a handler for a GameEvent sub-opcode. Replaces any
    /// existing handler for that opcode.
    /// </summary>
    public void Register(GameEventType type, EventHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _handlers[type] = new RegistrationNode(type, handler, previous: null);
    }

    public IDisposable RegisterOwned(GameEventType type, EventHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _handlers.TryGetValue(type, out RegistrationNode? previous);
        var node = new RegistrationNode(type, handler, previous);
        _handlers[type] = node;
        return new OwnedRegistration(this, node);
    }

    /// <summary>
    /// Remove the registered handler for a sub-opcode.
    /// </summary>
    public void Unregister(GameEventType type)
    {
        if (_handlers.Remove(type, out RegistrationNode? current))
            current.Retired = true;
    }

    public void Dispatch(GameEventEnvelope envelope)
    {
        if (_handlers.TryGetValue(envelope.EventType, out RegistrationNode? registration))
        {
            try
            {
                registration.Handler(envelope);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"[GameEvent] handler for 0x{(uint)envelope.EventType:X4} threw: {ex.Message}");
            }
        }
        else
        {
            _unhandledCounts.TryGetValue(envelope.EventType, out int n);
            _unhandledCounts[envelope.EventType] = n + 1;
        }
    }

    /// <summary>Number of events of the given type we've seen with no handler.</summary>
    public int GetUnhandledCount(GameEventType type) =>
        _unhandledCounts.TryGetValue(type, out var n) ? n : 0;

    public IReadOnlyDictionary<GameEventType, int> UnhandledCounts => _unhandledCounts;

    public void ResetUnhandledCounts() => _unhandledCounts.Clear();

    /// <summary>How many distinct sub-opcodes have a handler registered.</summary>
    public int RegisteredHandlerCount => _handlers.Count;

    private void Retire(RegistrationNode node)
    {
        node.Retired = true;
        if (!_handlers.TryGetValue(node.Type, out RegistrationNode? current)
            || !ReferenceEquals(current, node))
            return;

        RegistrationNode? predecessor = node.Previous;
        while (predecessor?.Retired == true)
            predecessor = predecessor.Previous;

        if (predecessor is null)
            _handlers.Remove(node.Type);
        else
            _handlers[node.Type] = predecessor;
    }
}
