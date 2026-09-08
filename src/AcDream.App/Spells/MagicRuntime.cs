using System;
using System.Collections.Generic;
using AcDream.Core.Items;
using AcDream.Core.Spells;
using AcDream.Runtime.Gameplay;
using AcDream.Content;

namespace AcDream.App.Spells;

public readonly record struct SpellExamineComponent(
    uint SpellComponentId,
    SpellComponentDescriptor Descriptor,
    bool Owned);

public sealed class MagicRuntime : IDisposable
{
    private readonly SpellComponentRequirementService _requirements;
    private IDisposable? _castOperationsBinding;

    private MagicRuntime(
        MagicCatalog catalog,
        RuntimeSpellCastState casting,
        SpellComponentRequirementService requirements,
        IDisposable castOperationsBinding)
    {
        Catalog = catalog;
        Casting = casting;
        _requirements = requirements;
        _castOperationsBinding = castOperationsBinding;
    }

    public MagicCatalog Catalog { get; }
    public RuntimeSpellCastState Casting { get; }

    public IReadOnlyList<SpellExamineComponent> GetExamineComponents(uint spellId)
    {
        IReadOnlyList<uint> formula = _requirements.GetAppropriateFormula(spellId);
        if (formula.Count == 0)
            return [];

        var result = new List<SpellExamineComponent>(formula.Count);
        foreach (uint spellComponentId in formula)
        {
            if (spellComponentId == 0u
                || !Catalog.TryGetComponentBySpellComponentId(
                    spellComponentId,
                    out SpellComponentDescriptor descriptor))
                continue;
            result.Add(new SpellExamineComponent(
                spellComponentId,
                descriptor,
                _requirements.IsComponentOwned(spellComponentId)));
        }
        return result;
    }

    internal static MagicRuntime Create(
        MagicCatalog catalog,
        RuntimeSpellCastState casting,
        RuntimeSpellCastOperationsSlot operationsSlot,
        ClientObjectTable objects,
        Func<uint> localPlayerId,
        Func<string> accountName,
        Action stopCompletely,
        Action<uint> sendUntargeted,
        Action<uint, uint> sendTargeted,
        Action<string> displayMessage,
        Action incrementBusy,
        Func<bool> canSend)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(casting);
        ArgumentNullException.ThrowIfNull(operationsSlot);
        ArgumentNullException.ThrowIfNull(objects);
        ArgumentNullException.ThrowIfNull(displayMessage);

        SpellComponentRequirementService requirements = catalog.CreateRequirementService(
            objects, localPlayerId, accountName);
        var operations = new LiveSpellCastOperations(
            requirements,
            objects,
            localPlayerId,
            stopCompletely,
            sendUntargeted,
            sendTargeted,
            displayMessage,
            incrementBusy,
            canSend);
        IDisposable binding = operationsSlot.BindOwned(operations);
        return new MagicRuntime(catalog, casting, requirements, binding);
    }

    public void Reset() => Casting.Reset();

    public void Dispose() =>
        Interlocked.Exchange(ref _castOperationsBinding, null)?.Dispose();
}
