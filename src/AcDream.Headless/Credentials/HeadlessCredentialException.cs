namespace AcDream.Headless.Credentials;

internal sealed class HeadlessCredentialException : Exception
{
    internal HeadlessCredentialException(string message)
        : base(message)
    {
    }

    internal HeadlessCredentialException(
        string message,
        Exception innerException)
        : base(message, innerException)
    {
    }
}
