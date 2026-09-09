using AcDream.App.Rendering;

namespace AcDream.App.Tests.Rendering;

[Collection(AcDream.App.Tests.ThreadSchedulingCollection.Name)]
public sealed class HostQuiescenceGateTests
{
    [Fact]
    public void ExternalStopWaitsForAdmittedCallbackToReturn()
    {
        var gate = new HostQuiescenceGate();
        using var callbackEntered = new ManualResetEventSlim(false);
        using var releaseCallback = new ManualResetEventSlim(false);
        int calls = 0;
        Exception? callbackError = null;
        var callbackThread = new Thread(() =>
        {
            try
            {
                gate.Invoke(() =>
                {
                    callbackEntered.Set();
                    Assert.True(releaseCallback.Wait(TimeSpan.FromSeconds(5)));
                    calls++;
                });
            }
            catch (Exception error)
            {
                callbackError = error;
            }
        })
        {
            IsBackground = true,
            Name = "HostQuiescenceGate admitted callback contract",
        };
        callbackThread.Start();
        Assert.True(callbackEntered.Wait(TimeSpan.FromSeconds(5)));

        using var stopStarted = new ManualResetEventSlim(false);
        Exception? stopError = null;
        var stopThread = new Thread(() =>
        {
            stopStarted.Set();
            try
            {
                gate.StopAccepting();
            }
            catch (Exception error)
            {
                stopError = error;
            }
        })
        {
            IsBackground = true,
            Name = "HostQuiescenceGate external stop contract",
        };
        stopThread.Start();
        bool stopStartedInTime = stopStarted.Wait(TimeSpan.FromSeconds(5));
        bool stopBlockedOnCallback = stopStartedInTime && SpinWait.SpinUntil(
            () => (stopThread.ThreadState & ThreadState.WaitSleepJoin) != 0,
            TimeSpan.FromSeconds(5));

        releaseCallback.Set();
        bool callbackJoined = callbackThread.Join(TimeSpan.FromSeconds(5));
        bool stopJoined = stopThread.Join(TimeSpan.FromSeconds(5));
        gate.Invoke(() => calls++);

        Assert.True(stopStartedInTime, "the dedicated stop thread did not start");
        Assert.True(stopBlockedOnCallback, "stop never waited for the admitted callback");
        Assert.True(callbackJoined, "the admitted callback did not finish after release");
        Assert.True(stopJoined, "stop did not finish after the callback returned");
        Assert.Null(callbackError);
        Assert.Null(stopError);
        Assert.Equal(1, calls);
        Assert.False(gate.IsAccepting);
    }

    [Fact]
    public void CallbackCanStopGateReentrantlyWithoutDeadlock()
    {
        var gate = new HostQuiescenceGate();
        int calls = 0;

        gate.Invoke(() =>
        {
            calls++;
            gate.StopAccepting();
        });
        gate.Invoke(() => calls++);

        Assert.Equal(1, calls);
        Assert.False(gate.IsAccepting);
    }
}
