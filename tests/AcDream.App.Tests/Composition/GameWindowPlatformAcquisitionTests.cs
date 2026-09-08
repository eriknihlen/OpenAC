using AcDream.App.Composition;

namespace AcDream.App.Tests.Composition;

public sealed class GameWindowPlatformAcquisitionTests
{
    [Fact]
    public void AcquirePublishesGraphicsBeforeInputConstruction()
    {
        var trace = new List<string>();
        var owner = new Publication(trace);
        var graphics = new Resource("graphics", trace);
        var input = new Resource("input", trace);

        GameWindowPlatformResult<Resource, Resource> result =
            GameWindowPlatformAcquisition.Acquire(
                () =>
                {
                    trace.Add("gl.factory");
                    return graphics;
                },
                value => value.ReleaseUnpublished(),
                () =>
                {
                    Assert.Same(graphics, owner.Graphics);
                    trace.Add("input.factory");
                    return input;
                },
                value => value.ReleaseUnpublished(),
                owner);

        Assert.Same(graphics, result.Graphics);
        Assert.Same(input, result.Input);
        Assert.Equal(
            ["gl.factory", "gl.publish", "input.factory", "input.publish"],
            trace);

        owner.Dispose();
        owner.Dispose();
        Assert.Equal(1, graphics.OwnerReleaseCalls);
        Assert.Equal(1, input.OwnerReleaseCalls);
        Assert.Equal(0, graphics.UnpublishedReleaseCalls);
        Assert.Equal(0, input.UnpublishedReleaseCalls);
        Assert.Equal("input.owner-release", trace[^2]);
        Assert.Equal("graphics.owner-release", trace[^1]);
    }

    [Fact]
    public void GraphicsFactoryFailureNeverConstructsInput()
    {
        var trace = new List<string>();
        var owner = new Publication(trace);

        Assert.Throws<InvalidOperationException>(() =>
            GameWindowPlatformAcquisition.Acquire<Resource, Resource>(
                () => throw new InvalidOperationException("graphics failed"),
                value => value.ReleaseUnpublished(),
                () =>
                {
                    trace.Add("input.factory");
                    return new Resource("input", trace);
                },
                value => value.ReleaseUnpublished(),
                owner));

        Assert.Empty(trace);
        Assert.Null(owner.Graphics);
        Assert.Null(owner.Input);
    }

    [Fact]
    public void InputFactoryFailureLeavesPublishedGraphicsLifetimeOwned()
    {
        var trace = new List<string>();
        var owner = new Publication(trace);
        var graphics = new Resource("graphics", trace);

        Assert.Throws<InvalidOperationException>(() =>
            GameWindowPlatformAcquisition.Acquire<Resource, Resource>(
                () =>
                {
                    trace.Add("gl.factory");
                    return graphics;
                },
                value => value.ReleaseUnpublished(),
                () =>
                {
                    Assert.Same(graphics, owner.Graphics);
                    trace.Add("input.factory");
                    throw new InvalidOperationException("input failed");
                },
                value => value.ReleaseUnpublished(),
                owner));

        Assert.Equal(["gl.factory", "gl.publish", "input.factory"], trace);
        Assert.Equal(0, graphics.UnpublishedReleaseCalls);
        owner.Dispose();
        Assert.Equal(1, graphics.OwnerReleaseCalls);
    }

    [Theory]
    [InlineData((int)GameWindowPlatformAcquisitionPoint.GraphicsPublished)]
    [InlineData((int)GameWindowPlatformAcquisitionPoint.InputPublished)]
    public void FailureImmediatelyAfterPublicationNeverRollsBackPublishedOwner(
        int failurePointValue)
    {
        var failurePoint = (GameWindowPlatformAcquisitionPoint)failurePointValue;
        var trace = new List<string>();
        var owner = new Publication(trace);
        var graphics = new Resource("graphics", trace);
        var input = new Resource("input", trace);

        Assert.Throws<InvalidOperationException>(() =>
            GameWindowPlatformAcquisition.Acquire(
                () => graphics,
                value => value.ReleaseUnpublished(),
                () => input,
                value => value.ReleaseUnpublished(),
                owner,
                point =>
                {
                    if (point == failurePoint)
                        throw new InvalidOperationException("injected failure");
                }));

        Assert.Equal(0, graphics.UnpublishedReleaseCalls);
        Assert.Equal(0, input.UnpublishedReleaseCalls);
        Assert.NotNull(owner.Graphics);
        Assert.Equal(
            failurePoint == GameWindowPlatformAcquisitionPoint.InputPublished,
            owner.Input is not null);

        owner.Dispose();
        Assert.Equal(1, graphics.OwnerReleaseCalls);
        Assert.Equal(
            failurePoint == GameWindowPlatformAcquisitionPoint.InputPublished ? 1 : 0,
            input.OwnerReleaseCalls);
    }

    private sealed class Publication(List<string> trace) :
        IGameWindowPlatformPublication<Resource, Resource>,
        IDisposable
    {
        public Resource? Graphics { get; private set; }
        public Resource? Input { get; private set; }

        public void PublishGraphics(Resource graphics)
        {
            if (Graphics is not null)
                throw new InvalidOperationException("graphics already published");
            Graphics = graphics;
            trace.Add("gl.publish");
        }

        public void PublishInput(Resource input)
        {
            if (Input is not null)
                throw new InvalidOperationException("input already published");
            Input = input;
            trace.Add("input.publish");
        }

        public void Dispose()
        {
            Resource? input = Input;
            Input = null;
            input?.ReleaseFromOwner();
            Resource? graphics = Graphics;
            Graphics = null;
            graphics?.ReleaseFromOwner();
        }
    }

    private sealed class Resource(string name, List<string> trace)
    {
        public int UnpublishedReleaseCalls { get; private set; }
        public int OwnerReleaseCalls { get; private set; }

        public void ReleaseUnpublished()
        {
            UnpublishedReleaseCalls++;
            trace.Add($"{name}.unpublished-release");
        }

        public void ReleaseFromOwner()
        {
            OwnerReleaseCalls++;
            trace.Add($"{name}.owner-release");
        }
    }
}
