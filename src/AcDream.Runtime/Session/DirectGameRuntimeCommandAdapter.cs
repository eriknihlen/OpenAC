using AcDream.Core.Chat;
using AcDream.Core.Combat;
using AcDream.Core.Items;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Selection;
using AcDream.Core.Spells;
using AcDream.Runtime.Entities;
using AcDream.Runtime.Gameplay;

namespace AcDream.Runtime.Session;

public sealed class DirectGameRuntimeCommandAdapter
    : IGameRuntimeCommands,
      IRuntimeSessionCommands,
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
      IRuntimeAllegianceCommands,
      IRuntimeInteractionTransport
{
    private sealed class CommandRoute(
        DirectGameRuntimeCommandAdapter owner,
        WorldSession session,
        RuntimeGenerationToken generation)
        : ILiveSessionCommandRouting
    {
        private bool _active;

        public void Activate()
        {
            if (_active)
                return;
            owner.Activate(session, generation, this);
            _active = true;
        }

        public void Dispose()
        {
            owner.Deactivate(this);
            _active = false;
        }
    }

    private readonly object _gate = new();
    private readonly GameRuntime _runtime;
    private readonly IRuntimeSessionCommands _sessionCommands;
    private WorldSession? _session;
    private CommandRoute? _route;
    private RuntimeGenerationToken _routeGeneration;

    public DirectGameRuntimeCommandAdapter(
        GameRuntime runtime,
        IRuntimeSessionCommands sessionCommands)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _sessionCommands = sessionCommands
            ?? throw new ArgumentNullException(nameof(sessionCommands));
    }

    public IRuntimeSessionCommands Session => this;
    public IRuntimeCharacterSelectionCommands CharacterSelection =>
        _runtime.Session;
    public IRuntimeCharacterCreationCommands CharacterCreation =>
        _runtime.Session;
    public IRuntimeSelectionCommands Selection => this;
    public IRuntimeCombatCommands Combat => this;
    public IRuntimeMagicCommands Magic => this;
    public IRuntimeMovementCommands Movement => this;
    public IRuntimeChatCommands Chat => this;
    public IRuntimePortalCommands Portal => this;
    public IRuntimeInventoryStateCommands InventoryState => this;
    public IRuntimeSpellbookCommands Spellbook => this;
    public IRuntimeCharacterCommands Character => this;
    public IRuntimeSocialCommands Social => this;
    public IRuntimeFellowshipCommands Fellowship => this;
    public IRuntimeAllegianceCommands Allegiance => this;

    public ILiveSessionCommandRouting CreateRoute(WorldSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return new CommandRoute(this, session, _runtime.Generation);
    }

    public RuntimeSessionStartResult Start(
        RuntimeGenerationToken expectedGeneration)
    {
        RuntimeLifecycleState previous = _runtime.Lifecycle.State;
        RuntimeSessionStartResult result =
            _sessionCommands.Start(expectedGeneration);
        _runtime.EventSink.EmitLifecycle(
            previous,
            _runtime.Lifecycle.State);
        return result;
    }

    public RuntimeSessionStartResult Reconnect(
        RuntimeGenerationToken expectedGeneration)
    {
        RuntimeLifecycleState previous = _runtime.Lifecycle.State;
        RuntimeSessionStartResult result =
            _sessionCommands.Reconnect(expectedGeneration);
        _runtime.EventSink.EmitLifecycle(
            previous,
            _runtime.Lifecycle.State);
        return result;
    }

    public RuntimeTeardownAcknowledgement Stop(
        RuntimeGenerationToken expectedGeneration)
    {
        RuntimeLifecycleState previous = _runtime.Lifecycle.State;
        RuntimeTeardownAcknowledgement result =
            _sessionCommands.Stop(expectedGeneration);
        _runtime.EventSink.EmitLifecycle(
            previous,
            _runtime.Lifecycle.State);
        return result;
    }

    public RuntimeCommandResult Execute(
        RuntimeGenerationToken expectedGeneration,
        in RuntimeChatCommand command)
    {
        RuntimeCommandStatus gate =
            Validate(expectedGeneration, out WorldSession? session);
        if (gate != RuntimeCommandStatus.Accepted)
            return Result(gate);
        if (string.IsNullOrWhiteSpace(command.Text))
            return EmitUnsupported(
                RuntimeCommandDomain.Chat,
                (int)command.Channel,
                RuntimeCommandStatus.Rejected);

        switch (command.Channel)
        {
            case RuntimeChatChannel.Say:
                session!.SendTalk(command.Text);
                break;
            case RuntimeChatChannel.Tell
                when !string.IsNullOrWhiteSpace(command.TargetName):
                session!.SendTell(command.TargetName, command.Text);
                break;
            default:
                if (!TrySendChannel(session!, command.Channel, command.Text))
                {
                    return EmitUnsupported(
                        RuntimeCommandDomain.Chat,
                        (int)command.Channel);
                }
                break;
        }

        _runtime.EventSink.EmitCommand(
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
        RuntimeCommandStatus gate =
            Validate(expectedGeneration, out WorldSession? session);
        if (gate != RuntimeCommandStatus.Accepted)
            return Result(gate);

        switch (command)
        {
            case RuntimePortalCommand.RecallLifestone:
                session!.SendTeleportToLifestone();
                break;
            case RuntimePortalCommand.RecallMarketplace:
                session!.SendTeleportToMarketplace();
                break;
            case RuntimePortalCommand.RecallHouse:
                session!.SendTeleportToHouse();
                break;
            case RuntimePortalCommand.RecallMansion:
                session!.SendTeleportToMansion();
                break;
            default:
                return EmitUnsupported(
                    RuntimeCommandDomain.Portal,
                    (int)command);
        }

        _runtime.EventSink.EmitCommand(
            RuntimeCommandDomain.Portal,
            (int)command,
            RuntimeCommandStatus.Accepted);
        return Result(RuntimeCommandStatus.Accepted);
    }

    public RuntimeCommandResult Execute(
        RuntimeGenerationToken expectedGeneration,
        RuntimeSelectionCommand command)
    {
        RuntimeCommandStatus gate =
            Validate(expectedGeneration, out WorldSession? session);
        if (gate != RuntimeCommandStatus.Accepted)
            return Result(gate);

        RuntimeCommandStatus status = RuntimeCommandStatus.Accepted;
        SelectionState selection = _runtime.ActionOwner.Selection;
        switch (command)
        {
            case RuntimeSelectionCommand.SelectClosestHostile:
            {
                uint? closest =
                    RuntimeHostileTargetQuery.FindClosest(_runtime);
                if (closest is { } objectId)
                {
                    selection.Select(
                        objectId,
                        SelectionChangeSource.Keyboard);
                }
                else
                {
                    selection.Clear(
                        SelectionChangeSource.Keyboard);
                }
                break;
            }
            case RuntimeSelectionCommand.SelectPrevious:
                selection.SelectPrevious();
                break;
            case RuntimeSelectionCommand.ExamineSelected:
                if (selection.SelectedObjectId is { } appraisalId)
                {
                    _runtime.ActionOwner.Transactions.TryRequestAppraisal(
                        appraisalId,
                        session!.SendAppraise);
                }
                else
                {
                    _runtime.ActionOwner.Interaction.EnterExamine();
                }
                break;
            case RuntimeSelectionCommand.UseSelected:
                status = UseSelected(session!);
                break;
            case RuntimeSelectionCommand.PickUpSelected:
                status = PickUpSelected();
                break;
            default:
                status = RuntimeCommandStatus.Unsupported;
                break;
        }

        uint selected = selection.SelectedObjectId ?? 0u;
        return EmitResult(
            RuntimeCommandDomain.Selection,
            (int)command,
            status,
            selected);
    }

    public RuntimeCommandResult SelectObject(
        RuntimeGenerationToken expectedGeneration,
        uint objectId)
    {
        RuntimeCommandStatus gate =
            Validate(expectedGeneration, out _);
        if (gate != RuntimeCommandStatus.Accepted)
            return Result(gate);

        RuntimeCommandStatus status =
            objectId != 0u
            && _runtime.InventoryOwner.Objects.Get(objectId) is not null
                ? RuntimeCommandStatus.Accepted
                : RuntimeCommandStatus.Rejected;
        if (status == RuntimeCommandStatus.Accepted)
        {
            _runtime.ActionOwner.Selection.Select(
                objectId,
                SelectionChangeSource.Plugin);
        }
        return EmitResult(
            RuntimeCommandDomain.Selection,
            operation: 0x100,
            status,
            objectId);
    }

    public RuntimeCommandResult Clear(
        RuntimeGenerationToken expectedGeneration)
    {
        RuntimeCommandStatus gate =
            Validate(expectedGeneration, out _);
        if (gate != RuntimeCommandStatus.Accepted)
            return Result(gate);
        _runtime.ActionOwner.Selection.Clear(
            SelectionChangeSource.Plugin);
        return EmitResult(
            RuntimeCommandDomain.Selection,
            operation: 0x101,
            RuntimeCommandStatus.Accepted);
    }

    public RuntimeCommandResult Execute(
        RuntimeGenerationToken expectedGeneration,
        RuntimeCombatCommand command)
    {
        RuntimeCommandStatus gate =
            Validate(expectedGeneration, out _);
        if (gate != RuntimeCommandStatus.Accepted)
            return Result(gate);

        RuntimeCommandStatus status;
        if (command == RuntimeCombatCommand.ToggleMode)
        {
            RuntimeCombatModeRequestResult result =
                _runtime.ActionOwner.CombatMode.Toggle();
            status = result.Status switch
            {
                RuntimeCombatModeRequestStatus.Sent =>
                    RuntimeCommandStatus.Accepted,
                RuntimeCombatModeRequestStatus.Inactive =>
                    RuntimeCommandStatus.Inactive,
                _ => RuntimeCommandStatus.Rejected,
            };
        }
        else
        {
            status = RuntimeCommandStatus.Unsupported;
        }
        return EmitResult(
            RuntimeCommandDomain.Combat,
            (int)command,
            status);
    }

    public RuntimeCommandResult ExecuteAttack(
        RuntimeGenerationToken expectedGeneration,
        in RuntimeCombatAttackInput command)
    {
        RuntimeCommandStatus gate =
            Validate(expectedGeneration, out _);
        if (gate != RuntimeCommandStatus.Accepted)
            return Result(gate);
        RuntimeCommandStatus status =
            _runtime.ActionOwner.CombatAttack.HandleCommand(command)
                ? RuntimeCommandStatus.Accepted
                : RuntimeCommandStatus.Unsupported;
        return EmitResult(
            RuntimeCommandDomain.Combat,
            0x100 + (int)command.Command,
            status);
    }

    public RuntimeCommandResult Execute(
        RuntimeGenerationToken expectedGeneration,
        in RuntimeMagicCommand command)
    {
        RuntimeCommandStatus gate =
            Validate(expectedGeneration, out _);
        if (gate != RuntimeCommandStatus.Accepted)
            return Result(gate);
        CastRequestResult cast =
            _runtime.ActionOwner.SpellCast.Cast(command.SpellId);
        RuntimeCommandStatus status = cast == CastRequestResult.Sent
            ? RuntimeCommandStatus.Accepted
            : RuntimeCommandStatus.Rejected;
        return EmitResult(
            RuntimeCommandDomain.Magic,
            (int)cast,
            status,
            command.SpellId);
    }

    public RuntimeCommandResult Execute(
        RuntimeGenerationToken expectedGeneration,
        RuntimeMovementCommand command)
    {
        RuntimeCommandStatus gate =
            Validate(expectedGeneration, out _);
        if (gate != RuntimeCommandStatus.Accepted)
            return Result(gate);
        RuntimeCommandStatus status =
            _runtime.MovementOwner.Execute(command)
                ? RuntimeCommandStatus.Accepted
                : RuntimeCommandStatus.Unsupported;
        return EmitResult(
            RuntimeCommandDomain.Movement,
            (int)command,
            status);
    }

    public RuntimeCommandResult ExecuteMotion(
        RuntimeGenerationToken expectedGeneration,
        uint motionCommand)
    {
        RuntimeCommandStatus gate =
            Validate(expectedGeneration, out _);
        if (gate != RuntimeCommandStatus.Accepted)
            return Result(gate);
        RuntimeCommandStatus status =
            _runtime.MovementOwner.ExecuteMotion(motionCommand)
                ? RuntimeCommandStatus.Accepted
                : RuntimeCommandStatus.Unsupported;
        return EmitResult(
            RuntimeCommandDomain.Movement,
            operation: 0x102,
            status,
            motionCommand);
    }

    public RuntimeCommandResult SetIntent(
        RuntimeGenerationToken expectedGeneration,
        in MovementInput input)
    {
        RuntimeCommandStatus gate =
            Validate(expectedGeneration, out _);
        if (gate != RuntimeCommandStatus.Accepted)
            return Result(gate);
        _runtime.MovementOwner.SetCommandInput(input);
        return EmitResult(
            RuntimeCommandDomain.Movement,
            operation: 0x100,
            RuntimeCommandStatus.Accepted);
    }

    public RuntimeCommandResult ClearIntent(
        RuntimeGenerationToken expectedGeneration)
    {
        RuntimeCommandStatus gate =
            Validate(expectedGeneration, out _);
        if (gate != RuntimeCommandStatus.Accepted)
            return Result(gate);
        _runtime.MovementOwner.ClearCommandInput();
        return EmitResult(
            RuntimeCommandDomain.Movement,
            operation: 0x101,
            RuntimeCommandStatus.Accepted);
    }

    public RuntimeCommandResult AddShortcut(
        RuntimeGenerationToken expectedGeneration,
        in RuntimeShortcutCommand command)
    {
        RuntimeCommandStatus gate =
            Validate(expectedGeneration, out WorldSession? session);
        if (gate != RuntimeCommandStatus.Accepted)
            return Result(gate);
        var entry = new ShortcutEntry(
            command.Index,
            command.ObjectId,
            command.SpellId);
        RuntimeCommandStatus status =
            _runtime.InventoryOwner.TryAddShortcut(
                entry,
                () => session!.SendAddShortcut(entry))
                ? RuntimeCommandStatus.Accepted
                : RuntimeCommandStatus.Rejected;
        return EmitResult(
            RuntimeCommandDomain.InventoryState,
            operation: 0,
            status,
            command.ObjectId);
    }

    public RuntimeCommandResult RemoveShortcut(
        RuntimeGenerationToken expectedGeneration,
        int index)
    {
        RuntimeCommandStatus gate =
            Validate(expectedGeneration, out WorldSession? session);
        if (gate != RuntimeCommandStatus.Accepted)
            return Result(gate);
        RuntimeCommandStatus status =
            _runtime.InventoryOwner.TryRemoveShortcut(
                index,
                () => session!.SendRemoveShortcut((uint)index))
                ? RuntimeCommandStatus.Accepted
                : RuntimeCommandStatus.Rejected;
        return EmitResult(
            RuntimeCommandDomain.InventoryState,
            operation: 1,
            status);
    }

    public RuntimeCommandResult AddFavorite(
        RuntimeGenerationToken expectedGeneration,
        int tabIndex,
        int position,
        uint spellId)
    {
        RuntimeCommandStatus gate =
            Validate(expectedGeneration, out WorldSession? session);
        if (gate != RuntimeCommandStatus.Accepted)
            return Result(gate);
        RuntimeCommandStatus status =
            _runtime.CharacterOwner.TryAddFavorite(
                tabIndex,
                position,
                spellId,
                () => session!.SendAddSpellFavorite(
                    spellId,
                    position,
                    tabIndex))
                ? RuntimeCommandStatus.Accepted
                : RuntimeCommandStatus.Rejected;
        return EmitResult(
            RuntimeCommandDomain.Spellbook,
            operation: 0,
            status,
            spellId);
    }

    public RuntimeCommandResult RemoveFavorite(
        RuntimeGenerationToken expectedGeneration,
        int tabIndex,
        uint spellId)
    {
        RuntimeCommandStatus gate =
            Validate(expectedGeneration, out WorldSession? session);
        if (gate != RuntimeCommandStatus.Accepted)
            return Result(gate);
        RuntimeCommandStatus status =
            _runtime.CharacterOwner.TryRemoveFavorite(
                tabIndex,
                spellId,
                () => session!.SendRemoveSpellFavorite(
                    spellId,
                    tabIndex))
                ? RuntimeCommandStatus.Accepted
                : RuntimeCommandStatus.Rejected;
        return EmitResult(
            RuntimeCommandDomain.Spellbook,
            operation: 1,
            status,
            spellId);
    }

    public RuntimeCommandResult SetFilter(
        RuntimeGenerationToken expectedGeneration,
        uint filters)
    {
        RuntimeCommandStatus gate =
            Validate(expectedGeneration, out WorldSession? session);
        if (gate != RuntimeCommandStatus.Accepted)
            return Result(gate);
        _runtime.CharacterOwner.SetSpellbookFilter(
            filters,
            () => session!.SendSpellbookFilter(filters));
        return EmitResult(
            RuntimeCommandDomain.Spellbook,
            operation: 2,
            RuntimeCommandStatus.Accepted);
    }

    public RuntimeCommandResult ForgetSpell(
        RuntimeGenerationToken expectedGeneration,
        uint spellId)
    {
        RuntimeCommandStatus gate =
            Validate(expectedGeneration, out WorldSession? session);
        if (gate != RuntimeCommandStatus.Accepted)
            return Result(gate);
        RuntimeCommandStatus status = spellId == 0u
            ? RuntimeCommandStatus.Rejected
            : RuntimeCommandStatus.Accepted;
        if (status == RuntimeCommandStatus.Accepted)
            session!.SendRemoveSpell(spellId);
        return EmitResult(
            RuntimeCommandDomain.Spellbook,
            operation: 3,
            status,
            spellId);
    }

    public RuntimeCommandResult SetDesiredComponent(
        RuntimeGenerationToken expectedGeneration,
        uint componentId,
        uint amount)
    {
        RuntimeCommandStatus gate =
            Validate(expectedGeneration, out WorldSession? session);
        if (gate != RuntimeCommandStatus.Accepted)
            return Result(gate);
        RuntimeCommandStatus status =
            _runtime.CharacterOwner.TrySetDesiredComponent(
                componentId,
                amount,
                () => session!.SendSetDesiredComponentLevel(
                    componentId,
                    amount))
                ? RuntimeCommandStatus.Accepted
                : RuntimeCommandStatus.Rejected;
        return EmitResult(
            RuntimeCommandDomain.Spellbook,
            operation: 4,
            status,
            componentId);
    }

    public RuntimeCommandResult ClearDesiredComponents(
        RuntimeGenerationToken expectedGeneration)
    {
        RuntimeCommandStatus gate =
            Validate(expectedGeneration, out WorldSession? session);
        if (gate != RuntimeCommandStatus.Accepted)
            return Result(gate);
        _runtime.CharacterOwner.ClearDesiredComponents(
            session!.SendClearDesiredComponents);
        return EmitResult(
            RuntimeCommandDomain.Spellbook,
            operation: 5,
            RuntimeCommandStatus.Accepted);
    }

    public RuntimeCommandResult Advance(
        RuntimeGenerationToken expectedGeneration,
        in RuntimeAdvancementCommand command)
    {
        RuntimeCommandStatus gate =
            Validate(expectedGeneration, out WorldSession? session);
        if (gate != RuntimeCommandStatus.Accepted)
            return Result(gate);
        RuntimeCommandStatus status = RuntimeCommandStatus.Accepted;
        if (command.StatId == 0u || command.Cost == 0u)
        {
            status = RuntimeCommandStatus.Rejected;
        }
        else
        {
            switch (command.Kind)
            {
                case RuntimeAdvancementKind.Attribute:
                    session!.SendRaiseAttribute(
                        command.StatId,
                        command.Cost);
                    break;
                case RuntimeAdvancementKind.Vital:
                    session!.SendRaiseVital(
                        command.StatId,
                        command.Cost);
                    break;
                case RuntimeAdvancementKind.Skill:
                    session!.SendRaiseSkill(
                        command.StatId,
                        command.Cost);
                    break;
                case RuntimeAdvancementKind.TrainSkill
                    when command.Cost <= uint.MaxValue:
                    session!.SendTrainSkill(
                        command.StatId,
                        (uint)command.Cost);
                    break;
                default:
                    status = RuntimeCommandStatus.Rejected;
                    break;
            }
        }
        return EmitResult(
            RuntimeCommandDomain.Character,
            (int)command.Kind,
            status,
            command.StatId);
    }

    public RuntimeCommandResult SetSingleOption(
        RuntimeGenerationToken expectedGeneration,
        uint optionId,
        bool value)
    {
        RuntimeCommandStatus gate =
            Validate(expectedGeneration, out WorldSession? session);
        if (gate != RuntimeCommandStatus.Accepted)
            return Result(gate);
        bool accepted = _runtime.CharacterOwner.Options.TrySetOption(
            optionId,
            value,
            sendAutoSave: session!.SendSetSingleCharacterOption);
        return EmitResult(
            RuntimeCommandDomain.Character,
            operation: 4,
            accepted ? RuntimeCommandStatus.Accepted : RuntimeCommandStatus.Rejected);
    }

    public RuntimeCommandResult SaveOptions(
        RuntimeGenerationToken expectedGeneration)
    {
        RuntimeCommandStatus gate =
            Validate(expectedGeneration, out WorldSession? session);
        if (gate != RuntimeCommandStatus.Accepted)
            return Result(gate);
        _runtime.CharacterOwner.Options.TryFlush(() =>
        {
            CharacterOptionsBlobEcho echo = CharacterOptionsBlobSource.Capture(
                _runtime.CharacterOwner,
                _runtime.InventoryOwner.Shortcuts);
            session!.SendSetCharacterOptions(
                echo.Options1,
                echo.Options2,
                echo.Shortcuts,
                echo.FavoriteSpells,
                echo.DesiredComponents,
                echo.SpellbookFilters);
        });
        return EmitResult(
            RuntimeCommandDomain.Character,
            operation: 5,
            RuntimeCommandStatus.Accepted);
    }

    public RuntimeCommandResult SetTitle(
        RuntimeGenerationToken expectedGeneration,
        uint titleId)
    {
        RuntimeCommandStatus gate =
            Validate(expectedGeneration, out WorldSession? session);
        if (gate != RuntimeCommandStatus.Accepted)
            return Result(gate);
        session!.SendSetTitle(titleId);
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
        RuntimeCommandStatus gate =
            Validate(expectedGeneration, out WorldSession? session);
        if (gate != RuntimeCommandStatus.Accepted)
            return Result(gate);
        RuntimeCommandStatus status = RuntimeCommandStatus.Accepted;
        switch (command.Kind)
        {
            case RuntimeFriendCommandKind.Add
                when !string.IsNullOrWhiteSpace(command.Name):
                session!.SendAddFriend(command.Name);
                break;
            case RuntimeFriendCommandKind.Remove
                when command.CharacterId != 0u:
                session!.SendRemoveFriend(command.CharacterId);
                break;
            case RuntimeFriendCommandKind.Clear:
                session!.SendClearFriends();
                break;
            case RuntimeFriendCommandKind.RequestLegacyList:
                session!.SendLegacyFriendsListRequest();
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
        RuntimeCommandStatus gate =
            Validate(expectedGeneration, out WorldSession? session);
        if (gate != RuntimeCommandStatus.Accepted)
            return Result(gate);
        RuntimeCommandStatus status = RuntimeCommandStatus.Accepted;
        switch (command.Scope)
        {
            case RuntimeSquelchScope.Character
                when command.CharacterId != 0u
                    && !string.IsNullOrWhiteSpace(command.Name):
                session!.SendModifyCharacterSquelch(
                    command.Add,
                    command.CharacterId,
                    command.Name,
                    command.MessageType);
                break;
            case RuntimeSquelchScope.Account
                when !string.IsNullOrWhiteSpace(command.Name):
                session!.SendModifyAccountSquelch(
                    command.Add,
                    command.Name);
                break;
            case RuntimeSquelchScope.Global:
                session!.SendModifyGlobalSquelch(
                    command.Add,
                    command.MessageType);
                break;
            default:
                status = RuntimeCommandStatus.Rejected;
                break;
        }
        return EmitResult(
            RuntimeCommandDomain.Social,
            4 + (int)command.Scope,
            status,
            command.CharacterId,
            command.Name);
    }


    public RuntimeCommandResult Create(
        RuntimeGenerationToken expectedGeneration,
        string fellowshipName,
        bool shareXp)
    {
        RuntimeCommandStatus gate =
            Validate(expectedGeneration, out WorldSession? session);
        if (gate != RuntimeCommandStatus.Accepted)
            return Result(gate);
        if (string.IsNullOrWhiteSpace(fellowshipName))
        {
            return EmitUnsupported(
                RuntimeCommandDomain.Fellowship,
                operation: 0,
                RuntimeCommandStatus.Rejected);
        }
        session!.SendFellowshipCreate(fellowshipName, shareXp);
        return EmitResult(
            RuntimeCommandDomain.Fellowship,
            operation: 0,
            RuntimeCommandStatus.Accepted);
    }

    public RuntimeCommandResult Recruit(
        RuntimeGenerationToken expectedGeneration,
        uint targetGuid)
    {
        RuntimeCommandStatus gate =
            Validate(expectedGeneration, out WorldSession? session);
        if (gate != RuntimeCommandStatus.Accepted)
            return Result(gate);
        if (targetGuid == 0u)
        {
            return EmitUnsupported(
                RuntimeCommandDomain.Fellowship,
                operation: 1,
                RuntimeCommandStatus.Rejected,
                targetGuid);
        }
        session!.SendFellowshipRecruit(targetGuid);
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
        RuntimeCommandStatus gate =
            Validate(expectedGeneration, out WorldSession? session);
        if (gate != RuntimeCommandStatus.Accepted)
            return Result(gate);
        if (targetGuid == 0u)
        {
            return EmitUnsupported(
                RuntimeCommandDomain.Fellowship,
                operation: 2,
                RuntimeCommandStatus.Rejected,
                targetGuid);
        }
        session!.SendFellowshipDismiss(targetGuid);
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
        RuntimeCommandStatus gate =
            Validate(expectedGeneration, out WorldSession? session);
        if (gate != RuntimeCommandStatus.Accepted)
            return Result(gate);
        if (_runtime.FellowshipOwner.RequiresLeaderHandoffBeforeQuit(
                _runtime.PlayerIdentity.ServerGuid,
                disband,
                out uint newLeaderGuid))
        {
            session!.SendFellowshipAssignNewLeader(newLeaderGuid);
        }
        session!.SendFellowshipQuit(disband);
        return EmitResult(
            RuntimeCommandDomain.Fellowship,
            operation: 3,
            RuntimeCommandStatus.Accepted);
    }

    public RuntimeCommandResult AssignLeader(
        RuntimeGenerationToken expectedGeneration,
        uint newLeaderGuid)
    {
        RuntimeCommandStatus gate =
            Validate(expectedGeneration, out WorldSession? session);
        if (gate != RuntimeCommandStatus.Accepted)
            return Result(gate);
        if (newLeaderGuid == 0u)
        {
            return EmitUnsupported(
                RuntimeCommandDomain.Fellowship,
                operation: 4,
                RuntimeCommandStatus.Rejected,
                newLeaderGuid);
        }
        session!.SendFellowshipAssignNewLeader(newLeaderGuid);
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
        RuntimeCommandStatus gate =
            Validate(expectedGeneration, out WorldSession? session);
        if (gate != RuntimeCommandStatus.Accepted)
            return Result(gate);
        session!.SendFellowshipChangeOpenness(isOpen);
        return EmitResult(
            RuntimeCommandDomain.Fellowship,
            operation: 5,
            RuntimeCommandStatus.Accepted);
    }

    public RuntimeCommandResult SetPanelOpen(
        RuntimeGenerationToken expectedGeneration,
        bool panelOpen)
    {
        RuntimeCommandStatus gate =
            Validate(expectedGeneration, out WorldSession? session);
        if (gate != RuntimeCommandStatus.Accepted)
            return Result(gate);
        session!.SendFellowshipUpdateRequest(panelOpen);
        return EmitResult(
            RuntimeCommandDomain.Fellowship,
            operation: 6,
            RuntimeCommandStatus.Accepted);
    }

    public RuntimeCommandResult Swear(
        RuntimeGenerationToken expectedGeneration,
        uint patronGuid)
    {
        RuntimeCommandStatus gate =
            Validate(expectedGeneration, out WorldSession? session);
        if (gate != RuntimeCommandStatus.Accepted)
            return Result(gate);
        if (patronGuid == 0u)
        {
            return EmitUnsupported(
                RuntimeCommandDomain.Allegiance,
                operation: 0,
                RuntimeCommandStatus.Rejected,
                patronGuid);
        }
        session!.SendAllegianceSwear(patronGuid);
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
        RuntimeCommandStatus gate =
            Validate(expectedGeneration, out WorldSession? session);
        if (gate != RuntimeCommandStatus.Accepted)
            return Result(gate);
        if (targetGuid == 0u)
        {
            return EmitUnsupported(
                RuntimeCommandDomain.Allegiance,
                operation: 1,
                RuntimeCommandStatus.Rejected,
                targetGuid);
        }
        session!.SendAllegianceBreak(targetGuid);
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
        RuntimeCommandStatus gate =
            Validate(expectedGeneration, out WorldSession? session);
        if (gate != RuntimeCommandStatus.Accepted)
            return Result(gate);
        if (vassalGuid == 0u)
        {
            return EmitUnsupported(
                RuntimeCommandDomain.Allegiance,
                operation: 2,
                RuntimeCommandStatus.Rejected,
                vassalGuid);
        }
        session!.SendAllegianceKick(vassalGuid);
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
        RuntimeCommandStatus gate =
            Validate(expectedGeneration, out WorldSession? session);
        if (gate != RuntimeCommandStatus.Accepted)
            return Result(gate);
        session!.SendAllegianceInfoRequest(playerName ?? string.Empty);
        return EmitResult(
            RuntimeCommandDomain.Allegiance,
            operation: 3,
            RuntimeCommandStatus.Accepted);
    }

    public RuntimeCommandResult SetUpdateSubscription(
        RuntimeGenerationToken expectedGeneration,
        bool on)
    {
        RuntimeCommandStatus gate =
            Validate(expectedGeneration, out WorldSession? session);
        if (gate != RuntimeCommandStatus.Accepted)
            return Result(gate);
        session!.SendAllegianceUpdateRequest(on);
        return EmitResult(
            RuntimeCommandDomain.Allegiance,
            operation: 4,
            RuntimeCommandStatus.Accepted);
    }

    bool IRuntimeInteractionTransport.IsInWorld
    {
        get
        {
            lock (_gate)
            {
                return _route is not null
                    && _session is not null
                    && _routeGeneration == _runtime.Generation
                    && _runtime.Session.IsInWorld;
            }
        }
    }

    bool IRuntimeInteractionTransport.TrySendUse(
        uint serverGuid,
        out uint sequence)
    {
        lock (_gate)
        {
            if (_route is null
                || _session is null
                || _routeGeneration != _runtime.Generation
                || !_runtime.Session.IsInWorld)
            {
                sequence = 0u;
                return false;
            }

            sequence = _session.NextGameActionSequence();
            _session.SendGameAction(
                InteractRequests.BuildUse(sequence, serverGuid));
            return true;
        }
    }

    bool IRuntimeInteractionTransport.TrySendPickup(
        uint itemGuid,
        uint destinationContainerId,
        int placement,
        out uint sequence)
    {
        lock (_gate)
        {
            if (_route is null
                || _session is null
                || _routeGeneration != _runtime.Generation
                || !_runtime.Session.IsInWorld)
            {
                sequence = 0u;
                return false;
            }

            sequence = _session.NextGameActionSequence();
            _session.SendGameAction(
                InteractRequests.BuildPickUp(
                    sequence,
                    itemGuid,
                    destinationContainerId,
                    placement));
            return true;
        }
    }

    private RuntimeCommandStatus UseSelected(WorldSession session)
    {
        if (_runtime.ActionOwner.Selection.SelectedObjectId
            is not uint selected)
        {
            _runtime.ActionOwner.Interaction.EnterUse();
            return RuntimeCommandStatus.Accepted;
        }
        if (_runtime.InventoryOwner.Objects.Get(selected)
            is not { } item)
        {
            return RuntimeCommandStatus.Rejected;
        }

        long nowMs = checked((long)Math.Floor(
            _runtime.Clock.SimulationTimeSeconds * 1000d));
        if (!_runtime.ActionOwner.Transactions
                .TryConsumeUseThrottle(nowMs))
        {
            return RuntimeCommandStatus.Accepted;
        }

        uint useability = item.Useability ?? ItemUseability.Undef;
        if (ItemUseability.IsTargeted(useability))
        {
            uint targetId;
            if (ItemUseability.AllowsSelfTarget(useability))
                targetId = _runtime.PlayerIdentity.ServerGuid;
            else if (ItemUseability.AllowsObjectSelfTarget(useability))
                targetId = selected;
            else
            {
                _runtime.ActionOwner.Interaction
                    .EnterUseItemOnTarget(selected);
                return RuntimeCommandStatus.Accepted;
            }

            bool sent = _runtime.ActionOwner.Transactions
                .TryDispatchTargetedUse(
                    selected,
                    targetId,
                    session.SendUseWithTarget,
                    incrementBusy: true);
            return sent
                ? RuntimeCommandStatus.Accepted
                : RuntimeCommandStatus.Rejected;
        }

        bool ownedByPlayer = IsOwnedByPlayer(item);
        ItemUseRequestReservation reservation;
        try
        {
            reservation = _runtime.ActionOwner.Transactions
                .BeginUseRequestReservation();
        }
        catch (InvalidOperationException)
        {
            return RuntimeCommandStatus.Rejected;
        }

        RuntimeInteractionDispatchResult dispatched =
            _runtime.ActionOwner.Transactions.TryDispatchUse(
                selected,
                ownedByPlayer,
                ownedByPlayer || ItemUseability.IsUseable(useability),
                reservation,
                this,
                out _);
        return dispatched == RuntimeInteractionDispatchResult.Dispatched
            ? RuntimeCommandStatus.Accepted
            : RuntimeCommandStatus.Rejected;
    }

    private RuntimeCommandStatus PickUpSelected()
    {
        if (_runtime.ActionOwner.Selection.SelectedObjectId
            is not uint selected)
        {
            return RuntimeCommandStatus.Accepted;
        }
        ClientObject? item =
            _runtime.InventoryOwner.Objects.Get(selected);
        if (item is null
            || (item.Type & ItemType.Creature) != 0)
        {
            return RuntimeCommandStatus.Rejected;
        }

        uint playerGuid = _runtime.PlayerIdentity.ServerGuid;
        if (playerGuid == 0u)
            return RuntimeCommandStatus.Inactive;
        uint localEntityId =
            _runtime.EntityObjects.Entities.TryGetActive(
                selected,
                out RuntimeEntityRecord record)
                ? record.LocalEntityId ?? 0u
                : 0u;
        var pickup = new RuntimePendingPickup(
            Token: 0u,
            selected,
            localEntityId,
            playerGuid,
            Placement: 0,
            PendingPlacementToken: 0u,
            ApproachToken: default);
        return _runtime.ActionOwner.Transactions.TryDispatchPickup(
            pickup,
            this,
            out _)
            ? RuntimeCommandStatus.Accepted
            : RuntimeCommandStatus.Rejected;
    }

    private bool IsOwnedByPlayer(ClientObject item)
    {
        uint playerGuid = _runtime.PlayerIdentity.ServerGuid;
        if (playerGuid == 0u)
            return false;
        ClientObject current = item;
        for (int depth = 0; depth < 16; depth++)
        {
            if (current.ObjectId == playerGuid
                || current.WielderId == playerGuid
                || current.ContainerId == playerGuid)
            {
                return true;
            }
            if (current.ContainerId == 0u
                || _runtime.InventoryOwner.Objects.Get(
                    current.ContainerId) is not { } parent)
            {
                return false;
            }
            current = parent;
        }
        return false;
    }

    private bool TrySendChannel(
        WorldSession session,
        RuntimeChatChannel channel,
        string text)
    {
        if (channel == RuntimeChatChannel.Allegiance
            && !_runtime.CommunicationOwner.TurbineChat.Enabled)
        {
            session.SendChannel(0x02000000u, text);
            return true;
        }

        if (TryMapTurbine(channel, out ChatChannelKindLite turbineKind))
        {
            TurbineChatGateResult gate = TurbineChatMembershipGate.Evaluate(
                turbineKind,
                _runtime.CommunicationOwner.TurbineChat,
                _runtime.CharacterOwner.Options,
                _runtime.CharacterOwner.IsOlthoiPlayer);

            // N3 (CH3 Opus review): shared refusal-text mapping — see
            // TurbineChatMembershipGate.ResolveRefusalText.
            if (gate.Status != TurbineChatGateStatus.Allowed)
            {
                if (TurbineChatMembershipGate.ResolveRefusalText(gate) is
                    (string refusalText, RetailLogTextType refusalType))
                {
                    _runtime.CommunicationOwner.AddText(refusalText, refusalType);
                }
                return true;
            }

            session.SendTurbineChatTo(
                gate.RoomId,
                gate.ChatType,
                (uint)TurbineChat.DispatchType.SendToRoomById,
                _runtime.PlayerIdentity.ServerGuid,
                text,
                _runtime.CommunicationOwner.TurbineChat.NextContextId());
            return true;
        }

        uint? legacyChannel = channel switch
        {
            RuntimeChatChannel.Fellowship => 0x00000800u,
            RuntimeChatChannel.AllegianceBroadcast => 0x02000000u,
            RuntimeChatChannel.Vassals => 0x00001000u,
            RuntimeChatChannel.Patron => 0x00002000u,
            RuntimeChatChannel.Monarch => 0x00004000u,
            RuntimeChatChannel.CoVassals => 0x01000000u,
            _ => null,
        };
        if (legacyChannel is not { } channelId)
            return false;
        session.SendChannel(channelId, text);
        return true;
    }

    private static bool TryMapTurbine(
        RuntimeChatChannel channel,
        out ChatChannelKindLite kind)
    {
        kind = channel switch
        {
            RuntimeChatChannel.Allegiance => ChatChannelKindLite.Allegiance,
            RuntimeChatChannel.General => ChatChannelKindLite.General,
            RuntimeChatChannel.Trade => ChatChannelKindLite.Trade,
            RuntimeChatChannel.LookingForGroup => ChatChannelKindLite.Lfg,
            RuntimeChatChannel.Roleplay => ChatChannelKindLite.Roleplay,
            RuntimeChatChannel.Society => ChatChannelKindLite.Society,
            RuntimeChatChannel.Olthoi => ChatChannelKindLite.Olthoi,
            _ => default,
        };
        return channel is RuntimeChatChannel.Allegiance
            or RuntimeChatChannel.General
            or RuntimeChatChannel.Trade
            or RuntimeChatChannel.LookingForGroup
            or RuntimeChatChannel.Roleplay
            or RuntimeChatChannel.Society
            or RuntimeChatChannel.Olthoi;
    }

    private RuntimeCommandResult EmitUnsupported(
        RuntimeCommandDomain domain,
        int operation,
        RuntimeCommandStatus status = RuntimeCommandStatus.Unsupported,
        uint primaryObjectId = 0u)
    {
        _runtime.EventSink.EmitCommand(
            domain,
            operation,
            status,
            primaryObjectId);
        return Result(status, primaryObjectId);
    }

    private RuntimeCommandResult EmitResult(
        RuntimeCommandDomain domain,
        int operation,
        RuntimeCommandStatus status,
        uint primaryObjectId = 0u,
        string? text = null)
    {
        _runtime.EventSink.EmitCommand(
            domain,
            operation,
            status,
            primaryObjectId,
            text);
        return Result(status, primaryObjectId);
    }

    private RuntimeCommandStatus Validate(
        RuntimeGenerationToken expectedGeneration,
        out WorldSession? session)
    {
        lock (_gate)
        {
            if (expectedGeneration != _runtime.Generation
                || (_route is not null
                    && _routeGeneration != expectedGeneration))
            {
                session = null;
                return RuntimeCommandStatus.StaleGeneration;
            }

            session = _session;
            return _route is not null
                && session is not null
                && _runtime.Session.IsInWorld
                    ? RuntimeCommandStatus.Accepted
                    : RuntimeCommandStatus.Inactive;
        }
    }

    private RuntimeCommandResult Result(
        RuntimeCommandStatus status,
        uint objectId = 0u) =>
        new(status, _runtime.Generation, objectId);

    private void Activate(
        WorldSession session,
        RuntimeGenerationToken generation,
        CommandRoute route)
    {
        lock (_gate)
        {
            if (_route is not null)
            {
                throw new InvalidOperationException(
                    "A direct Runtime command route is already active.");
            }
            _session = session;
            _routeGeneration = generation;
            _route = route;
        }
    }

    private void Deactivate(CommandRoute route)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_route, route))
                return;
            _route = null;
            _session = null;
            _routeGeneration = default;
        }
    }
}
