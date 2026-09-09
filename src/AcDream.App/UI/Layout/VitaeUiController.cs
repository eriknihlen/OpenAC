using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AcDream.Core.Items;
using AcDream.Core.Spells;

namespace AcDream.App.UI.Layout;

public sealed class VitaeUiController : IRetainedPanelController
{
    public const uint LayoutId = 0x21000020u;
    public const uint RootId = 0x100001C1u;
    public const uint MainTextId = 0x100001C3u;
    public const uint CloseId = 0x100000FCu;

    internal const uint VitaeCpPoolProperty = 0x81u;
    internal const uint DeathLevelProperty = 0x8Bu;
    internal const uint LevelProperty = 0x19u;

    private readonly Spellbook _spellbook;
    private readonly ClientObjectTable _objects;
    private readonly Func<uint> _playerGuid;
    private readonly VitaeStrings _strings;
    private readonly UiText _mainText;
    private readonly UiButton? _close;
    private IReadOnlyList<UiText.Line> _lines = Array.Empty<UiText.Line>();
    private bool _disposed;

    private VitaeUiController(
        ImportedLayout layout,
        Spellbook spellbook,
        ClientObjectTable objects,
        Func<uint> playerGuid,
        VitaeStrings strings,
        Action? close)
    {
        _spellbook = spellbook;
        _objects = objects;
        _playerGuid = playerGuid;
        _strings = strings;
        _mainText = (UiText)layout.FindElement(MainTextId)!;
        _close = layout.FindElement(CloseId) as UiButton;
        _mainText.LinesProvider = () => _lines;
        if (_close is not null) _close.OnClick = close;

        _spellbook.EnchantmentsChanged += Update;
        _objects.ObjectAdded += OnObjectChanged;
        _objects.ObjectUpdated += OnObjectChanged;
        _objects.Cleared += Update;
        Update();
    }

    public static VitaeUiController? Bind(
        ImportedLayout layout,
        Spellbook spellbook,
        ClientObjectTable objects,
        Func<uint> playerGuid,
        VitaeStrings strings,
        Action? close = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(spellbook);
        ArgumentNullException.ThrowIfNull(objects);
        ArgumentNullException.ThrowIfNull(playerGuid);
        ArgumentNullException.ThrowIfNull(strings);
        return layout.FindElement(MainTextId) is UiText
            ? new VitaeUiController(
                layout, spellbook, objects, playerGuid, strings, close)
            : null;
    }

    public void OnShown() => Update();

    private void OnObjectChanged(ClientObject item)
    {
        if (item.ObjectId == _playerGuid()) Update();
    }

    private void Update()
    {
        float vitae = CurrentVitae();
        int lostPercent = 100 - (int)(vitae * 100f);
        string body;
        if (lostPercent <= 0)
        {
            body = _strings.FullStrength;
        }
        else
        {
            ClientObject? player = _objects.Get(_playerGuid());
            int pool = player?.Properties.GetInt(VitaeCpPoolProperty) ?? 0;
            int level = 0;
            if (player is not null)
            {
                level = player.Properties.Ints.TryGetValue(
                    DeathLevelProperty, out int deathLevel)
                    ? deathLevel
                    : player.Properties.GetInt(LevelProperty);
            }
            int remaining = VitaeCpPoolThreshold(vitae, level) - pool;
            body = _strings.LostPrefix
                   + lostPercent
                   + _strings.LostSuffix
                   + _strings.SkillsPrefix
                   + lostPercent
                   + _strings.SkillsSuffix
                   + _strings.RecoveryPrefix
                   + FormatExperience(remaining)
                   + _strings.RecoverySuffix;
        }
        _lines = IndicatorDetailText.Shape(_mainText, body);
    }

    private float CurrentVitae()
        => _spellbook.ActiveEnchantmentSnapshot
            .Where(record => record.Bucket == 4u)
            .Select(record => record.StatModValue)
            .OfType<float>()
            .Where(float.IsFinite)
            .DefaultIfEmpty(1f)
            .Last();

    internal static int VitaeCpPoolThreshold(float vitae, int level)
        => (int)(((Math.Pow(level, 2.5d) * 2.5d) + 20d)
                 * Math.Pow(vitae, 5d)
                 + 0.5d);

    internal static string FormatExperience(int experience)
        => experience.ToString("N0", CultureInfo.InvariantCulture);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _spellbook.EnchantmentsChanged -= Update;
        _objects.ObjectAdded -= OnObjectChanged;
        _objects.ObjectUpdated -= OnObjectChanged;
        _objects.Cleared -= Update;
        if (_close is not null) _close.OnClick = null;
    }
}

public sealed record VitaeStrings(
    string FullStrength,
    string LostPrefix,
    string LostSuffix,
    string SkillsPrefix,
    string SkillsSuffix,
    string RecoveryPrefix,
    string RecoverySuffix)
{
    public static VitaeStrings English { get; } = new(
        "Your Vitae, or life force, is at full strength.",
        "Due to your recent death, you have temporarily lost ",
        "% of your Vitae, or life force.",
        "\n\nThis means that your health, stamina, mana, and skills are temporarily reduced by ",
        "%.  A reduction of less than 15% will not hinder you much, but beware losing much more than that.",
        "\n\nYou will regain 1% of your Vitae once you earn ",
        " more experience.");
}
