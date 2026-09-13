using System;
using System.Collections.Generic;

namespace AcDream.DrakBot.Meta;

/// <summary>
/// Result of loading a .af or .met file: rules plus any embedded NAV routes
/// that ship inside the macro. Embedded navs stay in memory — they are never
/// extracted to the NavProfiles folder.
/// </summary>
public sealed class LoadedMeta
{
    public List<MetaRule> Rules { get; set; } = new();

    public Dictionary<string, List<string>> EmbeddedNavs { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Non-fatal parse problems (unknown keywords, parse exception). Surfaced
    /// to the user on load so a typo'd / unsupported meta is visible instead of
    /// silently producing zero or partial rules.
    /// </summary>
    public List<string> Warnings { get; } = new();
}
