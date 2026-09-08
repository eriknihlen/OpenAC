using Avalonia;
using Avalonia.Headless;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(AcDream.Launcher.Tests.TestAppBuilder))]
[assembly: AvaloniaTestIsolation(AvaloniaTestIsolationLevel.PerAssembly)]

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace AcDream.Launcher.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
