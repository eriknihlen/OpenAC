// src/AcDream.Plugin.Abstractions/WorldEntitySnapshot.cs
using System.Numerics;

namespace AcDream.Plugin.Abstractions;

/// <summary>
/// Where one thing the client is drawing sits in the world, taken at the
/// moment the client last published it. It is a copy, not a live handle: it
/// does not change as the thing moves.
/// </summary>
/// <param name="Id">
/// The client's own id for this drawable thing, stable for as long as the
/// client keeps it. This is not the server's object id.
/// </param>
/// <param name="SourceId">
/// The id of the model the thing is built from, or 0 when the client has
/// not resolved one.
/// </param>
/// <param name="Position">Where it stands, in world coordinates (metres).</param>
/// <param name="Rotation">Which way it faces, as an orientation.</param>
public readonly record struct WorldEntitySnapshot(
    uint Id,
    uint SourceId,
    Vector3 Position,
    Quaternion Rotation);
