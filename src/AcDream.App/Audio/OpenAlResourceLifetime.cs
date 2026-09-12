using AcDream.App.Rendering;
using Silk.NET.OpenAL;

namespace AcDream.App.Audio;

internal interface IOpenAlResourceApi
{
    AL? AudioApi { get; }
    ALContext? ContextApi { get; }

    nint OpenDevice();

    /// <summary>True when this device lets a context turn its output limiter off.</summary>
    bool SupportsOutputLimiterControl(nint device);

    nint CreateContext(nint device, int[]? attributes);

    /// <summary>
    /// The device's current output-limiter setting, or null when the device does
    /// not answer the question.
    /// </summary>
    int? ReadOutputLimiterState(nint device);

    bool MakeContextCurrent(nint context);
    uint GenerateSource();
    void Configure3DSource(uint source);
    void DisableAlDistanceAttenuation();
    void StopSource(uint source);
    void DeleteSource(uint source);
    void DeleteBuffer(uint buffer);
    void DestroyContext(nint context);
    void CloseDevice(nint device);
}

internal interface IOpenAlResourceApiFactory
{
    IOpenAlResourceApi Create();
}

internal sealed class SilkOpenAlResourceApiFactory : IOpenAlResourceApiFactory
{
    public IOpenAlResourceApi Create() => new SilkOpenAlResourceApi();
}

internal sealed unsafe class SilkOpenAlResourceApi : IOpenAlResourceApi
{
    public SilkOpenAlResourceApi()
    {
        ContextApi = ALContext.GetApi(soft: true);
        AudioApi = AL.GetApi(soft: true);
    }

    public AL AudioApi { get; }
    public ALContext ContextApi { get; }

    public nint OpenDevice() => (nint)ContextApi.OpenDevice(string.Empty);

    public bool SupportsOutputLimiterControl(nint device) =>
        ContextApi.IsExtensionPresent(
            (Device*)device,
            OpenAlContextAttributes.OutputLimiterExtension);

    public nint CreateContext(nint device, int[]? attributes)
    {
        if (attributes is null)
            return (nint)ContextApi.CreateContext((Device*)device, null);

        fixed (int* pinned = attributes)
            return (nint)ContextApi.CreateContext((Device*)device, pinned);
    }

    public int? ReadOutputLimiterState(nint device)
    {
        int value = OpenAlContextAttributes.Unanswered;
        ContextApi.GetError((Device*)device);   // clear anything already pending
        ContextApi.GetContextProperty(
            (Device*)device,
            (GetContextInteger)OpenAlContextAttributes.OutputLimiter,
            1,
            &value);

        ContextError error = ContextApi.GetError((Device*)device);
        int? state = OpenAlContextAttributes.ReadLimiterState(
            value,
            errored: error != ContextError.NoError);
        if (state is null)
        {
            // Say exactly what the device left behind, so a driver that wrote
            // nothing can be told apart from one that objected. Silent on a
            // device that answers, which is every device that supports this.
            Console.WriteLine(FormattableString.Invariant(
                $"[audio] output limiter read answered nothing: raw 0x{value:X8}, error 0x{(int)error:X4}"));
        }

        return state;
    }

    public bool MakeContextCurrent(nint context) =>
        ContextApi.MakeContextCurrent((Context*)context);

    public uint GenerateSource() => AudioApi.GenSource();

    public void Configure3DSource(uint source)
    {
        AudioApi.SetSourceProperty(source, SourceFloat.Gain, 1f);
        AudioApi.SetSourceProperty(source, SourceFloat.RolloffFactor, 0f);
        AudioApi.SetSourceProperty(source, SourceBoolean.SourceRelative, true);
        AudioApi.SetSourceProperty(source, SourceBoolean.Looping, false);
    }

    public void DisableAlDistanceAttenuation() =>
        AudioApi.DistanceModel(DistanceModel.None);

    public void StopSource(uint source) => AudioApi.SourceStop(source);

    public void DeleteSource(uint source) => AudioApi.DeleteSource(source);

    public void DeleteBuffer(uint buffer) => AudioApi.DeleteBuffer(buffer);

    public void DestroyContext(nint context) =>
        ContextApi.DestroyContext((Context*)context);

    public void CloseDevice(nint device) =>
        ContextApi.CloseDevice((Device*)device);
}

internal sealed class OpenAlResourceLifetime : IRetryableResourceCleanup
{
    private sealed class SourceState(uint id)
    {
        public uint Id { get; } = id;
        public bool Released { get; set; }
    }

    private sealed class BufferState(uint id)
    {
        public uint Id { get; } = id;
        public bool Released { get; set; }
    }

    private readonly IOpenAlResourceApi _api;
    private readonly List<SourceState> _sources = [];
    private readonly List<BufferState> _buffers = [];
    private nint _device;
    private nint _context;
    private bool _contextCurrent;
    private bool _cleanupActive;

    public OpenAlResourceLifetime(IOpenAlResourceApi api)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
    }

    public nint Device => _device;
    public nint Context => _context;

    /// <summary>Whether this device let us ask for the output limiter at all.</summary>
    public bool OutputLimiterControllable { get; private set; }

    /// <summary>
    /// What the device reports its limiter doing once the context is live, or
    /// null when it answered nothing. There is deliberately no reading from
    /// before the context: the setting does not exist until a context is
    /// created, so an earlier read can only ever say "off" and would prove
    /// nothing.
    /// </summary>
    public int? OutputLimiterReported { get; private set; }

    /// <summary>The one line saying what we asked for and what the device says.</summary>
    public string DescribeOutputLimiter() =>
        OpenAlContextAttributes.Describe(OutputLimiterControllable, OutputLimiterReported);

    public bool IsCleanupComplete =>
        _sources.All(static source => source.Released)
        && _buffers.All(static buffer => buffer.Released)
        && _context == 0
        && _device == 0;

    public bool TryOpenDevice()
    {
        if (_device != 0)
            throw new InvalidOperationException("The OpenAL device is already open.");
        _device = _api.OpenDevice();
        return _device != 0;
    }

    public bool TryCreateContext()
    {
        if (_device == 0)
            throw new InvalidOperationException("An OpenAL device is required before its context.");
        if (_context != 0)
            throw new InvalidOperationException("The OpenAL context already exists.");

        OutputLimiterControllable = _api.SupportsOutputLimiterControl(_device);

        _context = _api.CreateContext(
            _device,
            OpenAlContextAttributes.Build(OutputLimiterControllable));
        return _context != 0;
    }

    public bool TryMakeCurrent()
    {
        if (_context == 0)
            throw new InvalidOperationException("An OpenAL context is required before activation.");
        _contextCurrent = _api.MakeContextCurrent(_context);
        // The limiter setting only exists once a context is live, so this is the
        // first and only moment the question can be asked meaningfully.
        if (_contextCurrent && OutputLimiterControllable)
            OutputLimiterReported = _api.ReadOutputLimiterState(_device);
        return _contextCurrent;
    }

    public uint Create3DSource()
    {
        uint source = _api.GenerateSource();
        _sources.Add(new SourceState(source));
        _api.Configure3DSource(source);
        return source;
    }

    public void OwnBuffer(uint buffer)
    {
        if (buffer == 0)
            throw new ArgumentOutOfRangeException(nameof(buffer));
        if (_buffers.Any(existing => existing.Id == buffer && !existing.Released))
            throw new InvalidOperationException($"OpenAL buffer {buffer} is already owned.");
        _buffers.Add(new BufferState(buffer));
    }

    public void ReleaseBuffer(uint buffer)
    {
        BufferState state = _buffers.LastOrDefault(candidate =>
            candidate.Id == buffer && !candidate.Released)
            ?? throw new InvalidOperationException($"OpenAL buffer {buffer} is not owned.");
        _api.DeleteBuffer(buffer);
        state.Released = true;
    }

    public void RetryCleanup()
    {
        if (_cleanupActive || IsCleanupComplete)
            return;

        _cleanupActive = true;
        List<Exception>? failures = null;
        try
        {
            // Sources borrow buffers; retire all sources first. Each deletion
            // is still attempted even when SourceStop reports a driver error.
            for (int i = _sources.Count - 1; i >= 0; i--)
            {
                SourceState source = _sources[i];
                if (source.Released)
                    continue;

                Exception? stopFailure = null;
                try
                {
                    _api.StopSource(source.Id);
                }
                catch (Exception failure)
                {
                    stopFailure = failure;
                }

                try
                {
                    _api.DeleteSource(source.Id);
                    source.Released = true;
                }
                catch (Exception failure)
                {
                    (failures ??= []).Add(new AggregateException(
                        $"OpenAL source {source.Id} could not be released.",
                        stopFailure is null ? [failure] : [stopFailure, failure]));
                }
            }

            for (int i = _buffers.Count - 1; i >= 0; i--)
            {
                BufferState buffer = _buffers[i];
                if (buffer.Released)
                    continue;
                try
                {
                    _api.DeleteBuffer(buffer.Id);
                    buffer.Released = true;
                }
                catch (Exception failure)
                {
                    (failures ??= []).Add(new InvalidOperationException(
                        $"OpenAL buffer {buffer.Id} could not be released.",
                        failure));
                }
            }

            bool childrenReleased =
                _sources.All(static source => source.Released)
                && _buffers.All(static buffer => buffer.Released);
            if (childrenReleased && _context != 0)
            {
                if (_contextCurrent)
                {
                    try
                    {
                        if (!_api.MakeContextCurrent(0))
                            throw new InvalidOperationException(
                                "OpenAL rejected clearing the current context.");
                        _contextCurrent = false;
                    }
                    catch (Exception failure)
                    {
                        (failures ??= []).Add(new InvalidOperationException(
                            "The current OpenAL context could not be cleared.",
                            failure));
                    }
                }

                if (!_contextCurrent)
                {
                    try
                    {
                        _api.DestroyContext(_context);
                        _context = 0;
                    }
                    catch (Exception failure)
                    {
                        (failures ??= []).Add(new InvalidOperationException(
                            "The OpenAL context could not be destroyed.",
                            failure));
                    }
                }
            }

            if (_context == 0 && _device != 0)
            {
                try
                {
                    _api.CloseDevice(_device);
                    _device = 0;
                }
                catch (Exception failure)
                {
                    (failures ??= []).Add(new InvalidOperationException(
                        "The OpenAL device could not be closed.",
                        failure));
                }
            }
        }
        finally
        {
            _cleanupActive = false;
        }

        if (failures is not null)
            throw new AggregateException(
                "OpenAL native-resource cleanup remains incomplete.",
                failures);
    }
}

internal sealed class OpenAlInitializationException : AggregateException,
    IRetryableResourceCleanup
{
    private readonly OpenAlResourceLifetime _lifetime;

    public OpenAlInitializationException(
        Exception initializationFailure,
        OpenAlResourceLifetime lifetime,
        AggregateException cleanupFailure)
        : base(
            "OpenAL initialization failed and native-resource cleanup remains incomplete.",
            [initializationFailure, .. cleanupFailure.InnerExceptions])
    {
        _lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
    }

    public bool IsCleanupComplete => _lifetime.IsCleanupComplete;

    public void RetryCleanup() => _lifetime.RetryCleanup();
}
