namespace AcDream.Plugins.MossTank;

internal enum MacroRuleSlot
{
    SplitPeasCritical,
    CraftFoodCritical,
    RechargeSelfNormal,
    RefillWieldedMana,
    BuffSelfNormal,
    SplitPeasNormal,
    DispelSelf,
    UseDispelItem,
    UseHealersHeart,
    RechargeOther,
    DispelAllies,
    CraftFood,
    RefillPetChargesNormal,
    FellowshipManager,
    OpenDoor,
    ReadScrollPriority,
    StackCramPriority,
    SalvageItemsPriority,
    NavigateCorpsePriority,
    OpenCorpsePriority,
    LootCorpsePriority,
    CorpseWaitPriority,
    NavigateRoutePriority,
    Attack,
    SplitPeasIdle,
    CraftFoodIdle,
    RefillPetChargesIdle,
    ReadScrollIdle,
    StackCramIdle,
    SalvageItemsIdle,
    NavigateCorpseIdle,
    OpenCorpseIdle,
    LootCorpseIdle,
    CorpseWaitIdle,
    BuffSelfIdle,
    NavigateMonster,
    RechargeSelfNoTarget,
    NavigateRouteIdle,
    RandomHelper,
    IdlePeace,
}

internal enum MacroIndependentSlot
{
    SummonPet,
}

/// <summary>
/// One row of the list: either a sentinel marker or an acting slot.
/// </summary>
internal readonly record struct MacroRuleEntry(
    string Name,
    MacroRuleSlot? Slot)
{
    public static MacroRuleEntry Sentinel(string marker) =>
        new("Sentinel " + marker, null);

    public static MacroRuleEntry Rule(MacroRuleSlot slot) =>
        new(slot.ToString(), slot);
}

/// <summary>Supplies the rule instance behind each slot.</summary>
internal interface IMacroRuleProvider
{
    IMacroRule Create(MacroRuleSlot slot);

    IMacroRule Create(MacroIndependentSlot slot);
}

internal static class MacroRuleTable
{
    public static readonly IReadOnlyList<MacroRuleEntry> Entries =
    [
        MacroRuleEntry.Sentinel("START"),
        MacroRuleEntry.Rule(MacroRuleSlot.SplitPeasCritical),
        MacroRuleEntry.Rule(MacroRuleSlot.CraftFoodCritical),               // :461  a9(6 kit/food types, idleCounts:false)
        MacroRuleEntry.Rule(MacroRuleSlot.RechargeSelfNormal),              // :470  cr("Recharge-Norm-*")
        MacroRuleEntry.Rule(MacroRuleSlot.RefillWieldedMana),               // :471  a0
        MacroRuleEntry.Rule(MacroRuleSlot.BuffSelfNormal),                  // :472  fz("RebuffTimeRemainingSeconds")
        MacroRuleEntry.Rule(MacroRuleSlot.SplitPeasNormal),                 // :473  as("SpellCompMin-Normal")
        MacroRuleEntry.Sentinel("POSTBUFF"),                                // :474
        MacroRuleEntry.Rule(MacroRuleSlot.DispelSelf),                      // :475  c8
        MacroRuleEntry.Rule(MacroRuleSlot.UseDispelItem),                   // :476  cx
        MacroRuleEntry.Rule(MacroRuleSlot.UseHealersHeart),                 // :477  fb("Recharge-Helper-HitP")
        MacroRuleEntry.Rule(MacroRuleSlot.RechargeOther),                   // :478  gu("Recharge-Helper-*")
        MacroRuleEntry.Rule(MacroRuleSlot.DispelAllies),                    // :479  af
        MacroRuleEntry.Rule(MacroRuleSlot.CraftFood),                       // :480  a9() (no type filter)
        MacroRuleEntry.Rule(MacroRuleSlot.RefillPetChargesNormal),          // :481  dq("PetRefillCount-Normal")
        MacroRuleEntry.Sentinel("POSTHELPER"),                              // :482
        MacroRuleEntry.Rule(MacroRuleSlot.FellowshipManager),               // :483  g5
        MacroRuleEntry.Sentinel("POSTAUTOFELLOW"),                          // :484
        MacroRuleEntry.Rule(MacroRuleSlot.OpenDoor),                        // :485  b7
        MacroRuleEntry.Sentinel("PREPRIORITYLOOTACTIONS"),                  // :486
        MacroRuleEntry.Rule(MacroRuleSlot.ReadScrollPriority),              // :487  gate EnableLooting + LootPriorityBoost
        MacroRuleEntry.Rule(MacroRuleSlot.StackCramPriority),               // :488  gate same
        MacroRuleEntry.Rule(MacroRuleSlot.SalvageItemsPriority),            // :489  gate same
        MacroRuleEntry.Sentinel("POSTPRIORITYLOOTACTIONS"),                 // :490
        MacroRuleEntry.Sentinel("PREPRIORITYLOOT"),                         // :491
        MacroRuleEntry.Rule(MacroRuleSlot.NavigateCorpsePriority),          // :492  gate EnableLooting + LootPriorityBoost + SetWaitingOnCorpseId
        MacroRuleEntry.Rule(MacroRuleSlot.OpenCorpsePriority),              // :498  gate LootPriorityBoost + SetWaitingOnCorpseId
        MacroRuleEntry.Rule(MacroRuleSlot.LootCorpsePriority),              // :503  gate EnableLooting + LootPriorityBoost
        MacroRuleEntry.Rule(MacroRuleSlot.CorpseWaitPriority),              // :504  gate same
        MacroRuleEntry.Sentinel("POSTPRIORITYLOOT"),                        // :505
        MacroRuleEntry.Sentinel("PREPRIORITYNAV"),                          // :506
        MacroRuleEntry.Rule(MacroRuleSlot.NavigateRoutePriority),           // :508  gate NavPriorityBoost + SetWaitingOnCorpseId
        MacroRuleEntry.Sentinel("POSTPRIORITYNAV"),                         // :513
        MacroRuleEntry.Sentinel("PREATTACK"),                               // :514
        MacroRuleEntry.Rule(MacroRuleSlot.Attack),                          // :515  b4
        MacroRuleEntry.Sentinel("POSTATTACK"),                              // :516
        MacroRuleEntry.Sentinel("PREIDLESTATUS"),                           // :517
        MacroRuleEntry.Rule(MacroRuleSlot.SplitPeasIdle),                   // :518  as("SpellCompMin-Idle")
        MacroRuleEntry.Rule(MacroRuleSlot.CraftFoodIdle),                   // :519  a9(6 types, idleCounts:true)
        MacroRuleEntry.Rule(MacroRuleSlot.RefillPetChargesIdle),            // :528  dq("PetRefillCount-Idle")
        MacroRuleEntry.Sentinel("PREIDLELOOTACTIONS"),                      // :529
        MacroRuleEntry.Rule(MacroRuleSlot.ReadScrollIdle),                  // :530  fallback IdlePeace
        MacroRuleEntry.Rule(MacroRuleSlot.StackCramIdle),                   // :531  fallback IdlePeace
        MacroRuleEntry.Rule(MacroRuleSlot.SalvageItemsIdle),                // :532  fallback IdlePeace
        MacroRuleEntry.Sentinel("POSTIDLELOOTACTIONS"),                     // :533
        MacroRuleEntry.Sentinel("PREIDLELOOT"),                             // :534
        MacroRuleEntry.Rule(MacroRuleSlot.NavigateCorpseIdle),
        MacroRuleEntry.Rule(MacroRuleSlot.OpenCorpseIdle),                  // :544  gate EnableLooting + SetWaitingOnCorpseId, fallback IdlePeace
        MacroRuleEntry.Rule(MacroRuleSlot.LootCorpseIdle),                  // :549  d0 (bare)
        MacroRuleEntry.Rule(MacroRuleSlot.CorpseWaitIdle),                  // :550  a1 (bare)
        MacroRuleEntry.Sentinel("POSTIDLELOOT"),                            // :551
        MacroRuleEntry.Sentinel("PREIDLEBUFF"),                             // :552
        MacroRuleEntry.Rule(MacroRuleSlot.BuffSelfIdle),                    // :553  gate IdleBuffTopoff
        MacroRuleEntry.Sentinel("POSTIDLEBUFF"),                            // :557
        MacroRuleEntry.Sentinel("PRETARGETAPPROACH"),                       // :558
        MacroRuleEntry.Rule(MacroRuleSlot.NavigateMonster),
        MacroRuleEntry.Sentinel("POSTTARGETAPPROACH"),                      // :564
        MacroRuleEntry.Sentinel("PREIDLERECHARGE"),                         // :565
        MacroRuleEntry.Rule(MacroRuleSlot.RechargeSelfNoTarget),            // :566  cr("Recharge-NoTarg-*")
        MacroRuleEntry.Sentinel("POSTIDLERECHARGE"),                        // :567
        MacroRuleEntry.Sentinel("PRENAVROUTE"),                             // :568
        MacroRuleEntry.Rule(MacroRuleSlot.NavigateRouteIdle),
        MacroRuleEntry.Sentinel("POSTNAVROUTE"),                            // :574
        MacroRuleEntry.Rule(MacroRuleSlot.RandomHelper),                    // :575  ba
        MacroRuleEntry.Sentinel("END"),                                     // :576
        MacroRuleEntry.Rule(MacroRuleSlot.IdlePeace),
    ];

    public static readonly IReadOnlyList<MacroIndependentSlot> IndependentEntries =
    [
        MacroIndependentSlot.SummonPet,
    ];

    public static MacroScheduler Build(IMacroRuleProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var main = new List<IMacroRule>(Entries.Count);
        foreach (MacroRuleEntry entry in Entries)
        {
            main.Add(entry.Slot is { } slot
                ? provider.Create(slot)
                : new MacroRuleSentinel(entry.Name["Sentinel ".Length..]));
        }

        var independent = new List<IMacroRule>(IndependentEntries.Count);
        foreach (MacroIndependentSlot slot in IndependentEntries)
            independent.Add(provider.Create(slot));

        return new MacroScheduler(main, independent);
    }
}
