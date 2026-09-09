using AcDream.App.Rendering.Gpu;
using AcDream.Core.Meshing;
using DatReaderWriter.Enums;

namespace AcDream.App.Rendering.Wb;

internal readonly record struct GroupKey(
    uint FirstIndex,
    int BaseVertex,
    int IndexCount,
    GpuTextureSlot TextureSlot,
    uint TextureLayer,
    TranslucencyKind Translucency,
    RetailSetSurfaceMaterialState MaterialState,
    uint FoliageFlags,
    float SurfaceOpacity = 1f,
    CullMode CullMode = CullMode.CounterClockwise);
