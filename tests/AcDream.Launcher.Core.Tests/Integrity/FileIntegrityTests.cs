using System.Security.Cryptography;
using System.Text;
using AcDream.Launcher.Core.Integrity;

namespace AcDream.Launcher.Core.Tests.Integrity;

public sealed class FileIntegrityTests : IDisposable
{
    private readonly string _root;

    public FileIntegrityTests()
    {
        _root = Path.Combine(
            Path.GetTempPath(),
            "acdream-launcher-integrity-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void ComputeSha256HexMatchesTheFrameworkHasher()
    {
        string path = Path.Combine(_root, "file.bin");
        byte[] content = Encoding.UTF8.GetBytes("acdream launcher integrity fixture");
        File.WriteAllBytes(path, content);
        string expected = Convert.ToHexStringLower(SHA256.HashData(content));

        string actual = FileIntegrity.ComputeSha256Hex(path);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task ComputeSha256HexAsyncMatchesTheSyncResult()
    {
        string path = Path.Combine(_root, "file.bin");
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes("async path fixture"));

        string sync = FileIntegrity.ComputeSha256Hex(path);
        string asyncResult = await FileIntegrity.ComputeSha256HexAsync(path);

        Assert.Equal(sync, asyncResult);
    }

    [Fact]
    public void VerifySucceedsForAMatchingDigestRegardlessOfCase()
    {
        string path = Path.Combine(_root, "file.bin");
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes("case-insensitive fixture"));
        string lower = FileIntegrity.ComputeSha256Hex(path);

        Assert.True(FileIntegrity.Verify(path, lower));
        Assert.True(FileIntegrity.Verify(path, lower.ToUpperInvariant()));
    }

    [Fact]
    public void VerifyFailsForAMismatchedDigest()
    {
        string path = Path.Combine(_root, "file.bin");
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes("original content"));

        Assert.False(FileIntegrity.Verify(path, new string('0', 64)));
    }

    [Fact]
    public void DifferentContentProducesDifferentDigests()
    {
        string pathA = Path.Combine(_root, "a.bin");
        string pathB = Path.Combine(_root, "b.bin");
        File.WriteAllBytes(pathA, Encoding.UTF8.GetBytes("content A"));
        File.WriteAllBytes(pathB, Encoding.UTF8.GetBytes("content B"));

        Assert.NotEqual(
            FileIntegrity.ComputeSha256Hex(pathA),
            FileIntegrity.ComputeSha256Hex(pathB));
    }

    [Fact]
    public void EmptyFileHashesToTheWellKnownSha256OfEmptyInput()
    {
        string path = Path.Combine(_root, "empty.bin");
        File.WriteAllBytes(path, []);

        string actual = FileIntegrity.ComputeSha256Hex(path);

        Assert.Equal(
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            actual);
    }
}
