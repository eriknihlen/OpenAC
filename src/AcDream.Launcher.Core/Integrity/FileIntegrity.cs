using System.Security.Cryptography;

namespace AcDream.Launcher.Core.Integrity;

public static class FileIntegrity
{
    public static string ComputeSha256Hex(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        using FileStream stream = new(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        byte[] hash = SHA256.HashData(stream);
        return Convert.ToHexStringLower(hash);
    }

    public static async Task<string> ComputeSha256HexAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        await using FileStream stream = new(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 1024,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken)
            .ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    public static bool Matches(string actualHex, string expectedHex)
    {
        ArgumentNullException.ThrowIfNull(actualHex);
        ArgumentNullException.ThrowIfNull(expectedHex);
        return string.Equals(actualHex, expectedHex, StringComparison.OrdinalIgnoreCase);
    }

    public static bool Verify(string filePath, string expectedHex) =>
        Matches(ComputeSha256Hex(filePath), expectedHex);
}
