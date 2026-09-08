using System.Numerics;
using AcDream.App.Interaction;
using AcDream.Core.Combat;
using AcDream.Core.Selection;

namespace AcDream.App.Combat;

internal interface ICombatCameraTargetSource
{
    Vector3? GetTrackedTargetPoint();
}

internal sealed class CombatCameraTargetSource : ICombatCameraTargetSource
{
    private readonly ICombatGameplaySettingsSource _settings;
    private readonly CombatState _combat;
    private readonly SelectionState _selection;
    private readonly IWorldSelectionQuery _world;

    public CombatCameraTargetSource(
        ICombatGameplaySettingsSource settings,
        CombatState combat,
        SelectionState selection,
        IWorldSelectionQuery world)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _combat = combat ?? throw new ArgumentNullException(nameof(combat));
        _selection = selection ?? throw new ArgumentNullException(nameof(selection));
        _world = world ?? throw new ArgumentNullException(nameof(world));
    }

    public Vector3? GetTrackedTargetPoint()
    {
        if (!_settings.ViewCombatTarget
            || !CombatInputPlanner.SupportsTargetedAttack(_combat.CurrentMode)
            || _selection.SelectedObjectId is not uint selected)
        {
            return null;
        }

        return _world.GetCombatCameraTargetPoint(selected);
    }
}
