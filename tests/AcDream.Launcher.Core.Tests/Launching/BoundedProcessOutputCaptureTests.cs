using System.Text;
using AcDream.Launcher.Core.Launching;

namespace AcDream.Launcher.Core.Tests.Launching;

public sealed class BoundedProcessOutputCaptureTests
{
    [Fact]
    public void AppendLineWritesEachLineWithATrailingNewline()
    {
        string path = TempPath();
        try
        {
            using var capture = new BoundedProcessOutputCapture(path);

            capture.AppendLine("first");
            capture.AppendLine("second");
            capture.Dispose();

            Assert.Equal("first\nsecond\n", File.ReadAllText(path));
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact]
    public void ANullLineFromTheEndOfStreamSentinelIsANoOp()
    {
        string path = TempPath();
        try
        {
            using var capture = new BoundedProcessOutputCapture(path);

            capture.AppendLine("kept");
            capture.AppendLine(null);
            capture.Dispose();

            Assert.Equal("kept\n", File.ReadAllText(path));
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact]
    public void WritesBeyondTheCapAreDroppedAndAOneTimeTruncationMarkerIsAppended()
    {
        string path = TempPath();
        try
        {
            using var capture = new BoundedProcessOutputCapture(path, maxBytes: 16);

            capture.AppendLine("0123456789"); // 11 bytes incl. newline
            capture.AppendLine("this line is dropped entirely");
            capture.AppendLine("so is this one");

            Assert.True(capture.IsDone);
            string written = File.ReadAllText(path);
            Assert.StartsWith("0123456789\n", written, StringComparison.Ordinal);
            Assert.Contains("truncated at 16 bytes", written, StringComparison.Ordinal);
            Assert.True(
                written.Length < 200,
                $"expected a small bounded file, got {written.Length} bytes");
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact]
    public void ALineWhoseTextExactlyExhaustsTheCap_DropsOnlyTheTrailingNewline()
    {
        string path = TempPath();
        try
        {
            using var capture = new BoundedProcessOutputCapture(path, maxBytes: 10);

            capture.AppendLine("0123456789");

            Assert.True(capture.IsDone);
            string written = File.ReadAllText(path);
            Assert.StartsWith("0123456789", written, StringComparison.Ordinal);
            Assert.Contains("truncated at 10 bytes", written, StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact]
    public void ALogSpammingChildCannotGrowTheFileUnboundedly()
    {
        string path = TempPath();
        try
        {
            using var capture = new BoundedProcessOutputCapture(
                path,
                maxBytes: BoundedProcessOutputCapture.DefaultMaxBytes);

            // Far more than the 2 MiB default cap.
            string spamLine = new('x', 4096);
            for (int i = 0; i < 4096; i++)
            {
                capture.AppendLine(spamLine);
                if (capture.IsDone)
                {
                    break;
                }
            }

            Assert.True(capture.IsDone);
            long fileLength = new FileInfo(path).Length;
            Assert.True(
                fileLength < BoundedProcessOutputCapture.DefaultMaxBytes + 256,
                $"expected the file to stay near the {BoundedProcessOutputCapture.DefaultMaxBytes}-byte "
                    + $"cap, got {fileLength} bytes");
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact]
    public void AppendCreatesTheSessionDirectoryOnFirstWrite()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "acdream-406-capture-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "client.err.log");
        Assert.False(Directory.Exists(directory));

        try
        {
            using var capture = new BoundedProcessOutputCapture(path);
            capture.AppendLine("hello");
            capture.Dispose();

            Assert.True(File.Exists(path));
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public void RawByteAppendsAreConcatenatedWithoutAnImpliedLineBoundary()
    {
        string path = TempPath();
        try
        {
            using var capture = new BoundedProcessOutputCapture(path);

            capture.Append(Encoding.UTF8.GetBytes("abc"));
            capture.Append(Encoding.UTF8.GetBytes("def"));
            capture.Dispose();

            Assert.Equal("abcdef", File.ReadAllText(path));
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact]
    public void EmptyAppendsAreNoOps()
    {
        string path = TempPath();
        try
        {
            using var capture = new BoundedProcessOutputCapture(path);

            capture.Append(ReadOnlySpan<byte>.Empty);
            capture.AppendLine(string.Empty);
            capture.Dispose();

            // An empty string line still gets its trailing newline —
            // only a genuinely zero-length byte span (or a null line) is
            // a true no-op.
            Assert.Equal("\n", File.ReadAllText(path));
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact]
    public void AppendAfterDisposeIsASilentNoOp()
    {
        string path = TempPath();
        try
        {
            var capture = new BoundedProcessOutputCapture(path);
            capture.AppendLine("before");
            capture.Dispose();

            capture.AppendLine("after — must not throw or reopen the file");

            Assert.Equal("before\n", File.ReadAllText(path));
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact]
    public void ConstructorRejectsANonPositiveMaxBytes()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new BoundedProcessOutputCapture(TempPath(), maxBytes: 0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new BoundedProcessOutputCapture(TempPath(), maxBytes: -1));
    }

    [Fact]
    public void ConstructorRejectsANullOrBlankPath()
    {
        Assert.Throws<ArgumentException>(() => new BoundedProcessOutputCapture(""));
        Assert.Throws<ArgumentException>(() => new BoundedProcessOutputCapture("   "));
    }

    private static string TempPath() => Path.Combine(
        Path.GetTempPath(),
        "acdream-406-capture-" + Guid.NewGuid().ToString("N") + ".log");

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }
}
