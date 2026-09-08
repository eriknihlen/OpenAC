using System.Reflection;
using System.Text.RegularExpressions;
using AcDream.App.Input;
using AcDream.App.Rendering;
using AcDream.App.Runtime;
using AcDream.App.Tests.Architecture;
using AcDream.Runtime;
using AcDream.Runtime.Gameplay;

namespace AcDream.App.Tests.Runtime;

public sealed class RuntimeMovementOwnershipTests
{
    [Fact]
    public void ProductionConstructsOneCanonicalRuntimeMovementOwner()
    {
        string root = FindRepositoryRoot();
        string appRoot = Path.Combine(root, "src", "AcDream.App");
        string runtimeRoot = Path.Combine(root, "src", "AcDream.Runtime");
        string app = string.Join(
            "\n",
            Directory.EnumerateFiles(appRoot, "*.cs", SearchOption.AllDirectories)
                .Select(File.ReadAllText));
        string runtime = string.Join(
            "\n",
            Directory.EnumerateFiles(runtimeRoot, "*.cs", SearchOption.AllDirectories)
                .Select(File.ReadAllText));

        Assert.False(File.Exists(Path.Combine(
            appRoot,
            "Input",
            "PlayerMovementController.cs")));
        Assert.False(File.Exists(Path.Combine(
            appRoot,
            "Input",
            "LocalPlayerOutboundController.cs")));
        Assert.DoesNotContain(
            "class PlayerMovementController",
            app,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "class LocalPlayerOutboundController",
            app,
            StringComparison.Ordinal);
        Assert.Empty(Regex.Matches(app, @"\bbool\s+_autoRunActive\b"));
        Assert.Empty(Regex.Matches(
            app,
            @"RuntimeLocalPlayerMovementState\s+\w+\s*=\s*new\s*\(")
            .Cast<Match>());
        Assert.Single(Regex.Matches(
            runtime,
            @"new\s+RuntimeLocalPlayerMovementState\s*\(")
            .Cast<Match>());
        Assert.DoesNotContain(
            "ACDREAM_RUN_SKILL",
            runtime,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ACDREAM_JUMP_SKILL",
            runtime,
            StringComparison.Ordinal);
    }

    [Fact]
    public void GraphicalInputRuntimeViewsAndShutdownBorrowTheExactOwner()
    {
        PropertyInfo movementProperty = typeof(GameWindow).GetProperty(
            "_playerControllerSlot",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMemberException(typeof(GameWindow).FullName,
                "_playerControllerSlot");
        Assert.Equal(typeof(RuntimeLocalPlayerMovementState),
            movementProperty.PropertyType);
        Assert.Contains(
            CompiledCallGraph.Read(movementProperty.GetMethod!),
            call => call.Target.DeclaringType == typeof(GameRuntime)
                && call.Target.Name == "get_MovementOwner");

        ConstructorInfo window = Assert.Single(
            typeof(GameWindow).GetConstructors(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
            constructor => CompiledCallGraph.Read(constructor).Any(call =>
                call.Target.DeclaringType == typeof(DispatcherMovementInputSource)
                && call.Target.IsConstructor));
        Assert.Contains(
            CompiledCallGraph.Read(window),
            call => call.Target.DeclaringType == typeof(GameWindow)
                && call.Target.Name == "get__playerControllerSlot");

        FieldInfo inputMovement = Assert.Single(
            typeof(DispatcherMovementInputSource).GetFields(
                BindingFlags.Instance | BindingFlags.NonPublic),
            field => field.Name == "_movement");
        Assert.Equal(typeof(RuntimeLocalPlayerMovementState), inputMovement.FieldType);
        Assert.Contains(
            CompiledCallGraph.ReadDeclared(typeof(DispatcherMovementInputSource)),
            call => call.Target.DeclaringType == typeof(RuntimeLocalPlayerMovementState)
                && call.Target.Name == "get_AutoRunActive");

        FieldInfo adapterRuntime = Assert.Single(
            typeof(CurrentGameRuntimeAdapter).GetFields(
                BindingFlags.Instance | BindingFlags.NonPublic),
            field => field.Name == "_runtime");
        Assert.Equal(typeof(GameRuntime), adapterRuntime.FieldType);
        FieldInfo commandMovement = Assert.Single(
            typeof(CurrentGameRuntimeCommandAdapter).GetFields(
                BindingFlags.Instance | BindingFlags.NonPublic),
            field => field.Name == "_movement");
        Assert.Equal(typeof(RuntimeLocalPlayerMovementState), commandMovement.FieldType);
        Assert.Contains(
            CompiledCallGraph.ReadDeclared(typeof(CurrentGameRuntimeCommandAdapter)),
            call => call.Target.DeclaringType == typeof(RuntimeLocalPlayerMovementState)
                && call.Target.Name == nameof(RuntimeLocalPlayerMovementState.Execute));

        Assert.Equal(typeof(GameRuntime),
            typeof(IngressShutdownRoots).GetProperty("Runtime")?.PropertyType);
        Assert.Equal(typeof(GameRuntime),
            typeof(LiveShutdownRoots).GetProperty("Runtime")?.PropertyType);
        Assert.Contains(
            "game runtime root",
            CompiledCallGraph.ReadStringLiterals(
                typeof(GameWindowShutdownManifest).GetMethod(
                    nameof(GameWindowShutdownManifest.Create),
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                ?? throw new MissingMethodException()));
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
