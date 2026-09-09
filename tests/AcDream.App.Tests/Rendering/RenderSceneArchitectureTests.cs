using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using AcDream.App.Rendering.Scene;

namespace AcDream.App.Tests.Rendering;

public sealed class RenderSceneArchitectureTests
{
    [Fact]
    public void ArchDependency_IsPinnedOnlyInAppAndNamespacesStayContained()
    {
        string root = FindRepoRoot();
        string appRoot = Path.Combine(root, "src", "AcDream.App");
        string allowedRoot = Path.GetFullPath(
            Path.Combine(appRoot, "Rendering", "Scene", "Arch"));
        string[] appSources =
            Directory.GetFiles(appRoot, "*.cs", SearchOption.AllDirectories);

        string[] namespaceViolations = appSources
            .Where(path => Regex.IsMatch(
                File.ReadAllText(path),
                @"(?:\busing\s+Arch(?:\.|;)|\bArch\.Core\b)"))
            .Where(path => !IsUnder(path, allowedRoot))
            .Select(Path.GetFullPath)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        string[] packageOwners = Directory
            .GetFiles(Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories)
            .Where(HasDirectArchReference)
            .Select(Path.GetFullPath)
            .ToArray();

        Assert.Empty(namespaceViolations);
        Assert.Equal(
            [Path.GetFullPath(Path.Combine(appRoot, "AcDream.App.csproj"))],
            packageOwners);
    }

    [Fact]
    public void ArchTypes_DoNotCrossAcdreamOwnedSceneBoundary()
    {
        Type[] appTypes = typeof(IRenderScene).Assembly.GetTypes();
        var violations = new List<string>();

        foreach (Type type in appTypes)
        {
            if (type.Namespace?.StartsWith(
                    "AcDream.App.Rendering.Scene.Arch",
                    StringComparison.Ordinal)
                == true)
            {
                continue;
            }

            foreach (FieldInfo field in type.GetFields(
                BindingFlags.Instance
                | BindingFlags.Static
                | BindingFlags.Public
                | BindingFlags.NonPublic
                | BindingFlags.DeclaredOnly))
            {
                if (ContainsArchType(field.FieldType))
                    violations.Add($"{type.FullName} field {field.Name}");
            }

            foreach (PropertyInfo property in type.GetProperties(
                BindingFlags.Instance
                | BindingFlags.Static
                | BindingFlags.Public
                | BindingFlags.NonPublic
                | BindingFlags.DeclaredOnly))
            {
                if (ContainsArchType(property.PropertyType))
                    violations.Add($"{type.FullName} property {property.Name}");
            }

            foreach (MethodBase method in type
                .GetMethods(
                    BindingFlags.Instance
                    | BindingFlags.Static
                    | BindingFlags.Public
                    | BindingFlags.NonPublic
                    | BindingFlags.DeclaredOnly)
                .Cast<MethodBase>()
                .Concat(type.GetConstructors(
                    BindingFlags.Instance
                    | BindingFlags.Public
                    | BindingFlags.NonPublic)))
            {
                if (method.GetParameters()
                    .Any(parameter => ContainsArchType(parameter.ParameterType)))
                {
                    violations.Add($"{type.FullName} method {method.Name}");
                }

                if (method is MethodInfo info
                    && ContainsArchType(info.ReturnType))
                {
                    violations.Add($"{type.FullName} return {method.Name}");
                }
            }
        }

        Assert.Empty(violations);
    }

    [Fact]
    public void PrimitiveIdentityExtraction_StaysInsideArchAdapter()
    {
        string root = FindRepoRoot();
        string appRoot = Path.Combine(root, "src", "AcDream.App");
        string allowedRoot = Path.GetFullPath(
            Path.Combine(appRoot, "Rendering", "Scene", "Arch"));
        string contracts = Path.GetFullPath(
            Path.Combine(
                appRoot,
                "Rendering",
                "Scene",
                "RenderSceneContracts.cs"));

        string[] violations = Directory
            .GetFiles(appRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => Regex.IsMatch(
                File.ReadAllText(path),
                @"\.\s*RawValue\b"))
            .Where(path =>
                !IsUnder(path, allowedRoot)
                && !Path.GetFullPath(path).Equals(
                    contracts,
                    StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFullPath)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.Empty(violations);
    }

    private static bool HasDirectArchReference(string projectPath)
    {
        XDocument project = XDocument.Load(projectPath);
        return project
            .Descendants("PackageReference")
            .Any(element => string.Equals(
                (string?)element.Attribute("Include"),
                "Arch",
                StringComparison.Ordinal));
    }

    private static bool ContainsArchType(Type type)
    {
        if (type.IsByRef || type.IsArray || type.IsPointer)
            return ContainsArchType(type.GetElementType()!);

        if (type.Namespace?.StartsWith("Arch", StringComparison.Ordinal) == true)
            return true;

        return type.IsGenericType
            && type.GetGenericArguments().Any(ContainsArchType);
    }

    private static bool IsUnder(string path, string root)
    {
        string relative = Path.GetRelativePath(root, Path.GetFullPath(path));
        return relative != ".."
            && !relative.StartsWith(
                $"..{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal);
    }

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
