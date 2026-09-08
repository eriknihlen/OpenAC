using AcDream.Core.Selection;

namespace AcDream.App.Rendering.Selection;

internal interface IRetailSelectionGeometrySource
{
    RetailSelectionMesh? Resolve(uint gfxObjId);
}
