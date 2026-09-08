namespace AcDream.Headless.Configuration;

internal sealed class HeadlessCommandLineException : Exception
{
    internal HeadlessCommandLineException(string message)
        : base(message)
    {
    }
}
