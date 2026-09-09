using System.Numerics;
using AcDream.Core.Selection;
namespace AcDream.App.Rendering.Selection;

internal interface IRetailSelectionRenderSink
{
    void AddVisiblePart(
        uint serverGuid,
        uint localEntityId,
        int partIndex,
        uint gfxObjId,
        Matrix4x4 partWorld);
}

/// <summary>
/// Read-only mirror of the exact geometry and drawing-sphere acceptance used
/// by the retained selection scene. G2's packed dispatcher referee uses it
/// without publishing a second picking frame.
/// </summary>
internal interface IRetailSelectionRenderOracle
{
    bool TryCreateVisiblePart(
        uint serverGuid,
        uint localEntityId,
        int partIndex,
        uint gfxObjId,
        Matrix4x4 partWorld,
        out RetailSelectionPart part);
}
