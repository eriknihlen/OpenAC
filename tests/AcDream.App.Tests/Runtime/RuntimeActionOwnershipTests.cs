using System.Reflection;
using System.Text.RegularExpressions;
using AcDream.App.Composition;
using AcDream.App.Net;
using AcDream.App.Rendering;
using AcDream.App.Runtime;
using AcDream.App.Tests.Architecture;
using AcDream.App.UI;
using AcDream.Runtime;
using AcDream.Runtime.Gameplay;

namespace AcDream.App.Tests.Runtime;

public sealed class RuntimeActionOwnershipTests
{
    [Fact]
    public void ProductionConstructsOneCanonicalActionOwner()
    {
        string root = FindRepositoryRoot();
        string gameWindow = ReadAppSource(
            root,
            "Rendering",
            "GameWindow.cs");
        string program = ReadAppSource(root, "Program.cs");

        Assert.Contains(
            "private readonly GameRuntime _runtime;",
            gameWindow,
            StringComparison.Ordinal);
        Assert.Contains(
            "_runtime = new GameRuntime(new GameRuntimeDependencies(",
            gameWindow,
            StringComparison.Ordinal);
        Assert.Contains(
            "_runtimeActions.Selection;",
            gameWindow,
            StringComparison.Ordinal);
        Assert.Contains(
            "_runtimeActions.Combat;",
            gameWindow,
            StringComparison.Ordinal);
        Assert.Contains(
            "window.Selection,",
            program,
            StringComparison.Ordinal);

        string[] productionFiles = Directory
            .EnumerateFiles(
                Path.Combine(root, "src", "AcDream.App"),
                "*.cs",
                SearchOption.AllDirectories)
            .Where(static path =>
                !path.Contains(
                    $"{Path.DirectorySeparatorChar}Studio{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal))
            .ToArray();
        string production = string.Join(
            "\n",
            productionFiles.Select(File.ReadAllText));

        Assert.Empty(Regex.Matches(
            production,
            @"\bnew\s+(?:AcDream\.Core\.Selection\.)?SelectionState\s*\("));
        Assert.Empty(Regex.Matches(
            production,
            @"\bnew\s+(?:AcDream\.Core\.Combat\.)?CombatState\s*\("));
        Assert.Empty(Regex.Matches(
            production,
            @"\bnew\s+(?:AcDream\.Runtime\.Gameplay\.)?InteractionState\s*\("));
        Assert.Empty(Regex.Matches(
            production,
            @"\bnew\s+(?:AcDream\.Runtime\.Gameplay\.)?RuntimeInteractionTransactionState\s*\("));
        Assert.Empty(Regex.Matches(
            production,
            @"\bnew\s+(?:AcDream\.Runtime\.Gameplay\.)?RuntimeCombatAttackState\s*\("));
        Assert.Empty(Regex.Matches(
            production,
            @"\bnew\s+(?:AcDream\.Runtime\.Gameplay\.)?RuntimeCombatTargetState\s*\("));
        Assert.Empty(Regex.Matches(
            production,
            @"\bnew\s+(?:AcDream\.Runtime\.Gameplay\.)?RuntimeCombatModeState\s*\("));
        Assert.Empty(Regex.Matches(
            production,
            @"\bnew\s+(?:AcDream\.Runtime\.Gameplay\.)?RuntimeSpellCastState\s*\("));
        Assert.Empty(Regex.Matches(
            production,
            @"\bnew\s+RuntimeActionState\s*\("));
    }

    [Fact]
    public void UiSessionRuntimeAndShutdownBorrowTheExactActionChildren()
    {
        IReadOnlyList<CompiledCall> ui =
            CompiledCallGraph.ReadOwned(typeof(RetailInteractionRetainedUiCompositionFactory));
        AssertActionChildren(
            ui,
            "get_Interaction",
            "get_Transactions",
            "get_Selection",
            "get_Combat",
            "get_SpellCast");
        AssertActionChildren(
            CompiledCallGraph.ReadOwned(typeof(InteractionRetainedUiCompositionPhase)),
            "get_CombatAttack");

        IReadOnlyList<CompiledCall> session =
            CompiledCallGraph.ReadOwned(typeof(SessionPlayerCompositionPhase));
        AssertActionChildren(session, "get_Selection", "get_Combat");

        PropertyInfo domainActions = typeof(LiveSessionDomainRuntime).GetProperty(
            "Actions",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new MissingMemberException(typeof(LiveSessionDomainRuntime).FullName, "Actions");
        Assert.Equal(typeof(RuntimeActionState), domainActions.PropertyType);

        FieldInfo commandActions = Assert.Single(
            typeof(CurrentGameRuntimeCommandAdapter).GetFields(
                BindingFlags.Instance | BindingFlags.NonPublic),
            field => field.Name == "_actions");
        Assert.Equal(typeof(RuntimeActionState), commandActions.FieldType);
        Assert.Contains(
            CompiledCallGraph.ReadDeclared(typeof(CurrentGameRuntimeCommandAdapter)),
            call => call.Target.DeclaringType == typeof(RuntimeActionState)
                && call.Target.Name == "get_Selection");

        FieldInfo transactions = Assert.Single(
            typeof(ItemInteractionController).GetFields(
                BindingFlags.Instance | BindingFlags.NonPublic),
            field => field.Name == "_runtimeTransactions");
        Assert.Equal(typeof(RuntimeInteractionTransactionState), transactions.FieldType);
        Assert.DoesNotContain(
            CompiledCallGraph.ReadDeclared(typeof(ItemInteractionController)),
            call => call.Target.DeclaringType == typeof(InteractionState)
                && call.Target.IsConstructor);

        IReadOnlyList<string> labels = CompiledCallGraph.ReadStringLiterals(
            typeof(GameWindowShutdownManifest).GetMethod(
                nameof(GameWindowShutdownManifest.Create),
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new MissingMethodException());
        Assert.Contains("game runtime root", labels);
        Assert.DoesNotContain(
            typeof(IngressShutdownRoots).GetProperties(),
            property => property.PropertyType == typeof(RuntimeActionState));
        Assert.DoesNotContain(
            typeof(LiveShutdownRoots).GetProperties(),
            property => property.PropertyType == typeof(RuntimeActionState));

        Assert.DoesNotContain(
            typeof(GameWindow).Assembly.GetTypes(),
            type => type.FullName == "AcDream.App.UI.InteractionState");
    }

    private static void AssertActionChildren(
        IReadOnlyList<CompiledCall> calls,
        params string[] getters)
    {
        foreach (string getter in getters)
        {
            Assert.True(
                calls.Any(call => call.Target.DeclaringType == typeof(RuntimeActionState)
                    && call.Target.Name == getter),
                $"Missing RuntimeActionState.{getter}. Runtime calls: "
                + string.Join(", ", calls
                    .Where(call => call.Target.DeclaringType?.Namespace?.StartsWith(
                        "AcDream.Runtime", StringComparison.Ordinal) == true)
                    .Select(call => $"{call.Target.DeclaringType?.Name}.{call.Target.Name}")
                    .Distinct()));
        }
    }

    private static string ReadAppSource(string root, params string[] relative) =>
        File.ReadAllText(Path.Combine(
            [root, "src", "AcDream.App", .. relative]));

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

    private static void AssertAppearsInOrder(
        string source,
        params string[] fragments)
    {
        int cursor = 0;
        foreach (string fragment in fragments)
        {
            int index = source.IndexOf(
                fragment,
                cursor,
                StringComparison.Ordinal);
            Assert.True(index >= 0, $"Missing source fragment: {fragment}");
            cursor = index + fragment.Length;
        }
    }
}
