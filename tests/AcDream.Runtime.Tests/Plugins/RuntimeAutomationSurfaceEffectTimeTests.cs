using System.Numerics;
using AcDream.Core.Physics;
using AcDream.Core.Spells;
using AcDream.Plugin.Abstractions;
using AcDream.Runtime.Plugins;
using AcDream.Runtime.Tests.Support;
using AcDream.Runtime.World;

namespace AcDream.Runtime.Tests.Plugins;

/// <summary>
/// The time left on an effect, as a plugin reads it. Both hosts stamp an
/// effect with the runtime's effect clock when it arrives, and the surface
/// measures "now" on that same clock. The server keeps counting an effect
/// down for as long as the character is in the world, portal space and
/// loading included, so the effect clock runs in real time rather than with
/// the simulated world.
/// </summary>
public sealed class RuntimeAutomationSurfaceEffectTimeTests
{
    private const uint SpellId = 0x0400u;
    private const uint CooldownId = 0x0010u;
    private const uint DestinationCell = 0x11340021u;

    /// <summary>
    /// Mutation: measure "now" on any other clock (a stopwatch, the
    /// simulation clock) and the buff reads that clock's offset too long or
    /// too short.
    /// </summary>
    [Fact]
    public void ATimedBuffReadsItsDurationLessTheTimeSinceItArrived()
    {
        var time = new ManualTimeProvider();
        using var host = new NoWindowGameRuntimeHost(timeProvider: time);
        GameRuntime runtime = host.Runtime;
        time.Advance(TimeSpan.FromSeconds(100));
        using var surface = Bind(runtime);

        AddEffect(runtime, SpellId, bucket: 1u, duration: 60d);
        time.Advance(TimeSpan.FromSeconds(10));
        _ = runtime.AdvanceFrameClock(10d);

        PluginActiveEnchantment buff = Assert.Single(surface.TimedEnchantments);
        Assert.Equal(SpellId, buff.SpellId);
        Assert.Equal(50d, buff.SecondsRemaining, 6);
    }

    /// <summary>
    /// Mutation: measure "now" on the simulation clock, which stands still
    /// in portal space, and the buff still reads 60 seconds after ten
    /// seconds in portal space.
    /// </summary>
    [Fact]
    public void ATimedBuffKeepsCountingDownInPortalSpace()
    {
        var time = new ManualTimeProvider();
        using var host = new NoWindowGameRuntimeHost(timeProvider: time);
        GameRuntime runtime = host.Runtime;
        using var surface = Bind(runtime);

        AddEffect(runtime, SpellId, bucket: 1u, duration: 60d);
        BeginPortal(runtime.TransitOwner);
        time.Advance(TimeSpan.FromSeconds(10));
        _ = runtime.AdvanceFrameClock(10d);

        PluginActiveEnchantment buff = Assert.Single(surface.TimedEnchantments);
        Assert.Equal(50d, buff.SecondsRemaining, 6);
    }

    /// <summary>
    /// Mutation: as above, for the cooldown a plugin waits out before it
    /// uses an item again.
    /// </summary>
    [Fact]
    public void ACooldownKeepsCountingDownInPortalSpace()
    {
        var time = new ManualTimeProvider();
        using var host = new NoWindowGameRuntimeHost(timeProvider: time);
        GameRuntime runtime = host.Runtime;
        using var surface = Bind(runtime);

        AddEffect(
            runtime,
            CooldownId + Spellbook.CooldownSpellOffset,
            Spellbook.CooldownBucket,
            duration: 30d);
        BeginPortal(runtime.TransitOwner);
        time.Advance(TimeSpan.FromSeconds(10));
        _ = runtime.AdvanceFrameClock(10d);

        Assert.Equal(20d, surface.GetCooldownRemaining(CooldownId), 6);
    }

    private static RuntimeAutomationSurface Bind(GameRuntime runtime)
    {
        var surface = new RuntimeAutomationSurface();
        surface.Bind(runtime, runtime.CharacterOwner, runtime.ActionOwner.SpellCast);
        return surface;
    }

    private static void AddEffect(
        GameRuntime runtime, uint spellId, uint bucket, double duration) =>
        runtime.CharacterOwner.Spellbook.OnEnchantmentAdded(
            new ActiveEnchantmentRecord(
                spellId,
                LayerId: 1u,
                Duration: duration,
                CasterGuid: 0u,
                Bucket: bucket,
                StartTime: runtime.EffectClock.NowSeconds));

    private static void BeginPortal(RuntimeWorldTransitState transit)
    {
        Assert.True(transit.TryQueueTeleportStart(1));
        Assert.True(transit.ActivateQueuedTeleport());
        Assert.True(transit.OfferTeleportDestination(
            new RuntimeTeleportDestination(
                EntityGuid: 0x50000001u,
                InstanceSequence: 1,
                PositionSequence: 1,
                TeleportSequence: 1,
                ForcePositionSequence: 1,
                Position: new Position(
                    DestinationCell,
                    new Vector3(1f, 2f, 3f),
                    Quaternion.Identity)),
            teleportTimestampAdvanced: true));
        Assert.True(transit.TryBeginPortalReveal(1, DestinationCell, out _));
        Assert.False(transit.IsWorldSimulationAvailable);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _timestamp;

        public void Advance(TimeSpan duration) =>
            _timestamp = checked(_timestamp + duration.Ticks);
    }
}
