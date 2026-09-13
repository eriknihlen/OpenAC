using System;
using System.Collections.Generic;

namespace AcDream.DrakBot.Meta;

/// <summary>
/// Single source of truth for the meta condition/action vocabulary (§3.3).
///
/// Replaces the hand-synced pairs that used to drift apart:
///   • AfFileParser.ConditionKeywords / ActionKeywords      (.af read)
///   • AfFileWriter.ConditionKeywordMap / ActionKeywordMap   (.af write)
///   • LegacyMetaUi._metaConditionNames / _metaActionNames   (ImGui labels)
/// (the §2.6 SecsOnSpell _LE→_GE data-loss bug was exactly a read/write map
/// desync). The arrays here are ordered by the enum int value, which is the
/// real wire contract (JSON bridge sends (int)Condition; the engine-side
/// MetaPanel label array and the .met VTank map all key off it). A static
/// self-check flags drift instead of letting it corrupt saves silently.
///
/// NOT folded in: MetFileParser's VTankCType/AType maps — those translate
/// VTank's *own* binary protocol numbering to our enum and are a separate
/// concern (an adapter, not a duplicate). And the engine-repo MetaPanel label
/// array can't be referenced from the plugin; the drift-check at least catches
/// an enum reorder that would silently break that contract.
///
/// No reflection / no custom attributes — explicit arrays are NativeAOT-safe
/// and deterministic (the review's "reflection at startup" suggestion would be
/// trim-fragile here).
/// </summary>
public static class MetaSchema
{
    internal readonly record struct CondInfo(MetaConditionType Type, string Keyword, string Label);
    internal readonly record struct ActInfo(MetaActionType Type, string Keyword, string Label);

    // Index == (int)MetaConditionType. Keyword is the .af token (W2b gives the
    // typed-vital rows real keywords so they round-trip instead of being
    // written as Expr{…} and reloaded as a generic Expression).
    internal static readonly CondInfo[] Conditions =
    {
        new(MetaConditionType.Never,                              "Never",               "Never"),
        new(MetaConditionType.Always,                             "Always",              "Always"),
        new(MetaConditionType.All,                                "All",                 "All"),
        new(MetaConditionType.Any,                                "Any",                 "Any"),
        new(MetaConditionType.ChatMessage,                        "ChatMatch",           "Chat Message"),
        new(MetaConditionType.PackSlots_LE,                       "MainSlotsLE",         "Pack Slots <="),
        new(MetaConditionType.SecondsInState_GE,                  "SecsInStateGE",       "Seconds in State >="),
        new(MetaConditionType.CharacterDeath,                     "Death",               "Character Death"),
        new(MetaConditionType.AnyVendorOpen,                      "VendorOpen",          "Any Vendor Open"),
        new(MetaConditionType.VendorClosed,                       "VendorClosed",        "Vendor Closed"),
        new(MetaConditionType.InventoryItemCount_LE,              "ItemCountLE",         "Inventory Item Count <="),
        new(MetaConditionType.InventoryItemCount_GE,              "ItemCountGE",         "Inventory Item Count >="),
        new(MetaConditionType.MonsterNameCountWithinDistance,     "MobsInDist_Name",     "Monster Name Count Within Distance"),
        new(MetaConditionType.MonsterPriorityCountWithinDistance, "MobsInDist_Priority", "Monster Priority Count Within Distance"),
        new(MetaConditionType.NeedToBuff,                         "NeedToBuff",          "Need To Buff"),
        new(MetaConditionType.NoMonstersWithinDistance,           "NoMobsInDist",        "No Monsters Within Distance"),
        new(MetaConditionType.Landblock_EQ,                       "BlockE",              "Landblock =="),
        new(MetaConditionType.Landcell_EQ,                        "CellE",               "Landcell =="),
        new(MetaConditionType.PortalspaceEntered,                 "IntoPortal",          "Portalspace Entered"),
        new(MetaConditionType.PortalspaceExited,                  "ExitPortal",          "Portalspace Exited"),
        new(MetaConditionType.Not,                                "Not",                 "Not"),
        new(MetaConditionType.SecondsInStateP_GE,                 "PSecsInStateGE",      "Seconds in State (P) >="),
        new(MetaConditionType.TimeLeftOnSpell_GE,                 "SecsOnSpellGE",       "Time Left On Spell >="),
        new(MetaConditionType.TimeLeftOnSpell_LE,                 "SecsOnSpellLE",       "Time Left On Spell <="),
        new(MetaConditionType.BurdenPercentage_GE,               "BuPercentGE",         "Burden Percentage >="),
        new(MetaConditionType.DistAnyRoutePT_GE,                  "DistToRteGE",         "Dist Any Route PT >="),
        new(MetaConditionType.Expression,                         "Expr",                "Expression"),
        new(MetaConditionType.ChatMessageCapture,                 "ChatCapture",         "Chat Message Capture"),
        new(MetaConditionType.NavrouteEmpty,                      "NavEmpty",            "Navroute Empty"),
        new(MetaConditionType.MainHealthLE,                       "MainHealthLE",        "Main Health <="),
        new(MetaConditionType.MainHealthPHE,                      "MainHealthPHE",       "Main Health % >="),
        new(MetaConditionType.MainManaLE,                         "MainManaLE",          "Main Mana <="),
        new(MetaConditionType.MainManaPHE,                        "MainManaPHE",         "Main Mana % >="),
        new(MetaConditionType.MainStamLE,                         "MainStamLE",          "Main Stam <="),
        new(MetaConditionType.VitaePHE,                           "VitaePHE",            "Vitae % >="),
    };

    // Index == (int)MetaActionType.
    internal static readonly ActInfo[] Actions =
    {
        new(MetaActionType.None,             "None",            "None"),
        new(MetaActionType.ChatCommand,      "Chat",            "Chat Command"),
        new(MetaActionType.SetMetaState,     "SetState",        "Set Meta State"),
        new(MetaActionType.EmbeddedNavRoute, "EmbedNav",        "Embedded Nav Route"),
        new(MetaActionType.All,              "DoAll",           "All"),
        new(MetaActionType.CallMetaState,    "CallState",       "Call Meta State"),
        new(MetaActionType.ReturnFromCall,   "Return",          "Return From Call"),
        new(MetaActionType.ExpressionAction, "DoExpr",          "Expression Action"),
        new(MetaActionType.ChatExpression,   "ChatExpr",        "Chat Expression"),
        new(MetaActionType.SetWatchdog,      "SetWatchdog",     "Set Watchdog"),
        new(MetaActionType.ClearWatchdog,    "ClearWatchdog",   "Clear Watchdog"),
        new(MetaActionType.GetRAOption,      "GetOpt",          "Get RA Option"),
        new(MetaActionType.SetRAOption,      "SetOpt",          "Set RA Option"),
        new(MetaActionType.CreateView,       "CreateView",      "Create View"),
        new(MetaActionType.DestroyView,      "DestroyView",     "Destroy View"),
        new(MetaActionType.DestroyAllViews,  "DestroyAllViews", "Destroy All Views"),
    };

    private static readonly Dictionary<string, MetaConditionType> _condByKeyword =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, MetaActionType> _actByKeyword =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly string[] _condLabels;
    private static readonly string[] _actLabels;

    /// <summary>Non-null if the schema arrays drifted from the enums. Logged
    /// once by MetaManager rather than thrown (a broken meta UI beats a dead
    /// plugin).</summary>
    internal static string? DriftError { get; }

    static MetaSchema()
    {
        foreach (var c in Conditions) _condByKeyword[c.Keyword] = c.Type;
        foreach (var a in Actions)    _actByKeyword[a.Keyword] = a.Type;
        _condLabels = Array.ConvertAll(Conditions, c => c.Label);
        _actLabels  = Array.ConvertAll(Actions,    a => a.Label);

        var errs = new List<string>();
        var condVals = Enum.GetValues<MetaConditionType>();
        if (Conditions.Length != condVals.Length)
            errs.Add($"condition count {Conditions.Length} != enum {condVals.Length}");
        else
            for (int i = 0; i < Conditions.Length; i++)
                if ((int)Conditions[i].Type != i)
                    errs.Add($"condition[{i}] is {Conditions[i].Type} (={(int)Conditions[i].Type}), expected enum value {i}");

        var actVals = Enum.GetValues<MetaActionType>();
        if (Actions.Length != actVals.Length)
            errs.Add($"action count {Actions.Length} != enum {actVals.Length}");
        else
            for (int i = 0; i < Actions.Length; i++)
                if ((int)Actions[i].Type != i)
                    errs.Add($"action[{i}] is {Actions[i].Type} (={(int)Actions[i].Type}), expected enum value {i}");

        DriftError = errs.Count == 0 ? null : string.Join("; ", errs);
    }

    // ── Lookups ───────────────────────────────────────────────────────────────

    public static bool TryCondition(string keyword, out MetaConditionType type)
        => _condByKeyword.TryGetValue(keyword, out type);

    public static bool TryAction(string keyword, out MetaActionType type)
        => _actByKeyword.TryGetValue(keyword, out type);

    public static string ConditionKeyword(MetaConditionType t)
        => (int)t >= 0 && (int)t < Conditions.Length ? Conditions[(int)t].Keyword : "Never";

    public static string ActionKeyword(MetaActionType t)
        => (int)t >= 0 && (int)t < Actions.Length ? Actions[(int)t].Keyword : "None";

    /// <summary>Display labels indexed by enum int — for the ImGui combos.</summary>
    public static string[] ConditionLabels => _condLabels;
    public static string[] ActionLabels => _actLabels;
}
