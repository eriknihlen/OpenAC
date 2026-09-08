using System;
using System.Linq;
using System.Text;
using AcDream.Core.Physics;
using Xunit;
using Xunit.Abstractions;

namespace AcDream.Core.Tests.Physics.Motion;

public sealed class RemoteChaseDrainBisectTests
{
    private readonly ITestOutputHelper _out;
    public RemoteChaseDrainBisectTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void StanceChange_DrainChain_TickByTick()
    {
        var h = new RemoteChaseHarness();

        // settle spawn
        for (int i = 0; i < 30; i++) h.Tick();
        DumpState(h, "pre-stance");

        h.UmInterpreted(RemoteChaseHarness.Combat, RemoteChaseHarness.Ready);
        DumpState(h, "post-UM (t=0)");

        for (int i = 1; i <= 90; i++)
        {
            TickWithHookTrace(h, i);
            if (i % 6 == 0 || i <= 3)
                DumpState(h, $"tick {i} (t={i / 60f:F2})");
            if (!h.Interp.MotionsPending() && h.Seq.Manager.PendingAnimations.Any() == false)
            {
                DumpState(h, $"DRAINED at tick {i}");
                return;
            }
        }

        DumpState(h, "END — still wedged");
        Assert.Fail("drain chain wedged after stance UM — see output for where");
    }

    private void TickWithHookTrace(RemoteChaseHarness h, int i)
    {
        h.Now += RemoteChaseHarness.Dt;
        h.Mgr.UseTime();
        if (h.Body.OnWalkable)
            h.Body.set_local_velocity(h.Interp.get_state_velocity(), false);

        h.Seq.Advance(RemoteChaseHarness.Dt);
        var hooks = h.Seq.ConsumePendingHooks();
        if (hooks.Count > 0)
        {
            var sb = new StringBuilder();
            sb.Append(FormattableString.Invariant($"tick {i}: hooks=["));
            for (int k = 0; k < hooks.Count; k++)
            {
                if (k > 0) sb.Append(", ");
                sb.Append(hooks[k]?.GetType().Name ?? "null");
            }
            sb.Append(']');
            _out.WriteLine(sb.ToString());
        }
        for (int k = 0; k < hooks.Count; k++)
        {
            if (hooks[k] is DatReaderWriter.Types.AnimationDoneHook)
                h.Seq.Manager.AnimationDone(success: true);
        }
        h.Seq.Manager.UseTime();
    }

    private void DumpState(RemoteChaseHarness h, string tag)
    {
        var mgrQ = string.Join(" ", h.Seq.Manager.PendingAnimations
            .Select(p => FormattableString.Invariant($"(0x{p.Motion:X8},t={p.NumAnims})")));
        var core = h.Seq.Core;
        var seqList = new StringBuilder();
        for (var n = core.AnimList.First; n is not null; n = n.Next)
        {
            if (seqList.Length > 0) seqList.Append(',');
            seqList.Append(FormattableString.Invariant($"fr={n.Value.Framerate:F0}"));
            if (ReferenceEquals(n, core.FirstCyclicNode)) seqList.Append('*');
            if (ReferenceEquals(n, core.CurrAnimNode)) seqList.Append('^');
        }
        _out.WriteLine(FormattableString.Invariant(
            $"[{tag}] interpPending={h.Interp.MotionsPending()} mgrQ=[{mgrQ}] counter={h.Seq.Manager.AnimationCounter} seq=[{seqList}] frame={core.FrameNumber:F1} substate=0x{h.Seq.Manager.State.Substate:X8} style=0x{h.Seq.Manager.State.Style:X8}"));
    }
}
