using System.Globalization;
using System.Runtime.CompilerServices;

namespace AcDream.Tests;

internal static class TestCultureInitializer
{
    internal const string CultureVariable = "ACDREAM_TEST_CULTURE";

    [ModuleInitializer]
    internal static void Initialize()
    {
        string? requested = Environment.GetEnvironmentVariable(CultureVariable);
        if (string.IsNullOrWhiteSpace(requested))
        {
            return;
        }

        try
        {
            var culture = CultureInfo.GetCultureInfo(requested);
            CultureInfo.DefaultThreadCurrentCulture = culture;
            CultureInfo.DefaultThreadCurrentUICulture = culture;
        }
        catch (CultureNotFoundException)
        {
        }
    }
}
