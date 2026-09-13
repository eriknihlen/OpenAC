using AcDream.Plugin.Abstractions;

namespace AcDream.DrakBot.Combat;

/// <summary>
/// Makes ammunition from wrapped bundles when the quiver runs dry, the way
/// RynthAi's missile crafting manager does: the best recipe the character
/// can fletch with bundles in the pack is combined up to twice (a head
/// bundle applied to a shaft bundle), each combine confirmed by the item
/// use completion or the output turning up, and the caller then wields
/// what was made. Prismatic heads above Greater need Fletching specialized;
/// everything needs it trained.
/// </summary>
public sealed class AmmoCrafter
{
    private const double CombineRetrySeconds = 2d;
    private const double CombineTimeoutSeconds = 15d;
    private const int CombinesPerRefill = 2;

    public enum WeaponCategory
    {
        Bow,
        Crossbow,
        Atlatl,
    }

    public enum Verdict
    {
        /// <summary>No bundles for this weapon, or Fletching untrained.</summary>
        Nothing,
        /// <summary>A combine is in flight; ask again next tick.</summary>
        Crafting,
        /// <summary>Ammunition was made; the caller wields it.</summary>
        Crafted,
        /// <summary>The combine never completed.</summary>
        Failed,
    }

    public sealed record Recipe(string Heads, string Shafts, string Output, WeaponCategory Category, bool RequiresSpecialized, int Priority);

    public static readonly IReadOnlyList<Recipe> Recipes = Build();

    private Recipe? _recipe;
    private uint _headsId;
    private uint _shaftsId;
    private int _combinesDone;
    private int _outputBefore;
    private long _completionBefore;
    private double _startedAt = double.NegativeInfinity;
    private double _lastApplyAt = double.NegativeInfinity;

    public bool IsCrafting => _recipe is not null;

    public string Status { get; private set; } = string.Empty;

    /// <summary>What a missile weapon fires, by its name.</summary>
    public static WeaponCategory CategoryOf(string weaponName)
    {
        if (weaponName.Contains("crossbow", StringComparison.OrdinalIgnoreCase))
            return WeaponCategory.Crossbow;
        if (weaponName.Contains("atlatl", StringComparison.OrdinalIgnoreCase))
            return WeaponCategory.Atlatl;
        return WeaponCategory.Bow;
    }

    public Verdict Tick(IAutomationSurface surface, WeaponCategory category, double now, out string detail)
    {
        detail = string.Empty;
        IItemAutomation items = surface.Items;
        IReadOnlyList<PluginInventoryItem> owned = items.CaptureOwnedItems();

        if (_recipe is null)
        {
            Recipe? recipe = BestCraftable(owned, category, FletchingTraining(surface.Character));
            if (recipe is null)
            {
                detail = "no bundles to fletch";
                return Verdict.Nothing;
            }
            _recipe = recipe;
            _headsId = FindBundle(owned, recipe.Heads)!.Value.ObjectId;
            _shaftsId = FindBundle(owned, recipe.Shafts)!.Value.ObjectId;
            _combinesDone = 0;
            _startedAt = now;
            _lastApplyAt = double.NegativeInfinity;
            _outputBefore = CountOutput(owned, recipe.Output);
            _completionBefore = items.LastCompletion.Revision;
        }

        Recipe current = _recipe;
        int output = CountOutput(owned, current.Output);
        PluginItemUseCompletion completion = items.LastCompletion;
        bool landed = output > _outputBefore
            || (completion.Revision != _completionBefore && completion.IsSuccess && completion.SourceObjectId == _headsId);
        if (landed)
        {
            _combinesDone++;
            _outputBefore = output;
            _completionBefore = completion.Revision;
            _lastApplyAt = double.NegativeInfinity;
            bool bundlesLeft = FindBundle(owned, current.Heads) is not null && FindBundle(owned, current.Shafts) is not null;
            if (_combinesDone >= CombinesPerRefill || !bundlesLeft)
            {
                detail = $"made {current.Output}";
                Status = string.Empty;
                _recipe = null;
                return Verdict.Crafted;
            }
        }
        else if (completion.Revision != _completionBefore && !completion.IsSuccess && completion.SourceObjectId == _headsId)
        {
            detail = $"fletching {current.Output} failed ({completion.WeenieError})";
            _completionBefore = completion.Revision;
            Status = string.Empty;
            _recipe = null;
            return Verdict.Failed;
        }

        if (now - _startedAt > CombineTimeoutSeconds)
        {
            Status = string.Empty;
            _recipe = null;
            if (_combinesDone > 0)
            {
                detail = $"made {current.Output}";
                return Verdict.Crafted;
            }
            detail = $"fletching {current.Output} timed out";
            return Verdict.Failed;
        }

        if (now - _lastApplyAt >= CombineRetrySeconds)
        {
            PluginItemCommandResult apply = items.Apply(_headsId, _shaftsId);
            if (apply.Status != PluginItemCommandStatus.Busy)
            {
                _lastApplyAt = now;
                if (!apply.Accepted)
                {
                    detail = $"cannot fletch {current.Output}: {apply.Status}";
                    Status = string.Empty;
                    _recipe = null;
                    return Verdict.Failed;
                }
            }
        }
        Status = $"fletching {current.Output} ({_combinesDone + 1}/{CombinesPerRefill})";
        detail = Status;
        return Verdict.Crafting;
    }

    public void Reset()
    {
        _recipe = null;
        Status = string.Empty;
    }

    /// <summary>The highest-priority recipe the character can fletch with bundles in the pack.</summary>
    public static Recipe? BestCraftable(IReadOnlyList<PluginInventoryItem> owned, WeaponCategory category, PluginSkillTraining fletching)
    {
        if (fletching is not (PluginSkillTraining.Trained or PluginSkillTraining.Specialized))
            return null;
        foreach (Recipe recipe in Recipes)
        {
            if (recipe.Category != category)
                continue;
            if (recipe.RequiresSpecialized && fletching != PluginSkillTraining.Specialized)
                continue;
            if (FindBundle(owned, recipe.Heads) is not null && FindBundle(owned, recipe.Shafts) is not null)
                return recipe;
        }
        return null;
    }

    private static PluginSkillTraining FletchingTraining(ICharacterInfo character)
    {
        foreach (PluginSkillInfo skill in character.Skills)
        {
            if (skill.Name.Equals("Fletching", StringComparison.OrdinalIgnoreCase))
                return skill.Training;
        }
        return PluginSkillTraining.Unknown;
    }

    private static PluginInventoryItem? FindBundle(IReadOnlyList<PluginInventoryItem> owned, string bundleName)
    {
        foreach (PluginInventoryItem item in owned)
        {
            if (!item.IsEquipped && item.StackSize > 0 && item.Name.Equals(bundleName, StringComparison.OrdinalIgnoreCase))
                return item;
        }
        return null;
    }

    private static int CountOutput(IReadOnlyList<PluginInventoryItem> owned, string output)
    {
        int count = 0;
        foreach (PluginInventoryItem item in owned)
        {
            if (item.Name.Equals(output, StringComparison.OrdinalIgnoreCase))
                count += Math.Max(1, item.StackSize);
        }
        return count;
    }

    private static List<Recipe> Build()
    {
        var recipes = new List<Recipe>();
        (string Head, string Output, bool Specialized, int Priority)[] grades =
        [
            ("Lethal Prismatic", "Lethal Prismatic", true, 40),
            ("Deadly Prismatic", "Deadly Prismatic", true, 30),
            ("Greater Prismatic", "Greater Prismatic", false, 20),
            ("Prismatic", "Prismatic", false, 10),
            ("Armor Piercing", "Armor Piercing", false, 6),
            ("Broad", "Broad Head", false, 5),
            ("Blunt", "Blunt", false, 4),
            ("Frog Crotch", "Frog Crotch", false, 3),
            ("", "", false, 1),
        ];
        foreach ((WeaponCategory category, string heads, string shafts, string ammo) in new[]
        {
            (WeaponCategory.Bow, "Arrowheads", "Wrapped Bundle of Arrowshafts", "Arrow"),
            (WeaponCategory.Crossbow, "Quarrelheads", "Wrapped Bundle of Quarrelshafts", "Quarrel"),
            (WeaponCategory.Atlatl, "Atlatl Dart Heads", "Wrapped Bundle of Atlatl Dart Shafts", "Atlatl Dart"),
        })
        {
            foreach ((string head, string output, bool specialized, int priority) in grades)
            {
                if (category == WeaponCategory.Atlatl && head == "Frog Crotch")
                    continue;
                string headName = head.Length == 0 ? $"Wrapped Bundle of {heads}" : $"Wrapped Bundle of {head} {heads}";
                string outputName = output.Length == 0 ? ammo : $"{output} {ammo}";
                recipes.Add(new Recipe(headName, shafts, outputName, category, specialized, priority));
            }
        }
        // Highest priority first within a category, so the first craftable match wins.
        recipes.Sort((a, b) => b.Priority.CompareTo(a.Priority));
        return recipes;
    }
}
