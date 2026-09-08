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
        Assert.Equal(20, api.GeneratedSources.Count);
        Assert.Equal(16, api.Configured3D.Count);
        Assert.Equal(4, api.ConfiguredUi.Count);

        engine.Dispose();
        engine.Dispose();

        Assert.True(engine.IsDisposalComplete);
        Assert.Equal(20, api.DeletedSources.Count);
        Assert.Equal(
            Enumerable.Range(1, 20).Reverse().Select(value => (uint)value),
            api.DeletedSources);
        Assert.Equal(1, api.ClearCurrentCalls);
        Assert.Equal(1, api.DestroyContextCalls);
        Assert.Equal(1, api.CloseDeviceCalls);
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
        public List<uint> GeneratedSources { get; } = [];
        public List<uint> Configured3D { get; } = [];
        public List<uint> ConfiguredUi { get; } = [];
        public List<uint> DeletedSources { get; } = [];
        public int ClearCurrentCalls { get; private set; }
        public int DestroyContextCalls { get; private set; }
        public int CloseDeviceCalls { get; private set; }

        public nint OpenDevice() => DeviceResult;

        public nint CreateContext(nint device) => ContextResult;

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

        public void ConfigureUiSource(uint source)
        {
            ConfiguredUi.Add(source);
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
