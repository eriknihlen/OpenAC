using AcDream.App.Rendering.Wb;

namespace AcDream.App.Tests.Rendering.Wb;

public sealed class RetryableResourceReleaseLedgerTests
{
    [Fact]
    public void Advance_AttemptsEveryStage_AndRetriesOnlyUnfinishedStage()
    {
        int firstCalls = 0;
        int flakyCalls = 0;
        int lastCalls = 0;
        var ledger = new RetryableResourceReleaseLedger(
        [
            ("first", () => firstCalls++),
            ("flaky", () =>
            {
                flakyCalls++;
                if (flakyCalls == 1)
                    throw new InvalidOperationException("injected pre-commit failure");
            }),
            ("last", () => lastCalls++),
        ]);

        ResourceReleaseAttempt first = ledger.Advance();

        Assert.False(ledger.IsComplete);
        Assert.Equal(1, ledger.RemainingCount);
        Assert.Equal(3, first.AttemptedCount);
        Assert.Equal(2, first.CompletedCount);
        Assert.Single(first.Failures);
        Assert.False(first.Failures[0].MutationCommitted);
        Assert.Equal(1, firstCalls);
        Assert.Equal(1, flakyCalls);
        Assert.Equal(1, lastCalls);

        ResourceReleaseAttempt retry = ledger.Advance();

        Assert.True(ledger.IsComplete);
        Assert.Equal(0, ledger.RemainingCount);
        Assert.Equal(1, retry.AttemptedCount);
        Assert.Equal(1, retry.CompletedCount);
        Assert.Empty(retry.Failures);
        Assert.Equal(1, firstCalls);
        Assert.Equal(2, flakyCalls);
        Assert.Equal(1, lastCalls);
    }

    [Fact]
    public void Advance_CommittedExceptionalMutation_IsRecordedButNeverReplayed()
    {
        int committedCalls = 0;
        int laterCalls = 0;
        var ledger = new RetryableResourceReleaseLedger(
        [
            ("committed", () =>
            {
                committedCalls++;
                throw new MeshReferenceMutationException(
                    "injected post-commit failure",
                    mutationCommitted: true,
                    new InvalidOperationException("observer failed"));
            }),
            ("later", () => laterCalls++),
        ]);

        ResourceReleaseAttempt attempt = ledger.Advance();

        Assert.True(ledger.IsComplete);
        Assert.Equal(2, attempt.AttemptedCount);
        Assert.Equal(2, attempt.CompletedCount);
        ResourceReleaseFailure failure = Assert.Single(attempt.Failures);
        Assert.Equal("committed", failure.Stage);
        Assert.True(failure.MutationCommitted);
        Assert.Equal(1, committedCalls);
        Assert.Equal(1, laterCalls);

        ResourceReleaseAttempt duplicate = ledger.Advance();

        Assert.Equal(0, duplicate.AttemptedCount);
        Assert.Empty(duplicate.Failures);
        Assert.Equal(1, committedCalls);
        Assert.Equal(1, laterCalls);
    }

    [Fact]
    public void Advance_MultipleIndependentFailures_AllRunAndConvergeIndependently()
    {
        int firstFailures = 0;
        int secondFailures = 0;
        int successfulTail = 0;
        var ledger = new RetryableResourceReleaseLedger(
        [
            ("first", () =>
            {
                if (firstFailures++ == 0)
                    throw new InvalidOperationException("first");
            }),
            ("second", () =>
            {
                if (secondFailures++ < 2)
                    throw new InvalidOperationException("second");
            }),
            ("tail", () => successfulTail++),
        ]);

        ResourceReleaseAttempt first = ledger.Advance();
        ResourceReleaseAttempt second = ledger.Advance();
        ResourceReleaseAttempt third = ledger.Advance();

        Assert.Equal(2, first.Failures.Count);
        Assert.Single(second.Failures);
        Assert.Empty(third.Failures);
        Assert.True(ledger.IsComplete);
        Assert.Equal(1, successfulTail);
        Assert.Equal(2, firstFailures);
        Assert.Equal(3, secondFailures);
    }

    [Fact]
    public void Advance_ReentrantAttempt_DoesNotExecuteActiveStageTwice()
    {
        RetryableResourceReleaseLedger? ledger = null;
        int activeCalls = 0;
        int tailCalls = 0;
        ledger = new RetryableResourceReleaseLedger(
        [
            ("active", () =>
            {
                activeCalls++;
                ledger!.Advance();
            }),
            ("tail", () => tailCalls++),
        ]);

        ResourceReleaseAttempt attempt = ledger.Advance();

        Assert.True(ledger.IsComplete);
        Assert.Empty(attempt.Failures);
        Assert.Equal(1, activeCalls);
        Assert.Equal(1, tailCalls);
    }

    [Fact]
    public void Advance_ReentrantAttemptDoesNotGiveFailedTailTwoAttemptsInOnePass()
    {
        RetryableResourceReleaseLedger? ledger = null;
        int activeCalls = 0;
        int flakyTailCalls = 0;
        ledger = new RetryableResourceReleaseLedger(
        [
            ("active", () =>
            {
                activeCalls++;
                ledger!.Advance();
            }),
            ("flaky-tail", () =>
            {
                flakyTailCalls++;
                if (flakyTailCalls == 1)
                    throw new InvalidOperationException("first pass");
            }),
        ]);

        ResourceReleaseAttempt first = ledger.Advance();

        Assert.Single(first.Failures);
        Assert.Equal(1, activeCalls);
        Assert.Equal(1, flakyTailCalls);
        Assert.False(ledger.IsComplete);

        ResourceReleaseAttempt second = ledger.Advance();
        Assert.Empty(second.Failures);
        Assert.True(ledger.IsComplete);
        Assert.Equal(2, flakyTailCalls);
    }
}
