using AcDream.App.Input;
using AcDream.App.Interaction;
using AcDream.App.Net;
using AcDream.Core.Items;
using AcDream.Core.Selection;
using AcDream.Runtime;
using AcDream.Runtime.Gameplay;
using AcDream.Runtime.Session;
using AcDream.UI.Abstractions;
using AcDream.UI.Abstractions.Input;

namespace AcDream.App.Runtime;

internal sealed class CurrentGameRuntimeCommandAdapter
    : IRuntimeSessionCommands,
      IRuntimeSelectionCommands,
      IRuntimeCombatCommands,
      IRuntimeMagicCommands,
      IRuntimeMovementCommands,
      IRuntimeChatCommands,
      IRuntimePortalCommands,
      IRuntimeInventoryStateCommands,
      IRuntimeSpellbookCommands,
      IRuntimeCharacterCommands,
      IRuntimeSocialCommands,
      IRuntimeFellowshipCommands,
      IRuntimeAllegianceCommands
{
    private readonly LiveSessionController _session;
    private readonly LiveSessionHost _sessionHost;
    private readonly ICommandBus _commands;
    private readonly IGameRuntimeView _view;
    private readonly RuntimeInventoryState _inventory;
    private readonly RuntimeCharacterState _character;
    private readonly RuntimeActionState _actions;
    private readonly RuntimeLocalPlayerMovementState _movement;
    private readonly RuntimeFellowshipState _fellowship;
    private readonly SelectionInteractionController _selection;
    private readonly IGameRuntimeEventSink _events;

    public CurrentGameRuntimeCommandAdapter(
        LiveSessionController session,
        LiveSessionHost sessionHost,
        ICommandBus commands,
        IGameRuntimeView view,
        RuntimeInventoryState inventory,
        RuntimeCharacterState character,
        RuntimeActionState actions,
        RuntimeLocalPlayerMovementState movement,
        RuntimeFellowshipState fellowship,
        SelectionInteractionController selection,
        IGameRuntimeEventSink events)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _sessionHost = sessionHost ?? throw new ArgumentNullException(nameof(sessionHost));
        _commands = commands ?? throw new ArgumentNullException(nameof(commands));
        _view = view ?? throw new ArgumentNullException(nameof(view));
        _inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
        _character = character ?? throw new ArgumentNullException(nameof(character));
        _actions = actions
            ?? throw new ArgumentNullException(nameof(actions));
        _movement = movement
            ?? throw new ArgumentNullException(nameof(movement));
        _fellowship = fellowship
            ?? throw new ArgumentNullException(nameof(fellowship));
        _selection = selection ?? throw new ArgumentNullException(nameof(selection));
        _events = events ?? throw new ArgumentNullException(nameof(events));
    }

    public RuntimeSessionStartResult Start(
        RuntimeGenerationToken expectedGeneration)
    {
        RuntimeLifecycleState previous = _view.Lifecycle.State;
        RuntimeCommandStatus gate = Validate(expectedGeneration, requireWorld: false);
        if (gate != RuntimeCommandStatus.Accepted)
            return RejectedStart(gate);

        RuntimeSessionStartResult result =
            _sessionHost.Start(expectedGeneration);
        _events.EmitCommand(
            RuntimeCommandDomain.Session,
            operation: 0,
            ToCommandStatus(result.Status),
            result.CharacterId,
            result.CharacterName);
        _events.EmitLifecycle(previous, _view.Lifecycle.State);
        return result;
    }

    public RuntimeSessionStartResult Reconnect(
        RuntimeGenerationToken expectedGeneration)
    {
        RuntimeLifecycleState previous = _view.Lifecycle.State;
        RuntimeCommandStatus gate = Validate(expectedGeneration, requireWorld: false);
        if (gate != RuntimeCommandStatus.Accepted)
            return RejectedStart(gate);

        RuntimeSessionStartResult result =
            _sessionHost.Reconnect(expectedGeneration);
        _events.EmitCommand(
            RuntimeCommandDomain.Session,
            operation: 1,
            ToCommandStatus(result.Status),
            result.CharacterId,
            result.CharacterName);
        _events.EmitLifecycle(previous, _view.Lifecycle.State);
        return result;
    }

    public RuntimeTeardownAcknowledgement Stop(
        RuntimeGenerationToken expectedGeneration)
    {
        RuntimeLifecycleState previous = _view.Lifecycle.State;
        RuntimeCommandStatus gate = Validate(expectedGeneration, requireWorld: false);
        if (gate != RuntimeCommandStatus.Accepted)
        {
            return new RuntimeTeardownAcknowledgement(
                expectedGeneration,
                _view.Generation,
                gate,
                RuntimeTeardownStage.None);
        }

        RuntimeTeardownAcknowledgement acknowledgement =
            _sessionHost.Stop(expectedGeneration);
        _events.EmitCommand(
            RuntimeCommandDomain.Session,
            operation: 2,
            acknowledgement.Status,
            text: acknowledgement.Error?.GetType().Name ?? string.Empty);
        _events.EmitLifecycle(previous, _view.Lifecycle.State);
        return acknowledgement;
    }

    public RuntimeCommandResult Execute(
        RuntimeGenerationToken expectedGeneration,
        RuntimeSelectionCommand command)
    {
        RuntimeCommandStatus gate = Validate(expectedGeneration, requireWorld: true);
        if (gate != RuntimeCommandStatus.Accepted)
            return Result(gate);

        InputAction action = command switch
        {
            RuntimeSelectionCommand.SelectClosestHostile =>
                InputAction.SelectionClosestMonster,
            RuntimeSelectionCommand.SelectPrevious =>
                InputAction.SelectionPreviousSelection,
            RuntimeSelectionCommand.ExamineSelected =>
                InputAction.SelectionExamine,
            RuntimeSelectionCommand.UseSelected =>
                InputAction.UseSelected,
            RuntimeSelectionCommand.PickUpSelected =>
                InputAction.SelectionPickUp,
            _ => InputAction.None,
        };
        RuntimeCommandStatus status = action != InputAction.None
            && _selection.HandleInputAction(action)
                ? RuntimeCommandStatus.Accepted
                : RuntimeCommandStatus.Unsupported;
        uint selected = _actions.Selection.SelectedObjectId ?? 0u;
        _events.EmitCommand(
            RuntimeCommandDomain.Selection,
            (int)command,
            status,
            selected);
        return Result(status, selected);
    }

    public RuntimeCommandResult SelectObject(
        RuntimeGenerationToken expectedGeneration,
        uint objectId)
    {
        RuntimeCommandStatus gate = Validate(
            expectedGeneration,
            requireWorld: true);
        if (gate != RuntimeCommandStatus.Accepted)
            return Result(gate);

        RuntimeCommandStatus status =
            objectId != 0u
            && _inventory.Objects.Get(objectId) is not null
                ? RuntimeCommandStatus.Accepted
                : RuntimeCommandStatus.Rejected;
        if (status == RuntimeCommandStatus.Accepted)
        {
            _actions.Selection.Select(
                objectId,
                SelectionChangeSource.Plugin);
        }
        _events.EmitCommand(
            RuntimeCommandDomain.Selection,
            operation: 0x100,
            status,
            objectId);
        return Result(status, objectId);
    }

    public RuntimeCommandResult Clear(
        RuntimeGenerationToken expectedGeneration)
    {
        RuntimeCommandStatus gate = Validate(
            expectedGeneration,
            requireWorld: true);
        if (gate != RuntimeCommandStatus.Accepted)
            return Result(gate);
        _actions.Selection.Clear(SelectionChangeSource.Plugin);
        _events.EmitCommand(
            RuntimeCommandDomain.Selection,
            operation: 0x101,
            RuntimeCommandStatus.Accepted);
        return Result(RuntimeCommandStatus.Accepted);
    }

    public RuntimeCommandResult Execute(
        RuntimeGenerationToken expectedGeneration,
        RuntimeCombatCommand command)
    {
        RuntimeCommandStatus gate = Validate(expectedGeneration, requireWorld: true);
        if (gate != RuntimeCommandStatus.Accepted)
            return Result(gate);

        RuntimeCommandStatus status;
        if (command == RuntimeCombatCommand.ToggleMode)
        {
            RuntimeCombatModeRequestResult result =
                _actions.CombatMode.Toggle();
            status = result.Status switch
            {
                RuntimeCombatModeRequestStatus.Sent =>
                    RuntimeCommandStatus.Accepted,
                RuntimeCombatModeRequestStatus.Inactive =>
                    RuntimeCommandStatus.Inactive,
                _ => RuntimeCommandStatus.Rejected,
            };
            if (AcDream.Core.Net.NetDiagnostics.ProbeNet)
            {
                Console.WriteLine(
                    $"[cmd-gate] combat toggle result={result.Status}"
                    + $" mode={result.Mode}");
            }
        }
        else
        {
            status = RuntimeCommandStatus.Unsupported;
        }

        _events.EmitCommand(RuntimeCommandDomain.Combat, (int)command, status);
        return Result(status);
    }

    public RuntimeCommandResult ExecuteAttack(
        RuntimeGenerationToken expectedGeneration,
        in RuntimeCombatAttackInput command)
    {
        RuntimeCommandStatus gate = Validate(
            expectedGeneration,
            requireWorld: true);
        if (gate != RuntimeCommandStatus.Accepted)
            return Result(gate);

        RuntimeCommandStatus status = _actions.CombatAttack.HandleCommand(
            command)
                ? RuntimeCommandStatus.Accepted
                : RuntimeCommandStatus.Unsupported;
        _events.EmitCommand(
            RuntimeCommandDomain.Combat,
            operation: 0x100 + (int)command.Command,
            status);
        return Result(status);
    }

    public RuntimeCommandResult Execute(
        RuntimeGenerationToken expectedGeneration,
        in RuntimeMagicCommand command)
    {
        RuntimeCommandStatus gate = Validate(
            expectedGeneration,
            requireWorld: true);
        if (gate != RuntimeCommandStatus.Accepted)
            return Result(gate);

        CastRequestResult cast = _actions.SpellCast.Cast(command.SpellId);
        RuntimeCommandStatus status = cast == CastRequestResult.Sent
            ? RuntimeCommandStatus.Accepted
            : RuntimeCommandStatus.Rejected;
        _events.EmitCommand(
            RuntimeCommandDomain.Magic,
            operation: (int)cast,
            status,
            command.SpellId);
        return Result(status, command.SpellId);
    }

    public RuntimeCommandResult Execute(
        RuntimeGenerationToken expectedGeneration,
        RuntimeMovementCommand command)
    {
        RuntimeCommandStatus gate = Validate(expectedGeneration, requireWorld: true);
        if (gate != RuntimeCommandStatus.Accepted)
            return Result(gate);

        RuntimeCommandStatus status = _movement.Execute(command)
            ? RuntimeCommandStatus.Accepted
            : RuntimeCommandStatus.Unsupported;

        _events.EmitCommand(RuntimeCommandDomain.Movement, (int)command, status);
        return Result(status);
    }

    public RuntimeCommandResult ExecuteMotion(
        RuntimeGenerationToken expectedGeneration,
        uint motionCommand)
    {
        RuntimeCommandStatus gate = Validate(
            expectedGeneration,
            requireWorld: true);
        if (gate != RuntimeCommandStatus.Accepted)
            return Result(gate);

        RuntimeCommandStatus status = _movement.ExecuteMotion(motionCommand)
            ? RuntimeCommandStatus.Accepted
            : RuntimeCommandStatus.Unsupported;

        _events.EmitCommand(
            RuntimeCommandDomain.Movement,
            operation: 0x102,
            status,
            motionCommand);
        return Result(status, motionCommand);
    }

    public RuntimeCommandResult SetIntent(
        RuntimeGenerationToken expectedGeneration,
        in MovementInput input)
    {
        RuntimeCommandStatus gate = Validate(
            expectedGeneration,
            requireWorld: true);
        if (gate != RuntimeCommandStatus.Accepted)
            return Result(gate);
        _movement.SetCommandInput(input);
        _events.EmitCommand(
            RuntimeCommandDomain.Movement,
            operation: 0x100,
            RuntimeCommandStatus.Accepted);
        return Result(RuntimeCommandStatus.Accepted);
    }

    public RuntimeCommandResult ClearIntent(
        RuntimeGenerationToken expectedGeneration)
    {
        RuntimeCommandStatus gate = Validate(
            expectedGeneration,
            requireWorld: true);
        if (gate != RuntimeCommandStatus.Accepted)
            return Result(gate);
        _movement.ClearCommandInput();
        _events.EmitCommand(
            RuntimeCommandDomain.Movement,
            operation: 0x101,
            RuntimeCommandStatus.Accepted);
        return Result(RuntimeCommandStatus.Accepted);
    }

    public RuntimeCommandResult Execute(
        RuntimeGenerationToken expectedGeneration,
        in RuntimeChatCommand command)
    {
        RuntimeCommandStatus gate = Validate(expectedGeneration, requireWorld: true);
        if (gate != RuntimeCommandStatus.Accepted)
            return Result(gate);

        if (!TryMap(command.Channel, out ChatChannelKind channel))
        {
            _events.EmitCommand(
                RuntimeCommandDomain.Chat,
                (int)command.Channel,
                RuntimeCommandStatus.Unsupported);
            return Result(RuntimeCommandStatus.Unsupported);
        }

        _commands.Publish(new SendChatCmd(
            channel,
            command.TargetName,
            command.Text));
        _events.EmitCommand(
            RuntimeCommandDomain.Chat,
            (int)command.Channel,
            RuntimeCommandStatus.Accepted,
            text: command.Text);
        return Result(RuntimeCommandStatus.Accepted);
    }

    public RuntimeCommandResult Execute(
        RuntimeGenerationToken expectedGeneration,
        RuntimePortalCommand command)
    {
        RuntimeCommandStatus gate = Validate(expectedGeneration, requireWorld: true);
        if (gate != RuntimeCommandStatus.Accepted)
            return Result(gate);

        ClientCommandId? commandId = command switch
        {
            RuntimePortalCommand.RecallLifestone =>
                ClientCommandId.LifestoneRecall,
            RuntimePortalCommand.RecallMarketplace =>
                ClientCommandId.MarketplaceRecall,
            RuntimePortalCommand.RecallHouse =>
                ClientCommandId.HouseRecall,
            RuntimePortalCommand.RecallMansion =>
                ClientCommandId.MansionRecall,
            _ => null,
        };
        if (commandId is null)
        {
            _events.EmitCommand(
                RuntimeCommandDomain.Portal,
                (int)command,
                RuntimeCommandStatus.Unsupported);
            return Result(RuntimeCommandStatus.Unsupported);
        }

        _commands.Publish(new ExecuteClientCommandCmd(
            commandId.Value,
            string.Empty));
        _events.EmitCommand(
            RuntimeCommandDomain.Portal,
            (int)command,
            RuntimeCommandStatus.Accepted);
        return Result(RuntimeCommandStatus.Accepted);
    }

    public RuntimeCommandResult AddShortcut(
        RuntimeGenerationToken expectedGeneration,
        in RuntimeShortcutCommand command)
    {
        RuntimeCommandStatus gate = Validate(expectedGeneration, requireWorld: true);
        if (gate != RuntimeCommandStatus.Accepted)
            return Result(gate);
        var entry = new ShortcutEntry(
            command.Index,
            command.ObjectId,
            command.SpellId);
        if (!_inventory.TryAddShortcut(
                entry,
                () => _commands.Publish(new AddShortcutRuntimeCmd(entry))))
        {
            return EmitResult(
                RuntimeCommandDomain.InventoryState,
                operation: 0,
                RuntimeCommandStatus.Rejected,
                command.ObjectId);
        }

        return EmitResult(
            RuntimeCommandDomain.InventoryState,
            operation: 0,
            RuntimeCommandStatus.Accepted,
            command.ObjectId);
    }

    public RuntimeCommandResult RemoveShortcut(
        RuntimeGenerationToken expectedGeneration,
        int index)
    {
        RuntimeCommandStatus gate = Validate(expectedGeneration, requireWorld: true);
        if (gate != RuntimeCommandStatus.Accepted)
            return Result(gate);
        if (!_inventory.TryRemoveShortcut(
                index,
                () => _commands.Publish(
                    new RemoveShortcutRuntimeCmd((uint)index))))
        {
            return EmitResult(
                RuntimeCommandDomain.InventoryState,
                operation: 1,
                RuntimeCommandStatus.Rejected);
        }

        return EmitResult(
            RuntimeCommandDomain.InventoryState,
            operation: 1,
            RuntimeCommandStatus.Accepted);
    }

    public RuntimeCommandResult AddFavorite(
        RuntimeGenerationToken expectedGeneration,
        int tabIndex,
        int position,
        uint spellId)
    {
        RuntimeCommandStatus gate = Validate(expectedGeneration, requireWorld: true);
        if (gate != RuntimeCommandStatus.Accepted)
            return Result(gate);
        if (!_character.TryAddFavorite(
                tabIndex,
                position,
                spellId,
                () => _commands.Publish(new AddFavoriteRuntimeCmd(
                    spellId,
                    position,
                    tabIndex))))
        {
            return EmitResult(
                RuntimeCommandDomain.Spellbook,
                operation: 0,
                RuntimeCommandStatus.Rejected,
                spellId);
        }

        return EmitResult(
            RuntimeCommandDomain.Spellbook,
            operation: 0,
            RuntimeCommandStatus.Accepted,
            spellId);
    }

    public RuntimeCommandResult RemoveFavorite(
        RuntimeGenerationToken expectedGeneration,
        int tabIndex,
        uint spellId)
    {
        RuntimeCommandStatus gate = Validate(expectedGeneration, requireWorld: true);
        if (gate != RuntimeCommandStatus.Accepted)
            return Result(gate);
        if (!_character.TryRemoveFavorite(
                tabIndex,
                spellId,
                () => _commands.Publish(
                    new RemoveFavoriteRuntimeCmd(spellId, tabIndex))))
        {
            return EmitResult(
                RuntimeCommandDomain.Spellbook,
                operation: 1,
                RuntimeCommandStatus.Rejected,
                spellId);
        }

        return EmitResult(
            RuntimeCommandDomain.Spellbook,
            operation: 1,
            RuntimeCommandStatus.Accepted,
            spellId);
    }

    public RuntimeCommandResult SetFilter(
        RuntimeGenerationToken expectedGeneration,
        uint filters)
    {
        RuntimeCommandStatus gate = Validate(expectedGeneration, requireWorld: true);
        if (gate != RuntimeCommandStatus.Accepted)
            return Result(gate);
        _character.SetSpellbookFilter(
            filters,
            () => _commands.Publish(
                new SetSpellbookFilterRuntimeCmd(filters)));
        return EmitResult(
            RuntimeCommandDomain.Spellbook,
            operation: 2,
            RuntimeCommandStatus.Accepted);
    }

    public RuntimeCommandResult ForgetSpell(
        RuntimeGenerationToken expectedGeneration,
        uint spellId)
    {
        RuntimeCommandStatus gate = Validate(expectedGeneration, requireWorld: true);
        if (gate != RuntimeCommandStatus.Accepted)
            return Result(gate);
        if (spellId == 0u)
        {
            return EmitResult(
                RuntimeCommandDomain.Spellbook,
                operation: 3,
                RuntimeCommandStatus.Rejected,
                spellId);
        }

        _commands.Publish(new ForgetSpellRuntimeCmd(spellId));
        return EmitResult(
            RuntimeCommandDomain.Spellbook,
            operation: 3,
            RuntimeCommandStatus.Accepted,
            spellId);
    }

    public RuntimeCommandResult SetDesiredComponent(
        RuntimeGenerationToken expectedGeneration,
        uint componentId,
        uint amount)
    {
        RuntimeCommandStatus gate = Validate(expectedGeneration, requireWorld: true);
        if (gate != RuntimeCommandStatus.Accepted)
            return Result(gate);
        if (!_character.TrySetDesiredComponent(
                componentId,
                amount,
                () => _commands.Publish(new SetDesiredComponentRuntimeCmd(
                    componentId,
                    amount))))
        {
            return EmitResult(
                RuntimeCommandDomain.Spellbook,
                operation: 4,
                RuntimeCommandStatus.Rejected,
                componentId);
        }

        return EmitResult(
            RuntimeCommandDomain.Spellbook,
            operation: 4,
            RuntimeCommandStatus.Accepted,
            componentId);
    }

    public RuntimeCommandResult ClearDesiredComponents(
        RuntimeGenerationToken expectedGeneration)
    {
        RuntimeCommandStatus gate = Validate(expectedGeneration, requireWorld: true);
        if (gate != RuntimeCommandStatus.Accepted)
            return Result(gate);
        _character.ClearDesiredComponents(
            () => _commands.Publish(
                new ClearDesiredComponentsRuntimeCmd()));
        return EmitResult(
            RuntimeCommandDomain.Spellbook,
            operation: 5,
            RuntimeCommandStatus.Accepted);
    }

    public RuntimeCommandResult Advance(
        RuntimeGenerationToken expectedGeneration,
        in RuntimeAdvancementCommand command)
    {
        RuntimeCommandStatus gate = Validate(expectedGeneration, requireWorld: true);
        if (gate != RuntimeCommandStatus.Accepted)
            return Result(gate);
        if (command.StatId == 0u
            || command.Cost == 0u)
        {
            return EmitResult(
                RuntimeCommandDomain.Character,
                (int)command.Kind,
                RuntimeCommandStatus.Rejected,
                command.StatId);
        }

        switch (command.Kind)
        {
            case RuntimeAdvancementKind.Attribute:
                _commands.Publish(new RaiseAttributeRuntimeCmd(
                    command.StatId,
                    command.Cost));
                break;
            case RuntimeAdvancementKind.Vital:
                _commands.Publish(new RaiseVitalRuntimeCmd(
                    command.StatId,
                    command.Cost));
                break;
            case RuntimeAdvancementKind.Skill:
                _commands.Publish(new RaiseSkillRuntimeCmd(
                    command.StatId,
                    command.Cost));
                break;
            case RuntimeAdvancementKind.TrainSkill
                when command.Cost <= uint.MaxValue:
                _commands.Publish(new TrainSkillRuntimeCmd(
                    command.StatId,
                    (uint)command.Cost));
                break;
            default:
                return EmitResult(
                    RuntimeCommandDomain.Character,
                    (int)command.Kind,
                    RuntimeCommandStatus.Rejected,
                    command.StatId);
        }

        return EmitResult(
            RuntimeCommandDomain.Character,
            (int)command.Kind,
            RuntimeCommandStatus.Accepted,
            command.StatId);
    }

    public RuntimeCommandResult SetSingleOption(
        RuntimeGenerationToken expectedGeneration,
        uint optionId,
        bool value)
    {
        RuntimeCommandStatus gate = Validate(expectedGeneration, requireWorld: true);
        if (gate != RuntimeCommandStatus.Accepted)
            return Result(gate);
        if (!CharacterOptionTable.TryGet(optionId, out _))
        {
            return EmitResult(
                RuntimeCommandDomain.Character,
                operation: 4,
                RuntimeCommandStatus.Rejected);
        }
        _commands.Publish(new SetSingleCharacterOptionRuntimeCmd(optionId, value));
        return EmitResult(
            RuntimeCommandDomain.Character,
            operation: 4,
            RuntimeCommandStatus.Accepted);
    }

    public RuntimeCommandResult SaveOptions(
        RuntimeGenerationToken expectedGeneration)
    {
        RuntimeCommandStatus gate = Validate(expectedGeneration, requireWorld: true);
        if (gate != RuntimeCommandStatus.Accepted)
            return Result(gate);
        _commands.Publish(new SaveCharacterOptionsRuntimeCmd());
        return EmitResult(
            RuntimeCommandDomain.Character,
            operation: 5,
            RuntimeCommandStatus.Accepted);
    }

    public RuntimeCommandResult SetTitle(
        RuntimeGenerationToken expectedGeneration,
        uint titleId)
    {
        RuntimeCommandStatus gate = Validate(expectedGeneration, requireWorld: true);
        if (gate != RuntimeCommandStatus.Accepted)
            return Result(gate);
        _commands.Publish(new SetTitleRuntimeCmd(titleId));
        return EmitResult(
            RuntimeCommandDomain.Character,
            operation: 6,
            RuntimeCommandStatus.Accepted,
            titleId);
    }

    public RuntimeCommandResult Execute(
        RuntimeGenerationToken expectedGeneration,
        in RuntimeFriendCommand command)
    {
        RuntimeCommandStatus gate = Validate(expectedGeneration, requireWorld: true);
        if (gate != RuntimeCommandStatus.Accepted)
            return Result(gate);
        RuntimeCommandStatus status = RuntimeCommandStatus.Accepted;
        switch (command.Kind)
        {
            case RuntimeFriendCommandKind.Add
                when !string.IsNullOrWhiteSpace(command.Name):
                _commands.Publish(new AddFriendRuntimeCmd(command.Name));
                break;
            case RuntimeFriendCommandKind.Remove
                when command.CharacterId != 0u:
                _commands.Publish(new RemoveFriendRuntimeCmd(
                    command.CharacterId));
                break;
            case RuntimeFriendCommandKind.Clear:
                _commands.Publish(new ClearFriendsRuntimeCmd());
                break;
            case RuntimeFriendCommandKind.RequestLegacyList:
                _commands.Publish(new RequestLegacyFriendsRuntimeCmd());
                break;
            default:
                status = RuntimeCommandStatus.Rejected;
                break;
        }

        return EmitResult(
            RuntimeCommandDomain.Social,
            (int)command.Kind,
            status,
            command.CharacterId,
            command.Name);
    }

    public RuntimeCommandResult Execute(
        RuntimeGenerationToken expectedGeneration,
        in RuntimeSquelchCommand command)
    {
        RuntimeCommandStatus gate = Validate(expectedGeneration, requireWorld: true);
        if (gate != RuntimeCommandStatus.Accepted)
            return Result(gate);
        RuntimeCommandStatus status = RuntimeCommandStatus.Accepted;
        switch (command.Scope)
        {
            case RuntimeSquelchScope.Character
                when command.CharacterId != 0u
                    && !string.IsNullOrWhiteSpace(command.Name):
                _commands.Publish(new ModifyCharacterSquelchRuntimeCmd(
                    command.Add,
                    command.CharacterId,
                    command.Name,
                    command.MessageType));
                break;
            case RuntimeSquelchScope.Account
                when !string.IsNullOrWhiteSpace(command.Name):
                _commands.Publish(new ModifyAccountSquelchRuntimeCmd(
                    command.Add,
                    command.Name));
                break;
            case RuntimeSquelchScope.Global:
                _commands.Publish(new ModifyGlobalSquelchRuntimeCmd(
                    command.Add,
                    command.MessageType));
                break;
            default:
                status = RuntimeCommandStatus.Rejected;
                break;
        }

        return EmitResult(
            RuntimeCommandDomain.Social,
            operation: 4 + (int)command.Scope,
            status,
            command.CharacterId,
            command.Name);
    }


    public RuntimeCommandResult Create(
        RuntimeGenerationToken expectedGeneration,
        string fellowshipName,
        bool shareXp)
    {
        RuntimeCommandStatus gate = Validate(expectedGeneration, requireWorld: true);
        if (gate != RuntimeCommandStatus.Accepted)
            return Result(gate);
        if (string.IsNullOrWhiteSpace(fellowshipName))
        {
            return EmitResult(
                RuntimeCommandDomain.Fellowship,
                operation: 0,
                RuntimeCommandStatus.Rejected);
        }
        _commands.Publish(new FellowshipCreateRuntimeCmd(fellowshipName, shareXp));
        return EmitResult(
            RuntimeCommandDomain.Fellowship,
            operation: 0,
            RuntimeCommandStatus.Accepted);
    }

    public RuntimeCommandResult Recruit(
        RuntimeGenerationToken expectedGeneration,
        uint targetGuid)
    {
        RuntimeCommandStatus gate = Validate(expectedGeneration, requireWorld: true);
        if (gate != RuntimeCommandStatus.Accepted)
            return Result(gate);
        if (targetGuid == 0u)
        {
            return EmitResult(
                RuntimeCommandDomain.Fellowship,
                operation: 1,
                RuntimeCommandStatus.Rejected,
                targetGuid);
        }
        _commands.Publish(new FellowshipRecruitRuntimeCmd(targetGuid));
        return EmitResult(
            RuntimeCommandDomain.Fellowship,
            operation: 1,
            RuntimeCommandStatus.Accepted,
            targetGuid);
    }

    public RuntimeCommandResult Dismiss(
        RuntimeGenerationToken expectedGeneration,
        uint targetGuid)
    {
        RuntimeCommandStatus gate = Validate(expectedGeneration, requireWorld: true);
        if (gate != RuntimeCommandStatus.Accepted)
            return Result(gate);
        if (targetGuid == 0u)
        {
            return EmitResult(
                RuntimeCommandDomain.Fellowship,
                operation: 2,
                RuntimeCommandStatus.Rejected,
                targetGuid);
        }
        _commands.Publish(new FellowshipDismissRuntimeCmd(targetGuid));
        return EmitResult(
            RuntimeCommandDomain.Fellowship,
            operation: 2,
            RuntimeCommandStatus.Accepted,
            targetGuid);
    }

    public RuntimeCommandResult Quit(
        RuntimeGenerationToken expectedGeneration,
        bool disband)
    {
        RuntimeCommandStatus gate = Validate(expectedGeneration, requireWorld: true);
        if (gate != RuntimeCommandStatus.Accepted)
            return Result(gate);
        if (_fellowship.RequiresLeaderHandoffBeforeQuit(
                _view.Lifecycle.PlayerGuid,
                disband,
                out uint newLeaderGuid))
        {
            _commands.Publish(new FellowshipAssignNewLeaderRuntimeCmd(newLeaderGuid));
        }
        _commands.Publish(new FellowshipQuitRuntimeCmd(disband));
        return EmitResult(
            RuntimeCommandDomain.Fellowship,
            operation: 3,
            RuntimeCommandStatus.Accepted);
    }

    public RuntimeCommandResult AssignLeader(
        RuntimeGenerationToken expectedGeneration,
        uint newLeaderGuid)
    {
        RuntimeCommandStatus gate = Validate(expectedGeneration, requireWorld: true);
        if (gate != RuntimeCommandStatus.Accepted)
            return Result(gate);
        if (newLeaderGuid == 0u)
        {
            return EmitResult(
                RuntimeCommandDomain.Fellowship,
                operation: 4,
                RuntimeCommandStatus.Rejected,
                newLeaderGuid);
        }
        _commands.Publish(new FellowshipAssignNewLeaderRuntimeCmd(newLeaderGuid));
        return EmitResult(
            RuntimeCommandDomain.Fellowship,
            operation: 4,
            RuntimeCommandStatus.Accepted,
            newLeaderGuid);
    }

    public RuntimeCommandResult SetOpen(
        RuntimeGenerationToken expectedGeneration,
        bool isOpen)
    {
        RuntimeCommandStatus gate = Validate(expectedGeneration, requireWorld: true);
        if (gate != RuntimeCommandStatus.Accepted)
            return Result(gate);
        _commands.Publish(new FellowshipChangeOpennessRuntimeCmd(isOpen));
        return EmitResult(
            RuntimeCommandDomain.Fellowship,
            operation: 5,
            RuntimeCommandStatus.Accepted);
    }

    public RuntimeCommandResult SetPanelOpen(
        RuntimeGenerationToken expectedGeneration,
        bool panelOpen)
    {
        RuntimeCommandStatus gate = Validate(expectedGeneration, requireWorld: true);
        if (gate != RuntimeCommandStatus.Accepted)
            return Result(gate);
        _commands.Publish(new FellowshipUpdateRequestRuntimeCmd(panelOpen));
        return EmitResult(
            RuntimeCommandDomain.Fellowship,
            operation: 6,
            RuntimeCommandStatus.Accepted);
    }

    public RuntimeCommandResult Swear(
        RuntimeGenerationToken expectedGeneration,
        uint patronGuid)
    {
        RuntimeCommandStatus gate = Validate(expectedGeneration, requireWorld: true);
        if (gate != RuntimeCommandStatus.Accepted)
            return Result(gate);
        if (patronGuid == 0u)
        {
            return EmitResult(
                RuntimeCommandDomain.Allegiance,
                operation: 0,
                RuntimeCommandStatus.Rejected,
                patronGuid);
        }
        _commands.Publish(new AllegianceSwearRuntimeCmd(patronGuid));
        return EmitResult(
            RuntimeCommandDomain.Allegiance,
            operation: 0,
            RuntimeCommandStatus.Accepted,
            patronGuid);
    }

    public RuntimeCommandResult Break(
        RuntimeGenerationToken expectedGeneration,
        uint targetGuid)
    {
        RuntimeCommandStatus gate = Validate(expectedGeneration, requireWorld: true);
        if (gate != RuntimeCommandStatus.Accepted)
            return Result(gate);
        if (targetGuid == 0u)
        {
            return EmitResult(
                RuntimeCommandDomain.Allegiance,
                operation: 1,
                RuntimeCommandStatus.Rejected,
                targetGuid);
        }
        _commands.Publish(new AllegianceBreakRuntimeCmd(targetGuid));
        return EmitResult(
            RuntimeCommandDomain.Allegiance,
            operation: 1,
            RuntimeCommandStatus.Accepted,
            targetGuid);
    }

    public RuntimeCommandResult Kick(
        RuntimeGenerationToken expectedGeneration,
        uint vassalGuid)
    {
        RuntimeCommandStatus gate = Validate(expectedGeneration, requireWorld: true);
        if (gate != RuntimeCommandStatus.Accepted)
            return Result(gate);
        if (vassalGuid == 0u)
        {
            return EmitResult(
                RuntimeCommandDomain.Allegiance,
                operation: 2,
                RuntimeCommandStatus.Rejected,
                vassalGuid);
        }
        _commands.Publish(new AllegianceKickRuntimeCmd(vassalGuid));
        return EmitResult(
            RuntimeCommandDomain.Allegiance,
            operation: 2,
            RuntimeCommandStatus.Accepted,
            vassalGuid);
    }

    public RuntimeCommandResult RequestInfo(
        RuntimeGenerationToken expectedGeneration,
        string playerName)
    {
        RuntimeCommandStatus gate = Validate(expectedGeneration, requireWorld: true);
        if (gate != RuntimeCommandStatus.Accepted)
            return Result(gate);
        _commands.Publish(new AllegianceInfoRequestRuntimeCmd(playerName ?? string.Empty));
        return EmitResult(
            RuntimeCommandDomain.Allegiance,
            operation: 3,
            RuntimeCommandStatus.Accepted);
    }

    public RuntimeCommandResult SetUpdateSubscription(
        RuntimeGenerationToken expectedGeneration,
        bool on)
    {
        RuntimeCommandStatus gate = Validate(expectedGeneration, requireWorld: true);
        if (gate != RuntimeCommandStatus.Accepted)
            return Result(gate);
        _commands.Publish(new AllegianceUpdateRequestRuntimeCmd(on));
        return EmitResult(
            RuntimeCommandDomain.Allegiance,
            operation: 4,
            RuntimeCommandStatus.Accepted);
    }

    private RuntimeCommandStatus Validate(
        RuntimeGenerationToken expectedGeneration,
        bool requireWorld)
    {
        RuntimeCommandStatus status;
        if (_view.Lifecycle.State == RuntimeLifecycleState.Disposed)
            status = RuntimeCommandStatus.Inactive;
        else if (expectedGeneration != _view.Generation)
            status = RuntimeCommandStatus.StaleGeneration;
        else if (requireWorld && !_session.IsInWorld)
            status = RuntimeCommandStatus.Inactive;
        else
            status = RuntimeCommandStatus.Accepted;

        if (status != RuntimeCommandStatus.Accepted
            && AcDream.Core.Net.NetDiagnostics.ProbeNet)
        {
            Console.WriteLine(
                $"[cmd-gate] REJECT status={status}"
                + $" expected={expectedGeneration}"
                + $" view={_view.Generation}"
                + $" lifecycle={_view.Lifecycle.State}"
                + $" inWorld={_session.IsInWorld}");
        }
        return status;
    }

    private RuntimeCommandResult Result(
        RuntimeCommandStatus status,
        uint objectId = 0u) =>
        new(status, _view.Generation, objectId);

    private RuntimeCommandResult EmitResult(
        RuntimeCommandDomain domain,
        int operation,
        RuntimeCommandStatus status,
        uint objectId = 0u,
        string? text = null)
    {
        _events.EmitCommand(domain, operation, status, objectId, text);
        return Result(status, objectId);
    }

    private RuntimeSessionStartResult RejectedStart(RuntimeCommandStatus status) =>
        new(
            status == RuntimeCommandStatus.StaleGeneration
                ? RuntimeSessionStartStatus.StaleGeneration
                : RuntimeSessionStartStatus.Inactive,
            _view.Generation);

    private static RuntimeCommandStatus ToCommandStatus(
        RuntimeSessionStartStatus status) =>
        status switch
        {
            RuntimeSessionStartStatus.Failed => RuntimeCommandStatus.Rejected,
            RuntimeSessionStartStatus.Inactive => RuntimeCommandStatus.Inactive,
            RuntimeSessionStartStatus.StaleGeneration =>
                RuntimeCommandStatus.StaleGeneration,
            _ => RuntimeCommandStatus.Accepted,
        };

    private static bool TryMap(
        RuntimeChatChannel channel,
        out ChatChannelKind result)
    {
        result = channel switch
        {
            RuntimeChatChannel.Say => ChatChannelKind.Say,
            RuntimeChatChannel.Tell => ChatChannelKind.Tell,
            RuntimeChatChannel.Fellowship => ChatChannelKind.Fellowship,
            RuntimeChatChannel.Allegiance => ChatChannelKind.Allegiance,
            RuntimeChatChannel.AllegianceBroadcast => ChatChannelKind.AllegianceBroadcast,
            RuntimeChatChannel.Vassals => ChatChannelKind.Vassals,
            RuntimeChatChannel.Patron => ChatChannelKind.Patron,
            RuntimeChatChannel.Monarch => ChatChannelKind.Monarch,
            RuntimeChatChannel.CoVassals => ChatChannelKind.CoVassals,
            RuntimeChatChannel.General => ChatChannelKind.General,
            RuntimeChatChannel.Trade => ChatChannelKind.Trade,
            RuntimeChatChannel.LookingForGroup => ChatChannelKind.Lfg,
            RuntimeChatChannel.Roleplay => ChatChannelKind.Roleplay,
            RuntimeChatChannel.Society => ChatChannelKind.Society,
            RuntimeChatChannel.Olthoi => ChatChannelKind.Olthoi,
            _ => ChatChannelKind.Unknown,
        };
        return result != ChatChannelKind.Unknown;
    }
}
