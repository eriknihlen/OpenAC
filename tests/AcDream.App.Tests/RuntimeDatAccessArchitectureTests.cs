using DatReaderWriter;
using AcDream.Content;
using System.Reflection;
using System.Text.RegularExpressions;

namespace AcDream.App.Tests;

public sealed class RuntimeDatAccessArchitectureTests
{
    [Fact]
    public void ProductionTypes_KeepRawDatCollectionInsideContentOwnerSeam()
    {
        Type rawType = typeof(DatReaderWriter.DatCollection);
        Type ownerType = typeof(RuntimeDatCollection);
        Type adapterType = typeof(DatCollectionAdapter);
        Type[] productionTypes =
            typeof(AcDream.App.Rendering.GameWindow).Assembly.GetTypes()
            .Concat(typeof(RuntimeDatCollectionFactory).Assembly.GetTypes())
            .Distinct()
            .ToArray();
        var violations = new List<string>();

        foreach (Type type in productionTypes.Where(
            type => type != ownerType && type != adapterType))
        {
            foreach (FieldInfo field in type.GetFields(
                BindingFlags.Instance | BindingFlags.Static |
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (field.FieldType == rawType)
                    violations.Add($"{type.FullName} field {field.Name}");
            }

            foreach (PropertyInfo property in type.GetProperties(
                BindingFlags.Instance | BindingFlags.Static |
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (property.PropertyType == rawType)
                    violations.Add($"{type.FullName} property {property.Name}");
            }

            foreach (MethodBase method in type.GetMethods(
                    BindingFlags.Instance | BindingFlags.Static |
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Cast<MethodBase>()
                .Concat(type.GetConstructors(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)))
            {
                if (method.GetParameters().Any(parameter => parameter.ParameterType == rawType))
                    violations.Add($"{type.FullName} method {method.Name}");
                if (method is MethodInfo info && info.ReturnType == rawType)
                    violations.Add($"{type.FullName} return {method.Name}");
            }
        }

        Assert.Empty(violations);
    }

    [Fact]
    public void ProductionSource_CreatesRawHandlesAndBoundedAdapterOnlyInRuntimeOwner()
    {
        string root = FindRepoRoot();
        string appRoot = Path.Combine(root, "src", "AcDream.App");
        string contentRoot = Path.Combine(root, "src", "AcDream.Content");
        string ownerPath = Path.GetFullPath(
            Path.Combine(contentRoot, "RuntimeDatCollectionFactory.cs"));
        string[] sources = Directory
            .GetFiles(appRoot, "*.cs", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(
                contentRoot,
                "*.cs",
                SearchOption.AllDirectories))
            .ToArray();

        string[] rawCreates = FindMatchingFiles(sources, @"\bnew\s+DatCollection\s*\(");
        string[] adapterCreates = FindMatchingFiles(sources, @"\bnew\s+DatCollectionAdapter\s*\(");
        string[] rawTypedReads = FindMatchingFiles(
            sources,
            @"\.Db\s*\.\s*(?:Get|TryGet)\s*<");

        Assert.Equal([ownerPath], rawCreates);
        Assert.Equal([ownerPath], adapterCreates);
        Assert.Empty(rawTypedReads);
    }

    private static string[] FindMatchingFiles(IEnumerable<string> sources, string pattern) =>
        sources
            .Where(path => Regex.IsMatch(File.ReadAllText(path), pattern))
            .Select(Path.GetFullPath)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string FindRepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "AcDream.slnx")))
                return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate AcDream.slnx.");
    }
}
