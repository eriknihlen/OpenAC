namespace AcDream.App.Plugins;

internal static class VtankProfilesDefault
{
    internal static string Resolve(string dataDirectory) =>
        Path.Combine(dataDirectory, "vtank");
}
