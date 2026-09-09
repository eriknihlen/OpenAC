using AcDream.App.Interaction;
using AcDream.App.Net;
using AcDream.Core.CharGen;
using AcDream.Runtime;
using AcDream.Runtime.Chat;
using AcDream.Runtime.Session;
using AcDream.Runtime.World;
using AcDream.UI.Abstractions;

namespace AcDream.App.Runtime;

internal sealed class CurrentGameRuntimeAdapter
    : IGameRuntimeView,
      IGameRuntimeCommands,
      IRuntimeEventSource,
      IDisposable
{
    private readonly GameRuntime _runtime;
    private readonly CurrentGameRuntimeCommandAdapter _commands;
    private readonly ICommandBus _commandBus;
    private readonly CharacterSelectionProjection _characterSelection;
    private readonly CharacterCreationProjection _characterCreation;
    private readonly IDisposable _hostLease;
    private readonly object _subscriptionGate = new();
    private readonly HashSet<AdapterSubscription> _subscriptions = [];
    private bool _disposed;

    public CurrentGameRuntimeAdapter(
        GameRuntime runtime,
        LiveSessionHost sessionHost,
        ICommandBus commands,
        SelectionInteractionController selection)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        ArgumentNullException.ThrowIfNull(sessionHost);
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(selection);

        _commandBus = commands;
        _hostLease = runtime.AcquireHostLease(
            "graphical game-runtime command adapter");
        try
        {
            _characterSelection = new CharacterSelectionProjection(this);
            _characterCreation = new CharacterCreationProjection(this);
            _commands = new CurrentGameRuntimeCommandAdapter(
                runtime.Session,
                sessionHost,
                commands,
                this,
                runtime.InventoryOwner,
                runtime.CharacterOwner,
                runtime.ActionOwner,
                runtime.MovementOwner,
                runtime.FellowshipOwner,
                selection,
                runtime.EventSink);
        }
        catch
        {
            _hostLease.Dispose();
            throw;
        }
    }

    private bool IsActive =>
        !Volatile.Read(ref _disposed)
        && !_runtime.Session.IsDisposalComplete;

    public RuntimeGenerationToken Generation => _runtime.Generation;

    public RuntimeLifecycleSnapshot Lifecycle
    {
        get
        {
            RuntimeLifecycleSnapshot current = _runtime.Lifecycle;
            return IsActive
                ? current
                : current with
                {
                    State = RuntimeLifecycleState.Disposed,
                    HasTransport = false,
                };
        }
    }

    public IGameRuntimeClock Clock => _runtime.Clock;
    public IRuntimeEntityView Entities => _runtime.Entities;
    public IRuntimeInventoryView Inventory => _runtime.Inventory;
    public IRuntimeInventoryStateView InventoryState =>
        _runtime.InventoryState;
    public IRuntimeCharacterView Character => _runtime.Character;
    public IRuntimeSocialView Social => _runtime.Social;
    public IRuntimeChatView Chat => _runtime.Chat;
    public IRuntimeCharacterSelectionView CharacterSelection =>
        _characterSelection;
    public IRuntimeConnectionView Connection => _runtime.Connection;
    public IRuntimeCharacterCreationView CharacterCreation =>
        _characterCreation;
    public IRuntimeFellowshipView Fellowship => _runtime.Fellowship;
    public IRuntimeAllegianceView Allegiance => _runtime.Allegiance;
    public IRuntimeActionView Actions => _runtime.Actions;
    public IRuntimeMovementView Movement => _runtime.Movement;
    public IRuntimeWorldEnvironmentView Environment => _runtime.Environment;
    public IRuntimePortalView Portal => _runtime.Portal;

    public IRuntimeSessionCommands Session => _commands;
    public IRuntimeCharacterSelectionCommands CharacterSelectionCommands =>
        _characterSelection;
    IRuntimeCharacterSelectionCommands IGameRuntimeCommands.CharacterSelection =>
        _characterSelection;
    public IRuntimeCharacterCreationCommands CharacterCreationCommands =>
        _characterCreation;
    IRuntimeCharacterCreationCommands IGameRuntimeCommands.CharacterCreation =>
        _characterCreation;
    public IRuntimeSelectionCommands Selection => _commands;
    public IRuntimeCombatCommands Combat => _commands;
    public IRuntimeMagicCommands Magic => _commands;
    public IRuntimeMovementCommands MovementCommands => _commands;
    IRuntimeMovementCommands IGameRuntimeCommands.Movement => _commands;
    public IRuntimeChatCommands ChatCommands => _commands;
    IRuntimeChatCommands IGameRuntimeCommands.Chat => _commands;
    public IRuntimePortalCommands PortalCommands => _commands;
    IRuntimePortalCommands IGameRuntimeCommands.Portal => _commands;
    public IRuntimeInventoryStateCommands InventoryCommands => _commands;
    IRuntimeInventoryStateCommands IGameRuntimeCommands.InventoryState =>
        _commands;
    public IRuntimeSpellbookCommands SpellbookCommands => _commands;
    IRuntimeSpellbookCommands IGameRuntimeCommands.Spellbook => _commands;
    public IRuntimeCharacterCommands CharacterCommands => _commands;
    IRuntimeCharacterCommands IGameRuntimeCommands.Character => _commands;
    public IRuntimeSocialCommands SocialCommands => _commands;
    IRuntimeSocialCommands IGameRuntimeCommands.Social => _commands;
    public IRuntimeFellowshipCommands FellowshipCommands => _commands;
    IRuntimeFellowshipCommands IGameRuntimeCommands.Fellowship => _commands;
    public IRuntimeAllegianceCommands AllegianceCommands => _commands;
    IRuntimeAllegianceCommands IGameRuntimeCommands.Allegiance => _commands;

    internal bool SubmitChatText(string text)
    {
        if (!IsActive || string.IsNullOrWhiteSpace(text))
            return false;
        SubmitOutcome outcome = ChatCommandRouter.Submit(
            text,
            new RuntimeChatCommandFeedback(_runtime.CommunicationOwner),
            _commandBus,
            ChatChannelKind.Say);
        return outcome is not (SubmitOutcome.Empty
            or SubmitOutcome.UnknownCommand
            or SubmitOutcome.Dropped);
    }

    public RuntimeStateCheckpoint CaptureCheckpoint()
    {
        RuntimeStateCheckpoint checkpoint = _runtime.CaptureCheckpoint();
        return checkpoint with { Lifecycle = Lifecycle.State };
    }

    public IDisposable Subscribe(IRuntimeEventObserver observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        lock (_subscriptionGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            IDisposable runtimeSubscription = _runtime.Subscribe(observer);
            var subscription = new AdapterSubscription(
                this,
                runtimeSubscription);
            _subscriptions.Add(subscription);
            return subscription;
        }
    }

    public void Dispose()
    {
        lock (_subscriptionGate)
        {
            if (_disposed)
                return;
            _disposed = true;
            while (_subscriptions.Count != 0)
                _subscriptions.First().DisposeCore();
        }
        _hostLease.Dispose();
    }

    private void Remove(AdapterSubscription subscription)
    {
        lock (_subscriptionGate)
            _subscriptions.Remove(subscription);
    }

    private RuntimeCharacterSelectionSnapshot CharacterSelectionSnapshot()
    {
        lock (_subscriptionGate)
        {
            if (IsActive)
                return _runtime.CharacterSelection.Snapshot;
            return new RuntimeCharacterSelectionSnapshot(
                _runtime.Generation,
                RuntimeCharacterSelectionLifecycle.Inactive,
                Revision: 0,
                AccountName: string.Empty,
                SlotCount: 0,
                RosterCount: 0,
                WorldName: string.Empty,
                HighlightedCharacterId: 0u,
                HighlightedDisplayIndex: -1,
                PendingDeleteCharacterId: 0u,
                LastRestoreRequestedCharacterId: 0u,
                Operation: RuntimeCharacterSelectionOperation.None,
                Error: null,
                Buttons: RuntimeCharacterSelectionButtons.None);
        }
    }

    private bool TryGetCharacterSelectionAt(
        int displayIndex,
        out RuntimeCharacterSelectionEntry character)
    {
        lock (_subscriptionGate)
        {
            if (IsActive)
            {
                return _runtime.CharacterSelection.TryGetAt(
                    displayIndex,
                    out character);
            }
            character = default;
            return false;
        }
    }

    private bool TryGetCharacterSelection(
        uint characterId,
        out RuntimeCharacterSelectionEntry character)
    {
        lock (_subscriptionGate)
        {
            if (IsActive)
            {
                return _runtime.CharacterSelection.TryGet(
                    characterId,
                    out character);
            }
            character = default;
            return false;
        }
    }

    private void VisitCharacterSelection(
        IRuntimeCharacterSelectionVisitor visitor)
    {
        ArgumentNullException.ThrowIfNull(visitor);
        lock (_subscriptionGate)
        {
            if (IsActive)
                _runtime.CharacterSelection.Visit(visitor);
        }
    }

    private IDisposable SubscribeCharacterSelection(
        IRuntimeCharacterSelectionObserver observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        lock (_subscriptionGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var gated = new AdapterCharacterSelectionObserver(this, observer);
            IDisposable runtimeSubscription =
                _runtime.CharacterSelection.Subscribe(gated);
            var subscription = new AdapterSubscription(
                this,
                runtimeSubscription);
            _subscriptions.Add(subscription);
            return subscription;
        }
    }

    private RuntimeCommandResult ExecuteCharacterSelection(
        Func<IRuntimeCharacterSelectionCommands, RuntimeCommandResult> execute)
    {
        lock (_subscriptionGate)
        {
            if (!IsActive)
            {
                return new RuntimeCommandResult(
                    RuntimeCommandStatus.Inactive,
                    _runtime.Generation);
            }
            return execute(_runtime.Session);
        }
    }

    private void ForwardCharacterSelection(
        IRuntimeCharacterSelectionObserver observer,
        in RuntimeCharacterSelectionDelta delta)
    {
        lock (_subscriptionGate)
        {
            if (IsActive)
                observer.OnCharacterSelectionChanged(in delta);
        }
    }


    private RuntimeCharacterCreationSnapshot CharacterCreationSnapshot()
    {
        lock (_subscriptionGate)
        {
            if (IsActive)
                return _runtime.CharacterCreation.Snapshot;
            return default;
        }
    }

    private ChargenSkillAdvancementClass CharacterCreationSkillLevel(uint skillId)
    {
        lock (_subscriptionGate)
        {
            return IsActive
                ? _runtime.CharacterCreation.GetSkillLevel(skillId)
                : ChargenSkillAdvancementClass.Inactive;
        }
    }

    private ChargenOptions CharacterCreationOptions()
    {
        lock (_subscriptionGate)
        {
            return IsActive
                ? _runtime.CharacterCreation.Options
                : ChargenOptions.Empty;
        }
    }

    private IDisposable SubscribeCharacterCreation(
        IRuntimeCharacterCreationObserver observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        lock (_subscriptionGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var gated = new AdapterCharacterCreationObserver(this, observer);
            IDisposable runtimeSubscription =
                _runtime.CharacterCreation.Subscribe(gated);
            var subscription = new AdapterSubscription(
                this,
                runtimeSubscription);
            _subscriptions.Add(subscription);
            return subscription;
        }
    }

    private RuntimeCommandResult ExecuteCharacterCreation(
        Func<IRuntimeCharacterCreationCommands, RuntimeCommandResult> execute)
    {
        lock (_subscriptionGate)
        {
            if (!IsActive)
            {
                return new RuntimeCommandResult(
                    RuntimeCommandStatus.Inactive,
                    _runtime.Generation);
            }
            return execute(_runtime.Session);
        }
    }

    private void ForwardCharacterCreation(
        IRuntimeCharacterCreationObserver observer,
        in RuntimeCharacterCreationDelta delta)
    {
        lock (_subscriptionGate)
        {
            if (IsActive)
                observer.OnCharacterCreationChanged(in delta);
        }
    }

    private sealed class CharacterSelectionProjection(
        CurrentGameRuntimeAdapter owner)
        : IRuntimeCharacterSelectionView,
          IRuntimeCharacterSelectionCommands
    {
        public RuntimeCharacterSelectionSnapshot Snapshot =>
            owner.CharacterSelectionSnapshot();

        public bool TryGetAt(
            int displayIndex,
            out RuntimeCharacterSelectionEntry character) =>
            owner.TryGetCharacterSelectionAt(displayIndex, out character);

        public bool TryGet(
            uint characterId,
            out RuntimeCharacterSelectionEntry character) =>
            owner.TryGetCharacterSelection(characterId, out character);

        public void Visit(IRuntimeCharacterSelectionVisitor visitor) =>
            owner.VisitCharacterSelection(visitor);

        public IDisposable Subscribe(
            IRuntimeCharacterSelectionObserver observer) =>
            owner.SubscribeCharacterSelection(observer);

        public RuntimeCommandResult Highlight(
            RuntimeGenerationToken expectedGeneration,
            uint characterId) =>
            owner.ExecuteCharacterSelection(
                commands => commands.Highlight(
                    expectedGeneration,
                    characterId));

        public RuntimeCommandResult Enter(
            RuntimeGenerationToken expectedGeneration) =>
            owner.ExecuteCharacterSelection(
                commands => commands.Enter(expectedGeneration));

        public RuntimeCommandResult RequestDelete(
            RuntimeGenerationToken expectedGeneration) =>
            owner.ExecuteCharacterSelection(
                commands => commands.RequestDelete(expectedGeneration));

        public RuntimeCommandResult ConfirmDelete(
            RuntimeGenerationToken expectedGeneration) =>
            owner.ExecuteCharacterSelection(
                commands => commands.ConfirmDelete(expectedGeneration));

        public RuntimeCommandResult Restore(
            RuntimeGenerationToken expectedGeneration) =>
            owner.ExecuteCharacterSelection(
                commands => commands.Restore(expectedGeneration));

        public RuntimeCommandResult Cancel(
            RuntimeGenerationToken expectedGeneration) =>
            owner.ExecuteCharacterSelection(
                commands => commands.Cancel(expectedGeneration));
    }

    private sealed class AdapterCharacterSelectionObserver(
        CurrentGameRuntimeAdapter owner,
        IRuntimeCharacterSelectionObserver observer)
        : IRuntimeCharacterSelectionObserver
    {
        public void OnCharacterSelectionChanged(
            in RuntimeCharacterSelectionDelta delta) =>
            owner.ForwardCharacterSelection(observer, in delta);
    }

    private sealed class CharacterCreationProjection(
        CurrentGameRuntimeAdapter owner)
        : IRuntimeCharacterCreationView,
          IRuntimeCharacterCreationCommands
    {
        public RuntimeCharacterCreationSnapshot Snapshot =>
            owner.CharacterCreationSnapshot();

        public ChargenSkillAdvancementClass GetSkillLevel(uint skillId) =>
            owner.CharacterCreationSkillLevel(skillId);

        public ChargenOptions Options => owner.CharacterCreationOptions();

        public IDisposable Subscribe(IRuntimeCharacterCreationObserver observer) =>
            owner.SubscribeCharacterCreation(observer);

        public RuntimeCommandResult SelectHeritage(
            RuntimeGenerationToken expectedGeneration,
            uint heritageId) =>
            owner.ExecuteCharacterCreation(
                commands => commands.SelectHeritage(expectedGeneration, heritageId));

        public RuntimeCommandResult SelectGender(
            RuntimeGenerationToken expectedGeneration,
            uint genderKey) =>
            owner.ExecuteCharacterCreation(
                commands => commands.SelectGender(expectedGeneration, genderKey));

        public RuntimeCommandResult SelectTemplate(
            RuntimeGenerationToken expectedGeneration,
            uint templateIndex) =>
            owner.ExecuteCharacterCreation(
                commands => commands.SelectTemplate(expectedGeneration, templateIndex));

        public RuntimeCommandResult SetAttribute(
            RuntimeGenerationToken expectedGeneration,
            ChargenAttributeId attributeId,
            int value) =>
            owner.ExecuteCharacterCreation(
                commands => commands.SetAttribute(expectedGeneration, attributeId, value));

        public RuntimeCommandResult SetAttributeLock(
            RuntimeGenerationToken expectedGeneration,
            ChargenAttributeId attributeId,
            bool locked) =>
            owner.ExecuteCharacterCreation(
                commands => commands.SetAttributeLock(expectedGeneration, attributeId, locked));

        public RuntimeCommandResult TrainSkill(
            RuntimeGenerationToken expectedGeneration,
            uint skillId) =>
            owner.ExecuteCharacterCreation(
                commands => commands.TrainSkill(expectedGeneration, skillId));

        public RuntimeCommandResult SpecializeSkill(
            RuntimeGenerationToken expectedGeneration,
            uint skillId) =>
            owner.ExecuteCharacterCreation(
                commands => commands.SpecializeSkill(expectedGeneration, skillId));

        public RuntimeCommandResult UntrainSkill(
            RuntimeGenerationToken expectedGeneration,
            uint skillId) =>
            owner.ExecuteCharacterCreation(
                commands => commands.UntrainSkill(expectedGeneration, skillId));

        public RuntimeCommandResult SetAppearanceIndex(
            RuntimeGenerationToken expectedGeneration,
            ChargenAppearanceSlot slot,
            uint index) =>
            owner.ExecuteCharacterCreation(
                commands => commands.SetAppearanceIndex(expectedGeneration, slot, index));

        public RuntimeCommandResult SetShade(
            RuntimeGenerationToken expectedGeneration,
            ChargenShadeSlot slot,
            double value) =>
            owner.ExecuteCharacterCreation(
                commands => commands.SetShade(expectedGeneration, slot, value));

        public RuntimeCommandResult SelectStartArea(
            RuntimeGenerationToken expectedGeneration,
            int startAreaIndex) =>
            owner.ExecuteCharacterCreation(
                commands => commands.SelectStartArea(expectedGeneration, startAreaIndex));

        public RuntimeCommandResult SetName(
            RuntimeGenerationToken expectedGeneration,
            string name) =>
            owner.ExecuteCharacterCreation(
                commands => commands.SetName(expectedGeneration, name));

        public RuntimeCommandResult SetSlot(
            RuntimeGenerationToken expectedGeneration,
            uint slot) =>
            owner.ExecuteCharacterCreation(
                commands => commands.SetSlot(expectedGeneration, slot));

        public RuntimeCommandResult Finish(
            RuntimeGenerationToken expectedGeneration,
            bool confirmUnspentCredits = false) =>
            owner.ExecuteCharacterCreation(
                commands => commands.Finish(expectedGeneration, confirmUnspentCredits));

        public RuntimeCommandResult AcknowledgeRejection(
            RuntimeGenerationToken expectedGeneration) =>
            owner.ExecuteCharacterCreation(
                commands => commands.AcknowledgeRejection(expectedGeneration));

        public RuntimeCommandResult RandomizeCharacter(
            RuntimeGenerationToken expectedGeneration) =>
            owner.ExecuteCharacterCreation(
                commands => commands.RandomizeCharacter(expectedGeneration));

        public RuntimeCommandResult RandomizeAppearance(
            RuntimeGenerationToken expectedGeneration) =>
            owner.ExecuteCharacterCreation(
                commands => commands.RandomizeAppearance(expectedGeneration));

        public RuntimeCommandResult RandomizeClothing(
            RuntimeGenerationToken expectedGeneration) =>
            owner.ExecuteCharacterCreation(
                commands => commands.RandomizeClothing(expectedGeneration));
    }

    private sealed class AdapterCharacterCreationObserver(
        CurrentGameRuntimeAdapter owner,
        IRuntimeCharacterCreationObserver observer)
        : IRuntimeCharacterCreationObserver
    {
        public void OnCharacterCreationChanged(
            in RuntimeCharacterCreationDelta delta) =>
            owner.ForwardCharacterCreation(observer, in delta);
    }

    private sealed class AdapterSubscription(
        CurrentGameRuntimeAdapter owner,
        IDisposable runtimeSubscription) : IDisposable
    {
        private CurrentGameRuntimeAdapter? _owner = owner;
        private IDisposable? _runtimeSubscription = runtimeSubscription;

        public void Dispose() => DisposeCore();

        public void DisposeCore()
        {
            IDisposable? runtime = Interlocked.Exchange(
                ref _runtimeSubscription,
                null);
            if (runtime is null)
                return;
            runtime.Dispose();
            Interlocked.Exchange(ref _owner, null)?.Remove(this);
        }
    }
}
