namespace AcDream.App.Rendering.Selection;

internal interface IRetailSelectionLightingSource
{
    bool TryGetLighting(
        uint serverGuid,
        uint localEntityId,
        out RetailSelectionLighting lighting);

    /// <summary>True while any part of this entity is flashing. The draw path
    /// asks this once per entity and only then asks per part, so an ordinary
    /// frame with nothing selected costs one bool test per entity.</summary>
    bool HasPartLighting(uint serverGuid, uint localEntityId);

    bool TryGetPartLighting(
        uint serverGuid,
        uint localEntityId,
        int partIndex,
        out RetailSelectionLighting lighting);
}
