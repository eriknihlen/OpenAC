using System.Reflection;
using AcDream.Runtime.Chat;

namespace AcDream.Runtime.Tests.Chat;

public sealed class ChatExtractionTests
{
    [Fact]
    public void CompleteChatCoreLivesInRuntimeAssembly()
    {
        Assembly runtime = typeof(GameRuntime).Assembly;
        Type[] extracted =
        [
            typeof(ChatInputParser),
            typeof(ChatCommandRouter),
            typeof(RetailClientCommandCatalog),
            typeof(RetailCommandHelpTable),
            typeof(RetailChannelTagTable),
            typeof(ChannelResolver),
            typeof(ICommandBus),
            typeof(LiveCommandBus),
            typeof(NullCommandBus),
            typeof(ChatChannelKind),
            typeof(ClientCommandId),
            typeof(SendChatCmd),
            typeof(SendServerCommandCmd),
            typeof(SendRawChannelCmd),
            typeof(ExecuteClientCommandCmd),
        ];

        Assert.All(extracted, type => Assert.Same(runtime, type.Assembly));
        Assert.DoesNotContain(
            runtime.GetReferencedAssemblies(),
            reference => reference.Name is "AcDream.App"
                or "AcDream.UI.Abstractions");
    }

    [Fact]
    public void RouterDependsOnlyOnTheFourMemberFeedbackContract()
    {
        string[] members = typeof(IChatCommandFeedback)
            .GetMembers(BindingFlags.Instance | BindingFlags.Public)
            .Where(static member => member.MemberType is
                MemberTypes.Method or MemberTypes.Property)
            .Where(static member => member is not MethodInfo method
                || !method.IsSpecialName)
            .Select(static member => member.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            [
                "LastIncomingTellSender",
                "LastOutgoingTellTarget",
                "ShowInterfaceText",
                "ShowSystemMessage",
            ],
            members);
    }
}
