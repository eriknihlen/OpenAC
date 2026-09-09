using AcDream.Plugin.Abstractions;

namespace AcDream.Plugins.MossTank;

internal readonly record struct CraftingPlan(
    VtankCraftRecipe Recipe,
    uint FirstObjectId,
    uint SecondObjectId,
    string DesiredResult)
{
    public bool RequiresSplitFirstStack { get; init; }
    public uint SplitContainerObjectId { get; init; }
}

internal static class ConsumableClassifier
{
    private const uint HealingKitPublicFlag = 0x00010000u;
    private const uint LockpickPublicFlag = 0x00020000u;

    public static ConsumableCategory Classify(in PluginInventoryItem item)
    {
        if (item.Name.Equals(CraftingPlanner.AllPeas, StringComparison.Ordinal))
            return ConsumableCategory.AllPeas;
        if (item.Name.EndsWith(" Pea", StringComparison.Ordinal))
            return ConsumableCategory.Pea;
        if ((item.PublicFlags & LockpickPublicFlag) != 0u)
            return ConsumableCategory.Lockpick;
        if ((item.PublicFlags & HealingKitPublicFlag) != 0u)
            return KitCategory(item.Name);
        return item.BoosterVital switch
        {
            2 => ConsumableCategory.HealthFood,
            4 => ConsumableCategory.StaminaFood,
            6 => ConsumableCategory.ManaFood,
            _ => ClassifyName(item.Name),
        };
    }

    public static ConsumableCategory ClassifyName(string name)
    {
        if (name.Equals(CraftingPlanner.AllPeas, StringComparison.Ordinal))
            return ConsumableCategory.AllPeas;
        if (name.EndsWith(" Pea", StringComparison.Ordinal))
            return ConsumableCategory.Pea;
        return name.EndsWith(" Kit", StringComparison.Ordinal)
            ? KitCategory(name)
            : ConsumableCategory.Other;
    }

    private static ConsumableCategory KitCategory(string name) => name switch
    {
        "Medicated Stamina Kit" or "Eternal Stamina Kit"
            or "Greater Stamina Kit" or "Lesser Stamina Kit" =>
            ConsumableCategory.StaminaKit,
        "Medicated Mana Kit" or "Eternal Mana Kit"
            or "Greater Mana Kit" or "Lesser Mana Kit" =>
            ConsumableCategory.ManaKit,
        _ => ConsumableCategory.HealthKit,
    };
}

internal static class CraftingPlanner
{
    public const string AllPeas = "[All Peas]";

    public static CraftingPlan? Plan(
        IReadOnlyList<PluginInventoryItem> inventory,
        IEnumerable<string> desiredResults,
        ICharacterInfo character,
        int desiredCount = 1,
        int arrowheadFletchDifficultyExcess = 10)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(desiredResults);
        ArgumentNullException.ThrowIfNull(character);
        var counts = inventory
            .GroupBy(static item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                static group => group.Key,
                static group => group.Sum(item => Math.Max(1, item.StackSize)),
                StringComparer.OrdinalIgnoreCase);
        foreach (string desired in desiredResults
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static name => name, StringComparer.OrdinalIgnoreCase))
        {
            if (counts.GetValueOrDefault(desired) >= Math.Max(1, desiredCount))
                continue;
            CraftingPlan? plan = FindStep(
                desired,
                desired,
                inventory,
                character,
                counts,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                arrowheadFletchDifficultyExcess);
            if (plan is not null)
                return plan;
        }
        return null;
    }

    public static CraftingPlan? PlanPeaSplit(
        IReadOnlyList<PluginInventoryItem> inventory,
        ISet<string> consumableProfile,
        int minimumComponentCount)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(consumableProfile);
        int minimum = Math.Max(0, minimumComponentCount);
        if (minimum == 0)
            return null;
        PluginInventoryItem tool = Find(inventory, "Splitting Tool");
        if (tool.ObjectId == 0u)
            return null;
        bool allPeas = consumableProfile.Contains(AllPeas);
        var counts = inventory
            .GroupBy(static item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                static group => group.Key,
                static group => group.Sum(item => Math.Max(1, item.StackSize)),
                StringComparer.OrdinalIgnoreCase);
        foreach (VtankCraftRecipe recipe in VtankCraftDatabase.Recipes)
        {
            if (!recipe.FirstItem.Equals("Splitting Tool", StringComparison.Ordinal)
                || !recipe.SecondItem.EndsWith(" Pea", StringComparison.Ordinal)
                || (!allPeas && !consumableProfile.Contains(recipe.SecondItem))
                || counts.GetValueOrDefault(recipe.ResultItem) >= minimum)
            {
                continue;
            }
            PluginInventoryItem pea = Find(inventory, recipe.SecondItem);
            if (pea.ObjectId == 0u)
                continue;
            return new CraftingPlan(
                recipe,
                tool.ObjectId,
                pea.ObjectId,
                recipe.ResultItem);
        }
        return null;
    }

    private static CraftingPlan? FindStep(
        string result,
        string desiredResult,
        IReadOnlyList<PluginInventoryItem> inventory,
        ICharacterInfo character,
        IReadOnlyDictionary<string, int> counts,
        HashSet<string> visiting,
        int arrowheadFletchDifficultyExcess)
    {
        if (!visiting.Add(result))
            return null;
        try
        {
            foreach (VtankCraftRecipe recipe in VtankCraftDatabase.ForResult(result))
            {
                if (!HasRequiredSkill(
                        character,
                        recipe.RequiredSkill,
                        recipe.Difficulty,
                        arrowheadFletchDifficultyExcess))
                    continue;

                PluginInventoryItem first = Find(inventory, recipe.FirstItem);
                if (first.ObjectId == 0u)
                {
                    CraftingPlan? prerequisite = FindStep(
                        recipe.FirstItem,
                        desiredResult,
                        inventory,
                        character,
                        counts,
                        visiting,
                        arrowheadFletchDifficultyExcess);
                    if (prerequisite is not null)
                        return prerequisite;
                    continue;
                }

                PluginInventoryItem second = Find(
                    inventory,
                    recipe.SecondItem,
                    excludedObjectId: recipe.FirstItem.Equals(
                        recipe.SecondItem,
                        StringComparison.OrdinalIgnoreCase)
                            ? first.ObjectId
                            : 0u);
                if (second.ObjectId == 0u)
                {
                    if (recipe.FirstItem.Equals(
                            recipe.SecondItem,
                            StringComparison.OrdinalIgnoreCase)
                        && first.StackSize >= 2)
                    {
                        return new CraftingPlan(
                            recipe,
                            first.ObjectId,
                            0u,
                            desiredResult)
                        {
                            RequiresSplitFirstStack = true,
                            SplitContainerObjectId = first.ContainerObjectId,
                        };
                    }
                    CraftingPlan? prerequisite = FindStep(
                        recipe.SecondItem,
                        desiredResult,
                        inventory,
                        character,
                        counts,
                        visiting,
                        arrowheadFletchDifficultyExcess);
                    if (prerequisite is not null)
                        return prerequisite;
                    continue;
                }

                return new CraftingPlan(
                    recipe,
                    first.ObjectId,
                    second.ObjectId,
                    desiredResult);
            }
            return null;
        }
        finally
        {
            visiting.Remove(result);
        }
    }

    private static PluginInventoryItem Find(
        IReadOnlyList<PluginInventoryItem> inventory,
        string name,
        uint excludedObjectId = 0u)
    {
        foreach (PluginInventoryItem item in inventory)
        {
            if (item.ObjectId != excludedObjectId
                && item.StackSize > 0
                && item.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return item;
            }
        }
        return default;
    }

    private static bool HasRequiredSkill(
        ICharacterInfo character,
        uint requiredSkill,
        int difficulty,
        int arrowheadFletchDifficultyExcess)
    {
        if (requiredSkill == 0u)
            return true;
        if (!character.TryGetSkill(requiredSkill, out PluginSkillInfo skill)
            || skill.Training is not (PluginSkillTraining.Trained
                or PluginSkillTraining.Specialized))
        {
            return false;
        }
        return requiredSkill != 37u
            || skill.Current >= Math.Max(0, difficulty)
                + arrowheadFletchDifficultyExcess;
    }
}

internal sealed class CraftingController
{
    private const double SplitTimeoutSeconds = 10d;

    private readonly IPluginHost _host;
    private readonly InventorySettings _settings;
    private readonly CombatSettings _profiles;
    private CraftingPlan? _pending;
    private CraftingPlan? _pendingSplit;
    private long _observedCompletion;
    private long _observedInventoryCompletion;
    private double _untilScan;
    private double _untilCriticalScan;
    private double _untilIdleScan;
    private double _splitElapsed;
    private bool _splitAcknowledged;

    public CraftingController(
        IPluginHost host,
        InventorySettings settings,
        CombatSettings profiles)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
    }

    private Func<bool>? _readyToCraftInPeace;

    internal void BindPeaceGate(Func<bool> readyToCraftInPeace) =>
        _readyToCraftInPeace = readyToCraftInPeace
            ?? throw new ArgumentNullException(nameof(readyToCraftInPeace));

    private bool StartInPeace(IItemAutomation items, CraftingPlan? plan)
    {
        if (plan is not { } next)
            return false;
        if (_readyToCraftInPeace is { } ready && !ready())
        {
            Status = $"Entering peace mode to craft {next.Recipe.ResultItem}";
            return true;
        }
        return Start(items, next);
    }

    public string Status { get; private set; } = "AutoCraft idle";

    public bool Request(string resultName, int desiredCount = 1)
    {
        if (string.IsNullOrWhiteSpace(resultName)
            || _pending is not null
            || _pendingSplit is not null
            || !_host.Automation.IsAvailable)
        {
            return false;
        }
        IItemAutomation items = _host.Automation.Items;
        if (!items.IsAvailable || items.IsBusy)
            return false;
        CraftingPlan? plan = CraftingPlanner.Plan(
            items.CaptureOwnedItems(),
            [resultName],
            _host.Automation.Character,
            desiredCount,
            _settings.ArrowheadFletchDifficultyExcess);
        return StartInPeace(items, plan);
    }

    public bool CanRequest(string resultName, int desiredCount = 1)
    {
        if (string.IsNullOrWhiteSpace(resultName)
            || !_host.Automation.IsAvailable
            || !_host.Automation.Items.IsAvailable)
        {
            return false;
        }
        return CraftingPlanner.Plan(
                _host.Automation.Items.CaptureOwnedItems(),
                [resultName],
                _host.Automation.Character,
                desiredCount,
                _settings.ArrowheadFletchDifficultyExcess)
            is not null;
    }

    public bool TickCritical(double elapsedSeconds, bool canAct)
    {
        IItemAutomation items = _host.Automation.Items;
        ObserveCompletion(items);
        if (ObserveSplitCompletion(items, elapsedSeconds, canAct, advanceClock: true))
            return true;
        if (_pending is not null)
        {
            if (items.IsBusy)
                return true;
            _pending = null;
        }
        if (!canAct
            || !_settings.AutoCraftItems
            || !_host.Automation.IsAvailable
            || !items.IsAvailable
            || items.IsBusy)
        {
            return false;
        }
        _untilCriticalScan -= Math.Max(0d, elapsedSeconds);
        if (_untilCriticalScan > 0d)
            return false;
        _untilCriticalScan = Math.Max(0.1d, _settings.ScanIntervalSeconds);
        IReadOnlyList<PluginInventoryItem> inventory = items.CaptureOwnedItems();
        CraftingPlan? plan = _settings.SplitPeas
            ? CraftingPlanner.PlanPeaSplit(
                inventory,
                _profiles.ConsumableNames,
                _settings.CriticalComponentMinimum)
            : null;
        plan ??= PlanCategoryCraft(
            inventory,
            idleCounts: false);
        return StartInPeace(items, plan);
    }

    public bool Tick(double elapsedSeconds, bool canAct)
    {
        IItemAutomation items = _host.Automation.Items;
        ObserveCompletion(items);
        if (ObserveSplitCompletion(items, elapsedSeconds, canAct, advanceClock: false))
            return true;
        if (_pending is not null)
        {
            if (items.IsBusy)
                return true;
            _pending = null;
        }
        if (!canAct
            || !_settings.AutoCraftItems
            || !_host.Automation.IsAvailable
            || !items.IsAvailable
            || items.IsBusy)
        {
            return false;
        }

        _untilScan -= Math.Max(0d, elapsedSeconds);
        if (_untilScan > 0d)
            return false;
        _untilScan = Math.Max(0.1d, _settings.ScanIntervalSeconds);
        IReadOnlyList<PluginInventoryItem> inventory = items.CaptureOwnedItems();
        CraftingPlan? plan = _settings.SplitPeas
            ? CraftingPlanner.PlanPeaSplit(
                inventory,
                _profiles.ConsumableNames,
                _settings.NormalComponentMinimum)
            : null;
        plan ??= CraftingPlanner.Plan(
            inventory,
            _profiles.ConsumableNames
                .Concat(_profiles.CombatItemNames)
                .Where(static name =>
                    !name.Equals(CraftingPlanner.AllPeas, StringComparison.Ordinal)
                    && !name.EndsWith(" Pea", StringComparison.Ordinal)),
            _host.Automation.Character,
            arrowheadFletchDifficultyExcess:
                _settings.ArrowheadFletchDifficultyExcess);
        if (plan is null)
        {
            Status = "AutoCraft idle";
            return false;
        }

        return StartInPeace(items, plan);
    }

    public bool TickIdle(double elapsedSeconds, bool canAct)
    {
        IItemAutomation items = _host.Automation.Items;
        ObserveCompletion(items);
        if (ObserveSplitCompletion(items, elapsedSeconds, canAct, advanceClock: false))
            return true;
        if (_pending is not null)
        {
            if (items.IsBusy)
                return true;
            _pending = null;
        }
        if (!canAct
            || !_settings.AutoCraftItems
            || !_host.Automation.IsAvailable
            || !items.IsAvailable
            || items.IsBusy)
        {
            return false;
        }
        _untilIdleScan -= Math.Max(0d, elapsedSeconds);
        if (_untilIdleScan > 0d)
            return false;
        _untilIdleScan = Math.Max(0.1d, _settings.ScanIntervalSeconds);
        IReadOnlyList<PluginInventoryItem> inventory = items.CaptureOwnedItems();
        CraftingPlan? plan = _settings.SplitPeas
            ? CraftingPlanner.PlanPeaSplit(
                inventory,
                _profiles.ConsumableNames,
                _settings.IdleComponentMinimum)
            : null;
        plan ??= PlanCategoryCraft(inventory, idleCounts: true);
        return StartInPeace(items, plan);
    }

    private CraftingPlan? PlanCategoryCraft(
        IReadOnlyList<PluginInventoryItem> inventory,
        bool idleCounts)
    {
        foreach (string name in _profiles.ConsumableNames
            .OrderBy(static name => name, StringComparer.Ordinal))
        {
            ConsumableCategory category = _profiles.ConsumableCategories
                .TryGetValue(name, out ConsumableCategory stored)
                    ? stored
                    : ConsumableClassifier.ClassifyName(name);
            int desired = idleCounts ? IdleCount(category) : category switch
            {
                ConsumableCategory.HealthKit
                    or ConsumableCategory.HealthFood
                    or ConsumableCategory.StaminaKit
                    or ConsumableCategory.StaminaFood
                    or ConsumableCategory.ManaKit
                    or ConsumableCategory.ManaFood => 1,
                _ => 0,
            };
            if (desired <= 0)
                continue;
            CraftingPlan? plan = CraftingPlanner.Plan(
                inventory,
                [name],
                _host.Automation.Character,
                desired,
                _settings.ArrowheadFletchDifficultyExcess);
            if (plan is not null)
                return plan;
        }
        return null;
    }

    private int IdleCount(ConsumableCategory category) => category switch
    {
        ConsumableCategory.HealthKit => _settings.IdleHealthKitCount,
        ConsumableCategory.StaminaKit => _settings.IdleStaminaKitCount,
        ConsumableCategory.ManaKit => _settings.IdleManaKitCount,
        ConsumableCategory.HealthFood => _settings.IdleHealthFoodCount,
        ConsumableCategory.StaminaFood => _settings.IdleStaminaFoodCount,
        ConsumableCategory.ManaFood => _settings.IdleManaFoodCount,
        _ => 0,
    };

    private bool Start(IItemAutomation items, CraftingPlan next)
    {
        if (next.RequiresSplitFirstStack)
        {
            long completionBefore = items.LastInventoryCompletion.Revision;
            PluginItemCommandResult split = items.MoveToContainer(
                next.FirstObjectId,
                next.SplitContainerObjectId,
                amount: 1u);
            if (!split.Accepted)
            {
                Status = $"AutoCraft split waiting: {split.Status}";
                return split.Status == PluginItemCommandStatus.Busy;
            }
            _pendingSplit = next;
            _observedInventoryCompletion = completionBefore;
            _splitElapsed = 0d;
            _splitAcknowledged = false;
            Status = $"Splitting {next.Recipe.FirstItem} for crafting";
            return true;
        }
        PluginItemCommandResult result = items.Apply(
            next.FirstObjectId,
            next.SecondObjectId);
        if (!result.Accepted)
        {
            Status = $"AutoCraft waiting: {result.Status}";
            return result.Status == PluginItemCommandStatus.Busy;
        }
        _pending = next;
        Status = $"Crafting {next.Recipe.ResultItem}";
        return true;
    }

    public void Reset()
    {
        _pending = null;
        _pendingSplit = null;
        _untilScan = 0d;
        _untilCriticalScan = 0d;
        _untilIdleScan = 0d;
        _splitElapsed = 0d;
        _splitAcknowledged = false;
        Status = "AutoCraft idle";
    }

    private void ObserveCompletion(IItemAutomation items)
    {
        PluginItemUseCompletion completion = items.LastCompletion;
        if (completion.Revision == 0 || completion.Revision == _observedCompletion)
            return;
        _observedCompletion = completion.Revision;
        if (_pending is not { } pending
            || completion.SourceObjectId != pending.FirstObjectId)
        {
            return;
        }
        Status = completion.IsSuccess
            ? $"Crafted {pending.Recipe.ResultItem}"
            : $"Craft failed (0x{completion.WeenieError:X})";
        _pending = null;
        _untilScan = 0d;
        _untilCriticalScan = 0d;
        _untilIdleScan = 0d;
    }

    private bool ObserveSplitCompletion(
        IItemAutomation items,
        double elapsedSeconds,
        bool canAct,
        bool advanceClock)
    {
        if (_pendingSplit is not { } splitPlan)
            return false;

        if (advanceClock)
            _splitElapsed += Math.Max(0d, elapsedSeconds);
        PluginInventoryCompletion completion = items.LastInventoryCompletion;
        if (completion.Revision != 0
            && completion.Revision != _observedInventoryCompletion)
        {
            _observedInventoryCompletion = completion.Revision;
            if (completion.SourceObjectId == splitPlan.FirstObjectId)
            {
                if (!completion.IsSuccess)
                {
                    Status = $"AutoCraft split failed (0x{completion.WeenieError:X})";
                    ClearPendingSplit();
                    return true;
                }
                _splitAcknowledged = true;
            }
        }

        if (canAct && _splitAcknowledged && TryStartAfterSplit(items, splitPlan))
            return true;
        if (_splitElapsed < SplitTimeoutSeconds)
        {
            Status = _splitAcknowledged
                ? "AutoCraft waiting for split inventory"
                : $"Splitting {splitPlan.Recipe.FirstItem} for crafting";
            return true;
        }

        Status = "AutoCraft split timed out";
        ClearPendingSplit();
        return true;
    }

    private bool TryStartAfterSplit(
        IItemAutomation items,
        CraftingPlan splitPlan)
    {
        PluginInventoryItem[] inputs = items.CaptureOwnedItems()
            .Where(item => item.Name.Equals(
                splitPlan.Recipe.FirstItem,
                StringComparison.OrdinalIgnoreCase))
            .OrderBy(static item => item.ObjectId)
            .ToArray();
        if (inputs.Length < 2)
            return false;
        CraftingPlan ready = splitPlan with
        {
            FirstObjectId = inputs[0].ObjectId,
            SecondObjectId = inputs[1].ObjectId,
            RequiresSplitFirstStack = false,
        };
        ClearPendingSplit();
        return Start(items, ready);
    }

    private void ClearPendingSplit()
    {
        _pendingSplit = null;
        _splitElapsed = 0d;
        _splitAcknowledged = false;
        _untilScan = Math.Max(0.1d, _settings.ScanIntervalSeconds);
        _untilCriticalScan = Math.Max(0.1d, _settings.ScanIntervalSeconds);
        _untilIdleScan = Math.Max(0.1d, _settings.ScanIntervalSeconds);
    }
}
