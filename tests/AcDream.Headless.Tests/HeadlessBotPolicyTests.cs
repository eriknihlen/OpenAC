using System.Reflection;
using AcDream.Headless.Policies;
using AcDream.Runtime;

namespace AcDream.Headless.Tests;

public sealed class HeadlessBotPolicyTests
{
    [Fact]
    public void IdlePolicyIsPassiveAndNeverCompletesAutonomously()
    {
        var policy = new IdleHeadlessBotPolicy();
        IGameRuntimeView view = CreateNoTouchProxy<IGameRuntimeView>(
            out InvocationCountingProxy viewCalls);
        IGameRuntimeCommands commands =
            CreateNoTouchProxy<IGameRuntimeCommands>(
                out InvocationCountingProxy commandCalls);

        for (int index = 0; index < 3; index++)
            policy.Tick(view, commands);

        RuntimeLifecycleDelta lifecycle = default;
        RuntimeCommandDelta command = default;
        RuntimeEntityDelta entity = default;
        RuntimeInventoryDelta inventory = default;
        RuntimeChatDelta chat = default;
        RuntimeMovementDelta movement = default;
        RuntimePortalDelta portal = default;
        RuntimeCombatDelta combat = default;
        policy.OnLifecycle(in lifecycle);
        policy.OnCommand(in command);
        policy.OnEntity(in entity);
        policy.OnInventory(in inventory);
        policy.OnChat(in chat);
        policy.OnMovement(in movement);
        policy.OnPortal(in portal);
        policy.OnCombat(in combat);

        Assert.False(policy.IsComplete);
        Assert.Equal(0, viewCalls.InvocationCount);
        Assert.Equal(0, commandCalls.InvocationCount);

        policy.Dispose();
        policy.Dispose();
        Assert.False(policy.IsComplete);
    }

    private static T CreateNoTouchProxy<T>(
        out InvocationCountingProxy proxy)
        where T : class
    {
        T value = DispatchProxy.Create<T, InvocationCountingProxy>();
        proxy = (InvocationCountingProxy)(object)value;
        return value;
    }

    public class InvocationCountingProxy : DispatchProxy
    {
        public int InvocationCount { get; private set; }

        protected override object? Invoke(
            MethodInfo? targetMethod,
            object?[]? args)
        {
            InvocationCount++;
            throw new InvalidOperationException(
                $"Idle policy unexpectedly invoked {targetMethod?.Name}.");
        }
    }
}
