using System.Reflection;
using AcDream.App.Composition;
using AcDream.App.Net;
using AcDream.App.Rendering;
using AcDream.App.Tests.Architecture;
using AcDream.Core.Player;
using AcDream.Core.Spells;
using AcDream.Runtime;
using AcDream.Runtime.Gameplay;
using AcDream.Runtime.Session;

namespace AcDream.App.Tests.Runtime;

public sealed class RuntimeCharacterOwnershipTests
{
    private const BindingFlags Declared = BindingFlags.Instance
        | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic
        | BindingFlags.DeclaredOnly;

    [Fact]
    public void ProductionConstructsOnlyTheRuntimeCharacterOwner()
    {
        FieldInfo runtime = typeof(GameWindow).GetField("_runtime", Declared)
            ?? throw new MissingFieldException(typeof(GameWindow).FullName, "_runtime");
        Assert.Equal(typeof(GameRuntime), runtime.FieldType);
        Assert.Single(
            CompiledCallGraph.ReadDeclared(typeof(GameWindow)),
            call => call.Target.DeclaringType == typeof(GameRuntime)
                && call.Target.IsConstructor);

        Type[] childOwners =
        [
            typeof(RuntimeCharacterState),
            typeof(Spellbook),
            typeof(LocalPlayerState),
        ];
        Assert.DoesNotContain(
            CompiledCallGraph.ReadDeclared(typeof(GameWindow)),
            call => call.Target.IsConstructor
                && childOwners.Contains(call.Target.DeclaringType));

        AssertGetterRoutesToCharacterChild("SpellBook", "get_Spellbook");
        AssertGetterRoutesToCharacterChild("LocalPlayer", "get_LocalPlayer");
    }

    [Fact]
    public void SessionRoutingAndShutdownBorrowTheExactRuntimeOwner()
    {
        PropertyInfo character = typeof(LiveSessionDomainRuntime).GetProperty(
            "Character",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new MissingMemberException(typeof(LiveSessionDomainRuntime).FullName,
                "Character");
        Assert.Equal(typeof(RuntimeCharacterState), character.PropertyType);

        IReadOnlyList<CompiledCall> router =
            CompiledCallGraph.ReadOwned(typeof(LiveSessionEventRouter));
        AssertCharacterChildren(router, "get_Spellbook", "get_LocalPlayer");

        MethodInfo install = typeof(RetailContentEffectsAudioCompositionFactory).GetMethod(
            nameof(RetailContentEffectsAudioCompositionFactory.InstallSpellMetadata),
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new MissingMethodException();
        Assert.Contains(
            CompiledCallGraph.Read(install),
            call => call.Target.DeclaringType == typeof(RuntimeCharacterState)
                && call.Target.Name == nameof(RuntimeCharacterState.InstallSpellMetadata));

        IReadOnlyList<CompiledCall> ui =
            CompiledCallGraph.ReadOwned(typeof(RetailInteractionRetainedUiCompositionFactory));
        AssertCharacterChildren(ui, "get_Spellbook", "get_LocalPlayer");

        Assert.DoesNotContain(
            typeof(RuntimeInventoryState).GetFields(Declared),
            field => field.FieldType.Name == "DesiredComponentState");

        IReadOnlyList<string> labels = CompiledCallGraph.ReadStringLiterals(
            typeof(GameWindowShutdownManifest).GetMethod(
                nameof(GameWindowShutdownManifest.Create),
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new MissingMethodException());
        Assert.Contains("game runtime root", labels);
        Assert.DoesNotContain(
            typeof(IngressShutdownRoots).GetProperties(),
            property => property.PropertyType == typeof(RuntimeCharacterState));
        Assert.DoesNotContain(
            typeof(LiveShutdownRoots).GetProperties(),
            property => property.PropertyType == typeof(RuntimeCharacterState));
    }

    [Fact]
    public void AppCharacterOptionAndMovementSkillOwnersWereDeleted()
    {
        HashSet<string> removed =
        [
            "PlayerCharacterOptionsState",
            "LocalPlayerSkillState",
        ];
        Assert.DoesNotContain(
            typeof(GameWindow).Assembly.GetTypes(),
            type => type.Name is not null && removed.Contains(type.Name));

        IReadOnlyList<CompiledCall> router =
            CompiledCallGraph.ReadOwned(typeof(LiveSessionEventRouter));
        Assert.Contains(
            router,
            call => call.Target.DeclaringType == typeof(RuntimeCharacterOptionsState)
                && call.Target.Name == nameof(RuntimeCharacterOptionsState.Replace));
        Assert.Contains(
            router,
            call => call.Target.DeclaringType == typeof(RuntimeMovementSkillState)
                && call.Target.Name.StartsWith("Update", StringComparison.Ordinal));
    }

    private static void AssertGetterRoutesToCharacterChild(
        string property,
        string childGetter)
    {
        MethodInfo getter = typeof(GameWindow).GetProperty(property, Declared)?.GetMethod
            ?? throw new MissingMemberException(typeof(GameWindow).FullName, property);
        IReadOnlyList<CompiledCall> calls = CompiledCallGraph.Read(getter);
        Assert.Contains(calls, call => call.Target.DeclaringType == typeof(GameWindow)
            && call.Target.Name == "get__runtimeCharacter");
        Assert.Contains(calls, call => call.Target.DeclaringType == typeof(RuntimeCharacterState)
            && call.Target.Name == childGetter);
    }

    private static void AssertCharacterChildren(
        IReadOnlyList<CompiledCall> calls,
        params string[] getters)
    {
        foreach (string getter in getters)
        {
            Assert.True(
                calls.Any(call => call.Target.DeclaringType == typeof(RuntimeCharacterState)
                    && call.Target.Name == getter),
                $"Missing RuntimeCharacterState.{getter}. Runtime calls: "
                + string.Join(", ", calls
                    .Where(call => call.Target.DeclaringType?.Namespace?.StartsWith(
                        "AcDream.Runtime", StringComparison.Ordinal) == true)
                    .Select(call => $"{call.Target.DeclaringType?.Name}.{call.Target.Name}")
                    .Distinct()));
        }
    }
}
