using System.Text.Json.Serialization;

namespace AcDream.DrakBot.Meta;

/// <summary>
/// The condition vocabulary of a VTank-style meta. The order is the wire
/// contract: <c>.met</c> files and the <see cref="MetaSchema"/> keyword
/// tables key off the integer value, so members are only ever appended.
/// </summary>
public enum MetaConditionType
{
    Never,
    Always,
    All,
    Any,
    ChatMessage,
    PackSlots_LE,
    SecondsInState_GE,
    CharacterDeath,
    AnyVendorOpen,
    VendorClosed,
    InventoryItemCount_LE,
    InventoryItemCount_GE,
    MonsterNameCountWithinDistance,
    MonsterPriorityCountWithinDistance,
    NeedToBuff,
    NoMonstersWithinDistance,
    Landblock_EQ,
    Landcell_EQ,
    PortalspaceEntered,
    PortalspaceExited,
    Not,
    SecondsInStateP_GE,
    TimeLeftOnSpell_GE,
    TimeLeftOnSpell_LE,
    BurdenPercentage_GE,
    DistAnyRoutePT_GE,
    Expression,
    ChatMessageCapture,
    NavrouteEmpty,
    MainHealthLE,
    MainHealthPHE,
    MainManaLE,
    MainManaPHE,
    MainStamLE,
    VitaePHE,
}

public enum MetaActionType
{
    None,
    ChatCommand,
    SetMetaState,
    EmbeddedNavRoute,
    All,
    CallMetaState,
    ReturnFromCall,
    ExpressionAction,
    ChatExpression,
    SetWatchdog,
    ClearWatchdog,
    GetRAOption,
    SetRAOption,
    CreateView,
    DestroyView,
    DestroyAllViews,
}

/// <summary>
/// One rule of a meta: in <see cref="State"/>, when <see cref="Condition"/>
/// holds, do <see cref="Action"/>. Compound conditions (All/Any/Not) carry
/// <see cref="Children"/>; a DoAll action carries <see cref="ActionChildren"/>.
/// </summary>
public sealed class MetaRule
{
    public string State { get; set; } = "Default";
    public MetaConditionType Condition { get; set; }
    public string ConditionData { get; set; } = string.Empty;
    public MetaActionType Action { get; set; }
    public string ActionData { get; set; } = string.Empty;
    public List<MetaRule> Children { get; set; } = new();
    public List<MetaRule> ActionChildren { get; set; } = new();

    /// <summary>A disabled rule never evaluates; absent in older files, so it defaults on.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Latched once the rule fires in the current state visit; cleared on a state change.</summary>
    [JsonIgnore]
    public bool HasFired { get; set; }

    /// <summary>Engine time of the last fire, for the dashboard.</summary>
    [JsonIgnore]
    public double LastFiredAt { get; set; } = double.NegativeInfinity;
}
