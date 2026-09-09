using AcDream.Runtime.Gameplay;

namespace AcDream.Runtime;

public readonly record struct RuntimePendingInventoryRequestSnapshot(
    ulong Token,
    int Kind,
    uint ItemId,
    bool Dispatched);

public readonly record struct RuntimeInventoryStateSnapshot(
    uint RequestedExternalContainerId,
    uint CurrentExternalContainerId,
    int BusyCount,
    bool CanBeginRequest,
    RuntimePendingInventoryRequestSnapshot? PendingRequest,
    int ShortcutCount,
    long ShortcutRevision,
    int ItemManaCount,
    long ItemManaRevision);

public readonly record struct RuntimeShortcutSnapshot(
    int Index,
    uint ObjectId,
    uint SpellId);

public interface IRuntimeInventoryStateView
{
    RuntimeInventoryStateSnapshot Snapshot { get; }

    bool TryGetShortcut(int index, out RuntimeShortcutSnapshot shortcut);

    bool TryGetItemMana(uint objectId, out float fraction);
}

public readonly record struct RuntimeCharacterSnapshot(
    long CharacterRevision,
    long SpellbookRevision,
    RuntimeCharacterOptionsSnapshot Options,
    RuntimeMovementSkillSnapshot MovementSkills,
    int LearnedSpellCount,
    int ActiveEnchantmentCount,
    int DesiredComponentCount,
    int SkillCount,
    uint SpellbookFilters,
    RuntimeCharacterTitleSnapshot Titles = default);

public readonly record struct RuntimeVitalSnapshot(
    int Kind,
    uint Ranks,
    uint Start,
    uint Experience,
    uint Current,
    uint Maximum);

public readonly record struct RuntimeAttributeSnapshot(
    int Kind,
    uint Ranks,
    uint Start,
    uint Experience,
    uint Current);

public readonly record struct RuntimeSkillSnapshot(
    uint SkillId,
    uint Ranks,
    uint Status,
    uint Experience,
    uint Initial,
    uint Resistance,
    double LastUsed,
    uint FormulaBonus,
    uint CurrentLevel);

public interface IRuntimeCharacterView
{
    RuntimeCharacterSnapshot Snapshot { get; }

    bool TryGetVital(int kind, out RuntimeVitalSnapshot vital);

    bool TryGetAttribute(int kind, out RuntimeAttributeSnapshot attribute);

    bool TryGetSkill(uint skillId, out RuntimeSkillSnapshot skill);

    bool KnowsSpell(uint spellId);

    bool TryGetFavorite(int tabIndex, int position, out uint spellId);

    bool TryGetDesiredComponent(uint componentId, out uint amount);
}

public readonly record struct RuntimeSocialSnapshot(
    long FriendsRevision,
    int FriendCount,
    long SquelchRevision,
    int SquelchedAccountCount,
    int SquelchedCharacterCount,
    int GlobalSquelchTypeCount,
    int NegotiatedChatRoomCount);

public readonly record struct RuntimeFriendSnapshot(
    uint Id,
    string Name,
    bool Online,
    bool AppearOffline);

public interface IRuntimeSocialView
{
    RuntimeSocialSnapshot Snapshot { get; }

    bool TryGetFriend(uint characterId, out RuntimeFriendSnapshot friend);
}


public readonly record struct RuntimeFellowMemberSnapshot(
    uint Guid,
    string Name,
    uint Level,
    uint MaxHealth,
    uint MaxStamina,
    uint MaxMana,
    uint CurrentHealth,
    uint CurrentStamina,
    uint CurrentMana,
    bool ShareLoot);

public readonly record struct RuntimeFellowshipSnapshot(
    long Revision,
    bool IsInFellowship,
    string Name,
    uint LeaderGuid,
    bool ShareXp,
    bool EvenXpSplit,
    bool IsOpen,
    bool Locked,
    int MemberCount)
{
    public RuntimeFellowshipSnapshot()
        : this(0, false, string.Empty, 0u, false, false, false, false, 0)
    {
    }
}

public interface IRuntimeFellowshipView
{
    RuntimeFellowshipSnapshot Snapshot { get; }

    bool TryGetMember(uint guid, out RuntimeFellowMemberSnapshot member);

    IEnumerable<RuntimeFellowMemberSnapshot> GetMembers();
}

public readonly record struct RuntimeAllegianceMemberSnapshot(
    uint CharacterId,
    uint ParentGuid,
    bool IsLoggedIn,
    string Name,
    ushort Rank,
    uint Level,
    ushort Loyalty,
    ushort Leadership,
    uint CpCached,
    uint CpTithed,
    byte Gender,
    byte HeritageGroup,
    bool MayPassupExperience);

public readonly record struct RuntimeAllegianceSnapshot(
    long Revision,
    bool HasServerSeed,
    bool HasProfile,
    uint Rank,
    uint TotalMembers,
    uint TotalVassals,
    string AllegianceName,
    uint MonarchGuid,
    int RecordCount)
{
    public bool HasMonarch => MonarchGuid != 0u;

    public RuntimeAllegianceSnapshot()
        : this(0, false, false, 0u, 0u, 0u, string.Empty, 0u, 0)
    {
    }
}

public interface IRuntimeAllegianceView
{
    RuntimeAllegianceSnapshot Snapshot { get; }

    bool TryGetMonarch(out RuntimeAllegianceMemberSnapshot monarch);

    bool TryGetMember(uint guid, out RuntimeAllegianceMemberSnapshot member);

    bool TryGetPatron(uint guid, out RuntimeAllegianceMemberSnapshot patron);

    IEnumerable<RuntimeAllegianceMemberSnapshot> GetVassals(uint guid);
}
