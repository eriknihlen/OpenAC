using AcDream.DrakBot.Profiles;

namespace AcDream.DrakBot;

/// <summary>Binding object for the in-game status window.</summary>
internal sealed class BotPanel(BotEngine engine)
{
    internal const string WindowId = "status";

    internal const string Markup = """
        <panel x="0" y="0" w="260" h="168" title="DrakBot">
          <label x="8" y="8" text="{StatusText}"/>
          <label x="8" y="26" text="{BehaviorText}"/>
          <label x="8" y="44" text="{ProfileText}"/>
          <button x="8" y="68" w="116" h="22" text="Start" onclick="{Start}"/>
          <button x="132" y="68" w="116" h="22" text="Stop" onclick="{Stop}"/>
          <toggle x="8" y="98" w="116" h="18" text="Buffs" checked="{BuffsEnabled}" onclick="{ToggleBuffs}"/>
          <toggle x="132" y="98" w="116" h="18" text="Combat" checked="{CombatEnabled}" onclick="{ToggleCombat}"/>
          <toggle x="8" y="120" w="116" h="18" text="Loot" checked="{LootEnabled}" onclick="{ToggleLoot}"/>
          <toggle x="132" y="120" w="116" h="18" text="Navigation" checked="{NavigationEnabled}" onclick="{ToggleNavigation}"/>
          <label x="8" y="146" text="{ReasonText}"/>
        </panel>
        """;

    public string StatusText => engine.IsRunning ? "Running" : "Stopped";

    public string BehaviorText => "Doing: " + engine.ActiveBehaviorName;

    public string ProfileText => "Profile: " + engine.Profile.Name;

    public string ReasonText => engine.LastReason;

    public bool BuffsEnabled => engine.Profile.Buffs.Enabled;

    public bool CombatEnabled => engine.Profile.Combat.Enabled;

    public bool LootEnabled => engine.Profile.Loot.Enabled;

    public bool NavigationEnabled => engine.Profile.Navigation.Enabled;

    public Action Start => engine.Start;

    public Action Stop => engine.Stop;

    public Action ToggleBuffs => () => Update(profile =>
        profile with { Buffs = profile.Buffs with { Enabled = !profile.Buffs.Enabled } });

    public Action ToggleCombat => () => Update(profile =>
        profile with { Combat = profile.Combat with { Enabled = !profile.Combat.Enabled } });

    public Action ToggleLoot => () => Update(profile =>
        profile with { Loot = profile.Loot with { Enabled = !profile.Loot.Enabled } });

    public Action ToggleNavigation => () => Update(profile =>
        profile with { Navigation = profile.Navigation with { Enabled = !profile.Navigation.Enabled } });

    private void Update(Func<BotProfile, BotProfile> change) =>
        engine.Profile = change(engine.Profile);
}
