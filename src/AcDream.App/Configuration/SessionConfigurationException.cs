namespace AcDream.App.Configuration;

internal sealed class SessionConfigurationException : Exception
{
    internal SessionConfigurationException(string message)
        : base(message)
    {
    }

    internal SessionConfigurationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
