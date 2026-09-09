using AcDream.App.Update;
using AcDream.Runtime;

namespace AcDream.App.Input;

internal sealed class RetailLocalPlayerFrameController : IPostNetworkCommandFramePhase
{
    private readonly RuntimeLocalPlayerFrameController _runtime;

    public readonly record struct PresentationFrame(
        MovementResult Movement,
        bool Hidden,
        bool AdvancedBeforeNetwork);

    internal bool HiddenPartPoseDirty =>
        _runtime.HiddenPartPoseDirty;

    public RetailLocalPlayerFrameController(
        ILocalPlayerFrameRuntime runtime,
        IMovementInputSource movementInput)
    {
        _runtime = new RuntimeLocalPlayerFrameController(
            runtime ?? throw new ArgumentNullException(nameof(runtime)),
            movementInput
                ?? throw new ArgumentNullException(nameof(movementInput)));
    }

    public RetailLocalPlayerFrameController(
        GameRuntime gameRuntime,
        ILocalPlayerFrameRuntime runtime,
        IMovementInputSource movementInput)
    {
        ArgumentNullException.ThrowIfNull(gameRuntime);
        _runtime = gameRuntime.CreateLocalPlayerFrameController(
            runtime ?? throw new ArgumentNullException(nameof(runtime)),
            movementInput
                ?? throw new ArgumentNullException(nameof(movementInput)));
    }

    /// <summary>
    /// Advances the existing local object exactly once on the object side of
    /// the inbound-network barrier.
    /// </summary>
    public void AdvanceBeforeNetwork(float deltaSeconds)
        => _runtime.AdvanceBeforeNetwork(deltaSeconds);

    public void RunPostNetworkCommandPhase()
        => _runtime.RunPostNetworkCommandPhase();

    public bool TryGetPresentationAfterNetwork(out PresentationFrame frame)
    {
        bool available = _runtime.TryGetPresentationAfterNetwork(
            out AcDream.Runtime.Gameplay.RuntimeLocalPlayerPresentationFrame
                shared);
        frame = available
            ? new PresentationFrame(
                shared.Movement,
                shared.Hidden,
                shared.AdvancedBeforeNetwork)
            : default;
        return available;
    }
}
