using AcDream.App.Rendering;
using AcDream.App.UI;

namespace AcDream.App.Tests.UI;

public sealed class UiCompositeShutdownTests
{
    [Fact]
    public void UiHostShutdownRetainsFailedInputAndProtectsDependentOwners()
    {
        var calls = new List<string>();
        bool failInput = true;
        ResourceShutdownTransaction transaction = UiHost.CreateShutdownTransaction(
        [
            () => calls.Add("last-input"),
            () =>
            {
                calls.Add("failed-input");
                if (failInput)
                    throw new InvalidOperationException("synthetic unsubscribe failure");
            },
        ],
        () => calls.Add("input-state"),
        () => calls.Add("windows"),
        () => calls.Add("text"));

        Assert.Throws<AggregateException>(transaction.CompleteOrThrow);

        Assert.Equal(1, calls.Count(call => call == "last-input"));
        Assert.Equal(3, calls.Count(call => call == "failed-input"));
        Assert.DoesNotContain("input-state", calls);
        Assert.DoesNotContain("windows", calls);
        Assert.DoesNotContain("text", calls);

        failInput = false;
        transaction.CompleteOrThrow();

        Assert.True(transaction.IsComplete);
        Assert.Equal(1, calls.Count(call => call == "last-input"));
        Assert.Equal(4, calls.Count(call => call == "failed-input"));
        Assert.Equal(1, calls.Count(call => call == "input-state"));
        Assert.Equal(1, calls.Count(call => call == "windows"));
        Assert.Equal(1, calls.Count(call => call == "text"));
    }

    [Fact]
    public void UiHostShutdownRetriesRendererWithoutReplayingWindowManager()
    {
        int windowCalls = 0;
        int rendererCalls = 0;
        bool failRenderer = true;
        ResourceShutdownTransaction transaction = UiHost.CreateShutdownTransaction(
            [],
            () => { },
            () => windowCalls++,
            () =>
            {
                rendererCalls++;
                if (failRenderer)
                    throw new InvalidOperationException("synthetic renderer failure");
            });

        Assert.Throws<AggregateException>(transaction.CompleteOrThrow);
        Assert.Equal(1, windowCalls);
        Assert.Equal(2, rendererCalls);

        failRenderer = false;
        transaction.CompleteOrThrow();

        Assert.True(transaction.IsComplete);
        Assert.Equal(1, windowCalls);
        Assert.Equal(3, rendererCalls);
    }

    [Fact]
    public void RetailRuntimeShutdownReentersSafelyAndGatesHostBehindControllers()
    {
        var calls = new List<string>();
        bool failGameplay = true;
        ResourceShutdownTransaction? transaction = null;
        transaction = RetailUiRuntime.CreateShutdownTransaction(
            () => calls.Add("automation"),
            () => calls.Add("persistence"),
            () => calls.Add("visibility"),
            () =>
            {
                calls.Add("item");
                transaction!.CompleteOrThrow();
            },
            () =>
            {
                calls.Add("gameplay");
                if (failGameplay)
                    throw new InvalidOperationException("synthetic controller failure");
            },
            () => calls.Add("dialogs"),
            () => calls.Add("panels"),
            () => calls.Add("host"));

        Assert.Throws<AggregateException>(transaction.CompleteOrThrow);

        Assert.Equal(1, calls.Count(call => call == "automation"));
        Assert.Equal(1, calls.Count(call => call == "persistence"));
        Assert.Equal(1, calls.Count(call => call == "visibility"));
        Assert.Equal(1, calls.Count(call => call == "item"));
        Assert.Equal(3, calls.Count(call => call == "gameplay"));
        Assert.DoesNotContain("dialogs", calls);
        Assert.DoesNotContain("panels", calls);
        Assert.DoesNotContain("host", calls);

        failGameplay = false;
        transaction.CompleteOrThrow();

        Assert.True(transaction.IsComplete);
        Assert.Equal(1, calls.Count(call => call == "item"));
        Assert.Equal(1, calls.Count(call => call == "dialogs"));
        Assert.Equal(1, calls.Count(call => call == "panels"));
        Assert.Equal(1, calls.Count(call => call == "host"));
    }
}
