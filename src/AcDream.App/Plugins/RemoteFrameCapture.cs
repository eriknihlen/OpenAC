using System.Collections.Concurrent;
using AcDream.App.Rendering.Gpu;
using AcDream.DrakBot.Remote;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace AcDream.App.Plugins;

/// <summary>
/// The phone's live view of this client: DrakBot Remote asks for frames
/// from its request threads, and this hands them the frame the renderer
/// just presented, as JPEG. The GPU keeps a host-readable copy of each
/// presented frame only while someone is watching (retention is switched
/// on at the first request and off five seconds after the last), so an
/// idle client pays nothing. Capture happens on the frame thread right
/// after a frame closes; the resize and JPEG encode run on the pool.
/// Requests that arrive between two frames share one capture.
/// </summary>
internal sealed class RemoteFrameCapture : IRemoteFrameSource
{
    /// <summary>How long after the last request the GPU copy is kept alive.</summary>
    public const int IdleReleaseMs = 5000;

    private readonly record struct Request(int Quality, int MaxWidth, TaskCompletionSource<byte[]?> Result);

    private readonly ConcurrentQueue<Request> _requests = new();
    private readonly Action<string> _log;
    private IGpuDevice? _device;
    private volatile bool _minimized;
    private volatile bool _available;
    private bool _retained;
    private bool _loggedFailure;
    private long _lastRequestTicks;

    public RemoteFrameCapture(Action<string>? log = null)
    {
        _log = log ?? (_ => { });
    }

    public bool IsAvailable => _available;

    public bool IsMinimized => _minimized;

    /// <summary>The frame thread hands over the device once graphics exist.</summary>
    public void Bind(IGpuDevice device)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _available = true;
    }

    /// <summary>The frame thread's word on the window, each frame.</summary>
    public void NoteWindowState(bool minimized) => _minimized = minimized;

    public Task<byte[]?> CaptureJpegAsync(int quality, int maxWidth, CancellationToken cancellation)
    {
        if (!_available || _minimized)
            return Task.FromResult<byte[]?>(null);
        var result = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _requests.Enqueue(new Request(Math.Clamp(quality, 1, 100), Math.Max(0, maxWidth), result));
        Volatile.Write(ref _lastRequestTicks, Environment.TickCount64);
        if (cancellation.CanBeCanceled)
            cancellation.Register(static state => ((TaskCompletionSource<byte[]?>)state!).TrySetCanceled(), result);
        return result.Task;
    }

    /// <summary>
    /// Frame thread, after a frame of <paramref name="width"/> by
    /// <paramref name="height"/> was presented: serves what is waiting, or
    /// lets the GPU copy go once nobody has asked for a while.
    /// </summary>
    public void OnFrame(int width, int height)
    {
        IGpuDevice? device = _device;
        if (device is null)
            return;
        if (_requests.IsEmpty)
        {
            if (_retained && Environment.TickCount64 - Volatile.Read(ref _lastRequestTicks) > IdleReleaseMs)
            {
                device.RetainBackbufferCapture(false);
                _retained = false;
                _log("[remote] live view idle; backbuffer capture released");
            }
            return;
        }
        if (!_retained)
        {
            // The copy is recorded with the next frame; the waiting requests are served after it.
            device.RetainBackbufferCapture(true);
            _retained = true;
            _log("[remote] live view requested; backbuffer capture retained");
            return;
        }
        if (!device.HasRetainedCapture || width <= 0 || height <= 0)
            return;

        byte[] rgba;
        try
        {
            rgba = device.CaptureBackbuffer(width, height);
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or NotSupportedException)
        {
            if (!_loggedFailure)
            {
                _loggedFailure = true;
                _log($"[remote] backbuffer capture failed: {error.Message}");
            }
            while (_requests.TryDequeue(out Request request))
                request.Result.TrySetResult(null);
            return;
        }

        var batch = new List<Request>();
        while (_requests.TryDequeue(out Request request))
            batch.Add(request);
        ThreadPool.UnsafeQueueUserWorkItem(_ => Encode(rgba, width, height, batch), null);
    }

    /// <summary>Pool thread: one image, one JPEG per distinct quality and width, every waiter answered.</summary>
    private static void Encode(byte[] rgba, int width, int height, List<Request> batch)
    {
        try
        {
            // The capture is the presented frame as is, top row first.
            using var image = new Image<Rgba32>(width, height);
            int stride = width * 4;
            image.ProcessPixelRows(accessor =>
            {
                for (int y = 0; y < height; y++)
                {
                    Span<byte> row = System.Runtime.InteropServices.MemoryMarshal.AsBytes(accessor.GetRowSpan(y));
                    rgba.AsSpan(y * stride, stride).CopyTo(row);
                }
            });
            var encoded = new Dictionary<(int Quality, int Width), byte[]>();
            foreach (Request request in batch)
            {
                int targetWidth = request.MaxWidth > 0 && request.MaxWidth < width ? request.MaxWidth : width;
                (int Quality, int Width) key = (request.Quality, targetWidth);
                if (!encoded.TryGetValue(key, out byte[]? jpeg))
                {
                    var encoder = new JpegEncoder { Quality = request.Quality };
                    using var output = new MemoryStream();
                    if (targetWidth == width)
                    {
                        image.SaveAsJpeg(output, encoder);
                    }
                    else
                    {
                        using Image<Rgba32> scaled = image.Clone(context => context.Resize(targetWidth, 0));
                        scaled.SaveAsJpeg(output, encoder);
                    }
                    jpeg = output.ToArray();
                    encoded[key] = jpeg;
                }
                request.Result.TrySetResult(jpeg);
            }
        }
        catch (Exception)
        {
            foreach (Request request in batch)
                request.Result.TrySetResult(null);
        }
    }
}
