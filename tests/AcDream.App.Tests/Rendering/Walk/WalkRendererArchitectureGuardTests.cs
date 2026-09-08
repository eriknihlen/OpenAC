using System.Reflection;
using System.Runtime.CompilerServices;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Walk;
using AcDream.App.Rendering.Wb;
using AcDream.Content;
using DatReaderWriter;

namespace AcDream.App.Tests.Rendering.Walk;

public sealed class WalkRendererArchitectureGuardTests
{
    private static readonly Assembly AppAssembly = typeof(RetailFrameWalk).Assembly;

    private static readonly string[] DeletedGraphTypes =
    [
        "PortalVisibilityBuilder",
        "PortalVisibilityFrame",
        "ExteriorPortalSeed",
        "IndoorDrawPlan",
        "CellDrawEntry",
        "ViewconeCuller",
    ];

    private static readonly string[] DeletedProbeSymbols =
    [
        "ProbeFacilityStairsEnabled",
        "ACDREAM_PROBE_FACILITY_STAIRS",
        "ProbeIndoorWalkEnabled",
        "ProbeIndoorLookupEnabled",
        "ProbeIndoorUploadEnabled",
        "ProbeIndoorXformEnabled",
        "ProbeIndoorCullEnabled",
        "ACDREAM_PROBE_INDOOR_WALK",
        "ACDREAM_PROBE_INDOOR_LOOKUP",
        "ACDREAM_PROBE_INDOOR_UPLOAD",
        "ACDREAM_PROBE_INDOOR_XFORM",
        "ACDREAM_PROBE_INDOOR_CULL",
        "ACDREAM_PROBE_INDOOR_ALL",
        "ProbeVisibilityEnabled",
        "ACDREAM_PROBE_VIS",
        "EmitVis",
        "ProbeEnvCellEnabled",
        "ACDREAM_PROBE_ENVCELL",
        "ProbeFlapEnabled",
        "ACDREAM_PROBE_FLAP",
        "ProbePvInputEnabled",
        "ACDREAM_PROBE_PVINPUT",
        "ProbeGlStateEnabled",
        "ACDREAM_PROBE_GLSTATE",
        "ProbeClipRouteEnabled",
        "ACDREAM_PROBE_CLIPROUTE",
        "ProbePortalChurnEnabled",
        "ACDREAM_PROBE_PORTAL_CHURN",
        "ProbeIndoorLightEnabled",
        "ACDREAM_PROBE_INDOOR_LIGHT",
        "EmitIndoorLight",
        "ProbeSeamDrawEnabled",
        "ACDREAM_PROBE_SEAMDRAW",
        "SeamDrawTargetCells",
    ];

    [Fact]
    public void AppAssembly_ContainsNoSupersededPortalGraphTypes()
    {
        string[] survivors = AppAssembly.GetTypes()
            .Where(type => DeletedGraphTypes.Contains(type.Name, StringComparer.Ordinal))
            .Select(type => type.FullName ?? type.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(survivors);
    }

    [Fact]
    public void WalkFrameOwners_AreUnique()
    {
        const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Static |
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        Type[] sourceTypes = AppAssembly.GetTypes()
            .Where(type => !type.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false))
            .ToArray();
        (Type Owner, FieldInfo Field)[] pviewOwners = sourceTypes
            .SelectMany(type => type.GetFields(Fields).Select(field => (type, field)))
            .Where(pair => pair.field.FieldType == typeof(WalkPView))
            .Select(pair => (pair.type, pair.field))
            .ToArray();
        Assert.All(pviewOwners, pair => Assert.Equal(typeof(RetailFrameWalk), pair.Owner));
        Assert.Equal(2, pviewOwners.Length);

        (Type Owner, FieldInfo Field)[] frameWalkOwners = sourceTypes
            .SelectMany(type => type.GetFields(Fields).Select(field => (type, field)))
            .Where(pair => pair.field.FieldType == typeof(RetailFrameWalk))
            .Select(pair => (pair.type, pair.field))
            .ToArray();
        (Type Owner, FieldInfo Field) owner = Assert.Single(frameWalkOwners);
        Assert.Equal(typeof(RetailPViewRenderer), owner.Owner);
    }

    [Fact]
    public void FrameTimeWalkOwners_HaveNoRawDatDependency()
    {
        Type[] owners =
        [
            typeof(RetailFrameWalk),
            typeof(WalkFrameDriver),
            typeof(RetailPViewRenderer),
            typeof(RetailPViewPassExecutor),
            typeof(WalkProductionLeafRenderer),
            typeof(WalkStaticStreamPopulator),
            typeof(WbDrawDispatcher),
        ];
        var violations = new List<string>();

        foreach (Type owner in owners)
        {
            const BindingFlags Members = BindingFlags.Instance | BindingFlags.Static |
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            foreach (FieldInfo field in owner.GetFields(Members))
                AddIfRawDat(violations, owner, $"field {field.Name}", field.FieldType);
            foreach (PropertyInfo property in owner.GetProperties(Members))
                AddIfRawDat(violations, owner, $"property {property.Name}", property.PropertyType);

            IEnumerable<MethodBase> methods = owner.GetMethods(Members).Cast<MethodBase>()
                .Concat(owner.GetConstructors(Members));
            foreach (MethodBase method in methods)
            {
                foreach (ParameterInfo parameter in method.GetParameters())
                {
                    AddIfRawDat(
                        violations,
                        owner,
                        $"parameter {method.Name}.{parameter.Name}",
                        parameter.ParameterType);
                }

                if (method is MethodInfo methodInfo)
                    AddIfRawDat(violations, owner, $"return {method.Name}", methodInfo.ReturnType);
            }
        }

        Assert.Empty(violations);
    }

    [Fact]
    public void ProductionSource_ContainsNoSecondPortalGraph()
    {
        string[] sources = ProductionSources();
        string[] banned = [.. DeletedGraphTypes, "ClipFrameAssembler.Assemble"];
        AssertNoSourceMatches(sources, banned);
    }

    [Fact]
    public void OrderedWalkStream_HasNoCrossStreamReorder()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "src", "AcDream.App", "Rendering", "Wb", "WbDrawDispatcher.OrderedStream.cs"));

        Assert.DoesNotContain(".Sort(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("OrderBy(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("OrderByDescending(", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionSource_ContainsNoDeletedRendererProbe_AndRetainsWalkTranscriptProof()
    {
        AssertNoSourceMatches(ProductionSources(), DeletedProbeSymbols);

        string root = FindRepoRoot();
        string diagnostics = File.ReadAllText(Path.Combine(
            root, "src", "AcDream.Core", "Rendering", "RenderingDiagnostics.cs"));
        Assert.Contains("DumpWalkTranscriptEnabled", diagnostics, StringComparison.Ordinal);
    }

    private static void AddIfRawDat(
        ICollection<string> violations,
        Type owner,
        string member,
        Type memberType)
    {
        if (ContainsRawDat(memberType))
            violations.Add($"{owner.FullName} {member}: {memberType}");
    }

    private static bool ContainsRawDat(Type type)
    {
        if (type.IsByRef || type.IsArray || type.IsPointer)
            return ContainsRawDat(type.GetElementType()!);
        if (type == typeof(DatCollection) || type == typeof(IDatReaderWriter))
            return true;
        return type.IsGenericType && type.GetGenericArguments().Any(ContainsRawDat);
    }

    private static void AssertNoSourceMatches(IEnumerable<string> sources, IEnumerable<string> banned)
    {
        string[] violations = sources
            .SelectMany(path => banned
                .Where(symbol => File.ReadAllText(path).Contains(symbol, StringComparison.Ordinal))
                .Select(symbol => $"{Path.GetRelativePath(FindRepoRoot(), path)}: {symbol}"))
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        Assert.Empty(violations);
    }

    private static string[] ProductionSources()
    {
        string root = Path.Combine(FindRepoRoot(), "src");
        return Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string FindRepoRoot()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory, "AcDream.slnx")))
                return directory;
            directory = Directory.GetParent(directory)?.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate AcDream.slnx.");
    }
}
