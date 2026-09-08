using System.Reflection;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using AcDream.Content;
using AcDream.Headless.Configuration;
using AcDream.Headless.Hosting;
using AcDream.Headless.Platform;

namespace AcDream.Headless.Tests;

public sealed class HeadlessProcessContentOwnerTests
{
    [Fact]
    public void DisposeRequestRetiresResourcesAfterExactFinalLease()
    {
        var factory = new FixtureContentFactory();
        var owner = new HeadlessProcessContentOwner(
            ContentDescriptor(),
            _ => { },
            factory);
        using var first = owner.AcquireLease("first");
        using var second = owner.AcquireLease("second");

        Assert.Equal(1, factory.OpenCount);
        Assert.Equal(
            new HeadlessProcessContentSnapshot(
                LeaseCount: 2,
                IsDisposeRequested: false,
                IsDisposed: false,
                MappedVirtualBytes: 4096L),
            owner.CaptureSnapshot());

        owner.Dispose();

        Assert.True(owner.CaptureSnapshot().IsDisposeRequested);
        Assert.False(owner.CaptureSnapshot().IsDisposed);
        Assert.Throws<ObjectDisposedException>(
            () => owner.AcquireLease("late"));

        first.Dispose();
        Assert.Equal(1, owner.CaptureSnapshot().LeaseCount);
        Assert.Equal(0, factory.Dats.DisposeSuccessCount);
        Assert.Equal(0, factory.Prepared.DisposeSuccessCount);

        second.Dispose();

        Assert.True(owner.CaptureSnapshot().IsConverged);
        Assert.Equal(1, factory.Dats.DisposeSuccessCount);
        Assert.Equal(1, factory.Prepared.DisposeSuccessCount);

        owner.Dispose();
        first.Dispose();
        second.Dispose();
        Assert.Equal(1, factory.Dats.DisposeSuccessCount);
        Assert.Equal(1, factory.Prepared.DisposeSuccessCount);
    }

    [Fact]
    public void FailedFinalDrainRetainsRetryableOwnerSuffix()
    {
        var factory = new FixtureContentFactory();
        var owner = new HeadlessProcessContentOwner(
            ContentDescriptor(),
            _ => { },
            factory);
        using var lease = owner.AcquireLease("retry");
        owner.Dispose();
        factory.Prepared.FailNextDispose = true;

        Assert.Throws<IOException>(lease.Dispose);

        HeadlessProcessContentSnapshot failed = owner.CaptureSnapshot();
        Assert.Equal(0, failed.LeaseCount);
        Assert.True(failed.IsDisposeRequested);
        Assert.False(failed.IsDisposed);
        Assert.Equal(0, factory.Dats.DisposeSuccessCount);

        owner.Dispose();

        Assert.True(owner.CaptureSnapshot().IsConverged);
        Assert.Equal(1, factory.Prepared.DisposeSuccessCount);
        Assert.Equal(1, factory.Dats.DisposeSuccessCount);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(10)]
    [InlineData(30)]
    public void SessionsShareContentButNotMutableRuntimeOwners(
        int sessionCount)
    {
        var factory = new FixtureContentFactory();
        var configuration = new HeadlessConfiguration
        {
            Version = 1,
            Process = new HeadlessProcessSettings
            {
                Content = ContentDescriptor(),
            },
            Sessions = Enumerable.Range(0, sessionCount)
                .Select(index => Session(index))
                .Cast<HeadlessSessionDescriptor?>()
                .ToList(),
        };
        using var input = new StringReader(string.Concat(
            Enumerable.Repeat(
                "fixture-password" + Environment.NewLine,
                sessionCount)));
        using var diagnostics = new StringWriter();
        using var host = new HeadlessProcessHost(
            configuration,
            HeadlessPathSet.Resolve(new HeadlessPathOverrides()),
            input,
            diagnostics,
            contentFactory: factory);

        Assert.Equal(1, factory.OpenCount);
        Assert.Equal(sessionCount, host.Sessions.Count);
        Assert.Equal(sessionCount, host.Content?.LeaseCount);
        IPreparedCollisionSource sharedCollision =
            Assert.IsAssignableFrom<IPreparedCollisionSource>(
                host.Sessions[0].Content?.PreparedCollision);
        float[] sharedHeightTable = Assert.IsType<float[]>(
            ImmutableCollectionsMarshal.AsArray(
                host.Sessions[0].Content!.HeightTable));
        Assert.All(
            host.Sessions,
            session =>
            {
                Assert.Same(
                    factory.DatsResource,
                    session.Content?.Dats);
                Assert.Same(
                    factory.PreparedResource,
                    session.Content?.PreparedAssets);
                Assert.Same(
                    MagicCatalog.Empty,
                    session.Content?.MagicCatalog);
                Assert.Same(
                    sharedCollision,
                    session.Content?.PreparedCollision);
                Assert.Same(
                    sharedHeightTable,
                    ImmutableCollectionsMarshal.AsArray(
                        session.Content!.HeightTable));
            });
        Assert.Equal(
            sessionCount,
            host.Sessions
                .Select(static session => session.Runtime)
                .Distinct(ReferenceEqualityComparer.Instance)
                .Count());
        Assert.Equal(
            sessionCount,
            host.Sessions
                .Select(static session =>
                    session.Runtime.EntityObjects.Physics.Engine)
                .Distinct(ReferenceEqualityComparer.Instance)
                .Count());
        Assert.Equal(
            sessionCount,
            host.Sessions
                .Select(static session =>
                    session.Runtime.EntityObjects.Physics.DataCache)
                .Distinct(ReferenceEqualityComparer.Instance)
                .Count());
        Assert.Equal(
            sessionCount,
            host.Sessions
                .Select(static session =>
                    session.Runtime.MovementOwner)
                .Distinct(ReferenceEqualityComparer.Instance)
                .Count());

        HeadlessSessionHost[] sessions = host.Sessions.ToArray();
        host.Dispose();

        Assert.True(host.Content?.IsConverged);
        Assert.All(
            sessions,
            static session =>
                Assert.True(
                    session.Runtime.CaptureOwnership().IsConverged));
        Assert.Equal(1, factory.Prepared.DisposeSuccessCount);
        Assert.Equal(1, factory.Dats.DisposeSuccessCount);
        string output = diagnostics.ToString();
        Assert.Contains(
            "\"state\":\"disposed\"",
            output,
            StringComparison.Ordinal);
        Assert.Contains(
            $"\"convergedRuntimeCount\":{sessionCount}",
            output,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"isConverged\":true",
            output,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "fixture-password",
            output,
            StringComparison.Ordinal);
    }

    private static HeadlessContentDescriptor ContentDescriptor() => new()
    {
        DatDirectory = "fixture-dats",
        PreparedAssetPath = "fixture.pak",
    };

    private static HeadlessSessionDescriptor Session(int index) => new()
    {
        Id = $"bot-{index}",
        Endpoint = new HeadlessEndpointDescriptor
        {
            Host = "127.0.0.1",
            Port = 9000,
        },
        Account = $"account-{index}",
        Character = new HeadlessCharacterSelector
        {
            Index = 0,
        },
        Policy = new HeadlessBotPolicyDescriptor
        {
            Id = "idle",
        },
        Credential = new HeadlessCredentialReference
        {
            Provider = HeadlessCredentialProviderKind.StandardInput,
            Reference = $"stdin-{index}",
        },
    };

    private sealed class FixtureContentFactory
        : IHeadlessProcessContentFactory
    {
        internal FixtureContentFactory()
        {
            DatsResource =
                DispatchProxy.Create<IDatReaderWriter, TestResourceProxy>();
            PreparedResource =
                DispatchProxy.Create<ITestPreparedSource, TestResourceProxy>();
            Dats = TestResourceProxy.For(DatsResource);
            Prepared = TestResourceProxy.For(PreparedResource);
            Prepared.MappedVirtualBytes = 4096L;
        }

        internal int OpenCount { get; private set; }
        internal IDatReaderWriter DatsResource { get; }
        internal ITestPreparedSource PreparedResource { get; }
        internal TestResourceProxy Dats { get; }
        internal TestResourceProxy Prepared { get; }

        public HeadlessOpenedProcessContent Open(
            HeadlessContentDescriptor descriptor,
            Action<string> diagnostic)
        {
            OpenCount++;
            return new HeadlessOpenedProcessContent(
                DatsResource,
                PreparedResource,
                MagicCatalog.Empty,
                ImmutableArray.CreateRange(new float[256]));
        }
    }
}

public interface ITestPreparedSource
    : IPreparedAssetSource, IPreparedCollisionSource;

public class TestResourceProxy : DispatchProxy
{
    public int DisposeSuccessCount { get; private set; }
    public bool FailNextDispose { get; set; }
    public long MappedVirtualBytes { get; set; }

    public static TestResourceProxy For<T>(T resource)
        where T : class =>
        (TestResourceProxy)(object)resource;

    protected override object? Invoke(
        MethodInfo? targetMethod,
        object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        if (targetMethod.Name == nameof(IDisposable.Dispose))
        {
            if (FailNextDispose)
            {
                FailNextDispose = false;
                throw new IOException("fixture disposal failure");
            }
            DisposeSuccessCount++;
            MappedVirtualBytes = 0L;
            return null;
        }
        if (targetMethod.Name == "get_MappedVirtualBytes")
            return MappedVirtualBytes;

        Type returnType = targetMethod.ReturnType;
        return returnType == typeof(void) || !returnType.IsValueType
            ? null
            : Activator.CreateInstance(returnType);
    }
}
