using AcDream.Core.Selection;
using AcDream.Core.Spells;
using AcDream.Runtime.Gameplay;

namespace AcDream.Runtime.Tests.Gameplay;

public sealed class RuntimeSpellCastStateTests
{
    [Fact]
    public void Cast_SelfTargeted_SendsLocalPlayerAsTarget()
    {
        Spellbook book = MakeBook(flags: 0x8, untargeted: true, targetMask: 0x10);
        uint target = 0;
        var operations = new FakeOperations
        {
            LocalPlayerId = 42u,
            SendTargetedAction = (id, _) => target = id,
        };
        RuntimeSpellCastState state = Create(book, operations, selected: 99u);

        Assert.Equal(CastRequestResult.Sent, state.Cast(1));
        Assert.Equal(42u, target);
    }

    [Fact]
    public void Cast_TargetedWithoutSelection_DoesNotSend()
    {
        Spellbook book = MakeBook(flags: 0, untargeted: false, targetMask: 0x10);
        var operations = new FakeOperations { LocalPlayerId = 42u };
        RuntimeSpellCastState state = Create(book, operations);

        Assert.Equal(CastRequestResult.NoTarget, state.Cast(1));
        Assert.Equal(0, operations.TargetedSends);
        Assert.Contains(
            "select",
            operations.LastMessage,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Cast_Untargeted_StopsThenSendsThenIncrementsBusy()
    {
        Spellbook book = MakeBook(flags: 0, untargeted: true, targetMask: 0);
        var order = new List<string>();
        var operations = new FakeOperations
        {
            LocalPlayerId = 42u,
            StopAction = () => order.Add("stop"),
            SendUntargetedAction = _ => order.Add("send"),
            IncrementBusyAction = () => order.Add("busy"),
        };
        RuntimeSpellCastState state = Create(book, operations);

        Assert.Equal(CastRequestResult.Sent, state.Cast(1));
        Assert.Equal(["stop", "send", "busy"], order);
    }

    [Fact]
    public void HasRequiredComponents_AnswersTheSamePredicateCastUsesAndDoesNotMoveTheGate()
    {
        Spellbook book = MakeBook(flags: 0x8, untargeted: true, targetMask: 0x10);
        var stocked = new FakeOperations { LocalPlayerId = 42u };
        var empty = new FakeOperations { LocalPlayerId = 42u, HasComponents = false };

        RuntimeSpellCastState withComponents = Create(book, stocked);
        RuntimeSpellCastState without = Create(book, empty);

        Assert.True(withComponents.HasRequiredComponents(1));
        Assert.False(without.HasRequiredComponents(1));
        Assert.Equal(CastRequestResult.MissingComponents, without.Cast(1));
        Assert.Equal(0, empty.UntargetedSends);
        Assert.Equal(
            withComponents.EvaluateCastGate(1),
            without.EvaluateCastGate(1));
    }

    [Fact]
    public void Cast_MissingComponents_DoesNotIncrementBusyOrSend()
    {
        Spellbook book = MakeBook(flags: 0, untargeted: true, targetMask: 0);
        var operations = new FakeOperations
        {
            LocalPlayerId = 42u,
            HasComponents = false,
        };
        RuntimeSpellCastState state = Create(book, operations);

        Assert.Equal(CastRequestResult.MissingComponents, state.Cast(1));
        Assert.Equal(0, operations.UntargetedSends);
        Assert.Equal(0, operations.BusyIncrements);
    }

    [Fact]
    public void Cast_Disconnected_DoesNotIncrementBusy()
    {
        Spellbook book = MakeBook(flags: 0, untargeted: true, targetMask: 0);
        var operations = new FakeOperations
        {
            LocalPlayerId = 42u,
            CanSend = false,
        };
        RuntimeSpellCastState state = Create(book, operations);

        Assert.Equal(CastRequestResult.Unavailable, state.Cast(1));
        Assert.Equal(0, operations.BusyIncrements);
    }

    [Fact]
    public void Cast_SendThrows_DoesNotIncrementBusyAndRethrows()
    {
        Spellbook book = MakeBook(flags: 0, untargeted: true, targetMask: 0);
        var operations = new FakeOperations
        {
            LocalPlayerId = 42u,
            SendUntargetedAction =
                _ => throw new InvalidOperationException("wire"),
        };
        RuntimeSpellCastState state = Create(book, operations);

        Assert.Throws<InvalidOperationException>(() => state.Cast(1));
        Assert.Equal(0, operations.BusyIncrements);
        Assert.Null(state.LastRequestedSpellId);
    }

    [Fact]
    public void CompleteUse_PublishesOneExactServerReceipt()
    {
        Spellbook book = MakeBook(flags: 0, untargeted: false, targetMask: 0x10);
        var operations = new FakeOperations { LocalPlayerId = 42u };
        RuntimeSpellCastState state = Create(book, operations, selected: 99u);

        Assert.Equal(CastRequestResult.Sent, state.Cast(1));
        Assert.Equal(1u, state.PendingSpellId);
        Assert.Equal(99u, state.PendingTargetId);
        Assert.True(state.CompleteUse(0x0402u));

        Assert.Equal(
            new RuntimeSpellCastCompletion(1, 1u, 99u, 0x0402u),
            state.LastCompletion);
        Assert.Null(state.PendingSpellId);
        Assert.False(state.CompleteUse(0u));
        Assert.Equal(1, state.LastCompletion.Revision);
    }

    [Fact]
    public void Cast_DoesNotOverwritePendingReceiptIdentity()
    {
        Spellbook book = MakeBook(flags: 0, untargeted: false, targetMask: 0x10);
        var operations = new FakeOperations { LocalPlayerId = 42u };
        RuntimeSpellCastState state = Create(book, operations, selected: 99u);

        Assert.Equal(CastRequestResult.Sent, state.Cast(1));
        Assert.Equal(CastRequestResult.Unavailable, state.Cast(1));
        Assert.Equal(1, operations.TargetedSends);
        Assert.Equal(1u, state.PendingSpellId);
        Assert.Equal(99u, state.PendingTargetId);
    }

    private static RuntimeSpellCastState Create(
        Spellbook book,
        FakeOperations operations,
        uint? selected = null)
    {
        var selection = new SelectionState();
        if (selected is uint id)
            selection.Select(id, SelectionChangeSource.System);
        return new RuntimeSpellCastState(book, selection, operations);
    }

    private static Spellbook MakeBook(uint flags, bool untargeted, uint targetMask)
    {
        const string header =
            "Spell ID,Name,Flags [Hex],IsUntargetted,TargetMask [Hex]";
        string row = $"1,Test,0x{flags:X},{untargeted},0x{targetMask:X}";
        SpellTable table = SpellTable.LoadFromReader(
            new StringReader($"{header}\n{row}"));
        var book = new Spellbook(table);
        book.OnSpellLearned(1);
        return book;
    }

    private sealed class FakeOperations : IRuntimeSpellCastOperations
    {
        public uint LocalPlayerId { get; init; }
        public bool CanSend { get; init; } = true;
        public bool HasComponents { get; init; } = true;
        public bool TargetCompatible { get; init; } = true;
        public int UntargetedSends { get; private set; }
        public int TargetedSends { get; private set; }
        public int BusyIncrements { get; private set; }
        public string LastMessage { get; private set; } = string.Empty;
        public Action? StopAction { get; init; }
        public Action<uint>? SendUntargetedAction { get; init; }
        public Action<uint, uint>? SendTargetedAction { get; init; }
        public Action? IncrementBusyAction { get; init; }

        public bool HasRequiredComponents(uint spellId) => HasComponents;

        public bool IsTargetCompatible(
            uint targetId,
            SpellMetadata spell,
            bool showMessage) => TargetCompatible;

        public void StopCompletely() => StopAction?.Invoke();

        public void SendUntargeted(uint spellId)
        {
            UntargetedSends++;
            SendUntargetedAction?.Invoke(spellId);
        }

        public void SendTargeted(uint targetId, uint spellId)
        {
            TargetedSends++;
            SendTargetedAction?.Invoke(targetId, spellId);
        }

        public void DisplayMessage(string message) => LastMessage = message;

        public void IncrementBusy()
        {
            BusyIncrements++;
            IncrementBusyAction?.Invoke();
        }
    }
}
