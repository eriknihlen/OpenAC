using System.Numerics;
using AcDream.Core.Physics;
using AcDream.Core.Vfx;
using DatReaderWriter.Types;
using DatPhysicsScript = DatReaderWriter.DBObjs.PhysicsScript;

namespace AcDream.Core.Tests.Vfx;

public sealed class PhysicsScriptRunnerTests
{
    private sealed class RecordingSink : IAnimationHookSink
    {
        public List<(uint EntityId, Vector3 Position, AnimationHook Hook)> Calls { get; } = new();

        public Action<uint, Vector3, AnimationHook>? Callback { get; init; }

        public void OnHook(uint entityId, Vector3 worldPosition, AnimationHook hook)
        {
            Calls.Add((entityId, worldPosition, hook));
            Callback?.Invoke(entityId, worldPosition, hook);
        }
    }

    private static DatPhysicsScript BuildScript(params (double Time, AnimationHook Hook)[] items)
    {
        var script = new DatPhysicsScript();
        foreach ((double time, AnimationHook hook) in items)
            script.ScriptData.Add(new PhysicsScriptData { StartTime = time, Hook = hook });
        return script;
    }

    private static CreateParticleHook CreateHook(uint emitterInfoId) =>
        new() { EmitterInfoId = emitterInfoId };

    private static PhysicsScriptRunner MakeRunner(
        IAnimationHookSink sink,
        Func<double>? clock = null,
        Func<double>? randomUnit = null,
        Func<uint, bool>? canAdvanceOwner = null,
        params (uint Id, DatPhysicsScript Script)[] scripts)
    {
        var table = scripts.ToDictionary(entry => entry.Id, entry => entry.Script);
        return new PhysicsScriptRunner(
            id => table.TryGetValue(id, out DatPhysicsScript? script) ? script : null,
            sink,
            clock,
            randomUnit,
            canAdvanceOwner);
    }

    [Fact]
    public void PlayDirect_MissingOrZeroInputReturnsFalseWithoutQueueCorruption()
    {
        var sink = new RecordingSink();
        var runner = MakeRunner(sink);

        Assert.False(runner.PlayDirect(1u, 0u));
        Assert.False(runner.PlayDirect(0u, 0x33000001u));
        Assert.False(runner.PlayDirect(1u, 0x33000001u));
        Assert.Equal(0, runner.ActiveScriptCount);
        Assert.Empty(sink.Calls);
    }

    [Fact]
    public void HooksFireInOrderAtAbsoluteScheduledTimes()
    {
        DatPhysicsScript script = BuildScript(
            (0.0, CreateHook(100)),
            (0.5, CreateHook(101)),
            (1.0, CreateHook(102)));
        var sink = new RecordingSink();
        var runner = MakeRunner(sink, scripts: [(0x330000AAu, script)]);
        runner.SetOwnerAnchor(7u, new Vector3(1, 2, 3));

        Assert.True(runner.PlayDirect(7u, 0x330000AAu));
        runner.Tick(0.25);
        runner.Tick(0.60);
        runner.Tick(1.50);

        Assert.Equal([100u, 101u, 102u],
            sink.Calls.Select(call => ((CreateParticleHook)call.Hook).EmitterInfoId.DataId));
        Assert.All(sink.Calls, call => Assert.Equal(new Vector3(1, 2, 3), call.Position));
        Assert.Equal(0, runner.ActiveScriptCount);
    }

    [Fact]
    public void DuplicatePlaysAppendSeriallyToOneOwner()
    {
        DatPhysicsScript script = BuildScript(
            (0.0, CreateHook(1)),
            (1.0, CreateHook(2)));
        var sink = new RecordingSink();
        var runner = MakeRunner(sink, scripts: [(0x330000AAu, script)]);

        Assert.True(runner.PlayDirect(1u, 0x330000AAu));
        Assert.True(runner.PlayDirect(1u, 0x330000AAu));
        Assert.Equal(2, runner.ActiveScriptCount);
        Assert.Equal(1, runner.ActiveOwnerCount);

        runner.Tick(0.0);
        Assert.Equal([1u], EmitterIds(sink));

        // At exactly one second the first tail and the second head are both due.
        runner.Tick(1.0);
        Assert.Equal([1u, 2u, 1u], EmitterIds(sink));

        runner.Tick(2.0);
        Assert.Equal([1u, 2u, 1u, 2u], EmitterIds(sink));
        Assert.Equal(0, runner.ActiveScriptCount);
    }

    [Fact]
    public void DifferentOwnersProgressIndependently()
    {
        DatPhysicsScript script = BuildScript(
            (0.0, CreateHook(1)),
            (1.0, CreateHook(2)));
        var sink = new RecordingSink();
        var runner = MakeRunner(sink, scripts: [(0x330000AAu, script)]);

        runner.PlayDirect(1u, 0x330000AAu);
        runner.Tick(0.5);
        runner.PlayDirect(2u, 0x330000AAu);
        runner.Tick(1.0);

        Assert.Equal([(1u, 1u), (1u, 2u), (2u, 1u)],
            sink.Calls.Select(call =>
                (call.EntityId, ((CreateParticleHook)call.Hook).EmitterInfoId.DataId)));
        Assert.Equal(1, runner.ActiveScriptCount);
        Assert.Equal(1, runner.ActiveOwnerCount);
    }

    [Fact]
    public void CatchUpTickDrainsEveryDueHookAndQueuedScript()
    {
        DatPhysicsScript script = BuildScript(
            (0.0, CreateHook(1)),
            (0.25, CreateHook(2)),
            (0.5, CreateHook(3)));
        var sink = new RecordingSink();
        var runner = MakeRunner(sink, scripts: [(0x330000AAu, script)]);
        runner.PlayDirect(1u, 0x330000AAu);
        runner.PlayDirect(1u, 0x330000AAu);

        runner.Tick(5.0);

        Assert.Equal([1u, 2u, 3u, 1u, 2u, 3u], EmitterIds(sink));
        Assert.Equal(0, runner.ActiveScriptCount);
    }

    [Fact]
    public void HookMayAppendToCurrentOwnerWithoutInvalidatingCatchUp()
    {
        DatPhysicsScript outer = BuildScript((0.0, CreateHook(1)));
        DatPhysicsScript nested = BuildScript((0.0, CreateHook(2)));
        PhysicsScriptRunner? runner = null;
        var sink = new RecordingSink
        {
            Callback = (owner, _, hook) =>
            {
                if (((CreateParticleHook)hook).EmitterInfoId.DataId == 1u)
                    runner!.PlayDirect(owner, 0x330000BBu);
            },
        };
        runner = MakeRunner(
            sink,
            scripts: [(0x330000AAu, outer), (0x330000BBu, nested)]);
        runner.PlayDirect(1u, 0x330000AAu);

        runner.Tick(0.0);

        Assert.Equal([1u, 2u], EmitterIds(sink));
        Assert.Equal(0, runner.ActiveScriptCount);
    }

    [Fact]
    public void HookMayDeleteItsOwnerWithoutDispatchingDetachedTail()
    {
        DatPhysicsScript script = BuildScript(
            (0.0, CreateHook(1)),
            (0.0, CreateHook(2)));
        PhysicsScriptRunner? runner = null;
        var sink = new RecordingSink
        {
            Callback = (owner, _, hook) =>
            {
                if (((CreateParticleHook)hook).EmitterInfoId.DataId == 1u)
                    runner!.StopAllForEntity(owner);
            },
        };
        runner = MakeRunner(sink, scripts: [(0x330000AAu, script)]);
        runner.PlayDirect(1u, 0x330000AAu);

        runner.Tick(0.0);

        Assert.Equal([1u], EmitterIds(sink));
        Assert.Equal(0, runner.ActiveScriptCount);
    }

    [Fact]
    public void ScheduleCallPesUsesInjectedUniformDelayAndNearZeroAppendsImmediately()
    {
        DatPhysicsScript script = BuildScript((0.0, CreateHook(9)));
        var sink = new RecordingSink();
        var runner = MakeRunner(
            sink,
            randomUnit: () => 0.5,
            scripts: [(0x330000AAu, script)]);

        Assert.True(runner.ScheduleCallPes(1u, 0x330000AAu, 2f));
        Assert.Equal(1, runner.ScheduledCallPesCount);
        runner.Tick(0.999);
        Assert.Empty(sink.Calls);
        runner.Tick(1.0);
        Assert.Equal([9u], EmitterIds(sink));

        Assert.True(runner.ScheduleCallPes(
            1u,
            0x330000AAu,
            PhysicsScriptRunner.ImmediateCallPesThresholdSeconds / 2f));
        Assert.Equal(1, runner.ActiveScriptCount);
        runner.Tick(1.0);
        Assert.Equal([9u, 9u], EmitterIds(sink));
    }

    [Fact]
    public void PublishedAnimationPhaseRunsImmediateScriptButDefersTimedCallOnePass()
    {
        DatPhysicsScript script = BuildScript((0.0, CreateHook(9)));
        var sink = new RecordingSink();
        var runner = MakeRunner(
            sink,
            randomUnit: () => 0.0,
            scripts: [(0x330000AAu, script)]);

        runner.PublishTime(1.0);
        Assert.True(runner.ScheduleCallPes(1u, 0x330000AAu, 0.5f));
        Assert.True(runner.ScheduleCallPes(
            2u,
            0x330000AAu,
            PhysicsScriptRunner.ImmediateCallPesThresholdSeconds / 2f));
        runner.Tick(1.0);
        Assert.Equal([(2u, 9u)],
            sink.Calls.Select(call =>
                (call.EntityId, ((CreateParticleHook)call.Hook).EmitterInfoId.DataId)));

        runner.PublishTime(1.1);
        runner.Tick(1.1);

        Assert.Equal([(2u, 9u), (1u, 9u)],
            sink.Calls.Select(call =>
                (call.EntityId, ((CreateParticleHook)call.Hook).EmitterInfoId.DataId)));
    }

    [Fact]
    public void UpdatePhasePlayUsesPublishedCurrentClockForPositiveHookOffset()
    {
        DatPhysicsScript script = BuildScript((0.1, CreateHook(9)));
        var sink = new RecordingSink();
        var runner = MakeRunner(sink, scripts: [(0x330000AAu, script)]);

        runner.PublishTime(1.0);
        Assert.True(runner.PlayDirect(1u, 0x330000AAu));
        runner.Tick(1.0);
        Assert.Empty(sink.Calls);

        runner.PublishTime(1.1);
        runner.Tick(1.1);
        Assert.Equal([9u], EmitterIds(sink));
    }

    [Fact]
    public void IneligibleOwnerRetainsScriptsAndTimedCallsUntilReentry()
    {
        bool eligible = true;
        DatPhysicsScript script = BuildScript((1.0, CreateHook(9)));
        var sink = new RecordingSink();
        var runner = MakeRunner(
            sink,
            randomUnit: () => 0.5,
            canAdvanceOwner: _ => eligible,
            scripts: [(0x330000AAu, script)]);
        runner.PlayDirect(1u, 0x330000AAu);
        runner.ScheduleCallPes(2u, 0x330000AAu, 2f);

        eligible = false;
        runner.Tick(2.0);
        Assert.Empty(sink.Calls);
        Assert.Equal(1, runner.ActiveScriptCount);
        Assert.Equal(1, runner.ScheduledCallPesCount);

        eligible = true;
        runner.Tick(2.0);
        Assert.Equal([(1u, 9u)],
            sink.Calls.Select(call =>
                (call.EntityId, ((CreateParticleHook)call.Hook).EmitterInfoId.DataId)));
        runner.Tick(3.0);
        Assert.Equal([(1u, 9u), (2u, 9u)],
            sink.Calls.Select(call =>
                (call.EntityId, ((CreateParticleHook)call.Hook).EmitterInfoId.DataId)));
    }

    [Fact]
    public void ZeroTimeRecursiveCallChainIsRejectedWithoutHanging()
    {
        const uint firstDid = 0x330000AAu;
        const uint secondDid = 0x330000BBu;
        PhysicsScriptRunner? runner = null;
        var sink = new RecordingSink
        {
            Callback = (owner, _, hook) =>
            {
                uint target = ((CreateParticleHook)hook).EmitterInfoId.DataId == 1u
                    ? secondDid
                    : firstDid;
                runner!.PlayDirect(owner, target);
            },
        };
        runner = MakeRunner(
            sink,
            scripts:
            [
                (firstDid, BuildScript((0.0, CreateHook(1)))),
                (secondDid, BuildScript((0.0, CreateHook(2)))),
            ]);
        var diagnostics = new List<string>();
        runner.DiagnosticSink = diagnostics.Add;
        runner.PlayDirect(1u, firstDid);

        runner.Tick(0.0);

        Assert.Equal([1u, 2u], EmitterIds(sink));
        Assert.Single(diagnostics, message => message.Contains("recursive", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0, runner.ActiveScriptCount);
    }

    [Fact]
    public void PositiveDurationRecursiveChainDrainsCatchUpWithoutFalseCycleRejection()
    {
        const uint firstDid = 0x33000428u;
        const uint secondDid = 0x33000429u;
        PhysicsScriptRunner? runner = null;
        var sink = new RecordingSink
        {
            Callback = (owner, _, hook) =>
            {
                uint target = ((CreateParticleHook)hook).EmitterInfoId.DataId == 1u
                    ? secondDid
                    : firstDid;
                runner!.PlayDirect(owner, target);
            },
        };
        runner = MakeRunner(
            sink,
            scripts:
            [
                (firstDid, BuildScript((2.8, CreateHook(1)))),
                (secondDid, BuildScript((2.8, CreateHook(2)))),
            ]);
        var diagnostics = new List<string>();
        runner.DiagnosticSink = diagnostics.Add;
        runner.PlayDirect(1u, firstDid);

        runner.Tick(8.4);

        Assert.Equal([1u, 2u, 1u], EmitterIds(sink));
        Assert.DoesNotContain(
            diagnostics,
            message => message.Contains("recursive", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, runner.ActiveScriptCount);
    }

    [Fact]
    public void CrossOwnerReentrantPlayWaitsForNextGlobalScriptPass()
    {
        const uint parentDid = 0x330000AAu;
        const uint childCurrentDid = 0x330000BBu;
        const uint childDefaultDid = 0x330000CCu;
        PhysicsScriptRunner? runner = null;
        var sink = new RecordingSink
        {
            Callback = (_, _, hook) =>
            {
                if (((CreateParticleHook)hook).EmitterInfoId.DataId == 1u)
                    runner!.PlayDirect(2u, childDefaultDid);
            },
        };
        runner = MakeRunner(
            sink,
            scripts:
            [
                (parentDid, BuildScript((0.0, CreateHook(1)))),
                (childCurrentDid, BuildScript((0.0, CreateHook(2)))),
                (childDefaultDid, BuildScript((0.0, CreateHook(3)))),
            ]);
        runner.PlayDirect(1u, parentDid);
        runner.PlayDirect(2u, childCurrentDid);

        runner.Tick(0.0);
        Assert.Equal([1u, 2u], EmitterIds(sink));

        runner.Tick(0.0);
        Assert.Equal([1u, 2u, 3u], EmitterIds(sink));
    }

    [Fact]
    public void NonFiniteTimelineIsRejectedBeforeItCanBypassRecursionGuard()
    {
        const uint scriptDid = 0x330000AAu;
        var sink = new RecordingSink();
        var runner = MakeRunner(
            sink,
            scripts: [(scriptDid, BuildScript((double.NaN, CreateHook(1))))]);
        var diagnostics = new List<string>();
        runner.DiagnosticSink = diagnostics.Add;

        Assert.False(runner.PlayDirect(1u, scriptDid));
        runner.Tick(0.0);

        Assert.Empty(sink.Calls);
        Assert.Contains(diagnostics, message => message.Contains("non-finite", StringComparison.Ordinal));
    }

    [Fact]
    public void ScheduleCallPesRejectsNonFiniteInputsAndStopClearsDelayedCalls()
    {
        DatPhysicsScript script = BuildScript((0.0, CreateHook(9)));
        var sink = new RecordingSink();
        var runner = MakeRunner(
            sink,
            randomUnit: () => 0.5,
            scripts: [(0x330000AAu, script)]);

        Assert.False(runner.ScheduleCallPes(1u, 0x330000AAu, float.NaN));
        Assert.False(runner.ScheduleCallPes(1u, 0x330000AAu, float.PositiveInfinity));
        Assert.True(runner.ScheduleCallPes(1u, 0x330000AAu, 2f));
        runner.StopAllForEntity(1u);
        runner.Tick(10.0);

        Assert.Equal(0, runner.ScheduledCallPesCount);
        Assert.Empty(sink.Calls);
    }

    [Fact]
    public void CurrentAnchorIsReadWhenHookFires()
    {
        DatPhysicsScript script = BuildScript((1.0, CreateHook(1)));
        var sink = new RecordingSink();
        var runner = MakeRunner(sink, scripts: [(0x330000AAu, script)]);
        runner.SetOwnerAnchor(1u, new Vector3(1, 0, 0));
        runner.PlayDirect(1u, 0x330000AAu);
        runner.SetOwnerAnchor(1u, new Vector3(7, 8, 9));

        runner.Tick(1.0);

        Assert.Equal(new Vector3(7, 8, 9), Assert.Single(sink.Calls).Position);
    }

    [Fact]
    public void EveryHookFansOutThroughCompleteRouter()
    {
        DatPhysicsScript script = BuildScript((0.0, CreateHook(1)));
        var first = new RecordingSink();
        var second = new RecordingSink();
        var router = new AnimationHookRouter();
        router.Register(first);
        router.Register(second);
        var runner = MakeRunner(router, scripts: [(0x330000AAu, script)]);
        runner.PlayDirect(1u, 0x330000AAu);

        runner.Tick(0.0);

        Assert.Single(first.Calls);
        Assert.Single(second.Calls);
    }

    [Fact]
    public void LoaderFailureIsCachedAndDoesNotCorruptOtherOwners()
    {
        DatPhysicsScript good = BuildScript((0.0, CreateHook(1)));
        int failingLoads = 0;
        var sink = new RecordingSink();
        var runner = new PhysicsScriptRunner(
            id =>
            {
                if (id == 0x330000AAu)
                    return good;
                failingLoads++;
                throw new InvalidDataException("fixture DAT failure");
            },
            sink);

        Assert.False(runner.PlayDirect(1u, 0x330000BBu));
        Assert.False(runner.PlayDirect(1u, 0x330000BBu));
        Assert.True(runner.PlayDirect(2u, 0x330000AAu));
        runner.Tick(0.0);

        Assert.Equal(1, failingLoads);
        Assert.Equal([1u], EmitterIds(sink));
    }

    [Fact]
    public void TickRejectsNonFiniteOrBackwardsTime()
    {
        var runner = MakeRunner(new RecordingSink());
        runner.Tick(1.0);

        Assert.Throws<ArgumentOutOfRangeException>(() => runner.Tick(0.5));
        Assert.Throws<ArgumentOutOfRangeException>(() => runner.Tick(double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => runner.Tick(double.PositiveInfinity));
    }

    private static uint[] EmitterIds(RecordingSink sink) =>
        sink.Calls
            .Select(call => ((CreateParticleHook)call.Hook).EmitterInfoId.DataId)
            .ToArray();
}
