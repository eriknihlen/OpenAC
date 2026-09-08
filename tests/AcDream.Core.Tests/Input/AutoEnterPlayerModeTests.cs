using AcDream.App.Input;

namespace AcDream.Core.Tests.Input;

public sealed class AutoEnterPlayerModeTests
{
    private sealed class State
    {
        public bool LiveInWorld;
        public bool PlayerEntityPresent;
        public bool PlayerControllerReady;
        public bool WorldReady = true;
        public bool PlayerModeActive;
        public int  EnteredCount;

        public PlayerModeAutoEntry Build() =>
            new(
                isLiveInWorld:           () => LiveInWorld,
                isPlayerEntityPresent:   () => PlayerEntityPresent,
                isPlayerControllerReady: () => PlayerControllerReady,
                isWorldReady:            () => WorldReady,
                enterPlayerMode:         () => EnteredCount++,
                isPlayerModeActive:      () => PlayerModeActive);
    }

    [Fact]
    public void TryEnter_Armed_WorldNotReady_DoesNotFire()
    {
        var s = new State
        {
            LiveInWorld = true, PlayerEntityPresent = true,
            PlayerControllerReady = true, WorldReady = false,
        };
        var guard = s.Build();
        guard.Arm();

        Assert.False(guard.TryEnter());
        Assert.Equal(0, s.EnteredCount);
        Assert.True(guard.IsArmed);

        s.WorldReady = true;
        Assert.True(guard.TryEnter());
        Assert.Equal(1, s.EnteredCount);
    }

    [Fact]
    public void TryEnter_NotArmed_DoesNotFire()
    {
        var s = new State { LiveInWorld = true, PlayerEntityPresent = true, PlayerControllerReady = true };
        var guard = s.Build();

        // Not armed → must NOT fire even though every precondition is true.
        Assert.False(guard.TryEnter());
        Assert.Equal(0, s.EnteredCount);
        Assert.False(guard.IsArmed);
    }

    [Fact]
    public void TryEnter_Armed_LiveNotInWorld_DoesNotFire()
    {
        var s = new State { LiveInWorld = false, PlayerEntityPresent = true, PlayerControllerReady = true };
        var guard = s.Build();
        guard.Arm();

        Assert.False(guard.TryEnter());
        Assert.Equal(0, s.EnteredCount);
        Assert.True(guard.IsArmed);
    }

    [Fact]
    public void TryEnter_Armed_PlayerEntityNotPresent_DoesNotFire()
    {
        var s = new State { LiveInWorld = true, PlayerEntityPresent = false, PlayerControllerReady = true };
        var guard = s.Build();
        guard.Arm();

        Assert.False(guard.TryEnter());
        Assert.Equal(0, s.EnteredCount);
        Assert.True(guard.IsArmed);
    }

    [Fact]
    public void TryEnter_Armed_PlayerControllerNotReady_DoesNotFire()
    {
        var s = new State { LiveInWorld = true, PlayerEntityPresent = true, PlayerControllerReady = false };
        var guard = s.Build();
        guard.Arm();

        Assert.False(guard.TryEnter());
        Assert.Equal(0, s.EnteredCount);
        Assert.True(guard.IsArmed);
    }

    [Fact]
    public void TryEnter_AllConditionsSatisfied_FiresExactlyOnce()
    {
        var s = new State { LiveInWorld = true, PlayerEntityPresent = true, PlayerControllerReady = true };
        var guard = s.Build();
        guard.Arm();

        Assert.True(guard.TryEnter());
        Assert.Equal(1, s.EnteredCount);
        Assert.False(guard.IsArmed);

        // Subsequent tick must not re-fire — one-shot semantics.
        Assert.False(guard.TryEnter());
        Assert.Equal(1, s.EnteredCount);
    }

    [Fact]
    public void TryEnter_FiresOnLaterTickWhenPreconditionsBecomeTrue()
    {
        var s = new State();
        var guard = s.Build();
        guard.Arm();

        // Tick 1: only LiveInWorld true.
        s.LiveInWorld = true;
        Assert.False(guard.TryEnter());

        // Tick 2: + PlayerEntityPresent.
        s.PlayerEntityPresent = true;
        Assert.False(guard.TryEnter());

        // Tick 3: + PlayerControllerReady → fires.
        s.PlayerControllerReady = true;
        Assert.True(guard.TryEnter());
        Assert.Equal(1, s.EnteredCount);
    }

    [Fact]
    public void Cancel_BeforeFiring_SuppressesAutoEntry()
    {
        var s = new State();
        var guard = s.Build();
        guard.Arm();

        // User opts out before any precondition is true.
        guard.Cancel();
        Assert.False(guard.IsArmed);

        s.LiveInWorld = true;
        s.PlayerEntityPresent = true;
        s.PlayerControllerReady = true;
        Assert.False(guard.TryEnter());
        Assert.Equal(0, s.EnteredCount);
    }

    [Fact]
    public void Arm_WhileAlreadyArmed_IsIdempotent()
    {
        var s = new State { LiveInWorld = true, PlayerEntityPresent = true, PlayerControllerReady = true };
        var guard = s.Build();
        guard.Arm();
        guard.Arm();   // second Arm() — no-op.

        Assert.True(guard.TryEnter());
        Assert.Equal(1, s.EnteredCount);
    }

    [Fact]
    public void PortalEntryThatAlreadyActivatedPlayerMode_RetiresArmedGuard()
    {
        var s = new State
        {
            LiveInWorld = true,
            PlayerEntityPresent = true,
            PlayerControllerReady = true,
        };
        var guard = s.Build();
        guard.Arm();

        s.PlayerModeActive = true;

        Assert.False(guard.TryEnter());
        Assert.False(guard.IsArmed);
        Assert.Equal(0, s.EnteredCount);
    }
}
