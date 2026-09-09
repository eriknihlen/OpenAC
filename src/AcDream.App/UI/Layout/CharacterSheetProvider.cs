using System;
using System.Collections.Generic;
using AcDream.App.Net;
using AcDream.Core.Items;
using AcDream.Core.Player;
using AcDream.Runtime.Gameplay;
using DatReaderWriter;
using AcDream.Content;

namespace AcDream.App.UI.Layout;

public sealed class CharacterSheetProvider
{
    private const uint UnassignedXpPropertyId = 2u;

    private static readonly uint[] SkillCreditPropertyIds = { 0x18u };

    private readonly ClientObjectTable _objects;
    private readonly LocalPlayerState _localPlayer;
    private readonly Func<uint> _playerGuid;
    private readonly Func<string?>? _activeToonName;
    private readonly Func<string, CharacterSheet>? _fallbackSheet;
    private readonly Func<bool>? _canSendRaise;
    private readonly Action<uint, ulong>? _sendRaiseAttribute;
    private readonly Action<uint, ulong>? _sendRaiseVital;
    private readonly Action<uint, ulong>? _sendRaiseSkill;
    private readonly Action<uint, uint>? _sendTrainSkill;

    private readonly RuntimeCharacterTitleState? _titles;

    private readonly Func<uint, string?>? _resolveDisplayTitle;

    private readonly Func<string, string?>? _resolveUiString;

    public DatReaderWriter.DBObjs.SkillTable? SkillTable { get; set; }

    public DatReaderWriter.DBObjs.ExperienceTable? ExperienceTable { get; set; }

    public CharacterSheetProvider(
        ClientObjectTable objects,
        LocalPlayerState localPlayer,
        Func<uint> playerGuid,
        Func<string?>? activeToonName = null,
        Func<string, CharacterSheet>? fallbackSheet = null,
        Func<bool>? canSendRaise = null,
        Action<uint, ulong>? sendRaiseAttribute = null,
        Action<uint, ulong>? sendRaiseVital = null,
        Action<uint, ulong>? sendRaiseSkill = null,
        Action<uint, uint>? sendTrainSkill = null,
        RuntimeCharacterTitleState? titles = null,
        Func<uint, string?>? resolveDisplayTitle = null,
        Func<string, string?>? resolveUiString = null)
    {
        _objects = objects ?? throw new ArgumentNullException(nameof(objects));
        _localPlayer = localPlayer ?? throw new ArgumentNullException(nameof(localPlayer));
        _playerGuid = playerGuid ?? throw new ArgumentNullException(nameof(playerGuid));
        _activeToonName = activeToonName;
        _fallbackSheet = fallbackSheet;
        _canSendRaise = canSendRaise;
        _sendRaiseAttribute = sendRaiseAttribute;
        _sendRaiseVital = sendRaiseVital;
        _sendRaiseSkill = sendRaiseSkill;
        _sendTrainSkill = sendTrainSkill;
        _titles = titles;
        _resolveDisplayTitle = resolveDisplayTitle;
        _resolveUiString = resolveUiString;
    }

    public IDisposable SubscribeChanged(Action changed)
    {
        ArgumentNullException.ThrowIfNull(changed);
        return new ChangeBinding(this, changed);
    }

    // ── Sheet assembly ─────────────────────────────────────────────────────

    /// <summary>Best display name: active toon key, else the live object's name, else "Player".</summary>
    public string CharacterName()
    {
        string? toon = _activeToonName?.Invoke();
        if (!string.IsNullOrWhiteSpace(toon) && toon != "default")
            return toon;
        uint guid = _playerGuid();
        if (guid != 0u && _objects.Get(guid)?.Name is { Length: > 0 } objectName)
            return objectName;
        return "Player";
    }

    public CharacterSheet BuildSheet()
    {
        if (!HasLiveData())
            return _fallbackSheet?.Invoke(CharacterName()) ?? new CharacterSheet { Name = CharacterName() };

        var props = CurrentPlayerProperties();
        bool hasLevel = props.Ints.TryGetValue(0x19u, out int levelValue);
        int level = hasLevel ? levelValue : 0;
        int? displayLevel = hasLevel ? levelValue : null;
        long totalXp = props.GetInt64(1u);
        long unassignedXp = props.GetInt64(UnassignedXpPropertyId);
        var xp = ComputeLevelXp(level, totalXp);

        int skillCredits = props.GetInt(0x18u);

        return new CharacterSheet
        {
            Name = AllegianceRankTitleTable.ComposeFullName(
                props.GetInt(AllegianceRankTitleTable.AllegianceRankPropertyId),
                props.GetInt(CharacterIdentityText.HeritageGroupPropertyId),
                props.GetInt(CharacterIdentityText.GenderPropertyId),
                CharacterName()),
            Level = displayLevel,
            Gender = CharacterIdentityText.GenderDisplayName(
                props.GetInt(CharacterIdentityText.GenderPropertyId)),
            Heritage = CharacterIdentityText.HeritageGroupDisplayName(
                props.GetInt(CharacterIdentityText.HeritageGroupPropertyId)),
            Title = _titles is not null && _resolveDisplayTitle is not null
                ? _resolveDisplayTitle(_titles.DisplayTitleId)
                : null,
            PkStatus = PkStatusText(CurrentPlayerBitfield(), _resolveUiString),
            TotalXp = totalXp,
            XpToNextLevel = xp.toNext,
            XpFraction = xp.fraction,
            AvailableLuminance = props.GetInt64(6u),
            MaximumLuminance = props.GetInt64(7u),

            HealthCurrent = VitalCurrent(LocalPlayerState.VitalKind.Health),
            HealthMax = VitalMax(LocalPlayerState.VitalKind.Health),
            StaminaCurrent = VitalCurrent(LocalPlayerState.VitalKind.Stamina),
            StaminaMax = VitalMax(LocalPlayerState.VitalKind.Stamina),
            ManaCurrent = VitalCurrent(LocalPlayerState.VitalKind.Mana),
            ManaMax = VitalMax(LocalPlayerState.VitalKind.Mana),
            VitalBaseMaxValues =
            [
                VitalBaseMax(LocalPlayerState.VitalKind.Health),
                VitalBaseMax(LocalPlayerState.VitalKind.Stamina),
                VitalBaseMax(LocalPlayerState.VitalKind.Mana),
            ],
            VitalVitaeModifiers =
            [
                _localPlayer.GetVitalVitaeModifier(LocalPlayerState.VitalKind.Health),
                _localPlayer.GetVitalVitaeModifier(LocalPlayerState.VitalKind.Stamina),
                _localPlayer.GetVitalVitaeModifier(LocalPlayerState.VitalKind.Mana),
            ],

            Strength = AttrEffective(LocalPlayerState.AttributeKind.Strength),
            Endurance = AttrEffective(LocalPlayerState.AttributeKind.Endurance),
            Coordination = AttrEffective(LocalPlayerState.AttributeKind.Coordination),
            Quickness = AttrEffective(LocalPlayerState.AttributeKind.Quickness),
            Focus = AttrEffective(LocalPlayerState.AttributeKind.Focus),
            Self = AttrEffective(LocalPlayerState.AttributeKind.Self),

            UnspentSkillCredits = skillCredits,
            SpecializedSkillCredits = 0,
            ChessRank = props.GetInt(0xB5u),
            FishingSkill = props.GetInt(0xC0u),
            BirthTimestamp = props.Ints.TryGetValue(0x62u, out int born)
                ? born
                : null,
            TotalPlayTimeSeconds = props.Ints.TryGetValue(0x7Du, out int played)
                ? played
                : null,
            Deaths = props.GetInt(0x2Bu),
            SkillCredits = skillCredits,
            AwaitingRaise = _awaitingRaise,
            UnassignedXp = unassignedXp,
            AttributeRaiseCosts = BuildAttributeRaiseCosts(amount: 1),
            AttributeRaise10Costs = BuildAttributeRaiseCosts(amount: 10),
            AttributeBaseValues = new[]
            {
                AttrCurrent(LocalPlayerState.AttributeKind.Strength),
                AttrCurrent(LocalPlayerState.AttributeKind.Endurance),
                AttrCurrent(LocalPlayerState.AttributeKind.Coordination),
                AttrCurrent(LocalPlayerState.AttributeKind.Quickness),
                AttrCurrent(LocalPlayerState.AttributeKind.Focus),
                AttrCurrent(LocalPlayerState.AttributeKind.Self),
            },
            Skills = BuildLiveCharacterSkills(props),
            BurdenCurrent = props.GetInt(5u),
            BurdenMax = props.GetInt(96u),
            EncumbranceAugmentations = props.GetInt(0xE6u),
            CharacterInfoProperties = new Dictionary<uint, int>(props.Ints),
        };
    }

    private bool HasLiveData()
    {
        var props = CurrentPlayerProperties();
        return props.Ints.Count > 0
            || props.Int64s.Count > 0
            || _localPlayer.Skills.Count > 0
            || _localPlayer.GetAttribute(LocalPlayerState.AttributeKind.Strength) is not null
            || _localPlayer.Get(LocalPlayerState.VitalKind.Health) is not null;
    }

    private PropertyBundle CurrentPlayerProperties()
    {
        uint guid = _playerGuid();
        return guid != 0u && _objects.Get(guid) is { } player
            ? player.Properties
            : _localPlayer.Properties;
    }

    private uint CurrentPlayerBitfield()
    {
        uint guid = _playerGuid();
        return guid != 0u && _objects.Get(guid) is { } player
            ? player.PublicWeenieBitfield ?? 0u
            : 0u;
    }

    private sealed class ChangeBinding : IDisposable
    {
        private CharacterSheetProvider? _owner;
        private readonly Action _changed;

        public ChangeBinding(CharacterSheetProvider owner, Action changed)
        {
            _owner = owner;
            _changed = changed;
            owner._objects.ObjectAdded += OnObjectChanged;
            owner._objects.ObjectUpdated += OnObjectChanged;
            owner._objects.ObjectRemoved += OnObjectChanged;
            owner._objects.Cleared += OnCleared;
            owner._localPlayer.AttributeChanged += OnAttributeChanged;
            owner._localPlayer.CharacterChanged += OnCharacterChanged;
            owner._localPlayer.Changed += OnVitalChanged;
            if (owner._localPlayer.Spellbook is { } spellbook)
                spellbook.EnchantmentsChanged += OnCleared;
            if (owner._titles is { } titles)
            {
                titles.TableReplaced += OnCleared;
                titles.DisplayTitleChanged += OnDisplayTitleChanged;
            }
        }

        private void OnDisplayTitleChanged(uint _) => OnCleared();

        private void OnObjectChanged(ClientObject value)
        {
            CharacterSheetProvider? owner = _owner;
            if (owner is not null && value.ObjectId == owner._playerGuid())
            {
                owner.ReleaseAwaitingRaise();
                _changed();
            }
        }

        private void OnCleared() => _changed();

        private void OnAttributeChanged(LocalPlayerState.AttributeKind _)
        {
            _owner?.ReleaseAwaitingRaise();
            _changed();
        }

        private void OnCharacterChanged()
        {
            _owner?.ReleaseAwaitingRaise();
            _changed();
        }

        private void OnVitalChanged(LocalPlayerState.VitalKind _)
        {
            CharacterSheetProvider? owner = _owner;
            if (owner is null || !owner._awaitingRaise)
                return;
            owner.ReleaseAwaitingRaise();
            _changed();
        }

        public void Dispose()
        {
            CharacterSheetProvider? owner = Interlocked.Exchange(ref _owner, null);
            if (owner is null)
                return;

            owner._objects.ObjectAdded -= OnObjectChanged;
            owner._objects.ObjectUpdated -= OnObjectChanged;
            owner._objects.ObjectRemoved -= OnObjectChanged;
            owner._objects.Cleared -= OnCleared;
            owner._localPlayer.AttributeChanged -= OnAttributeChanged;
            owner._localPlayer.CharacterChanged -= OnCharacterChanged;
            owner._localPlayer.Changed -= OnVitalChanged;
            if (owner._localPlayer.Spellbook is { } spellbook)
                spellbook.EnchantmentsChanged -= OnCleared;
            if (owner._titles is { } titles)
            {
                titles.TableReplaced -= OnCleared;
                titles.DisplayTitleChanged -= OnDisplayTitleChanged;
            }
            owner.ReleaseAwaitingRaise();
        }
    }

    public static DatReaderWriter.DBObjs.ExperienceTable? LoadExperienceTable(
        IDatReaderWriter dats, Action<string>? log = null)
    {
        if (dats is null) return null;

        try
        {
            var table = dats.Get<DatReaderWriter.DBObjs.ExperienceTable>(0x0E000018u);
            if (table is not null) return table;
        }
        catch (Exception ex)
        {
            log?.Invoke($"[UI] ExperienceTable 0x0E000018 read failed ({ex.GetType().Name}: {ex.Message}); trying type scan.");
        }

        try
        {
            foreach (uint id in dats.GetAllIdsOfType<DatReaderWriter.DBObjs.ExperienceTable>())
            {
                var table = dats.Get<DatReaderWriter.DBObjs.ExperienceTable>(id);
                if (table is not null) return table;
            }
        }
        catch (Exception ex)
        {
            log?.Invoke($"[UI] ExperienceTable type scan failed ({ex.GetType().Name}: {ex.Message}); raise costs unavailable.");
        }

        return null;
    }

    private (long toNext, float fraction) ComputeLevelXp(int level, long totalXp)
    {
        var levels = ExperienceTable?.Levels;
        if (levels is null || level < 0 || level + 1 >= levels.Length)
            return (0L, 0f);

        long current = ClampToLong(levels[level]);
        long next = ClampToLong(levels[level + 1]);
        if (next <= current) return (0L, 0f);

        long clampedXp = totalXp < current ? current : totalXp > next ? next : totalXp;
        long toNext = next - clampedXp;
        float fraction = (float)(clampedXp - current) / (next - current);
        return (toNext, fraction);
    }

    private long[] BuildAttributeRaiseCosts(int amount)
    {
        var xp = ExperienceTable;
        return new[]
        {
            AttributeRaiseCost(LocalPlayerState.AttributeKind.Strength),
            AttributeRaiseCost(LocalPlayerState.AttributeKind.Endurance),
            AttributeRaiseCost(LocalPlayerState.AttributeKind.Coordination),
            AttributeRaiseCost(LocalPlayerState.AttributeKind.Quickness),
            AttributeRaiseCost(LocalPlayerState.AttributeKind.Focus),
            AttributeRaiseCost(LocalPlayerState.AttributeKind.Self),
            VitalRaiseCost(LocalPlayerState.VitalKind.Health),
            VitalRaiseCost(LocalPlayerState.VitalKind.Stamina),
            VitalRaiseCost(LocalPlayerState.VitalKind.Mana),
        };

        long AttributeRaiseCost(LocalPlayerState.AttributeKind kind)
        {
            var attr = _localPlayer.GetAttribute(kind);
            return attr is null || xp is null ? 0L : RaiseCostFromXpCurve(xp.Attributes, attr.Value.Ranks, attr.Value.Xp, amount);
        }

        long VitalRaiseCost(LocalPlayerState.VitalKind kind)
        {
            var vital = _localPlayer.Get(kind);
            return vital is null || xp is null ? 0L : RaiseCostFromXpCurve(xp.Vitals, vital.Value.Ranks, vital.Value.Xp, amount);
        }
    }

    private IReadOnlyList<CharacterSkill> BuildLiveCharacterSkills(
        PropertyBundle properties)
    {
        var result = new List<CharacterSkill>();
        var skillTable = SkillTable;
        var xp = ExperienceTable;

        foreach (var snapshot in _localPlayer.Skills.Values)
        {
            var advancement = AdvancementFromStatus(snapshot.Status);
            if (advancement == CharacterSkillAdvancementClass.Inactive)
                continue;

            DatReaderWriter.Types.SkillBase? skillBase = null;
            if (skillTable?.Skills is not null)
                skillTable.Skills.TryGetValue((DatReaderWriter.Enums.SkillId)snapshot.SkillId, out skillBase);

            string? name = skillBase?.Name.Value;
            if (string.IsNullOrWhiteSpace(name))
                name = $"Skill {snapshot.SkillId}";

            uint icon = skillBase?.IconId.DataId ?? 0u;
            int trainedCost = skillBase?.TrainedCost ?? 0;
            int specializedCost = skillBase?.SpecializedCost ?? 0;
            long raiseCost = SkillRaiseCost(xp, advancement, snapshot, 1);
            long raise10Cost = SkillRaiseCost(xp, advancement, snapshot, 10);
            string? tooltipText = skillBase is null ? null : RetailSkillFormula.BuildTooltip(skillBase);

            PlayerSkillMath.Value values =
                _localPlayer.GetSkillValue(snapshot.SkillId, properties)
                ?? new PlayerSkillMath.Value(
                    checked((int)Math.Min(int.MaxValue, snapshot.CurrentLevel)),
                    checked((int)Math.Min(int.MaxValue, snapshot.CurrentLevel)),
                    0);

            result.Add(new CharacterSkill(
                snapshot.SkillId,
                name,
                icon,
                advancement,
                values.UnenchantedLevel,
                values.EffectiveLevel,
                IsUsableUntrained(snapshot.SkillId),
                trainedCost,
                specializedCost,
                raiseCost,
                raise10Cost,
                values.VitaeModifier,
                tooltipText));
        }

        return result;
    }

    private static CharacterSkillAdvancementClass AdvancementFromStatus(uint status) => status switch
    {
        1u => CharacterSkillAdvancementClass.Untrained,
        2u => CharacterSkillAdvancementClass.Trained,
        3u => CharacterSkillAdvancementClass.Specialized,
        _ => CharacterSkillAdvancementClass.Inactive,
    };

    private static bool IsUsableUntrained(uint skillId) => skillId switch
    {
        18u or 37u or 38u or 39u or 40u => false,
        _ => true,
    };

    private static long SkillRaiseCost(
        DatReaderWriter.DBObjs.ExperienceTable? xp,
        CharacterSkillAdvancementClass advancement,
        LocalPlayerState.SkillSnapshot skill,
        int amount)
    {
        if (xp is null) return 0L;
        uint[] curve = advancement == CharacterSkillAdvancementClass.Specialized
            ? xp.SpecializedSkills
            : xp.TrainedSkills;
        return RaiseCostFromXpCurve(curve, skill.Ranks, skill.Xp, amount);
    }

    private static long RaiseCostFromXpCurve(uint[]? curve, uint ranks, uint spentXp, int amount)
    {
        if (curve is null || amount <= 0) return 0L;
        long maxIndex = curve.Length - 1L;
        if (maxIndex <= ranks) return 0L;
        long targetLong = Math.Min((long)ranks + amount, maxIndex);
        long targetXp = curve[(int)targetLong];
        long cost = targetXp - spentXp;
        return cost > 0 ? cost : 0L;
    }

    private static long ClampToLong(ulong value) =>
        value > long.MaxValue ? long.MaxValue : (long)value;

    private static string? PkStatusText(uint publicWeenieBitfield, Func<string, string?>? resolveUiString)
    {
        var bitfield = (PublicWeenieFlags)publicWeenieBitfield;
        string key = (bitfield & PublicWeenieFlags.PlayerKiller) != 0
            ? "ID_StatManagement_Header_PKStatus_PK"
            : (bitfield & PublicWeenieFlags.PlayerKillerLite) != 0
                ? "ID_StatManagement_Header_PKStatus_PKL"
                : "ID_StatManagement_Header_PKStatus_NPK";
        return resolveUiString?.Invoke(key);
    }

    private int AttrCurrent(LocalPlayerState.AttributeKind kind) =>
        _localPlayer.GetAttribute(kind) is { } attr ? checked((int)Math.Min(int.MaxValue, attr.Current)) : 0;

    private int AttrEffective(LocalPlayerState.AttributeKind kind) =>
        _localPlayer.GetEffectiveAttribute(kind) ?? 0;

    private int VitalCurrent(LocalPlayerState.VitalKind kind) =>
        _localPlayer.Get(kind) is { } vital ? checked((int)Math.Min(int.MaxValue, vital.Current)) : 0;

    private int VitalMax(LocalPlayerState.VitalKind kind) =>
        _localPlayer.GetMaxApprox(kind) is { } max ? checked((int)Math.Min(int.MaxValue, max)) : 0;

    private int VitalBaseMax(LocalPlayerState.VitalKind kind) =>
        _localPlayer.GetBaseMaxApprox(kind) is { } max
            ? checked((int)Math.Min(int.MaxValue, max))
            : 0;

    // ── Raise-request flow ─────────────────────────────────────────────────

    public void HandleRaiseRequest(CharacterStatController.RaiseRequest request)
    {
        if (_awaitingRaise) return;
        if (request.Cost <= 0) return;
        if (_canSendRaise is not null && !_canSendRaise()) return;

        bool sent = false;
        switch (request.Kind)
        {
            case CharacterStatController.RaiseTargetKind.Attribute:
                if (_sendRaiseAttribute is not null)
                {
                    _sendRaiseAttribute(request.StatId, (ulong)request.Cost);
                    sent = true;
                }
                break;
            case CharacterStatController.RaiseTargetKind.Vital:
                if (_sendRaiseVital is not null)
                {
                    _sendRaiseVital(request.StatId, (ulong)request.Cost);
                    sent = true;
                }
                break;
            case CharacterStatController.RaiseTargetKind.Skill:
                if (_sendRaiseSkill is not null)
                {
                    _sendRaiseSkill(request.StatId, (ulong)request.Cost);
                    sent = true;
                }
                break;
            case CharacterStatController.RaiseTargetKind.TrainSkill:
                if (_sendTrainSkill is not null && request.Cost <= uint.MaxValue)
                {
                    _sendTrainSkill(request.StatId, (uint)request.Cost);
                    sent = true;
                }
                break;
        }

        if (sent)
            _awaitingRaise = true;
    }

    internal void ReleaseAwaitingRaise() => _awaitingRaise = false;

    private bool _awaitingRaise;

}
