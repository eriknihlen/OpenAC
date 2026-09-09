using AcDream.Core.Lighting;
using AcDream.Core.Rendering;
using AcDream.Core.World;

namespace AcDream.App.Streaming;

public sealed class LandblockPresentationRetirementOwner
{
    private readonly LandblockRenderPublisher _render;
    private readonly LandblockPhysicsPublisher _physics;
    private readonly LandblockStaticPresentationPublisher _staticPresentation;
    private readonly LightingHookSink _lighting;
    private readonly TranslucencyFadeManager _translucency;

    public LandblockPresentationRetirementOwner(
        LandblockRenderPublisher render,
        LandblockPhysicsPublisher physics,
        LandblockStaticPresentationPublisher staticPresentation,
        LightingHookSink lighting,
        TranslucencyFadeManager translucency)
    {
        _render = render ?? throw new ArgumentNullException(nameof(render));
        _physics = physics ?? throw new ArgumentNullException(nameof(physics));
        _staticPresentation = staticPresentation
            ?? throw new ArgumentNullException(nameof(staticPresentation));
        _lighting = lighting ?? throw new ArgumentNullException(nameof(lighting));
        _translucency = translucency
            ?? throw new ArgumentNullException(nameof(translucency));
        if (!_staticPresentation.MatchesResources(_lighting, _translucency))
        {
            throw new ArgumentException(
                "The retirement resources must be those owned by the static " +
                "presentation publisher.",
                nameof(staticPresentation));
        }
    }

    internal bool Matches(
        LandblockRenderPublisher render,
        LandblockPhysicsPublisher physics,
        LandblockStaticPresentationPublisher staticPresentation) =>
        ReferenceEquals(_render, render)
        && ReferenceEquals(_physics, physics)
        && ReferenceEquals(_staticPresentation, staticPresentation)
        && _staticPresentation.MatchesResources(_lighting, _translucency);

    public void Advance(LandblockRetirementTicket ticket)
    {
        ArgumentNullException.ThrowIfNull(ticket);

        ticket.RunForEachEntity(
            LandblockRetirementStage.EntityLighting,
            static entity => entity.ServerGuid == 0,
            _staticPresentation.RemoveLighting);
        ticket.RunForEachEntity(
            LandblockRetirementStage.EntityTranslucency,
            static entity => entity.ServerGuid == 0,
            _staticPresentation.RemoveTranslucency);
        ticket.RunForEachEntity(
            LandblockRetirementStage.PluginProjection,
            static entity => entity.ServerGuid == 0,
            _staticPresentation.RemovePluginProjection);

        if (!ticket.RunOnce(
            LandblockRetirementStage.Physics,
            () => ticket.Kind == LandblockRetirementKind.Full
                ? _physics.AdvanceRemoval(ticket.LandblockId)
                : _physics.AdvanceDemotion(ticket.LandblockId)))
        {
            return;
        }
        if (ticket.Kind == LandblockRetirementKind.Full)
        {
            ticket.RunOnce(
                LandblockRetirementStage.Terrain,
                () => _render.RemoveTerrain(ticket.LandblockId));
        }
        ticket.RunOnce(
            LandblockRetirementStage.CellVisibility,
            () => _render.RemoveCellVisibility(ticket.LandblockId));
        ticket.RunOnce(
            LandblockRetirementStage.BuildingRegistry,
            () => _render.RemoveBuildingRegistry(ticket.LandblockId));
        ticket.RunOnce(
            LandblockRetirementStage.EnvironmentCells,
            () => _render.RemoveEnvironmentCells(ticket.LandblockId));
    }

    internal LandblockRetirementOperationResult AdvanceOne(
        LandblockRetirementTicket ticket)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        return ticket.NextIncompleteStage switch
        {
            LandblockRetirementStage.EntityLighting =>
                ticket.RunEntityStep(
                    LandblockRetirementStage.EntityLighting,
                    static entity => entity.ServerGuid == 0,
                    _staticPresentation.RemoveLighting),
            LandblockRetirementStage.EntityTranslucency =>
                ticket.RunEntityStep(
                    LandblockRetirementStage.EntityTranslucency,
                    static entity => entity.ServerGuid == 0,
                    _staticPresentation.RemoveTranslucency),
            LandblockRetirementStage.PluginProjection =>
                ticket.RunEntityStep(
                    LandblockRetirementStage.PluginProjection,
                    static entity => entity.ServerGuid == 0,
                    _staticPresentation.RemovePluginProjection),
            LandblockRetirementStage.Terrain =>
                ticket.RunOnceStep(
                    LandblockRetirementStage.Terrain,
                    () => _render.RemoveTerrain(ticket.LandblockId)),
            LandblockRetirementStage.Physics =>
                ticket.RunOnceStep(
                    LandblockRetirementStage.Physics,
                    () => ticket.Kind == LandblockRetirementKind.Full
                        ? _physics.AdvanceRemoval(ticket.LandblockId)
                        : _physics.AdvanceDemotion(ticket.LandblockId)),
            LandblockRetirementStage.CellVisibility =>
                ticket.RunOnceStep(
                    LandblockRetirementStage.CellVisibility,
                    () => _render.RemoveCellVisibility(ticket.LandblockId)),
            LandblockRetirementStage.BuildingRegistry =>
                ticket.RunOnceStep(
                    LandblockRetirementStage.BuildingRegistry,
                    () => _render.RemoveBuildingRegistry(ticket.LandblockId)),
            LandblockRetirementStage.EnvironmentCells =>
                ticket.RunOnceStep(
                    LandblockRetirementStage.EnvironmentCells,
                    () => _render.RemoveEnvironmentCells(ticket.LandblockId)),
            _ => LandblockRetirementOperationResult.NoWork,
        };
    }
}
