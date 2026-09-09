using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

using System.Text.Json;
using AcDream.App.Rendering.Packs;

namespace AcDream.App.Diagnostics;

internal sealed class FrameScreenshotController
{
    private enum CaptureState
    {
        Pending,
        Complete,
        Failed,
    }

    private sealed record CaptureStatus(CaptureState State, string? Error = null);

    private readonly Func<int, int, byte[]> _readRgba;
    private readonly string _directory;
    private readonly Action<string> _log;
    private readonly Func<RenderPackDiagnosticsSnapshot>? _renderPackMetadata;
    private readonly Queue<string> _pending = new();
    private readonly Dictionary<string, CaptureStatus> _status =
        new(StringComparer.OrdinalIgnoreCase);

    internal FrameScreenshotController(
        Func<int, int, byte[]> readRgba,
        string directory,
        Action<string>? log = null,
        Func<RenderPackDiagnosticsSnapshot>? renderPackMetadata = null)
    {
        _readRgba = readRgba ?? throw new ArgumentNullException(nameof(readRgba));
        _directory = string.IsNullOrWhiteSpace(directory)
            ? throw new ArgumentException("A screenshot directory is required.", nameof(directory))
            : Path.GetFullPath(directory);
        _log = log ?? (_ => { });
        _renderPackMetadata = renderPackMetadata;
    }

    public bool TryRequest(string name, out string error)
    {
        if (!AutomationArtifactName.TryValidate(name, out error))
            return false;

        if (_status.TryGetValue(name, out CaptureStatus? status))
        {
            if (status.State != CaptureState.Failed)
                return true;
            error = status.Error ?? $"screenshot '{name}' failed";
            return false;
        }

        _status.Add(name, new CaptureStatus(CaptureState.Pending));
        _pending.Enqueue(name);
        _log($"[world-gate] screenshot-request name={name}");
        return true;
    }

    public bool TryRequestRetailScreenshot(out string path, out string error)
    {
        for (int index = 0; index < 100_000; index++)
        {
            string name = $"ScreenShot{index:D5}";
            string candidate = Path.Combine(_directory, name + ".png");
            if (File.Exists(candidate) || _status.ContainsKey(name))
                continue;

            if (TryRequest(name, out error))
            {
                path = candidate;
                return true;
            }

            path = string.Empty;
            return false;
        }

        path = string.Empty;
        error = "all retail screenshot names ScreenShot00000 through ScreenShot99999 are in use";
        return false;
    }

    public bool IsComplete(string name) =>
        _status.TryGetValue(name, out CaptureStatus? status)
        && status.State == CaptureState.Complete;

    public bool CapturePending(int width, int height)
    {
        if (_pending.Count == 0)
            return false;

        string name = _pending.Dequeue();
        try
        {
            if (width <= 0 || height <= 0)
                throw new InvalidOperationException($"invalid framebuffer size {width}x{height}");

            byte[] pixels = _readRgba(width, height);
            int expected = checked(width * height * 4);
            if (pixels.Length != expected)
                throw new InvalidOperationException(
                    $"framebuffer read returned {pixels.Length} bytes; expected {expected}");

            byte[] flipped = FlipRows(pixels, width, height);
            Directory.CreateDirectory(_directory);
            string path = Path.Combine(_directory, name + ".png");
            string temporaryPath = path + ".tmp";
            using (Image<Rgba32> image = Image.LoadPixelData<Rgba32>(flipped, width, height))
                image.SaveAsPng(temporaryPath);

            string? metadataPath = null;
            string? temporaryMetadataPath = null;
            if (_renderPackMetadata is not null)
            {
                metadataPath = Path.Combine(_directory, name + ".metadata.json");
                temporaryMetadataPath = metadataPath + ".tmp";
                var metadata = new FrameScreenshotMetadata(
                    SchemaVersion: 1,
                    Width: width,
                    Height: height,
                    RenderPack: _renderPackMetadata());
                File.WriteAllBytes(
                    temporaryMetadataPath,
                    JsonSerializer.SerializeToUtf8Bytes(
                        metadata,
                        new JsonSerializerOptions { WriteIndented = true }));
            }

            if (metadataPath is not null && temporaryMetadataPath is not null)
                File.Move(temporaryMetadataPath, metadataPath, overwrite: true);
            File.Move(temporaryPath, path, overwrite: true);

            _status[name] = new CaptureStatus(CaptureState.Complete);
            _log($"[world-gate] screenshot-complete name={name} path={path} size={width}x{height}");
            return true;
        }
        catch (Exception exception)
        {
            TryDelete(Path.Combine(_directory, name + ".png.tmp"));
            TryDelete(Path.Combine(_directory, name + ".metadata.json.tmp"));
            TryDelete(Path.Combine(_directory, name + ".metadata.json"));
            string message = $"screenshot '{name}' failed: {exception.Message}";
            _status[name] = new CaptureStatus(CaptureState.Failed, message);
            _log($"[world-gate] screenshot-failed name={name} error={exception.Message}");
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception error) when (error is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException)
        {
        }
    }

    internal static byte[] FlipRows(byte[] pixels, int width, int height)
    {
        int stride = checked(width * 4);
        byte[] flipped = new byte[pixels.Length];
        for (int row = 0; row < height; row++)
        {
            System.Buffer.BlockCopy(
                pixels,
                row * stride,
                flipped,
                (height - 1 - row) * stride,
                stride);
        }
        return flipped;
    }

    internal interface IDefaultFramebufferSurface
    {
        uint ReadFramebufferBinding { get; }

        uint DrawFramebufferBinding { get; }

        /// <summary>
        /// <c>GL_SAMPLES</c> for the default framebuffer. Queried with
        /// framebuffer 0 bound to both targets, because the value is
        /// framebuffer-dependent state and would otherwise report whichever
        /// offscreen target the frame left bound.
        /// </summary>
        int DefaultFramebufferSamples { get; }

        void BindReadFramebuffer(uint framebuffer);

        void BindDrawFramebuffer(uint framebuffer);

        uint CreateResolveTarget(int width, int height);

        void DeleteResolveTarget(uint framebuffer);

        void BlitColorNearest(int width, int height);

        void ReadRgba(int width, int height, byte[] destination);
    }

    internal static byte[] ReadDefaultFramebuffer(
        IDefaultFramebufferSurface surface,
        int width,
        int height)
    {
        ArgumentNullException.ThrowIfNull(surface);
        byte[] pixels = new byte[checked(width * height * 4)];
        uint previousRead = surface.ReadFramebufferBinding;
        uint previousDraw = surface.DrawFramebufferBinding;
        surface.BindReadFramebuffer(0u);
        surface.BindDrawFramebuffer(0u);
        try
        {
            if (surface.DefaultFramebufferSamples > 1)
                ResolveThenRead(surface, width, height, pixels);
            else
                surface.ReadRgba(width, height, pixels);
        }
        finally
        {
            surface.BindReadFramebuffer(previousRead);
            surface.BindDrawFramebuffer(previousDraw);
        }
        return pixels;
    }

    private static void ResolveThenRead(
        IDefaultFramebufferSurface surface,
        int width,
        int height,
        byte[] pixels)
    {
        uint resolve = surface.CreateResolveTarget(width, height);
        try
        {
            // Read is still framebuffer 0 â€” the multisampled source.
            surface.BindDrawFramebuffer(resolve);
            surface.BlitColorNearest(width, height);
            surface.BindReadFramebuffer(resolve);
            surface.ReadRgba(width, height, pixels);
        }
        finally
        {
            surface.DeleteResolveTarget(resolve);
        }
    }

}

internal sealed record FrameScreenshotMetadata(
    int SchemaVersion,
    int Width,
    int Height,
    RenderPackDiagnosticsSnapshot RenderPack);
