// src/AcDream.Plugin.Abstractions/WorldEntitySnapshot.cs
using System.Numerics;

namespace AcDream.Plugin.Abstractions;

public readonly record struct WorldEntitySnapshot(
    uint Id,
    uint SourceId,
    Vector3 Position,
    Quaternion Rotation);
