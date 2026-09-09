// src/AcDream.Plugin.Abstractions/IGameState.cs
namespace AcDream.Plugin.Abstractions;

public interface IGameState
{
    IReadOnlyList<WorldEntitySnapshot> Entities { get; }

    IReadOnlyList<ContractSnapshot> Contracts => [];
}
