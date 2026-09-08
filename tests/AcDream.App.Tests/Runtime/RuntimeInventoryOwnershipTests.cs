using System.Reflection;
using AcDream.App.Composition;
using AcDream.App.Net;
using AcDream.App.Rendering;
using AcDream.App.Tests.Architecture;
using AcDream.App.UI;
using AcDream.App.UI.Layout;
using AcDream.Core.Items;
using AcDream.Runtime;
using AcDream.Runtime.Gameplay;

namespace AcDream.App.Tests.Runtime;

public sealed class RuntimeInventoryOwnershipTests
{
    private const BindingFlags Declared = BindingFlags.Instance
        | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic
        | BindingFlags.DeclaredOnly;

    [Fact]
    public void ProductionConstructsOnlyTheRuntimeInventoryOwner()
    {
        FieldInfo runtime = typeof(GameWindow).GetField("_runtime", Declared)
            ?? throw new MissingFieldException(typeof(GameWindow).FullName, "_runtime");
        Assert.Equal(typeof(GameRuntime), runtime.FieldType);

        HashSet<string> displacedConstructors =
        [
            nameof(RuntimeInventoryState),
            nameof(ItemManaState),
            "DesiredComponentSnapshotState",
            "ShortcutSnapshotState",
            nameof(ExternalContainerState),
            "DesiredComponentState",
        ];
        Assert.DoesNotContain(
            CompiledCallGraph.ReadDeclared(typeof(GameWindow)),
            call => call.Target.IsConstructor
                && call.Target.DeclaringType?.Name is { } name
                && displacedConstructors.Contains(name));
        Assert.DoesNotContain(
            typeof(GameWindow).GetFields(Declared),
            field => displacedConstructors.Contains(field.FieldType.Name));
    }

    [Fact]
    public void UiSessionAndShutdownBorrowTheExactRuntimeOwner()
    {
        IReadOnlyList<CompiledCall> ui =
            CompiledCallGraph.ReadOwned(typeof(RetailInteractionRetainedUiCompositionFactory));
        AssertRuntimeCall(ui, typeof(RuntimeActionState), "get_Transactions");
        Assert.DoesNotContain(
            ui,
            call => call.Target.DeclaringType == typeof(InventoryTransactionState)
                && call.Target.IsConstructor);

        FieldInfo transactions = Assert.Single(
            typeof(ItemInteractionController).GetFields(Declared),
            field => field.Name == "_runtimeTransactions");
        Assert.Equal(typeof(RuntimeInteractionTransactionState), transactions.FieldType);
        Assert.DoesNotContain(
            typeof(ItemInteractionController).GetFields(Declared),
            field => field.Name == "_ownsTransactions");
        Assert.DoesNotContain(
            CompiledCallGraph.ReadDeclared(typeof(ItemInteractionController)),
            call => call.Target.DeclaringType == typeof(InventoryTransactionState)
                && call.Target.IsConstructor);

        IReadOnlyList<CompiledCall> bindings =
            CompiledCallGraph.ReadOwned(typeof(LiveSessionRuntimeFactory));
        Assert.Contains(bindings, call => call.Target.DeclaringType == typeof(ShortcutStore)
            && call.Target.Name == nameof(ShortcutStore.Load));
        Assert.Contains(bindings, call =>
            call.Target.DeclaringType == typeof(RuntimeInteractionTransactionState)
            && call.Target.Name == nameof(RuntimeInteractionTransactionState.CompleteUse));
        Assert.DoesNotContain(bindings, call =>
            call.Target.DeclaringType == typeof(RuntimeInventoryState)
            && call.Target.Name == "get_DesiredComponents");

        IReadOnlyList<string> labels = CompiledCallGraph.ReadStringLiterals(
            typeof(GameWindowShutdownManifest).GetMethod(
                nameof(GameWindowShutdownManifest.Create),
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new MissingMethodException());
        Assert.Contains("game runtime root", labels);
        Assert.Contains("game runtime", labels);
        Assert.DoesNotContain(
            typeof(IngressShutdownRoots).GetProperties(),
            property => property.PropertyType == typeof(RuntimeInventoryState));
        Assert.DoesNotContain(
            typeof(LiveShutdownRoots).GetProperties(),
            property => property.PropertyType == typeof(RuntimeInventoryState));
    }

    [Fact]
    public void ToolbarBorrowsRuntimeShortcutManagerWithoutAProviderOrMirror()
    {
        FieldInfo store = Assert.Single(
            typeof(ToolbarController).GetFields(Declared),
            field => field.Name == "_store");
        Assert.Equal(typeof(ShortcutStore), store.FieldType);
        Assert.DoesNotContain(
            CompiledCallGraph.ReadDeclared(typeof(ToolbarController)),
            call => call.Target.DeclaringType == typeof(ShortcutStore)
                && call.Target.IsConstructor);
        Assert.DoesNotContain(
            typeof(ToolbarController).GetFields(Declared),
            field => field.Name == "_storeLoaded"
                || IsShortcutProvider(field.FieldType));
        Assert.DoesNotContain(
            typeof(ToolbarController).GetConstructors(Declared)
                .SelectMany(constructor => constructor.GetParameters()),
            parameter => IsShortcutProvider(parameter.ParameterType));
        AssertRuntimeCall(
            CompiledCallGraph.ReadOwned(typeof(RetailInteractionRetainedUiCompositionFactory)),
            typeof(RuntimeInventoryState),
            "get_Shortcuts");
    }

    [Fact]
    public void SpellUiDoesNotApplyASecondLocalCommandMutation()
    {
        HashSet<string> duplicateMutations =
        [
            "SetSpellbookFilters",
            "SetDesiredComponent",
            "SetFavorite",
            "RemoveFavorite",
        ];
        Assert.DoesNotContain(
            CompiledCallGraph.ReadOwned(typeof(SpellbookWindowController))
                .Concat(CompiledCallGraph.ReadOwned(typeof(SpellcastingUiController))),
            call => duplicateMutations.Contains(call.Target.Name));
    }

    private static bool IsShortcutProvider(Type type) =>
        type.IsGenericType
        && type.GetGenericTypeDefinition() == typeof(Func<>)
        && type.GenericTypeArguments[0].IsGenericType
        && type.GenericTypeArguments[0].GetGenericTypeDefinition()
            == typeof(IReadOnlyList<>);

    private static void AssertRuntimeCall(
        IReadOnlyList<CompiledCall> calls,
        Type owner,
        string name) =>
        Assert.True(
            calls.Any(call => call.Target.DeclaringType == owner
                && call.Target.Name == name),
            $"Missing {owner.Name}.{name}. Runtime calls: "
            + string.Join(", ", calls
                .Where(call => call.Target.DeclaringType?.Namespace?.StartsWith(
                    "AcDream.Runtime", StringComparison.Ordinal) == true)
                .Select(call => $"{call.Target.DeclaringType?.Name}.{call.Target.Name}")
                .Distinct()));
}
