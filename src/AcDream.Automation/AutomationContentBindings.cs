using AcDream.Content;
using DatReaderWriter;
using DatReaderWriter.DBObjs;

namespace AcDream.Automation;

/// <summary>
/// Binds what the automation surface reads from the retail DATs: species
/// names, chargen palette colors, and the skill table's names and icons.
/// Both hosts call it once their DAT collection is open.
/// </summary>
public static class AutomationContentBindings
{
    public const uint SkillTableDid = 0x0E000004u;

    public static void BindDats(
        RuntimeAutomationSurface surface,
        IDatReaderWriter dats,
        Action<string>? warn = null)
    {
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(dats);
        surface.BindSpeciesNameResolver(
            CreatureDisplayNameResolver.Load(dats).Resolve);
        surface.BindPaletteColorResolver(
            new AcDream.Content.CharGen.ChargenAppearanceCatalog(dats));
        if (!dats.TryGet<SkillTable>(SkillTableDid, out var skillTable)
            || skillTable is null)
        {
            warn?.Invoke(
                "plugin automation: retail SkillTable 0x0E000004 missing; "
                + "plugins will see unnamed skills");
            return;
        }

        var names = new Dictionary<uint, string>(skillTable.Skills.Count);
        var icons = new Dictionary<uint, uint>(skillTable.Skills.Count);
        foreach (var entry in skillTable.Skills)
        {
            names[(uint)entry.Key] = entry.Value.Name;
            icons[(uint)entry.Key] = entry.Value.IconId;
        }
        surface.BindSkillNames(names);
        surface.BindSkillIcons(icons);
    }
}
