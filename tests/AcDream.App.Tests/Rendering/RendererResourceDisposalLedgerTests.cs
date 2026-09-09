using AcDream.App.Rendering;
using AcDream.App.Rendering.Wb;

namespace AcDream.App.Tests.Rendering;

public sealed class RendererResourceDisposalLedgerTests
{
    [Fact]
    public void FailedGpuResourceDoesNotStrandSiblingAndFinalizesOnlyAfterRetry()
    {
        int firstDeleteCalls = 0;
        int firstAccountingCalls = 0;
        int firstMetadataCalls = 0;
        int siblingDeleteCalls = 0;
        bool failAccounting = true;
        bool disposed = false;

        var first = new RetryableGpuResourceRelease(
            () => firstDeleteCalls++,
            () =>
            {
                firstAccountingCalls++;
                if (failAccounting)
                {
                    failAccounting = false;
                    throw new InvalidOperationException("injected accounting failure");
                }
            },
            () => firstMetadataCalls++);
        var sibling = new RetryableGpuResourceRelease(
            () => siblingDeleteCalls++);
        var ledger = new RetryableResourceReleaseLedger(
        [
            ("first", first.Run),
            ("sibling", sibling.Run),
        ]);

        void AdvanceOwnerDispose()
        {
            ResourceReleaseAttempt attempt = ledger.Advance();
            if (ledger.IsComplete)
                disposed = true;
            if (attempt.HasFailures)
                throw attempt.ToException("Synthetic renderer teardown failed.");
        }

        Assert.Throws<AggregateException>(AdvanceOwnerDispose);

        Assert.False(disposed);
        Assert.Equal(1, firstDeleteCalls);
        Assert.Equal(1, firstAccountingCalls);
        Assert.Equal(0, firstMetadataCalls);
        Assert.Equal(1, siblingDeleteCalls);

        AdvanceOwnerDispose();
        AdvanceOwnerDispose();

        Assert.True(disposed);
        Assert.Equal(1, firstDeleteCalls);
        Assert.Equal(2, firstAccountingCalls);
        Assert.Equal(1, firstMetadataCalls);
        Assert.Equal(1, siblingDeleteCalls);
    }
}
