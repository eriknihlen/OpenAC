using AcDream.Core.Terrain;

namespace AcDream.Core.Meshing;

/// <summary>
/// One sub-mesh of a GfxObj: a vertex+index buffer that uses a single Surface.
/// A GfxObj with multiple surfaces produces multiple sub-meshes.
/// </summary>
public sealed record GfxObjSubMesh(
    uint SurfaceId,
    Vertex[] Vertices,
    uint[] Indices)
{
    public TranslucencyKind Translucency { get; init; } = TranslucencyKind.Opaque;

    public float Luminosity { get; init; } = 0f;

    public float Diffuse { get; init; } = 1f;

    public bool NeedsUvRepeat { get; init; } = false;

    public float SurfOpacity { get; init; } = 1f;

    public bool DisableFog { get; init; } = false;
}
