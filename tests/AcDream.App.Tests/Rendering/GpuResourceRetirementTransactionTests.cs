using AcDream.App.Rendering;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Rendering.Wb;
using AcDream.App.Tests.Rendering.Gpu;

namespace AcDream.App.Tests.Rendering;

public sealed class GpuResourceRetirementTransactionTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Release_RetryResumesAtFirstUncommittedStage(int failingStage)
    {
        int[] calls = new int[4];
        bool failed = false;
        Action[] stages = Enumerable.Range(0, calls.Length)
            .Select<int, Action>(stage => () =>
            {
                calls[stage]++;
                if (stage == failingStage && !failed)
                {
                    failed = true;
                    throw new InvalidOperationException($"stage {stage}");
                }
            })
            .ToArray();
        var release = new RetryableGpuResourceRelease(stages);

        Assert.Throws<InvalidOperationException>(release.Run);
        Assert.Equal(failingStage, release.CompletedStageCount);

        release.Run();

        Assert.True(release.IsComplete);
        for (int stage = 0; stage < calls.Length; stage++)
            Assert.Equal(stage == failingStage ? 2 : 1, calls[stage]);
    }

    [Fact]
    public void Ledger_QueueInsertionFailureRetainsReleaseForPublicationRetry()
    {
        var queue = new FailBeforeAcceptQueue();
        var ledger = new GpuRetirementLedger(queue);
        int releases = 0;

        Assert.Throws<InvalidOperationException>(() =>
            ledger.Retire(new RetryableGpuResourceRelease(() => releases++)));
        Assert.Equal(1, ledger.AwaitingPublicationCount);
        Assert.Equal(0, releases);

        ledger.RetryPendingPublications();
        Assert.Equal(0, ledger.AwaitingPublicationCount);
        Assert.Single(queue.Actions);

        queue.Actions.Single()();
        Assert.Equal(1, releases);
    }

    [Fact]
    public void Ledger_ImmediateCallbackFailureRetainsCommittedStageCursor()
    {
        var ledger = new GpuRetirementLedger(ImmediateGpuResourceRetirementQueue.Instance);
        int first = 0;
        int second = 0;
        bool failSecond = true;
        var release = new RetryableGpuResourceRelease(
            () => first++,
            () =>
            {
                second++;
                if (failSecond)
                {
                    failSecond = false;
                    throw new InvalidOperationException("second stage");
                }
            });

        Assert.Throws<InvalidOperationException>(() => ledger.Retire(release));
        Assert.Equal(1, ledger.AwaitingPublicationCount);
        Assert.Equal(1, release.CompletedStageCount);

        ledger.RetryPendingPublications();
        Assert.Equal(0, ledger.AwaitingPublicationCount);
        Assert.Equal(1, first);
        Assert.Equal(2, second);
    }

    [Fact]
    public void Release_ReentrantDrainDoesNotReplayActiveStage()
    {
        RetryableGpuResourceRelease? release = null;
        int active = 0;
        int tail = 0;
        release = new RetryableGpuResourceRelease(
            () =>
            {
                active++;
                release!.Run();
            },
            () => tail++);

        release.Run();

        Assert.True(release.IsComplete);
        Assert.Equal(1, active);
        Assert.Equal(1, tail);
    }

    [Fact]
    public void Release_PostMutationValidationFailureDoesNotReplayMutationStage()
    {
        int mutations = 0;
        int validations = 0;
        int accounting = 0;
        var release = new RetryableGpuResourceRelease(
            () => mutations++,
            () =>
            {
                validations++;
                if (validations == 1)
                    throw new InvalidOperationException("post-mutation validation");
            },
            () => accounting++);

        Assert.Throws<InvalidOperationException>(release.Run);
        release.Run();

        Assert.Equal(1, mutations);
        Assert.Equal(2, validations);
        Assert.Equal(1, accounting);
        Assert.True(release.IsComplete);
    }

    [Fact]
    public void Ledger_RetryAttemptsEveryPendingPublicationDespiteOneFailure()
    {
        var queue = new FailFirstNQueue(3);
        var ledger = new GpuRetirementLedger(queue);
        Assert.Throws<InvalidOperationException>(() =>
            ledger.Retire(new RetryableGpuResourceRelease(() => { })));
        Assert.Throws<InvalidOperationException>(() =>
            ledger.Retire(new RetryableGpuResourceRelease(() => { })));

        AggregateException error = Assert.Throws<AggregateException>(
            ledger.RetryPendingPublications);

        Assert.Single(error.InnerExceptions);
        Assert.Equal(1, ledger.AwaitingPublicationCount);
        Assert.Single(queue.Actions);

        ledger.RetryPendingPublications();
        Assert.Equal(0, ledger.AwaitingPublicationCount);
        Assert.Equal(2, queue.Actions.Count);
    }

    [Fact]
    public void Ledger_RetrySpecificPublicationDoesNotRepublishOtherRelease()
    {
        var queue = new FailFirstNQueue(2);
        var ledger = new GpuRetirementLedger(queue);
        var first = new RetryableGpuResourceRelease(() => { });
        var second = new RetryableGpuResourceRelease(() => { });
        Assert.Throws<InvalidOperationException>(() => ledger.Retire(first));
        Assert.Throws<InvalidOperationException>(() => ledger.Retire(second));

        ledger.RetryPendingPublication(first);

        Assert.Equal(1, ledger.AwaitingPublicationCount);
        Assert.Single(queue.Actions);
        ledger.RetryPendingPublication(second);
        Assert.Equal(0, ledger.AwaitingPublicationCount);
        Assert.Equal(2, queue.Actions.Count);
    }

    [Fact]
    public void Ledger_BatchOwnsEveryReleaseBeforePublishingFirst()
    {
        var queue = new FailFirstNQueue(1);
        var ledger = new GpuRetirementLedger(queue);
        var first = new RetryableGpuResourceRelease(() => { });
        var second = new RetryableGpuResourceRelease(() => { });

        Assert.Throws<AggregateException>(() => ledger.RetireMany([first, second]));

        Assert.Equal(1, ledger.AwaitingPublicationCount);
        Assert.Single(queue.Actions);
        ledger.RetryPendingPublications();
        Assert.Equal(2, queue.Actions.Count);
    }

    private static IGpuBuffer StagedArenaBuffer(string name, long sizeBytes) =>
        new RecordingGpuBuffer(new GpuBufferDescription(
            name,
            sizeBytes,
            GpuBufferUsage.Vertex | GpuBufferUsage.TransferDestination,
            GpuMemoryResidency.DeviceLocal));

    [Fact]
    public void MigrationAbortTicket_RetainsBufferUntilEveryReleaseStageConverges()
    {
        int deleteCalls = 0;
        int accountingCalls = 0;
        bool failAccounting = true;
        IGpuBuffer staged = StagedArenaBuffer("staged-37", 4096);
        var ticket = new GlobalMeshMigrationAbortTicket(
            buffer: staged,
            capacityBytes: 4096,
            new RetryableGpuResourceRelease(
                () => deleteCalls++,
                () =>
                {
                    if (failAccounting)
                    {
                        failAccounting = false;
                        throw new InvalidOperationException("injected accounting failure");
                    }
                    accountingCalls++;
                }));

        Assert.Throws<InvalidOperationException>(ticket.Advance);
        Assert.False(ticket.IsComplete);
        Assert.Same(staged, ticket.Buffer);
        Assert.Equal(4096, ticket.CapacityBytes);
        Assert.Equal(1, deleteCalls);
        Assert.Equal(0, accountingCalls);

        ticket.Advance();
        ticket.Advance();

        Assert.True(ticket.IsComplete);
        Assert.Equal(1, deleteCalls);
        Assert.Equal(1, accountingCalls);
    }

    [Fact]
    public void MigrationAbortTicket_DeleteValidationFailureRetriesBeforeAccounting()
    {
        int deleteCalls = 0;
        int accountingCalls = 0;
        bool failDeleteValidation = true;
        var ticket = new GlobalMeshMigrationAbortTicket(
            buffer: StagedArenaBuffer("staged-41", 8192),
            capacityBytes: 8192,
            new RetryableGpuResourceRelease(
                () =>
                {
                    deleteCalls++;
                    if (failDeleteValidation)
                    {
                        failDeleteValidation = false;
                        throw new InvalidOperationException("injected GL delete validation failure");
                    }
                },
                () => accountingCalls++));

        Assert.Throws<InvalidOperationException>(ticket.Advance);
        Assert.False(ticket.IsComplete);
        Assert.Equal(1, deleteCalls);
        Assert.Equal(0, accountingCalls);

        ticket.Advance();

        Assert.True(ticket.IsComplete);
        Assert.Equal(2, deleteCalls);
        Assert.Equal(1, accountingCalls);
    }

    private sealed class FailBeforeAcceptQueue : IGpuResourceRetirementQueue
    {
        private bool _fail = true;
        public List<Action> Actions { get; } = [];

        public void Retire(Action release)
        {
            if (_fail)
            {
                _fail = false;
                throw new InvalidOperationException("queue insertion");
            }
            Actions.Add(release);
        }
    }

    private sealed class FailFirstNQueue(int failures) : IGpuResourceRetirementQueue
    {
        private int _remaining = failures;
        public List<Action> Actions { get; } = [];

        public void Retire(Action release)
        {
            if (_remaining-- > 0)
                throw new InvalidOperationException("synthetic queue publication failure");
            Actions.Add(release);
        }
    }

}
