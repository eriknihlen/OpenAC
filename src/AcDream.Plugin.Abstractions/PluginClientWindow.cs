namespace AcDream.Plugin.Abstractions;

/// <summary>
/// One of the client's own retained windows that a plugin may show, hide,
/// toggle, or query -- the same windows a player opens with a keybind or a
/// toolbar button. A no-window host, or a window this build does not mount,
/// reports every operation as unavailable (<c>false</c>).
/// </summary>
public enum PluginClientWindow
{
    /// <summary>The backpack and equipment window.</summary>
    Inventory,

    /// <summary>The character sheet: attributes, vitals and skills.</summary>
    Character,

    /// <summary>
    /// The character-information window: birth date, time played, deaths,
    /// innate attributes, masteries and augmentations.
    /// </summary>
    CharacterInformation,

    /// <summary>The spellbook.</summary>
    Spellbook,

    /// <summary>The two-tab window holding the map and the housing page.</summary>
    Map,

    /// <summary>The client's settings window.</summary>
    Options,

    /// <summary>The social window: friends, allegiance, fellowship and squelch.</summary>
    Social,

    /// <summary>The quest journal.</summary>
    Journal,

    /// <summary>The list of beneficial effects currently on the character.</summary>
    PositiveEffects,

    /// <summary>The list of harmful effects currently on the character.</summary>
    NegativeEffects,

    /// <summary>The connection-quality indicator.</summary>
    LinkStatus,

    /// <summary>The window showing the character's current death penalty.</summary>
    Vitae,

    /// <summary>The radar.</summary>
    Radar,
}
