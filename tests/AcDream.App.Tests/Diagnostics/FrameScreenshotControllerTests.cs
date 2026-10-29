using AcDream.App.Diagnostics;
using StbImageSharp;

namespace AcDream.App.Tests.Diagnostics;

/// <summary>
/// A screenshot is as large as what the reader captured. On a high-density
/// display the frame is measured in points and the backbuffer in pixels,
/// twice as many each way, and the capture is the backbuffer's.
/// </summary>
public sealed class FrameScreenshotControllerTests
{
    [Fact]
    public void TheScreenshotTakesTheCapturedSizeNotTheFramesPoints()
    {
        string directory = Path.Combine(Path.GetTempPath(), "acdream-shot-" + Guid.NewGuid().ToString("N"));
        try
        {
            (int, int)? asked = null;
            var controller = new FrameScreenshotController(
                (width, height) =>
                {
                    asked = (width, height);
                    return new FrameCapture(new byte[4 * 2 * 4], 4, 2);
                },
                directory);
            Assert.True(controller.TryRequest("retina", out string error), error);

            Assert.True(controller.CapturePending(2, 1));

            Assert.Equal((2, 1), asked);
            ImageResult image = ImageResult.FromMemory(
                File.ReadAllBytes(Path.Combine(directory, "retina.png")), ColorComponents.RedGreenBlueAlpha);
            Assert.Equal((4, 2), (image.Width, image.Height));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ACaptureWhosePixelsDoNotFillItsSizeFails()
    {
        string directory = Path.Combine(Path.GetTempPath(), "acdream-shot-" + Guid.NewGuid().ToString("N"));
        try
        {
            var controller = new FrameScreenshotController(
                (_, _) => new FrameCapture(new byte[4], 4, 2),
                directory);
            Assert.True(controller.TryRequest("short", out string error), error);

            Assert.False(controller.CapturePending(2, 1));
            Assert.False(controller.IsComplete("short"));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }
}
