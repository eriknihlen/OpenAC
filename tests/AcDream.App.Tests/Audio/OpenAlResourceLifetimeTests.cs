using AcDream.App.Audio;
using Silk.NET.OpenAL;

namespace AcDream.App.Tests.Audio;

public sealed class OpenAlResourceLifetimeTests
{
    [Fact]
    public void SuccessfulEngineConstructionOwnsAndReleasesEveryNativePrefixOnce()
    {
        var api = new RecordingApi();
        var engine = new OpenAlAudioEngine(new Factory(api));

        Assert.True(engine.IsAvailable);
        // OpenAC #42: one pool of sixteen for everything. An interface sound
        // shares these voices, so there is no second set of sources to make.
        Assert.Equal(16, api.GeneratedSources.Count);
        Assert.Equal(16, api.Configured3D.Count);

        engine.Dispose();
        engine.Dispose();

        Assert.True(engine.IsDisposalComplete);
        Assert.Equal(16, api.DeletedSources.Count);
        Assert.Equal(
            Enumerable.Range(1, 16).Reverse().Select(value => (uint)value),
            api.DeletedSources);
        Assert.Equal(1, api.ClearCurrentCalls);
        Assert.Equal(1, api.DestroyContextCalls);
        Assert.Equal(1, api.CloseDeviceCalls);
    }

    // OpenAC #42: the backend's output limiter pulls the whole mix down when a
    // burst of sounds clips it. The context asks for it off wherever the device
    // allows, and says so once at startup.
    [Fact]
    public void ContextCreation_AsksForTheOutputLimiterOff_AndReportsItOff()
    {
        var api = new RecordingApi();

        var engine = new OpenAlAudioEngine(new Factory(api));

        Assert.True(engine.IsAvailable);
        Assert.Equal(
            new[]
            {
                OpenAlContextAttributes.OutputLimiter,
                OpenAlContextAttributes.Off,
                OpenAlContextAttributes.EndOfList,
            },
            api.ContextAttributes);
        Assert.Equal(
            "[audio] output limiter: asked for off, device reports off",
            engine.OutputLimiterReport);
        engine.Dispose();
    }

    [Fact]
    public void ContextCreation_AsksForNothing_WhenTheDeviceCannotControlTheLimiter()
    {
        var api = new RecordingApi { OutputLimiterSupported = false };

        var engine = new OpenAlAudioEngine(new Factory(api));

        Assert.True(engine.IsAvailable);
        Assert.Null(api.ContextAttributes);
        Assert.Equal(0, api.LimiterReads);
        Assert.Equal(
            "[audio] output limiter: this device does not let us turn it off",
            engine.OutputLimiterReport);
        engine.Dispose();
    }

    // A device that answers nothing must not be reported as "off" — that would
    // be a lie in the one line whose job is to report what the limiter is doing.
    [Fact]
    public void AnUnansweredLimiterRead_IsReportedAsNothing_NotAsOff()
    {
        var api = new RecordingApi { OutputLimiterAnswers = false };

        var engine = new OpenAlAudioEngine(new Factory(api));

        Assert.True(engine.IsAvailable);
        Assert.Equal(
            "[audio] output limiter: asked for off, device reports nothing",
            engine.OutputLimiterReport);
        engine.Dispose();
    }

    // Nothing at runtime can catch a wrong key: the device drops the wrong
    // attribute at context creation without complaining, and refuses to read it
    // back. The key one below this one is a real attribute that only a loopback
    // device accepts, so a single wrong digit is silent in both directions.
    [Fact]
    public void TheOutputLimiterAttributeKey_IsTheLimiterKey_NotTheOneNextToIt()
    {
        Assert.Equal(0x199A, OpenAlContextAttributes.OutputLimiter);
    }

    [Fact]
    public void OutputLimiterAttributes_AreBuiltOnlyWhenTheDeviceSupportsThem()
    {
        Assert.Equal(
            new[]
            {
                OpenAlContextAttributes.OutputLimiter,
                OpenAlContextAttributes.Off,
                OpenAlContextAttributes.EndOfList,
            },
            OpenAlContextAttributes.Build(outputLimiterControllable: true));
        Assert.Null(OpenAlContextAttributes.Build(outputLimiterControllable: false));
    }

    // A device that writes nothing into the buffer, or raises an error, has not
    // answered — and an unanswered read must not read back as "off".
    [Theory]
    [InlineData(0, false, 0)]
    [InlineData(1, false, 1)]
    [InlineData(0, true, null)]
    [InlineData(OpenAlContextAttributes.Unanswered, false, null)]
    public void ALimiterRead_CountsOnlyWhenTheDeviceActuallyAnswered(
        int value,
        bool errored,
        int? expected)
    {
        Assert.Equal(expected, OpenAlContextAttributes.ReadLimiterState(value, errored));
    }

    [Theory]
    [InlineData(true, 0, "[audio] output limiter: asked for off, device reports off")]
    [InlineData(true, 1, "[audio] output limiter: asked for off, device reports on")]
    [InlineData(true, null, "[audio] output limiter: asked for off, device reports nothing")]
    [InlineData(false, null,
        "[audio] output limiter: this device does not let us turn it off")]
    public void OutputLimiterReport_StatesWhatWeAskedForAndWhatTheDeviceSays(
        bool controllable,
        int? reported,
        string expected)
    {
        Assert.Equal(expected, OpenAlContextAttributes.Describe(controllable, reported));
    }

    [Fact]
    public void ConfigurationFailureRollsBackTheExactGeneratedSourcePrefix()
    {
        var api = new RecordingApi
        {
            ConfigureFailureSource = 3,
        };

        var engine = new OpenAlAudioEngine(new Factory(api));

        Assert.False(engine.IsAvailable);
        Assert.True(engine.IsDisposalComplete);
        Assert.Equal([3u, 2u, 1u], api.DeletedSources);
        Assert.Equal(1, api.DestroyContextCalls);
        Assert.Equal(1, api.CloseDeviceCalls);
    }

    [Fact]
    public void IncompleteInitializationCleanupRemainsRetryableWithoutReplay()
    {
        var api = new RecordingApi
        {
            ConfigureFailureSource = 3,
            DeleteFailureSource = 2,
        };

        OpenAlInitializationException failure = Assert.Throws<OpenAlInitializationException>(
            () => new OpenAlAudioEngine(new Factory(api)));

        Assert.False(failure.IsCleanupComplete);
        Assert.Equal([3u, 1u], api.DeletedSources);
        Assert.Equal(0, api.DestroyContextCalls);
        Assert.Equal(0, api.CloseDeviceCalls);

        api.DeleteFailureSource = null;
        failure.RetryCleanup();
        failure.RetryCleanup();

        Assert.True(failure.IsCleanupComplete);
        Assert.Equal([3u, 1u, 2u], api.DeletedSources);
        Assert.Equal(1, api.DeletedSources.Count(source => source == 3u));
        Assert.Equal(1, api.DeletedSources.Count(source => source == 1u));
        Assert.Equal(1, api.DestroyContextCalls);
        Assert.Equal(1, api.CloseDeviceCalls);
    }

    [Fact]
    public void ContextCreationFailureClosesTheDeviceBeforeReturningUnavailable()
    {
        var api = new RecordingApi { ContextResult = 0 };

        var engine = new OpenAlAudioEngine(new Factory(api));

        Assert.False(engine.IsAvailable);
        Assert.True(engine.IsDisposalComplete);
        Assert.Empty(api.GeneratedSources);
        Assert.Equal(0, api.DestroyContextCalls);
        Assert.Equal(1, api.CloseDeviceCalls);
    }

    [Fact]
    public void UnavailableEngineWorldQuiescenceRemainsASafeNoOp()
    {
        var api = new RecordingApi { ContextResult = 0 };
        var engine = new OpenAlAudioEngine(new Factory(api));

        engine.SuspendWorldAudio();
        engine.StopAllForOwner(0x50000001u);
        engine.ResumeWorldAudio();

        Assert.False(engine.IsAvailable);
        Assert.True(engine.IsDisposalComplete);
        Assert.Empty(api.GeneratedSources);
    }

    private sealed class Factory(IOpenAlResourceApi api) : IOpenAlResourceApiFactory
    {
        public IOpenAlResourceApi Create() => api;
    }

    private sealed class RecordingApi : IOpenAlResourceApi
    {
        private uint _nextSource = 1;

        public AL? AudioApi => null;
        public ALContext? ContextApi => null;
        public nint DeviceResult { get; set; } = 101;
        public nint ContextResult { get; set; } = 202;
        public uint? ConfigureFailureSource { get; set; }
        public uint? DeleteFailureSource { get; set; }
        public bool OutputLimiterSupported { get; set; } = true;
        public bool OutputLimiterAnswers { get; set; } = true;
        public int[]? ContextAttributes { get; private set; }
        public int LimiterReads { get; private set; }
        public List<uint> GeneratedSources { get; } = [];
        public List<uint> Configured3D { get; } = [];
        public List<uint> DeletedSources { get; } = [];
        public int ClearCurrentCalls { get; private set; }
        public int DestroyContextCalls { get; private set; }
        public int CloseDeviceCalls { get; private set; }

        // A device that limits until it is told not to.
        private int _limiterState = 1;

        public nint OpenDevice() => DeviceResult;

        public bool SupportsOutputLimiterControl(nint device) => OutputLimiterSupported;

        public nint CreateContext(nint device, int[]? attributes)
        {
            ContextAttributes = attributes;
            if (attributes is not null)
            {
                for (int i = 0; i + 1 < attributes.Length; i += 2)
                {
                    if (attributes[i] == OpenAlContextAttributes.OutputLimiter)
                        _limiterState = attributes[i + 1];
                }
            }
            return ContextResult;
        }

        public int? ReadOutputLimiterState(nint device)
        {
            LimiterReads++;
            return OutputLimiterAnswers ? _limiterState : null;
        }

        public bool MakeContextCurrent(nint context)
        {
            if (context == 0)
                ClearCurrentCalls++;
            return true;
        }

        public uint GenerateSource()
        {
            uint source = _nextSource++;
            GeneratedSources.Add(source);
            return source;
        }

        public void Configure3DSource(uint source)
        {
            Configured3D.Add(source);
            ThrowIfConfiguredFailure(source);
        }

        public void DisableAlDistanceAttenuation() { }

        public void StopSource(uint source) { }

        public void DeleteSource(uint source)
        {
            if (DeleteFailureSource == source)
                throw new InvalidOperationException("synthetic delete failure");
            DeletedSources.Add(source);
        }

        public void DeleteBuffer(uint buffer) { }

        public void DestroyContext(nint context) => DestroyContextCalls++;

        public void CloseDevice(nint device) => CloseDeviceCalls++;

        private void ThrowIfConfiguredFailure(uint source)
        {
            if (ConfigureFailureSource == source)
                throw new InvalidOperationException("synthetic configure failure");
        }
    }
}
