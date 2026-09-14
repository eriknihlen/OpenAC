namespace AcDream.Automation;

public static class VtankProfilesDefault
{
    public static string Resolve(string dataDirectory) =>
        Path.Combine(dataDirectory, "vtank");
}
