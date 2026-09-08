using System;
using System.Collections.Generic;

namespace AcDream.Runtime.Chat;

public sealed class LiveCommandBus : ICommandBus
{
    private readonly Dictionary<Type, Delegate> _handlers = new();

    public void Register<T>(Action<T> handler) where T : notnull
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (_handlers.ContainsKey(typeof(T)))
            throw new InvalidOperationException(
                $"A handler for command type {typeof(T).FullName} is already registered.");
        _handlers[typeof(T)] = handler;
    }

    /// <inheritdoc />
    public void Publish<T>(T command) where T : notnull
    {
        ArgumentNullException.ThrowIfNull(command);
        if (_handlers.TryGetValue(typeof(T), out var handler))
        {
            ((Action<T>)handler).Invoke(command);
        }
        else
        {
            Console.WriteLine(
                $"[LiveCommandBus] no handler registered for {typeof(T).FullName}; dropping.");
        }
    }

    public void Clear() => _handlers.Clear();
}
