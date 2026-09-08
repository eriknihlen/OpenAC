using AcDream.Core.Items;
using AcDream.Core.Spells;
using AcDream.Runtime.Gameplay;

namespace AcDream.App.Spells;

internal sealed class RuntimeSpellCastOperationsSlot
    : IRuntimeSpellCastOperations
{
    private IRuntimeSpellCastOperations? _owner;

    public IDisposable BindOwned(IRuntimeSpellCastOperations owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (_owner is not null)
            throw new InvalidOperationException(
                "Runtime spell-cast operations are already bound.");
        _owner = owner;
        return new Binding(this, owner);
    }

    private void Unbind(IRuntimeSpellCastOperations expected)
    {
        if (ReferenceEquals(_owner, expected))
            _owner = null;
    }

    public uint LocalPlayerId => _owner?.LocalPlayerId ?? 0u;
    public bool CanSend => _owner?.CanSend == true;

    public bool HasRequiredComponents(uint spellId) =>
        _owner?.HasRequiredComponents(spellId) == true;

    public bool IsTargetCompatible(
        uint targetId,
        SpellMetadata spell,
        bool showMessage) =>
        _owner?.IsTargetCompatible(targetId, spell, showMessage) == true;

    public void StopCompletely() => _owner?.StopCompletely();
    public void SendUntargeted(uint spellId) =>
        _owner?.SendUntargeted(spellId);
    public void SendTargeted(uint targetId, uint spellId) =>
        _owner?.SendTargeted(targetId, spellId);
    public void DisplayMessage(string message) =>
        _owner?.DisplayMessage(message);
    public void IncrementBusy() => _owner?.IncrementBusy();

    private sealed class Binding : IDisposable
    {
        private RuntimeSpellCastOperationsSlot? _slot;
        private readonly IRuntimeSpellCastOperations _expected;

        public Binding(
            RuntimeSpellCastOperationsSlot slot,
            IRuntimeSpellCastOperations expected)
        {
            _slot = slot;
            _expected = expected;
        }

        public void Dispose() =>
            Interlocked.Exchange(ref _slot, null)?.Unbind(_expected);
    }
}

internal sealed class LiveSpellCastOperations : IRuntimeSpellCastOperations
{
    private readonly SpellComponentRequirementService _requirements;
    private readonly ClientObjectTable _objects;
    private readonly Func<uint> _localPlayerId;
    private readonly Action _stopCompletely;
    private readonly Action<uint> _sendUntargeted;
    private readonly Action<uint, uint> _sendTargeted;
    private readonly Action<string> _displayMessage;
    private readonly Action _incrementBusy;
    private readonly Func<bool> _canSend;

    public LiveSpellCastOperations(
        SpellComponentRequirementService requirements,
        ClientObjectTable objects,
        Func<uint> localPlayerId,
        Action stopCompletely,
        Action<uint> sendUntargeted,
        Action<uint, uint> sendTargeted,
        Action<string> displayMessage,
        Action incrementBusy,
        Func<bool> canSend)
    {
        _requirements = requirements
            ?? throw new ArgumentNullException(nameof(requirements));
        _objects = objects ?? throw new ArgumentNullException(nameof(objects));
        _localPlayerId = localPlayerId
            ?? throw new ArgumentNullException(nameof(localPlayerId));
        _stopCompletely = stopCompletely
            ?? throw new ArgumentNullException(nameof(stopCompletely));
        _sendUntargeted = sendUntargeted
            ?? throw new ArgumentNullException(nameof(sendUntargeted));
        _sendTargeted = sendTargeted
            ?? throw new ArgumentNullException(nameof(sendTargeted));
        _displayMessage = displayMessage
            ?? throw new ArgumentNullException(nameof(displayMessage));
        _incrementBusy = incrementBusy
            ?? throw new ArgumentNullException(nameof(incrementBusy));
        _canSend = canSend ?? throw new ArgumentNullException(nameof(canSend));
    }

    public uint LocalPlayerId => _localPlayerId();
    public bool CanSend => _canSend();

    public bool HasRequiredComponents(uint spellId) =>
        _requirements.HasRequiredComponents(spellId);

    public bool IsTargetCompatible(
        uint targetId,
        SpellMetadata spell,
        bool showMessage)
    {
        if (_objects.Get(targetId) is not { } targetObject)
            return false;

        SpellTargetPolicyResult result = RetailSpellTargetPolicy.Evaluate(
            LocalPlayerId,
            targetObject,
            spell);
        if (showMessage && !result.Allowed && result.Message is { } message)
            _displayMessage(message);
        return result.Allowed;
    }

    public void StopCompletely() => _stopCompletely();
    public void SendUntargeted(uint spellId) => _sendUntargeted(spellId);
    public void SendTargeted(uint targetId, uint spellId) =>
        _sendTargeted(targetId, spellId);
    public void DisplayMessage(string message) => _displayMessage(message);
    public void IncrementBusy() => _incrementBusy();
}
