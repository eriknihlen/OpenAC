// src/AcDream.Plugin.Abstractions/IGameState.cs
namespace AcDream.Plugin.Abstractions;

/// <summary>
/// A read-only view of what the client currently has in the world. Every
/// list is a snapshot the host rebuilds; it is never mutated underneath a
/// reader.
/// </summary>
public interface IGameState
{
    /// <summary>
    /// Everything the client is currently drawing, with its position and
    /// facing. Empty before the local player is in the world.
    /// </summary>
    IReadOnlyList<WorldEntitySnapshot> Entities { get; }

    /// <summary>
    /// The character's tracked quest contracts. Empty on a host that does
    /// not track them and before the server has sent any.
    /// </summary>
    IReadOnlyList<ContractSnapshot> Contracts => [];
}
