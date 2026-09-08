using System.Globalization;
using AcDream.Plugin.Abstractions;

namespace AcDream.Plugins.MossTank;

internal static class SpellComponentPolicy
{
    public static bool UsesBlacklistedComponent(
        ISpellCatalog catalog,
        in PluginSpellInfo spell,
        string setting)
    {
        if (string.IsNullOrWhiteSpace(setting)
            || spell.FormulaComponentIds.Count == 0)
        {
            return false;
        }
        foreach (uint componentId in spell.FormulaComponentIds)
        {
            if (ContainsNumber(setting, componentId))
                return true;
            if (!catalog.TryGetComponent(
                    componentId,
                    out PluginSpellComponentInfo component))
            {
                continue;
            }
            if (component.Name.Length != 0
                && setting.Contains(
                    component.Name,
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (ContainsNumber(setting, component.WeenieClassId))
                return true;
        }
        return false;
    }

    private static bool ContainsNumber(string setting, uint value)
    {
        if (value == 0u)
            return false;
        string decimalText = value.ToString(CultureInfo.InvariantCulture);
        string hexText = value.ToString("X", CultureInfo.InvariantCulture);
        return ContainsDelimited(setting, decimalText)
            || setting.Contains("0x" + hexText, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsDelimited(string text, string token)
    {
        int start = 0;
        while ((start = text.IndexOf(
                   token,
                   start,
                   StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            int end = start + token.Length;
            bool left = start == 0 || !char.IsDigit(text[start - 1]);
            bool right = end == text.Length || !char.IsDigit(text[end]);
            if (left && right)
                return true;
            start = end;
        }
        return false;
    }
}
