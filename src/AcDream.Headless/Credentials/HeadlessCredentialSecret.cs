using System.Security.Cryptography;

namespace AcDream.Headless.Credentials;

internal sealed class HeadlessCredentialSecret : IDisposable
{
    private char[]? _buffer;

    internal HeadlessCredentialSecret(
        string referenceId,
        ReadOnlySpan<char> value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(referenceId);
        if (value.IsEmpty)
        {
            throw new HeadlessCredentialException(
                $"Credential '{referenceId}' resolved to an empty secret.");
        }

        ReferenceId = referenceId;
        _buffer = value.ToArray();
    }

    internal string ReferenceId { get; }
    internal bool IsDisposed => _buffer is null;

    internal string Reveal()
    {
        ObjectDisposedException.ThrowIf(_buffer is null, this);
        return new string(_buffer);
    }

    public void Dispose()
    {
        char[]? buffer = Interlocked.Exchange(ref _buffer, null);
        if (buffer is null)
            return;
        CryptographicOperations.ZeroMemory(
            System.Runtime.InteropServices.MemoryMarshal.AsBytes(
                buffer.AsSpan()));
    }

    public override string ToString() =>
        $"[redacted:{ReferenceId}]";
}
