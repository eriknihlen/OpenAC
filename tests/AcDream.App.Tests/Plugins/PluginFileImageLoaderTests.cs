using System.Collections.Generic;
using AcDream.App.Plugins;
using AcDream.Tests.Fixtures.PluginIcons;

namespace AcDream.App.Tests.Plugins;

/// <summary>
/// Plugin markup's image files are read, decoded and mipmapped away from the
/// interface thread, once however many windows ask while the read is under
/// way, and every refusal comes back as a reason rather than an exception.
/// </summary>
public sealed class PluginFileImageLoaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"acdream-file-images-{Guid.NewGuid():N}");
    private readonly List<string> _reports = [];

    public PluginFileImageLoaderTests() => Directory.CreateDirectory(Path.Combine(_root, "icons"));

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void Write(string relative, byte[] bytes) =>
        File.WriteAllBytes(Path.Combine(_root, relative), bytes);

    /// <summary>Runs each read only when the test says so, and counts them.</summary>
    private sealed class HeldReads
    {
        private readonly List<(Func<PluginFileImageResult> Read, TaskCompletionSource<PluginFileImageResult> Done)> _held = [];

        internal int Started => _held.Count;

        internal Task<PluginFileImageResult> Run(Func<PluginFileImageResult> read)
        {
            var done = new TaskCompletionSource<PluginFileImageResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            _held.Add((read, done));
            return done.Task;
        }

        internal void FinishAll()
        {
            foreach ((Func<PluginFileImageResult> read, TaskCompletionSource<PluginFileImageResult> done) in _held)
                done.TrySetResult(read());
        }
    }

    private static PluginFileImageResult Wait(Task<PluginFileImageResult> task) =>
        task.Wait(TimeSpan.FromSeconds(10)) ? task.Result : throw new TimeoutException();

    [Fact]
    public void AFileIsReadOnAWorkerNotOnTheThreadThatAsks()
    {
        Write("icons/sword.png", PngTestData.Sized(24, 16));
        int asking = Environment.CurrentManagedThreadId;
        int reading = asking;
        var loader = new PluginFileImageLoader(_reports.Add, read => Task.Run(() =>
        {
            reading = Environment.CurrentManagedThreadId;
            return read();
        }));

        PluginFileImageResult result = Wait(loader.Load(_root, "icons/sword.png"));

        Assert.NotEqual(asking, reading);
        Assert.NotNull(result.Image);
        Assert.Equal((24, 16), (result.Image.MipLevels[0].Width, result.Image.MipLevels[0].Height));
    }

    [Fact]
    public void TheDefaultLoaderDoesNotReadOnTheCallersThread()
    {
        Write("icons/sword.png", PngTestData.Sized(8, 8));
        var loader = new PluginFileImageLoader(_reports.Add);

        PluginFileImageResult result = Wait(loader.Load(_root, "icons/sword.png"));

        Assert.NotNull(result.Image);
    }

    [Fact]
    public void AnImageComesWithEverySmallerLevel()
    {
        Write("icons/sword.png", PngTestData.Sized(8, 4));
        var loader = new PluginFileImageLoader(_reports.Add, read => Task.FromResult(read()));

        PluginFileImage image = Wait(loader.Load(_root, "icons/sword.png")).Image!;

        Assert.Equal([(8, 4), (4, 2), (2, 1), (1, 1)], image.MipLevels.Select(level => (level.Width, level.Height)));
        Assert.Equal("icons/sword.png", image.Path);
    }

    [Fact]
    public void WindowsAskingWhileAFileIsReadShareTheOneRead()
    {
        Write("icons/sword.png", PngTestData.Sized(8, 8));
        var reads = new HeldReads();
        var loader = new PluginFileImageLoader(_reports.Add, reads.Run);

        Task<PluginFileImageResult> first = loader.Load(_root, "icons/sword.png");
        Task<PluginFileImageResult> second = loader.Load(_root, @"icons\sword.png");
        reads.FinishAll();

        Assert.Same(first, second);
        Assert.Equal(1, reads.Started);
        Assert.NotNull(Wait(first).Image);
    }

    [Fact]
    public void AFileAskedForAgainAfterItsReadIsReadAgain()
    {
        Write("icons/sword.png", PngTestData.Sized(8, 8));
        var reads = new HeldReads();
        var loader = new PluginFileImageLoader(_reports.Add, reads.Run);
        Task<PluginFileImageResult> first = loader.Load(_root, "icons/sword.png");
        reads.FinishAll();
        Wait(first);

        Task<PluginFileImageResult> again = loader.Load(_root, "icons/sword.png");

        Assert.NotSame(first, again);
        Assert.Equal(2, reads.Started);
    }

    [Fact]
    public void AJpegIsRead()
    {
        Write("icons/red.jpg", JpegTestData.Red());
        var loader = new PluginFileImageLoader(_reports.Add, read => Task.FromResult(read()));

        PluginFileImage image = Wait(loader.Load(_root, "icons/red.jpg")).Image!;

        Assert.Equal((JpegTestData.Width, JpegTestData.Height), (image.MipLevels[0].Width, image.MipLevels[0].Height));
    }

    [Fact]
    public void AChangedFileComesBackAsANewVersion()
    {
        Write("icons/sword.png", PngTestData.Sized(8, 8));
        var loader = new PluginFileImageLoader(_reports.Add, read => Task.FromResult(read()));
        PluginFileImage old = Wait(loader.Load(_root, "icons/sword.png")).Image!;

        Write("icons/sword.png", PngTestData.Sized(16, 16));
        File.SetLastWriteTimeUtc(Path.Combine(_root, "icons/sword.png"), DateTime.UtcNow.AddMinutes(1));
        PluginFileImage fresh = Wait(loader.Load(_root, "icons/sword.png")).Image!;

        Assert.NotEqual(old.Version, fresh.Version);
        Assert.Equal(16, fresh.MipLevels[0].Width);
    }

    [Fact]
    public void AFileThatCannotBeUsedComesBackWithItsReason()
    {
        Write("icons/fake.png", "not an image"u8.ToArray());
        var loader = new PluginFileImageLoader(_reports.Add, read => Task.FromResult(read()));

        PluginFileImageResult missing = Wait(loader.Load(_root, "icons/missing.png"));
        PluginFileImageResult fake = Wait(loader.Load(_root, "icons/fake.png"));
        PluginFileImageResult outside = Wait(loader.Load(_root, "../secret.png"));

        Assert.Null(missing.Image);
        Assert.Contains("does not exist", missing.Reason, StringComparison.Ordinal);
        Assert.Contains("not a PNG or JPEG", fake.Reason, StringComparison.Ordinal);
        Assert.Contains("outside the plugin folder", outside.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AReasonIsReportedOncePerFileAndReason()
    {
        var loader = new PluginFileImageLoader(_reports.Add);

        loader.Report("acme.hello", "icons/missing.png", "does not exist");
        loader.Report("acme.hello", @"icons\missing.png", "does not exist");
        loader.Report("acme.hello", "icons/missing.png", "could not be read");

        Assert.Equal(2, _reports.Count);
        Assert.Contains("acme.hello/icons/missing.png", _reports[0], StringComparison.Ordinal);
        Assert.Contains("does not exist", _reports[0], StringComparison.Ordinal);
    }
}
