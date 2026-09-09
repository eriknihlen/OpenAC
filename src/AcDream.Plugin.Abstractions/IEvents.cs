// src/AcDream.Plugin.Abstractions/IEvents.cs
namespace AcDream.Plugin.Abstractions;

public interface IEvents
{
    event Action<WorldEntitySnapshot> EntitySpawned;

    event Action<double> Tick;
}
