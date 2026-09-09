using System.Text.RegularExpressions;
using AcDream.App.Runtime;
using AcDream.App.Streaming;
using AcDream.Runtime;
using AcDream.Runtime.World;

namespace AcDream.App.Tests.Runtime;

public sealed class RuntimeWorldTransitOwnershipTests
{
    [Fact]
    public void ProductionConstructsOneRuntimeTransitOwnerAndNoAppLifecycleOwner()
    {
        string root = FindRepositoryRoot();
        string appRoot = Path.Combine(root, "src", "AcDream.App");
        string app = string.Join(
            "\n",
            Directory.EnumerateFiles(appRoot, "*.cs", SearchOption.AllDirectories)
                .Select(File.ReadAllText));
        string runtimeRoot = Path.Combine(root, "src", "AcDream.Runtime");
        string runtime = string.Join(
            "\n",
            Directory.EnumerateFiles(runtimeRoot, "*.cs", SearchOption.AllDirectories)
                .Select(File.ReadAllText));

        Assert.False(File.Exists(Path.Combine(
            appRoot,
            "Streaming",
            "WorldRevealLifecycleTelemetry.cs")));
        Assert.False(File.Exists(Path.Combine(
            appRoot,
            "Streaming",
            "TeleportTransitCoordinator.cs")));
        Assert.DoesNotContain(
            "class WorldRevealLifecycleTelemetry",
            app,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "class TeleportTransitCoordinator",
            app,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "record struct WorldRevealLifecycleSnapshot",
            app,
            StringComparison.Ordinal);
        Assert.Empty(Regex.Matches(
            app,
            @"new\s+RuntimeWorldTransitState\s*\(")
            .Cast<Match>());
        Assert.Single(Regex.Matches(
            runtime,
            @"new\s+RuntimeWorldTransitState\s*\(")
            .Cast<Match>());
    }

    [Fact]
    public void GraphicalAdaptersBorrowRuntimeStateWithoutReconstructingPortalView()
    {
        var availabilityFields = typeof(WorldGenerationAvailabilityState)
            .GetFields(
                System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.NonPublic);
        Assert.Single(availabilityFields);
        Assert.Equal(
            typeof(RuntimeWorldTransitState),
            availabilityFields[0].FieldType);

        var adapterFields = typeof(CurrentGameRuntimeAdapter)
            .GetFields(
                System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.NonPublic);
        Assert.Contains(
            adapterFields,
            field => field.FieldType == typeof(GameRuntime));
        Assert.DoesNotContain(
            adapterFields,
            field => field.FieldType == typeof(IRuntimePortalView)
                || field.FieldType == typeof(RuntimeWorldTransitState));

        var coordinatorFields = typeof(WorldRevealCoordinator)
            .GetFields(
                System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.NonPublic);
        Assert.Contains(
            coordinatorFields,
            field => field.FieldType == typeof(RuntimeWorldTransitState));
        Assert.DoesNotContain(
            coordinatorFields,
            field => field.Name is "_activeGeneration"
                or "_worldSimulationReleased"
                or "_lifecycle"
                or "_reservationGeneration"
                or "_worldViewportReleased"
                or "_hostProjection");
        Assert.DoesNotContain(
            coordinatorFields,
            field => field.FieldType == typeof(RuntimePortalSnapshot)
                || field.FieldType == typeof(RuntimeDestinationReadiness)
                || field.FieldType == typeof(long)
                || field.FieldType == typeof(uint));

        Type hostProjection = Assert.Single(
            typeof(WorldRevealCoordinator).GetNestedTypes(
                System.Reflection.BindingFlags.NonPublic),
            type => type.Name == "HostProjection");
        var hostFields = hostProjection.GetFields(
            System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.NonPublic);
        Assert.Single(
            hostFields,
            field => field.FieldType
                == typeof(RuntimeWorldHostProjectionToken));
        Assert.DoesNotContain(
            hostFields,
            field => field.FieldType == typeof(RuntimePortalSnapshot)
                || field.FieldType == typeof(RuntimeDestinationReadiness)
                || field.FieldType == typeof(long)
                || field.FieldType == typeof(uint));

        var teleportFields = typeof(LocalPlayerTeleportController)
            .GetFields(
                System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.NonPublic);
        Assert.Single(
            teleportFields,
            field => field.FieldType == typeof(RuntimeWorldTransitState));
        Assert.DoesNotContain(
            teleportFields,
            field => field.Name.Contains(
                "acceptedDestination",
                StringComparison.OrdinalIgnoreCase));
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "AcDream.slnx")))
                return current.FullName;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("AcDream.slnx was not found.");
    }
}
