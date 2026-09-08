using AcDream.Core.Chat;
using AcDream.Core.Net.Messages;
using AcDream.Runtime.Gameplay;
using AcDream.Runtime.Session;

namespace AcDream.Runtime.Chat;

public sealed record LiveChatCommandBindings(
    Action<ExecuteClientCommandCmd> ExecuteClientCommand,
    RuntimeCommunicationState Communication,
    ChatLog Chat,
    TurbineChatState TurbineChat,
    RuntimeCharacterState CharacterState,
    Func<uint> PlayerGuid,
    Action<string> SendTalk,
    Action<string, string> SendTell,
    Action<uint, string> SendChannel,
    Action<uint, uint, uint, uint, string, uint> SendTurbineChat,
    Action<string>? Log = null,
    Func<string, RetailChatPose?>? ResolvePose = null,
    Action<uint>? ExecuteMotion = null,
    Action<string>? SendSoulEmote = null);

public sealed class LiveChatCommandRoute
    : ILiveSessionCommandRouting,
      ICommandBus
{
    private static readonly Dictionary<ChatChannelKind, ChatChannelKindLite>
        TurbineChannelKinds = new()
        {
            [ChatChannelKind.Allegiance] = ChatChannelKindLite.Allegiance,
            [ChatChannelKind.General] = ChatChannelKindLite.General,
            [ChatChannelKind.Trade] = ChatChannelKindLite.Trade,
            [ChatChannelKind.Lfg] = ChatChannelKindLite.Lfg,
            [ChatChannelKind.Roleplay] = ChatChannelKindLite.Roleplay,
            [ChatChannelKind.Society] = ChatChannelKindLite.Society,
            [ChatChannelKind.Olthoi] = ChatChannelKindLite.Olthoi,
        };

    private readonly object _gate = new();
    private LiveCommandBus? _commands;
    private int _state;

    public LiveChatCommandRoute(LiveChatCommandBindings bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(bindings.ExecuteClientCommand);
        ArgumentNullException.ThrowIfNull(bindings.Communication);
        ArgumentNullException.ThrowIfNull(bindings.Chat);
        ArgumentNullException.ThrowIfNull(bindings.TurbineChat);
        ArgumentNullException.ThrowIfNull(bindings.CharacterState);
        ArgumentNullException.ThrowIfNull(bindings.PlayerGuid);
        ArgumentNullException.ThrowIfNull(bindings.SendTalk);
        ArgumentNullException.ThrowIfNull(bindings.SendTell);
        ArgumentNullException.ThrowIfNull(bindings.SendChannel);
        ArgumentNullException.ThrowIfNull(bindings.SendTurbineChat);

        var commands = new LiveCommandBus();
        commands.Register<ExecuteClientCommandCmd>(command =>
            SendIfActive(() => bindings.ExecuteClientCommand(command)));
        commands.Register<SendServerCommandCmd>(command =>
        {
            if (!string.IsNullOrEmpty(command.Text))
                SendIfActive(() => bindings.SendTalk(command.Text));
        });
        commands.Register<SendChatCmd>(command => RouteChat(bindings, command));
        commands.Register<SendRawChannelCmd>(command =>
            SendIfActive(() =>
                bindings.SendChannel(command.ChannelId, command.Text)));
        _commands = commands;
    }

    public bool IsActive
    {
        get
        {
            lock (_gate)
                return _state == 1;
        }
    }

    public void Activate()
    {
        lock (_gate)
        {
            if (_state == 2)
                throw new ObjectDisposedException(nameof(LiveChatCommandRoute));
            _state = 1;
        }
    }

    public void Publish<T>(T command) where T : notnull
    {
        if (!TryPublish(command))
        {
            Console.WriteLine(
                $"[LiveChatCommandRoute] unsupported command type "
                + $"{typeof(T).FullName}; dropping.");
        }
    }

    public bool TryPublish<T>(T command) where T : notnull
    {
        ArgumentNullException.ThrowIfNull(command);
        Type type = typeof(T);
        if (type != typeof(ExecuteClientCommandCmd)
            && type != typeof(SendServerCommandCmd)
            && type != typeof(SendChatCmd)
            && type != typeof(SendRawChannelCmd))
        {
            return false;
        }

        lock (_gate)
        {
            if (_state == 1)
                _commands?.Publish(command);
        }
        return true;
    }

    public void Dispose()
    {
        LiveCommandBus? commands;
        lock (_gate)
        {
            _state = 2;
            commands = _commands;
            _commands = null;
        }
        commands?.Clear();
    }

    private void RouteChat(
        LiveChatCommandBindings bindings,
        SendChatCmd command)
    {
        if (string.IsNullOrEmpty(command.Text))
            return;

        switch (command.Channel)
        {
            case ChatChannelKind.Say:
                RoutePublicChat(bindings, command.Text);
                return;

            case ChatChannelKind.Tell:
                if (string.IsNullOrEmpty(command.TargetName))
                    return;
                SendIfActive(() => bindings.SendTell(command.TargetName, command.Text));
                return;
        }

        if (command.Channel == ChatChannelKind.Allegiance
            && !bindings.TurbineChat.Enabled)
        {
            RouteLegacyChannel(
                bindings,
                ChatChannelKind.AllegianceBroadcast,
                command.Text);
            return;
        }

        if (TurbineChannelKinds.TryGetValue(
                command.Channel,
                out ChatChannelKindLite liteKind))
        {
            RouteTurbineChat(bindings, liteKind, command.Text);
            return;
        }

        RouteLegacyChannel(bindings, command.Channel, command.Text);
    }

    private void RoutePublicChat(
        LiveChatCommandBindings bindings,
        string text)
    {
        string spoken = RetailPublicChatParser.ExtractPoses(
            text,
            bindings.ResolvePose,
            pose =>
            {
                bindings.ExecuteMotion?.Invoke(pose.MotionCommand);
                if (!string.IsNullOrEmpty(pose.OthersText))
                    bindings.SendSoulEmote?.Invoke(pose.OthersText);
                if (!string.IsNullOrEmpty(pose.SelfText))
                    bindings.Chat.OnSoulEmote("You", pose.SelfText, 0u);
            });
        if (!string.IsNullOrEmpty(spoken))
            SendIfActive(() => bindings.SendTalk(spoken));
    }

    private void RouteTurbineChat(
        LiveChatCommandBindings bindings,
        ChatChannelKindLite kind,
        string text)
    {
        TurbineChatState turbineChat = bindings.TurbineChat;
        TurbineChatGateResult gate = TurbineChatMembershipGate.Evaluate(
            kind,
            turbineChat,
            bindings.CharacterState.Options,
            bindings.CharacterState.IsOlthoiPlayer);

        if (gate.Status != TurbineChatGateStatus.Allowed)
        {
            if (TurbineChatMembershipGate.ResolveRefusalText(gate) is
                (string refusalText, RetailLogTextType refusalType))
            {
                bindings.Communication.AddText(refusalText, refusalType);
            }
            return;
        }

        uint cookie = turbineChat.NextContextId();
        uint senderGuid = bindings.PlayerGuid();
        bindings.Log?.Invoke(
            $"chat: outbound TurbineChat {gate.DisplayName} "
            + $"room=0x{gate.RoomId:X8} chatType={gate.ChatType} "
            + $"cookie=0x{cookie:X} sender=0x{senderGuid:X8} len={text.Length}");
        SendIfActive(() => bindings.SendTurbineChat(
            gate.RoomId,
            gate.ChatType,
            (uint)TurbineChat.DispatchType.SendToRoomById,
            senderGuid,
            text,
            cookie));
    }

    private void RouteLegacyChannel(
        LiveChatCommandBindings bindings,
        ChatChannelKind channel,
        string text)
    {
        ChannelResolver.Resolved? legacy = ChannelResolver.Resolve(channel);
        if (legacy is null)
        {
            bindings.Log?.Invoke(
                $"chat: SendChatCmd kind={channel} dropped (no legacy id)");
            return;
        }

        bindings.Log?.Invoke(
            $"chat: outbound legacy ChatChannel {legacy.Value.DisplayName} "
            + $"id=0x{legacy.Value.ChannelId:X8} len={text.Length}");
        if (!SendIfActive(() =>
                bindings.SendChannel(legacy.Value.ChannelId, text)))
        {
            return;
        }

        bool serverEchoes = new ChatChannelInfo.Legacy(
            legacy.Value.ChannelId,
            legacy.Value.DisplayName).IsSelfEchoChannel();
        if (serverEchoes)
            return;

        bindings.Chat.OnSelfSent(
            ChatKind.Channel,
            text,
            targetOrChannel: legacy.Value.DisplayName,
            logTextType: LegacyChannelChatType.Resolve(
                legacy.Value.ChannelId,
                ownSend: true));
    }

    private bool SendIfActive(Action send)
    {
        lock (_gate)
        {
            if (_state != 1)
                return false;
            send();
            return true;
        }
    }
}

public sealed class LiveChatCommandSurface : IPluginCommandBus
{
    private readonly object _gate = new();
    private readonly Func<string, bool>? _tryHandlePluginCommand;
    private LiveChatCommandRoute? _active;

    public LiveChatCommandSurface(Func<string, bool>? tryHandlePluginCommand = null)
    {
        _tryHandlePluginCommand = tryHandlePluginCommand;
    }

    public ILiveSessionCommandRouting Attach(LiveChatCommandRoute route)
    {
        ArgumentNullException.ThrowIfNull(route);
        lock (_gate)
        {
            if (_active is not null)
            {
                throw new InvalidOperationException(
                    "A live chat command route is already attached.");
            }
            _active = route;
            return new RouteLease(this, route);
        }
    }

    public void Publish<T>(T command) where T : notnull
    {
        LiveChatCommandRoute? route;
        lock (_gate)
            route = _active;
        route?.Publish(command);
    }

    public bool TryHandlePluginCommand(string commandLine) =>
        _tryHandlePluginCommand?.Invoke(commandLine) == true;

    private void Release(LiveChatCommandRoute expected)
    {
        expected.Dispose();
        lock (_gate)
        {
            if (ReferenceEquals(_active, expected))
                _active = null;
        }
    }

    private sealed class RouteLease(
        LiveChatCommandSurface owner,
        LiveChatCommandRoute route)
        : ILiveSessionCommandRouting
    {
        private readonly object _gate = new();
        private LiveChatCommandSurface? _owner = owner;

        public void Activate() => route.Activate();

        public void Dispose()
        {
            lock (_gate)
            {
                if (_owner is null)
                    return;
                _owner.Release(route);
                _owner = null;
            }
        }
    }
}
