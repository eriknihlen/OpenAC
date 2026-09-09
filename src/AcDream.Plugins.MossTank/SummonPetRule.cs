using AcDream.Plugin.Abstractions;

namespace AcDream.Plugins.MossTank;

internal sealed class SummonPetRule : IMacroRule
{
    private const uint SummoningSkillId = 54u;

    private const uint SummonCooldownSpellId = unchecked((uint)(-32555));

    private readonly IPluginHost _host;
    private readonly CombatSettings _settings;
    private readonly Func<bool> _combatOwnsTarget;
    private readonly PetAutomation _pets = new();

    private double _now;
    private bool _running;

    public SummonPetRule(
        IPluginHost host,
        CombatSettings settings,
        Func<bool> combatOwnsTarget)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _combatOwnsTarget = combatOwnsTarget
            ?? throw new ArgumentNullException(nameof(combatOwnsTarget));
    }

    public string Name => "SummonPet";

    public string Status { get; private set; } = string.Empty;

    public bool ValidNow(in MacroPassContext context)
    {
        _now += Math.Max(0d, context.ElapsedSeconds);

        IAutomationSurface automation = _host.Automation;
        if (!automation.IsAvailable)
            return false;
        if (!_settings.Enabled)          // EnableCombat, h1.cs:32-35
            return false;
        if (!_settings.SummonPets)       // SummonPets, h1.cs:36-39
            return false;
        if (!IsSummoningTrained(automation.Character))  // h1.cs:40-43
            return false;
        if (HasSummonCooldown(automation.Character))    // h1.cs:44-47
            return false;

        if (_combatOwnsTarget())
            return false;

        float range = (float)(_settings.PetRangeMode == PetRangeMode.Custom
            ? _settings.PetCustomRange
            : _settings.MaximumRange);
        IReadOnlyList<PluginCombatTarget> targets =
            automation.Combat.CaptureHostileTargets(range);

        bool claimed = _pets.Tick(
            automation.Items,
            automation.Character,
            targets,
            _settings,
            _now,
            out string status,
            allowRefill: false);
        Status = status;
        return claimed;
    }

    public bool Running
    {
        get => _running;
        set
        {
            _running = value;
            if (!value)
                Status = string.Empty;
        }
    }

    public void Reset()
    {
        _now = 0d;
        Status = string.Empty;
        _running = false;
    }

    private static bool IsSummoningTrained(ICharacterInfo character) =>
        character.TryGetSkill(SummoningSkillId, out PluginSkillInfo skill)
        && skill.Training is PluginSkillTraining.Trained
            or PluginSkillTraining.Specialized;

    private static bool HasSummonCooldown(ICharacterInfo character)
    {
        foreach (PluginActiveEnchantment enchantment in character.ActiveEnchantments)
        {
            if (enchantment.SpellId == SummonCooldownSpellId)
                return true;
        }
        return false;
    }
}
