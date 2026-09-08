using AcDream.Core.Physics;
using AcDream.Runtime.Gameplay;

namespace AcDream.Runtime.Tests.Gameplay;

public sealed class RuntimeLocalPlayerFrameControllerTests
{
    [Fact]
    public void AdvanceBeforeNetworkDoesNotThrowOnDormantControllerInSuspendDisposition()
    {
        PlayerMovementController dormant = CreateDormantController();
        var host = new SabotagingHost(
            dormant,
            RetailObjectClockDisposition.Suspend);
        var controller = new RuntimeLocalPlayerFrameController(
            host,
            new FixedMovementInputSource());

        Exception? exception = Record.Exception(
            () => controller.AdvanceBeforeNetwork(0.015f));

        Assert.Null(exception);
        Assert.Equal(0, host.ProjectCallCount);
    }

    [Fact]
    public void AdvanceBeforeNetworkDoesNotThrowOnDormantControllerInAdvanceDisposition()
    {
        PlayerMovementController dormant = CreateDormantController();
        var host = new SabotagingHost(
            dormant,
            RetailObjectClockDisposition.Advance);
        var controller = new RuntimeLocalPlayerFrameController(
            host,
            new FixedMovementInputSource());

        Exception? exception = Record.Exception(
            () => controller.AdvanceBeforeNetwork(0.015f));

        Assert.Null(exception);
        Assert.Equal(0, host.ProjectCallCount);
    }

    [Fact]
    public void RunPostNetworkCommandPhaseDoesNotThrowOnDormantController()
    {
        PlayerMovementController dormant = CreateDormantController();
        var host = new SabotagingHost(
            dormant,
            RetailObjectClockDisposition.Advance);
        var controller = new RuntimeLocalPlayerFrameController(
            host,
            new FixedMovementInputSource());

        Exception? exception = Record.Exception(
            controller.RunPostNetworkCommandPhase);

        Assert.Null(exception);
        Assert.Equal(0, host.SendPostNetworkCallCount);
    }

    [Fact]
    public void TryGetPresentationAfterNetworkReturnsFalseOnDormantControllerWithoutThrowing()
    {
        PlayerMovementController dormant = CreateDormantController();
        var host = new SabotagingHost(
            dormant,
            RetailObjectClockDisposition.Advance);
        var controller = new RuntimeLocalPlayerFrameController(
            host,
            new FixedMovementInputSource());

        bool result = false;
        Exception? exception = Record.Exception(() =>
            result = controller.TryGetPresentationAfterNetwork(
                out RuntimeLocalPlayerPresentationFrame _));

        Assert.Null(exception);
        Assert.False(result);
    }

    private static PlayerMovementController CreateDormantController()
    {
        PlayerMovementController candidate =
            PlayerMovementController.CreatePublicationCandidate(
                new PhysicsEngine(),
                PlayerMovementConstructionOptions.Fallback);
        candidate.SealPublicationCandidate();
        candidate.CommitRuntimeOwnership(new RetailObjectQuantumClock());
        Assert.True(candidate.IsRuntimeOwnedDormant);
        Assert.False(candidate.CanExecuteLiveMovement);
        return candidate;
    }

    private sealed class FixedMovementInputSource : IRuntimeMovementInputSource
    {
        public MovementInput Capture() => default;
    }

    private sealed class SabotagingHost(
        PlayerMovementController? controller,
        RetailObjectClockDisposition disposition)
        : IRuntimeLocalPlayerFrameHost
    {
        internal int ProjectCallCount { get; private set; }
        internal int SendPostNetworkCallCount { get; private set; }

        public bool CanAdvancePlayer => true;
        public PlayerMovementController? Controller => controller;

        public uint ResolveLocalEntityId() => 1u;
        public void HandleTargeting()
        {
        }

        public bool IsHidden => false;
        public RetailObjectClockDisposition ObjectClockDisposition =>
            disposition;

        public void Project(
            PlayerMovementController controller,
            MovementResult movement,
            bool hidden) =>
            ProjectCallCount++;

        public void SendPreNetwork(
            PlayerMovementController controller,
            MovementResult movement,
            bool hidden)
        {
        }

        public void SendPostNetwork(
            PlayerMovementController controller,
            bool hidden) =>
            SendPostNetworkCallCount++;
    }
}
