using AcDream.App.Input;
using AcDream.Core.Items;
using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;

namespace AcDream.App.Interaction;

internal interface IPlayerInteractionMovementSink
{
    bool BeginApproach(
        InteractionApproach approach,
        Action<PlayerApproachToken>? armAfterCancel = null);
}

internal sealed class PlayerInteractionMovementSink(
    Func<PlayerMovementController?> player,
    IPlayerApproachTokenSource approachTokens)
    : IPlayerInteractionMovementSink
{
    private readonly Func<PlayerMovementController?> _player = player
        ?? throw new ArgumentNullException(nameof(player));
    private readonly IPlayerApproachTokenSource _approachTokens = approachTokens
        ?? throw new ArgumentNullException(nameof(approachTokens));

    public bool BeginApproach(
        InteractionApproach approach,
        Action<PlayerApproachToken>? armAfterCancel = null)
    {
        PlayerMovementController? controller = _player();
        if (controller?.MoveTo is null)
            return false;

        var parameters = new MovementParameters
        {
            DistanceToObject = approach.UseRadius,
            CanCharge = approach.CanCharge,
        };
        var movement = new MovementStruct
        {
            ObjectId = approach.Target.ServerGuid,
            TopLevelId = approach.Target.ServerGuid,
            Pos = new Position(
                approach.Player.CellId,
                approach.Target.Entity.Position,
                System.Numerics.Quaternion.Identity),
            Params = parameters,
            Type = approach.IsCloseRange
                ? MovementType.TurnToObject
                : MovementType.MoveToObject,
            Radius = approach.TargetRadius,
            Height = approach.TargetHeight,
        };

        controller.Movement.CancelMoveTo(WeenieError.ActionCancelled);
        if (!_approachTokens.TryBeginApproach(out PlayerApproachToken token))
            return false;
        armAfterCancel?.Invoke(token);

        controller.SetLastMoveWasAutonomous(false);
        return controller.Movement.PerformMovement(movement) == WeenieError.None;
    }
}
