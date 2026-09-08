namespace AcDream.Launcher.Core.Profiles;

public sealed class LauncherProfileException : Exception
{
    public LauncherProfileException(string message)
        : base(message)
    {
    }

    public LauncherProfileException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
