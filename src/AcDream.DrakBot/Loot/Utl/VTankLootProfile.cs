using System.Collections.Generic;

namespace AcDream.DrakBot.Loot.Utl;

/// <summary>VTank loot rule action codes — the integer in the info line's second slot.</summary>
public enum VTankLootAction
{
    Keep      = 1,
    Salvage   = 2,
    Sell      = 3,
    Read      = 4,
    KeepUpTo  = 10,
}

/// <summary>One condition node attached to a VTank loot rule.</summary>
public sealed class VTankLootCondition
{
    /// <summary>Node type id from VTank — see VTankNodeTypes for known values.</summary>
    public int NodeType { get; set; }

    /// <summary>
    /// Raw v1+ length-code line preserved verbatim from the source file. The parser
    /// doesn't actually use this value for anything, but it must be round-tripped
    /// so the file is byte-stable. Empty string for v0 files.
    /// </summary>
    public string LengthCode { get; set; } = "0";

    /// <summary>
    /// Data lines for this condition, in VTank's documented order. Length
    /// matches VTankNodeTypes.GetDataLineCount(NodeType) for known types; for
    /// unknown types the parser collects raw lines until the next rule and
    /// stuffs them all in here so save still emits identical bytes.
    /// </summary>
    public List<string> DataLines { get; set; } = new();

    public VTankLootCondition() { }

    public VTankLootCondition(int nodeType, string lengthCode, IEnumerable<string> dataLines)
    {
        NodeType = nodeType;
        LengthCode = lengthCode;
        DataLines = new List<string>(dataLines);
    }
}

/// <summary>
/// Single VTank loot rule. Conditions are evaluated in order; ALL must match
/// for the rule's Action to fire (the standard "9999 DisabledRule" condition
/// returns false to disable a rule entirely).
/// </summary>
public sealed class VTankLootRule
{
    /// <summary>Rule display name as shown in VTank's rule list.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// v1+ custom-expression line, always present in v1 files but typically
    /// blank. Null for v0 files (the field doesn't exist there). Treated as a
    /// pure passthrough — VTank evaluates it server-side via something like
    /// CustomKeyExpression but our evaluator ignores it.
    /// </summary>
    public string? CustomExpression { get; set; }

    /// <summary>
    /// Priority — first slot of the info line. VTank uses priority for
    /// rule ordering ties; we just preserve it.
    /// </summary>
    public int Priority { get; set; }

    /// <summary>Loot action — second slot of the info line.</summary>
    public VTankLootAction Action { get; set; } = VTankLootAction.Keep;

    /// <summary>
    /// Keep-count for the KeepUpTo action — null for every other action. Stored
    /// on the line immediately after the info line in the source file.
    /// </summary>
    public int? KeepCount { get; set; }

    /// <summary>Condition nodes; same order as the type list in the info line.</summary>
    public List<VTankLootCondition> Conditions { get; set; } = new();

    /// <summary>
    /// Convenience — true when the rule has no DisabledRule (9999) condition or
    /// that condition's payload is "false". Editors can flip this to add/remove
    /// the DisabledRule node; the writer only emits the node when it's present.
    /// </summary>
    public bool Enabled
    {
        get
        {
            foreach (var c in Conditions)
                if (c.NodeType == 9999 && c.DataLines.Count > 0
                    && string.Equals(c.DataLines[0].Trim(), "true", System.StringComparison.OrdinalIgnoreCase))
                    return false;
            return true;
        }
        set
        {
            // Ensure exactly one DisabledRule node when disabled, none when enabled.
            for (int i = Conditions.Count - 1; i >= 0; i--)
                if (Conditions[i].NodeType == 9999) Conditions.RemoveAt(i);
            if (!value)
                Conditions.Add(new VTankLootCondition(9999, "0", new[] { "true" }));
        }
    }
}

/// <summary>Top-level VTank loot profile.</summary>
public sealed class VTankLootProfile
{
    /// <summary>0 for the legacy headerless format, 1+ for files prefixed with "UTL".</summary>
    public int FileVersion { get; set; } = 1;

    public List<VTankLootRule> Rules { get; set; } = new();

    /// <summary>Optional SalvageCombine block at the end of the file.</summary>
    public SalvageCombineSettings? SalvageCombine { get; set; }
}
