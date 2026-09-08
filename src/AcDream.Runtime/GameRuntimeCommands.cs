namespace AcDream.Runtime;

public enum RuntimeCommandStatus
{
    Accepted,
    Inactive,
    StaleGeneration,
    Unsupported,
    Rejected,
}

public readonly record struct RuntimeCommandResult(
    RuntimeCommandStatus Status,
    RuntimeGenerationToken Generation,
    uint ResultObjectId = 0u)
{
    public bool Accepted => Status == RuntimeCommandStatus.Accepted;
}

public enum RuntimeSessionStartStatus
{
    Disabled,
    MissingCredentials,
    NoCharacters,
    Connected,
    Deferred,
    Failed,
    Inactive,
    StaleGeneration,
    ProbeComplete,
    AwaitingCharacterSelection,
}

public readonly record struct RuntimeSessionStartResult(
    RuntimeSessionStartStatus Status,
    RuntimeGenerationToken Generation,
    uint CharacterId = 0u,
    string CharacterName = "",
    Exception? Error = null);

public enum RuntimeSelectionCommand
{
    SelectClosestHostile,
    SelectPrevious,
    ExamineSelected,
    UseSelected,
    PickUpSelected,
}

public enum RuntimeCombatCommand
{
    ToggleMode,
}

public enum RuntimeMovementCommand
{
    ToggleRunLock,
    Stop,
    Ready,
    Sit,
    Crouch,
    Sleep,
    StopCompletely,
    FinishJump,
}

public enum RuntimeChatChannel
{
    Say,
    Tell,
    Fellowship,
    Allegiance,
    Vassals,
    Patron,
    Monarch,
    CoVassals,
    General,
    Trade,
    LookingForGroup,
    Roleplay,
    Society,
    Olthoi,

    AllegianceBroadcast,
}

public readonly record struct RuntimeChatCommand(
    RuntimeChatChannel Channel,
    string Text,
    string? TargetName = null);

public enum RuntimePortalCommand
{
    RecallLifestone,
    RecallMarketplace,
    RecallHouse,
    RecallMansion,
}

public interface IRuntimeSessionCommands
{
    RuntimeSessionStartResult Start(RuntimeGenerationToken expectedGeneration);

    RuntimeSessionStartResult Reconnect(RuntimeGenerationToken expectedGeneration);

    RuntimeTeardownAcknowledgement Stop(RuntimeGenerationToken expectedGeneration);
}

public interface IRuntimeSelectionCommands
{
    RuntimeCommandResult Execute(
        RuntimeGenerationToken expectedGeneration,
        RuntimeSelectionCommand command);

    RuntimeCommandResult SelectObject(
        RuntimeGenerationToken expectedGeneration,
        uint objectId);

    RuntimeCommandResult Clear(
        RuntimeGenerationToken expectedGeneration);
}

public interface IRuntimeCombatCommands
{
    RuntimeCommandResult Execute(
        RuntimeGenerationToken expectedGeneration,
        RuntimeCombatCommand command);

    RuntimeCommandResult ExecuteAttack(
        RuntimeGenerationToken expectedGeneration,
        in Gameplay.RuntimeCombatAttackInput command);
}

public readonly record struct RuntimeMagicCommand(uint SpellId);

public interface IRuntimeMagicCommands
{
    RuntimeCommandResult Execute(
        RuntimeGenerationToken expectedGeneration,
        in RuntimeMagicCommand command);
}

public interface IRuntimeMovementCommands
{
    RuntimeCommandResult Execute(
        RuntimeGenerationToken expectedGeneration,
        RuntimeMovementCommand command);

    RuntimeCommandResult ExecuteMotion(
        RuntimeGenerationToken expectedGeneration,
        uint motionCommand);

    RuntimeCommandResult SetIntent(
        RuntimeGenerationToken expectedGeneration,
        in Gameplay.MovementInput input);

    RuntimeCommandResult ClearIntent(
        RuntimeGenerationToken expectedGeneration);
}

public interface IRuntimeChatCommands
{
    RuntimeCommandResult Execute(
        RuntimeGenerationToken expectedGeneration,
        in RuntimeChatCommand command);
}

public interface IRuntimePortalCommands
{
    RuntimeCommandResult Execute(
        RuntimeGenerationToken expectedGeneration,
        RuntimePortalCommand command);
}

public readonly record struct RuntimeShortcutCommand(
    int Index,
    uint ObjectId,
    uint SpellId);

public interface IRuntimeInventoryStateCommands
{
    RuntimeCommandResult AddShortcut(
        RuntimeGenerationToken expectedGeneration,
        in RuntimeShortcutCommand command);

    RuntimeCommandResult RemoveShortcut(
        RuntimeGenerationToken expectedGeneration,
        int index);
}

public interface IRuntimeSpellbookCommands
{
    RuntimeCommandResult AddFavorite(
        RuntimeGenerationToken expectedGeneration,
        int tabIndex,
        int position,
        uint spellId);

    RuntimeCommandResult RemoveFavorite(
        RuntimeGenerationToken expectedGeneration,
        int tabIndex,
        uint spellId);

    RuntimeCommandResult SetFilter(
        RuntimeGenerationToken expectedGeneration,
        uint filters);

    RuntimeCommandResult ForgetSpell(
        RuntimeGenerationToken expectedGeneration,
        uint spellId);

    RuntimeCommandResult SetDesiredComponent(
        RuntimeGenerationToken expectedGeneration,
        uint componentId,
        uint amount);

    RuntimeCommandResult ClearDesiredComponents(
        RuntimeGenerationToken expectedGeneration);
}

public enum RuntimeAdvancementKind
{
    Attribute,
    Vital,
    Skill,
    TrainSkill,
}

public readonly record struct RuntimeAdvancementCommand(
    RuntimeAdvancementKind Kind,
    uint StatId,
    ulong Cost);

public interface IRuntimeCharacterCommands
{
    RuntimeCommandResult Advance(
        RuntimeGenerationToken expectedGeneration,
        in RuntimeAdvancementCommand command);

    RuntimeCommandResult SetSingleOption(
        RuntimeGenerationToken expectedGeneration,
        uint optionId,
        bool value);

    RuntimeCommandResult SaveOptions(RuntimeGenerationToken expectedGeneration);

    RuntimeCommandResult SetTitle(
        RuntimeGenerationToken expectedGeneration,
        uint titleId);
}

public enum RuntimeFriendCommandKind
{
    Add,
    Remove,
    Clear,
    RequestLegacyList,
}

public readonly record struct RuntimeFriendCommand(
    RuntimeFriendCommandKind Kind,
    uint CharacterId = 0u,
    string? Name = null);

public enum RuntimeSquelchScope
{
    Character,
    Account,
    Global,
}

public readonly record struct RuntimeSquelchCommand(
    RuntimeSquelchScope Scope,
    bool Add,
    uint CharacterId = 0u,
    string? Name = null,
    uint MessageType = 0u);

public interface IRuntimeSocialCommands
{
    RuntimeCommandResult Execute(
        RuntimeGenerationToken expectedGeneration,
        in RuntimeFriendCommand command);

    RuntimeCommandResult Execute(
        RuntimeGenerationToken expectedGeneration,
        in RuntimeSquelchCommand command);
}


public interface IRuntimeFellowshipCommands
{
    RuntimeCommandResult Create(
        RuntimeGenerationToken expectedGeneration,
        string fellowshipName,
        bool shareXp);

    RuntimeCommandResult Recruit(
        RuntimeGenerationToken expectedGeneration,
        uint targetGuid);

    RuntimeCommandResult Dismiss(
        RuntimeGenerationToken expectedGeneration,
        uint targetGuid);

    RuntimeCommandResult Quit(
        RuntimeGenerationToken expectedGeneration,
        bool disband);

    RuntimeCommandResult AssignLeader(
        RuntimeGenerationToken expectedGeneration,
        uint newLeaderGuid);

    RuntimeCommandResult SetOpen(
        RuntimeGenerationToken expectedGeneration,
        bool isOpen);

    RuntimeCommandResult SetPanelOpen(
        RuntimeGenerationToken expectedGeneration,
        bool panelOpen);
}

/// <summary>Generation-gated allegiance outbound actions.</summary>
public interface IRuntimeAllegianceCommands
{
    RuntimeCommandResult Swear(
        RuntimeGenerationToken expectedGeneration,
        uint patronGuid);

    RuntimeCommandResult Break(
        RuntimeGenerationToken expectedGeneration,
        uint targetGuid);

    RuntimeCommandResult Kick(
        RuntimeGenerationToken expectedGeneration,
        uint vassalGuid);

    /// <summary><c>0x027B</c> — empty name queries self.</summary>
    RuntimeCommandResult RequestInfo(
        RuntimeGenerationToken expectedGeneration,
        string playerName);

    /// <summary><c>0x001F</c> — the allegiance panel's subscribe/unsubscribe toggle.</summary>
    RuntimeCommandResult SetUpdateSubscription(
        RuntimeGenerationToken expectedGeneration,
        bool on);
}


public interface IRuntimeCharacterCreationCommands
{
    RuntimeCommandResult SelectHeritage(
        RuntimeGenerationToken expectedGeneration,
        uint heritageId);

    RuntimeCommandResult SelectGender(
        RuntimeGenerationToken expectedGeneration,
        uint genderKey);

    RuntimeCommandResult SelectTemplate(
        RuntimeGenerationToken expectedGeneration,
        uint templateIndex);

    RuntimeCommandResult SetAttribute(
        RuntimeGenerationToken expectedGeneration,
        Session.ChargenAttributeId attributeId,
        int value);

    RuntimeCommandResult SetAttributeLock(
        RuntimeGenerationToken expectedGeneration,
        Session.ChargenAttributeId attributeId,
        bool locked);

    RuntimeCommandResult TrainSkill(
        RuntimeGenerationToken expectedGeneration,
        uint skillId);

    RuntimeCommandResult SpecializeSkill(
        RuntimeGenerationToken expectedGeneration,
        uint skillId);

    RuntimeCommandResult UntrainSkill(
        RuntimeGenerationToken expectedGeneration,
        uint skillId);

    RuntimeCommandResult SetAppearanceIndex(
        RuntimeGenerationToken expectedGeneration,
        Session.ChargenAppearanceSlot slot,
        uint index);

    RuntimeCommandResult SetShade(
        RuntimeGenerationToken expectedGeneration,
        Session.ChargenShadeSlot slot,
        double value);

    RuntimeCommandResult SelectStartArea(
        RuntimeGenerationToken expectedGeneration,
        int startAreaIndex);

    RuntimeCommandResult SetName(
        RuntimeGenerationToken expectedGeneration,
        string name);

    RuntimeCommandResult SetSlot(
        RuntimeGenerationToken expectedGeneration,
        uint slot);

    RuntimeCommandResult Finish(
        RuntimeGenerationToken expectedGeneration,
        bool confirmUnspentCredits = false);

    RuntimeCommandResult AcknowledgeRejection(
        RuntimeGenerationToken expectedGeneration);


    RuntimeCommandResult RandomizeCharacter(
        RuntimeGenerationToken expectedGeneration);

    RuntimeCommandResult RandomizeAppearance(
        RuntimeGenerationToken expectedGeneration);

    RuntimeCommandResult RandomizeClothing(
        RuntimeGenerationToken expectedGeneration);
}

public interface IGameRuntimeCommands
{
    IRuntimeSessionCommands Session { get; }

    Session.IRuntimeCharacterSelectionCommands CharacterSelection =>
        throw new NotSupportedException(
            "This command adapter does not project character selection.");

    IRuntimeCharacterCreationCommands CharacterCreation =>
        throw new NotSupportedException(
            "This command adapter does not project character creation.");

    IRuntimeSelectionCommands Selection { get; }

    IRuntimeCombatCommands Combat { get; }

    IRuntimeMagicCommands Magic { get; }

    IRuntimeMovementCommands Movement { get; }

    IRuntimeChatCommands Chat { get; }

    IRuntimePortalCommands Portal { get; }

    IRuntimeInventoryStateCommands InventoryState { get; }

    IRuntimeSpellbookCommands Spellbook { get; }

    IRuntimeCharacterCommands Character { get; }

    IRuntimeSocialCommands Social { get; }

    IRuntimeFellowshipCommands Fellowship { get; }

    IRuntimeAllegianceCommands Allegiance { get; }
}
