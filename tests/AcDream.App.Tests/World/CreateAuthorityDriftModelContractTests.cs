using System.Reflection;
using System.Reflection.Emit;
using AcDream.App.Tests.Architecture;
using AcDream.Runtime.Entities;

namespace AcDream.App.Tests.World;

public sealed class CreateAuthorityDriftModelContractTests
{
    private const BindingFlags Declared = BindingFlags.Instance
        | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic
        | BindingFlags.DeclaredOnly;

    [Fact]
    public void HandCalledDriftProbe_StillModelsTheExecutorDrainAdvance()
    {
        MethodInfo drain = RequiredMethod(
            typeof(RuntimeInitialCreateContinuationExecutor),
            "ApplyWeenieDescriptionAction");
        AssertCallOrder(
            drain,
            (typeof(RuntimeEntityDirectory),
                nameof(RuntimeEntityDirectory.ApplyAcceptedWeenieDescriptionSnapshot)),
            (typeof(RuntimeEntityDirectory), nameof(RuntimeEntityDirectory.RefreshSnapshot)),
            (typeof(RuntimeEntityDirectory),
                nameof(RuntimeEntityDirectory.AdvanceCreateAuthority)),
            (typeof(RuntimeInitialCreateResidenceState), "AdvanceExecutorBaseline"));
        Assert.Single(
            CompiledCallGraph.Read(drain),
            call => IsAuthorityAdvance(call.Target));

        MethodInfo registration = RequiredMethod(
            typeof(RuntimeEntityObjectLifetime),
            "RegisterEntityCore");
        ParameterInfo residenceFlag = Assert.Single(
            registration.GetParameters(),
            parameter => parameter.Name == "beginInitialResidence"
                && parameter.ParameterType == typeof(bool));
        Assert.NotNull(residenceFlag);

        IReadOnlyList<CompiledCall> registrationCalls =
            CompiledCallGraph.Read(registration);
        CompiledCall refresh = Assert.Single(
            registrationCalls,
            call => call.Target.DeclaringType == typeof(RuntimeEntityDirectory)
                && call.Target.Name == nameof(RuntimeEntityDirectory.RefreshSnapshot));
        CompiledCall ordinaryAdvance = Assert.Single(
            registrationCalls,
            call => IsAuthorityAdvance(call.Target));
        Assert.True(refresh.Offset < ordinaryAdvance.Offset);
        Assert.Contains(
            CompiledCallGraph.ReadBranches(registration),
            branch => branch.OpCode.FlowControl == FlowControl.Cond_Branch
                && branch.Offset > refresh.Offset
                && branch.Offset < ordinaryAdvance.Offset
                && branch.TargetOffset > ordinaryAdvance.Offset);

        MethodBase[] productionCallSites =
            MethodsCallingRuntimeAuthorityAdvance().ToArray();
        Assert.Equal(2, productionCallSites.Length);
        Assert.Contains(drain, productionCallSites);
        Assert.Contains(registration, productionCallSites);
    }

    private static IEnumerable<MethodBase> MethodsCallingRuntimeAuthorityAdvance() =>
        typeof(RuntimeEntityObjectLifetime).Assembly.GetTypes()
            .SelectMany(type => type.GetMethods(Declared)
                .Cast<MethodBase>()
                .Concat(type.GetConstructors(Declared)))
            .Where(method => method.GetMethodBody() is not null)
            .Where(method => CompiledCallGraph.Read(method).Any(call =>
                IsAuthorityAdvance(call.Target)));

    private static bool IsAuthorityAdvance(MethodBase method) =>
        method.DeclaringType == typeof(RuntimeEntityDirectory)
        && method.Name == nameof(RuntimeEntityDirectory.AdvanceCreateAuthority);

    private static MethodInfo RequiredMethod(Type owner, string name) =>
        owner.GetMethod(
            name,
            BindingFlags.Instance | BindingFlags.Static
                | BindingFlags.Public | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(owner.FullName, name);

    private static void AssertCallOrder(
        MethodBase method,
        params (Type Type, string Method)[] expected)
    {
        IReadOnlyList<CompiledCall> calls = CompiledCallGraph.Read(method);
        int cursor = -1;
        foreach ((Type type, string name) in expected)
        {
            int found = Enumerable.Range(cursor + 1, calls.Count - cursor - 1)
                .FirstOrDefault(index => calls[index].Target.DeclaringType == type
                    && calls[index].Target.Name == name, -1);
            Assert.True(found > cursor,
                $"Missing compiled edge after {cursor}: {type.FullName}.{name}.");
            cursor = found;
        }
    }
}
