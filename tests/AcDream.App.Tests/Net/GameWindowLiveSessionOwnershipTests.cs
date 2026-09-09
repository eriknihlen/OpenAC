using System.Reflection;
using AcDream.App.Net;
using AcDream.App.Rendering;
using AcDream.App.Tests.Architecture;
using AcDream.Core.Net;
using AcDream.Runtime;
using AcDream.Runtime.Session;

namespace AcDream.App.Tests.Net;

public sealed class GameWindowLiveSessionOwnershipTests
{
    private const BindingFlags PrivateInstance =
        BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void GameWindowRetainsCanonicalRuntimeAndFocusedHostButNoMirroredSession()
    {
        FieldInfo[] fields = typeof(GameWindow).GetFields(PrivateInstance);

        Assert.Contains(
            fields,
            field => field.Name == "_runtime"
                && field.FieldType == typeof(GameRuntime));
        Assert.Contains(
            fields,
            field => field.Name == "_liveSessionHost"
                && field.FieldType == typeof(LiveSessionHost));
        Assert.DoesNotContain(
            fields,
            field => field.Name == "_liveSessionController"
                || field.FieldType == typeof(LiveSessionController));
        Assert.DoesNotContain(fields, field => field.Name == "_liveSession");
        Assert.DoesNotContain(fields, field => field.FieldType == typeof(WorldSession));
        Assert.DoesNotContain(fields, field => field.FieldType == typeof(LiveSessionResetPlan));
        Assert.DoesNotContain(fields, field => field.Name == "_liveSessionEvents");
        Assert.DoesNotContain(fields, field => field.Name == "_liveSessionCommands");
    }

    [Fact]
    public void GraphicalSessionSourceBorrowsCanonicalRuntimeState()
    {
        FieldInfo[] fields = typeof(LiveSessionAppSource).GetFields(PrivateInstance);

        Assert.Equal(2, fields.Length);
        Assert.Contains(
            fields,
            field => field.Name == "_session"
                && field.FieldType == typeof(LiveSessionController));
        Assert.Contains(
            fields,
            field => field.Name == "_commands"
                && field.FieldType == typeof(LiveSessionCommandSurface));
        Assert.DoesNotContain(fields, field => field.FieldType == typeof(WorldSession));
        Assert.DoesNotContain(fields, field => field.FieldType == typeof(bool));
        Assert.DoesNotContain(
            fields,
            field => field.FieldType == typeof(RuntimeGenerationToken)
                || field.FieldType == typeof(ulong));
    }

    [Fact]
    public void ProductionWindowConstructsOnlyTheCanonicalRuntimeRoot()
    {
        IReadOnlyList<CompiledCall> calls =
            CompiledCallGraph.ReadDeclared(typeof(GameWindow));

        Assert.Single(
            calls,
            call => call.Target.DeclaringType == typeof(GameRuntime)
                && call.Target.IsConstructor);
        Assert.Single(
            calls,
            call => call.Target.DeclaringType == typeof(GameRuntime)
                && call.Target.Name == nameof(GameRuntime.AcquireHostLease));
        HashSet<string> forbiddenRuntimeRoots =
        [
            "RuntimeEntityObjectLifetime",
            "RuntimeInventoryState",
            "RuntimeCharacterState",
            "RuntimeCommunicationState",
            "RuntimeActionState",
            "RuntimeLocalPlayerMovementState",
            "RuntimeWorldTransitState",
            nameof(LiveSessionController),
            "GameRuntimeClock",
        ];
        Assert.DoesNotContain(
            calls,
            call => call.Target.IsConstructor
                && call.Target.DeclaringType is { } type
                && forbiddenRuntimeRoots.Contains(type.Name));
    }

    [Fact]
    public void ProductionSourceConstructsOnlyOneLiveSessionCommandSurface()
    {
        int total = typeof(GameWindow).Assembly
            .GetTypes()
            .SelectMany(CompiledCallGraph.ReadDeclared)
            .Count(call => call.Target.DeclaringType
                    == typeof(LiveSessionCommandSurface)
                && call.Target.IsConstructor);

        Assert.Equal(1, total);
    }

    [Theory]
    [InlineData("TryStartLiveSession")]
    [InlineData("ClearInboundEntityState")]
    [InlineData("WireLiveSessionEvents")]
    [InlineData("DisposeLiveSessionRouting")]
    [InlineData("CreateLiveSessionBinding")]
    [InlineData("ApplyLiveSessionSelection")]
    [InlineData("ApplyLiveSessionEnteredWorld")]
    public void DisplacedLifecycleBodiesAreAbsent(string methodName)
    {
        Assert.Null(typeof(GameWindow).GetMethod(methodName, PrivateInstance));
    }

    [Fact]
    public void LiveSessionRuntimeFactoryBindsCharacterCreatedAndCreationFailedToTheStatusWriter()
    {
        MethodInfo create = typeof(LiveSessionRuntimeFactory).GetMethod(
            nameof(LiveSessionRuntimeFactory.Create))!;
        MethodBase[] targets = CompiledCallGraph.ReadMethodReferences(create)
            .Select(call => call.Target)
            .Where(method => method.GetMethodBody() is not null)
            .Distinct()
            .ToArray();
        MethodBase created = Assert.Single(
            targets,
            target => CallsStatusWriter(target, nameof(SessionStatusWriter.CharacterCreated)));
        MethodBase failed = Assert.Single(
            targets,
            target => CallsStatusWriter(target, nameof(SessionStatusWriter.CreationFailed)));

        Assert.Contains(
            created.GetParameters(),
            parameter => parameter.ParameterType
                == typeof(RuntimeCharacterCreationIdentity));
        AssertCallOrder(
            created,
            (typeof(RuntimeCharacterCreationIdentity), "get_Guid"),
            (typeof(RuntimeCharacterCreationIdentity), "get_Name"),
            (typeof(SessionStatusWriter), nameof(SessionStatusWriter.CharacterCreated)));
        Assert.Contains(
            failed.GetParameters(),
            parameter => parameter.ParameterType
                == typeof(RuntimeCharacterCreationRejection));
        AssertCallOrder(
            failed,
            (typeof(RuntimeCharacterCreationRejection), "get_RawCode"),
            (typeof(RuntimeCharacterCreationRejection), "get_Reason"),
            (typeof(RuntimeCharacterCreationRejection), "get_AttemptedName"),
            (typeof(SessionStatusWriter), nameof(SessionStatusWriter.CreationFailed)));
    }

    private static bool CallsStatusWriter(MethodBase method, string methodName) =>
        CompiledCallGraph.Read(method).Any(call =>
            call.Target.DeclaringType == typeof(SessionStatusWriter)
            && call.Target.Name == methodName);

    private static void AssertCallOrder(
        MethodBase method,
        params (Type Type, string Method)[] expected)
    {
        IReadOnlyList<CompiledCall> calls = CompiledCallGraph.Read(method);
        int cursor = -1;
        foreach ((Type type, string name) in expected)
        {
            int found = CompiledCallGraph.IndexOf(calls, type, name, cursor + 1);
            Assert.True(found > cursor, $"Missing compiled edge {type.FullName}.{name}.");
            cursor = found;
        }
    }
}
