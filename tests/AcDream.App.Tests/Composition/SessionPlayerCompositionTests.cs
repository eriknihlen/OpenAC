using System.Reflection;
using AcDream.App.Combat;
using AcDream.App.Composition;
using AcDream.App.Diagnostics;
using AcDream.App.Input;
using AcDream.App.Net;
using AcDream.App.Physics;
using AcDream.App.Rendering;
using AcDream.App.Streaming;
using AcDream.App.Tests.Architecture;
using AcDream.App.UI;
using AcDream.App.World;
using AcDream.Core.Net.Messages;
using AcDream.Runtime.Session;
using DatReaderWriter.DBObjs;

namespace AcDream.App.Tests.Composition;

public sealed class SessionPlayerCompositionTests
{
    [Fact]
    public void RuntimeBindingsReleaseInReverseAndRetryOnlyFailedEdges()
    {
        var calls = new List<string>();
        var bindings = new SessionPlayerRuntimeBindings();
        bindings.Adopt("first", new RetryBinding("first", calls, 0));
        bindings.Adopt("second", new RetryBinding("second", calls, 1));

        Assert.Throws<AggregateException>(bindings.Dispose);
        Assert.Equal(["second", "first"], calls);

        bindings.Dispose();
        bindings.Dispose();

        Assert.Equal(["second", "first", "second"], calls);
        Assert.Throws<ObjectDisposedException>(() =>
            bindings.Adopt("late", new RetryBinding("late", calls, 0)));
    }

    [Fact]
    public void SpawnClaimClassifierPreservesIndoorBoundaryMemoAndReset()
    {
        int lookups = 0;
        uint lastDid = 0;
        var info = new LandBlockInfo { NumCells = 2 };
        var classifier = new DatSpawnClaimHydrationClassifier(did =>
        {
            lookups++;
            lastDid = did;
            return info;
        });

        Assert.False(classifier.IsUnhydratable(0x123400FFu));
        Assert.Equal(0, lookups);
        Assert.False(classifier.IsUnhydratable(0x12340100u));
        Assert.Equal(0x1234FFFEu, lastDid);
        Assert.False(classifier.IsUnhydratable(0x12340100u));
        Assert.Equal(1, lookups);
        Assert.False(classifier.IsUnhydratable(0x12340101u));
        Assert.True(classifier.IsUnhydratable(0x12340102u));

        classifier.Reset();
        Assert.True(classifier.IsUnhydratable(0x12340102u));
        Assert.Equal(4, lookups);
    }

    [Fact]
    public void MissingOrEmptyLandblockMakesIndoorClaimUnhydratable()
    {
        var missing = new DatSpawnClaimHydrationClassifier(_ => null);
        var empty = new DatSpawnClaimHydrationClassifier(
            _ => new LandBlockInfo { NumCells = 0 });

        Assert.True(missing.IsUnhydratable(0x12340100u));
        Assert.True(empty.IsUnhydratable(0x12340100u));
    }

    [Fact]
    public void GameWindowUsesSessionPhaseAndContainsNoPhaseSevenBody()
    {
        IReadOnlyList<CompiledCall> windowCalls =
            CompiledCallGraph.ReadDeclared(typeof(GameWindow));

        Assert.Single(
            windowCalls,
            call => call.Target.DeclaringType
                    == typeof(SessionPlayerCompositionPhase)
                && call.Target.IsConstructor);
        Assert.DoesNotContain(
            windowCalls,
            call => call.Target.DeclaringType == typeof(LandblockStreamer)
                && call.Target.Name == nameof(LandblockStreamer.CreateForRequests));
        Assert.DoesNotContain(
            windowCalls,
            call => call.Target.IsConstructor
                && call.Target.DeclaringType is { } type
                && (type == typeof(LiveSessionController)
                    || type == typeof(LocalPlayerTeleportController)
                    || type == typeof(DatSpawnClaimHydrationClassifier)));

        IReadOnlyList<FieldInfo> phaseFields =
            typeof(SessionPlayerCompositionPhase).GetFields(
                BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.DoesNotContain(
            phaseFields,
            field => field.FieldType == typeof(GameWindow));
    }

    [Fact]
    public void CompleteSessionPlayerBindsCombatFeedbackToTheClientLocalSpewBoxRoute()
    {
        MethodInfo complete = RequiredMethod(
            typeof(SessionPlayerCompositionPhase),
            "CompleteSessionPlayer");
        Assert.Contains(
            CompiledCallGraph.Read(complete),
            call => call.Target.DeclaringType
                    == typeof(AcDream.App.Combat.CombatFeedbackSlot)
                && call.Target.Name
                    == nameof(AcDream.App.Combat.CombatFeedbackSlot.BindOwned));

        IEnumerable<MethodBase> lambdas =
            CompiledCallGraph.ReadMethodReferences(complete)
                .Select(call => call.Target)
                .Where(method => method.GetMethodBody() is not null);
        Assert.Contains(
            lambdas,
            method => CompiledCallGraph.Read(method).Any(call =>
                call.Target.DeclaringType
                    == typeof(AcDream.Runtime.Gameplay.RuntimeCommunicationState)
                && call.Target.Name == "AddText"));
    }

    [Fact]
    public void ProductionPhaseStartsStreamerBeforeSessionAndTransfersPortalLast()
    {
        MethodInfo composeCore = RequiredMethod(
            typeof(SessionPlayerCompositionPhase),
            "ComposeCore");
        IReadOnlyList<CompiledCall> composeCalls =
            CompiledCallGraph.Read(composeCore);
        AssertCallOrder(
            composeCalls,
            (typeof(LandblockStreamer), nameof(LandblockStreamer.Start)),
            (typeof(StreamingController), ".ctor"),
            (typeof(WorldRevealCoordinator), ".ctor"),
            (typeof(SessionPlayerCompositionPhase), "CompleteSessionPlayer"));

        IEnumerable<MethodBase> streamerFactories =
            CompiledCallGraph.ReadMethodReferences(composeCore)
                .Select(call => call.Target)
                .Where(method => method.GetMethodBody() is not null);
        MethodBase streamerFactory = Assert.Single(
            streamerFactories,
            method => CompiledCallGraph.Read(method).Any(call =>
                    call.Target.DeclaringType == typeof(LandblockStreamer)
                    && call.Target.Name
                        == nameof(LandblockStreamer.CreateForRequests)));
        Assert.Contains(
            CompiledCallGraph.Read(streamerFactory),
            call => call.Target.DeclaringType == typeof(LandblockStreamer)
                && call.Target.Name == nameof(LandblockStreamer.CreateForRequests));

        MethodInfo complete = RequiredMethod(
            typeof(SessionPlayerCompositionPhase),
            "CompleteSessionPlayer");
        IReadOnlyList<CompiledCall> completeCalls =
            CompiledCallGraph.Read(complete);
        AssertCallOrder(
            completeCalls,
            (typeof(LiveEntityHydrationController), ".ctor"),
            (typeof(LiveEntityNetworkUpdateController), ".ctor"),
            (typeof(GameplayInputFrameController), ".ctor"),
            (typeof(PlayerModeController), ".ctor"),
            (typeof(TransferableResourceSlot<>), "Transfer"),
            (typeof(DeferredLocalPlayerTeleportNetworkSink), "BindOwned"),
            (typeof(LiveSessionRuntimeFactory), nameof(LiveSessionRuntimeFactory.Create)),
            (typeof(LiveCombatModeCommandSlot), "BindOwned"),
            (typeof(RuntimeDiagnosticCommandSlot), "BindOwned"),
            (typeof(GameplayInputActionRouter), nameof(GameplayInputActionRouter.Create)),
            (typeof(GameplayInputActionRouter), nameof(GameplayInputActionRouter.Attach)),
            (typeof(IGameWindowSessionPlayerPublication), "PublishSessionPlayer"));

        Assert.DoesNotContain(
            typeof(LiveSessionRuntimeFactory).GetFields(
                BindingFlags.Instance | BindingFlags.NonPublic),
            field => field.FieldType == typeof(GameWindow));
    }

    [Fact]
    public void GraphicalCompositionPausesOnlyWhenCharacterSelectorIsAbsent()
    {
        MethodInfo complete = RequiredMethod(
            typeof(SessionPlayerCompositionPhase),
            "CompleteSessionPlayer");
        IReadOnlyList<CompiledCall> sessionCalls =
            CompiledCallGraph.Read(complete);
        Assert.Contains(
            sessionCalls,
            call => call.Target.DeclaringType == typeof(RuntimeOptions)
                && call.Target.Name == "get_LiveCharacterSelector");
        Assert.Contains(
            sessionCalls,
            call => call.Target.DeclaringType == typeof(LiveSessionConnectOptions)
                && call.Target.IsConstructor);
        Assert.DoesNotContain(
            sessionCalls,
            call => call.Target.DeclaringType == typeof(CharacterList)
                && call.Target.Name == nameof(CharacterList.TrySelectFirstAvailable));

        MethodInfo retainedUi = typeof(RetailInteractionRetainedUiCompositionFactory)
            .GetMethod(nameof(
                RetailInteractionRetainedUiCompositionFactory.CreateRetainedUi))!;
        IReadOnlyList<CompiledCall> uiCalls = CompiledCallGraph.Read(retainedUi);
        Assert.Contains(
            uiCalls,
            call => call.Target.DeclaringType == typeof(RuntimeOptions)
                && call.Target.Name == "get_LiveCharacterSelector");
        Assert.DoesNotContain(
            uiCalls,
            call => call.Target.DeclaringType == typeof(LiveSessionConnectOptions)
                || call.Target.DeclaringType == typeof(CharacterList)
                    && call.Target.Name == nameof(CharacterList.TrySelectFirstAvailable));
        Assert.Contains(
            uiCalls,
            call => call.Target.DeclaringType
                    == typeof(CharacterSelectionRuntimeBindings)
                && call.Target.IsConstructor);
        Assert.Contains(
            uiCalls,
            call => call.Target.DeclaringType
                    == typeof(AcDream.App.UI.Layout.CharacterCreationRuntimeBindings)
                && call.Target.IsConstructor);
    }

    private sealed class RetryBinding(
        string name,
        List<string> calls,
        int failures) : IDisposable
    {
        private int _failures = failures;

        public void Dispose()
        {
            calls.Add(name);
            if (_failures-- > 0)
                throw new InvalidOperationException("retry");
        }
    }

    private static MethodInfo RequiredMethod(Type type, string name) =>
        type.GetMethod(
            name,
            BindingFlags.Instance | BindingFlags.Static
                | BindingFlags.Public | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(type.FullName, name);

    private static void AssertCallOrder(
        IReadOnlyList<CompiledCall> calls,
        params (Type DeclaringType, string MethodName)[] expected)
    {
        int cursor = -1;
        foreach ((Type declaringType, string methodName) in expected)
        {
            int found = calls
                .Select((call, index) => (call, index))
                .FirstOrDefault(
                    pair => pair.index > cursor
                        && MatchesType(pair.call.Target.DeclaringType, declaringType)
                        && pair.call.Target.Name == methodName,
                    defaultValue: (default, -1))
                .index;
            Assert.True(
                found > cursor,
                $"Missing compiled edge after index {cursor}: "
                    + $"{declaringType.FullName}.{methodName}");
            cursor = found;
        }
    }

    private static bool MatchesType(Type? actual, Type expected) =>
        actual == expected
        || expected.IsGenericTypeDefinition
            && actual?.IsGenericType == true
            && actual.GetGenericTypeDefinition() == expected;
}
