namespace AcDream.App.Rendering.Selection;

internal interface IRetailSelectionLightingSource
{
    void TickLighting();

    bool TryGetLighting(
        uint serverGuid,
        uint localEntityId,
        out RetailSelectionLighting lighting);
}
