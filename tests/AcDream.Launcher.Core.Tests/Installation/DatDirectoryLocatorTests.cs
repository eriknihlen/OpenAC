using AcDream.Launcher.Core.Installation;

namespace AcDream.Launcher.Core.Tests.Installation;

public sealed class DatDirectoryLocatorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "acdream-dat-locator-tests",
        Guid.NewGuid().ToString("N"));

    public DatDirectoryLocatorTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void PortableValidationRequiresTheFourExactDatFileNames()
    {
        string directory = Path.Combine(_root, "retail");
        Directory.CreateDirectory(directory);
        foreach (string fileName in DatDirectoryLocator.RequiredFileNames.Take(3))
        {
            File.WriteAllText(Path.Combine(directory, fileName), "fixture");
        }

        var locator = new DatDirectoryLocator(isWindows: false);
        DatDirectoryValidation incomplete = locator.Validate(directory);

        Assert.False(incomplete.IsValid);
        Assert.Equal(["client_local_English.dat"], incomplete.MissingFileNames);

        File.WriteAllText(
            Path.Combine(directory, "client_local_English.dat"),
            "fixture");
        DatDirectoryValidation valid = locator.Validate(directory);
        Assert.True(valid.IsValid);
        Assert.Equal(Path.GetFullPath(directory), valid.Directory);
        Assert.Empty(valid.MissingFileNames);
    }

    [Fact]
    public void WindowsDetectionChecksBothConventionalLocationsInOrder()
    {
        string documents = Path.Combine(_root, "Documents", "Asheron's Call");
        string turbine = Path.Combine(_root, "Turbine", "Asheron's Call");
        CreateCompleteDatDirectory(documents);
        Directory.CreateDirectory(turbine);
        File.WriteAllText(Path.Combine(turbine, "client_portal.dat"), "fixture");

        var locator = new DatDirectoryLocator(
            isWindows: true,
            windowsCandidates: [documents, turbine]);

        IReadOnlyList<DatDirectoryValidation> detected = locator.Detect();
        Assert.Equal(2, detected.Count);
        Assert.Equal(Path.GetFullPath(documents), detected[0].Directory);
        Assert.True(detected[0].IsValid);
        Assert.Equal(Path.GetFullPath(turbine), detected[1].Directory);
        Assert.False(detected[1].IsValid);
        Assert.Equal(3, detected[1].MissingFileNames.Count);
    }

    [Fact]
    public void LinuxHasNoWindowsAutoDetectionButManualValidationStillWorks()
    {
        string manual = Path.Combine(_root, "linux-dats");
        CreateCompleteDatDirectory(manual);
        var locator = new DatDirectoryLocator(
            isWindows: false,
            windowsCandidates: [manual]);

        Assert.Empty(locator.Detect());
        Assert.True(locator.Validate(manual).IsValid);
    }

    private static void CreateCompleteDatDirectory(string directory)
    {
        Directory.CreateDirectory(directory);
        foreach (string fileName in DatDirectoryLocator.RequiredFileNames)
        {
            File.WriteAllText(Path.Combine(directory, fileName), "fixture");
        }
    }
}
