namespace AcDream.DrakBot.Remote;

/// <summary>
/// Where the phone's live view comes from: the host's own presented frames,
/// as JPEG. A request may come from any thread; the host captures on its
/// frame thread and encodes wherever it likes. A frame is null when there
/// is nothing to show (no renderer yet, the window minimized).
/// </summary>
public interface IRemoteFrameSource
{
    /// <summary>Whether the host renders at all; a headless host does not.</summary>
    bool IsAvailable { get; }

    /// <summary>Whether the window is minimized, in which case nothing is presented to capture.</summary>
    bool IsMinimized { get; }

    /// <summary>
    /// The next presented frame as a JPEG at the given quality (1-100),
    /// scaled down to <paramref name="maxWidth"/> pixels wide when that is
    /// positive and smaller than the frame. Requests that arrive together
    /// share one capture.
    /// </summary>
    Task<byte[]?> CaptureJpegAsync(int quality, int maxWidth, CancellationToken cancellation);
}
