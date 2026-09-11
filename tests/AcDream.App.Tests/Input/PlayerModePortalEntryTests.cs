using System.Reflection;
using System.Runtime.CompilerServices;
using AcDream.App.Input;
using AcDream.Core.Physics;
using AcDream.Runtime.Gameplay;

namespace AcDream.App.Tests.Input;

public sealed class PlayerModePortalEntryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnreadyExistingController_PreservesAutoEntryUntilPortalModeAcquired(bool login)
    {
        var movement = PlayerMovementController.CreatePublicationCandidate(
            new PhysicsEngine(), PlayerMovementConstructionOptions.Fallback);
        movement.SealPublicationCandidate();
        movement.CommitRuntimeOwnership(new RetailObjectQuantumClock());
        var slot = new RuntimeLocalPlayerMovementState { Controller = movement };
        var autoEntry = new PlayerModeAutoEntry(
            () => true, () => true, () => true, () => true, () => { });
        autoEntry.Arm();

        // This transition uses only the already-attached mode, controller, and
        // auto-entry owners; constructing a renderer is unnecessary here.
        var mode = (PlayerModeController)RuntimeHelpers.GetUninitializedObject(
            typeof(PlayerModeController));
        SetField(mode, "_mode", new LocalPlayerModeState { IsPlayerMode = true });
        SetField(mode, "_controllerSlot", slot);
        mode.BindAutoEntry(autoEntry);

        Assert.False(movement.CanExecuteLiveMovement);
        Assert.False(login ? mode.TryEnterPortalSpaceForLogin() : mode.TryEnterPortalSpace());
        Assert.True(autoEntry.IsArmed);

        movement.ActivateRuntimePublication();
        Assert.True(login ? mode.TryEnterPortalSpaceForLogin() : mode.TryEnterPortalSpace());
        Assert.Equal(PlayerState.PortalSpace, movement.State);
        Assert.False(autoEntry.IsArmed);
    }

    private static void SetField(object target, string name, object value) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(target, value);
}
