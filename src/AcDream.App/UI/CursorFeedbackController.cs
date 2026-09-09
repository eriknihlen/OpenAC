using AcDream.Core.Combat;
using AcDream.Runtime.Gameplay;

namespace AcDream.App.UI;

public enum CursorFeedbackKind
{
    Default,
    Combat,
    Use,
    Examine,
    Busy,
    Text,
    WindowMove,
    ResizeHorizontal,
    ResizeVertical,
    ResizeDiagonalNwse,
    ResizeDiagonalNesw,
    Drag,
    DragAccept,
    DragReject,
    TargetPending,
    TargetValid,
    TargetInvalid,
}

public enum RetailCursorTargetMode
{
    None = 0,
    Use = 1,
    Examine = 2,
    UseTarget = 3,
}

public enum RetailGlobalCursorKind
{
    Default,
    DefaultFound,
    MeleeOrMissile,
    MeleeOrMissileFound,
    Magic,
    MagicFound,
    Examine,
    ExamineFound,
    Use,
    UseFound,
    Busy,
    BusyFound,
    TargetPending,
    TargetValid,
    TargetInvalid,
}

public readonly record struct CursorFeedback(
    CursorFeedbackKind Kind,
    UiCursorMedia Cursor = default,
    RetailGlobalCursorKind GlobalKind = RetailGlobalCursorKind.Default)
{
    public static CursorFeedback Default { get; } = new(CursorFeedbackKind.Default);
}

public readonly record struct CursorFeedbackSnapshot(
    object? DragPayload = null,
    UiItemSlot.DragAcceptState DragAccept = UiItemSlot.DragAcceptState.None,
    ResizeEdges ActiveResizeEdges = ResizeEdges.None,
    ResizeEdges HoverResizeEdges = ResizeEdges.None,
    bool WindowMoveActive = false,
    bool HoverWindowMove = false,
    bool HoverUi = false,
    bool HoverTextEdit = false,
    uint HoverTargetGuid = 0,
    bool? HoverTargetCompatible = null,
    int BusyCount = 0,
    RetailCursorTargetMode TargetMode = RetailCursorTargetMode.None,
    CombatMode CombatMode = CombatMode.NonCombat);

public sealed class CursorFeedbackController
{
    private readonly ItemInteractionController? _itemInteraction;
    private readonly Func<uint>? _worldTargetProvider;
    private readonly Func<CombatMode> _combatModeProvider;

    public CursorFeedbackController(
        ItemInteractionController? itemInteraction = null,
        Func<uint>? worldTargetProvider = null,
        Func<CombatMode>? combatModeProvider = null)
    {
        _itemInteraction = itemInteraction;
        _worldTargetProvider = worldTargetProvider;
        _combatModeProvider = combatModeProvider ?? (() => CombatMode.NonCombat);
    }

    public CursorFeedback Current { get; private set; } = CursorFeedback.Default;

    public CursorFeedback Update(UiRoot root)
    {
        ArgumentNullException.ThrowIfNull(root);

        UiElement? hover = root.Pick(root.MouseX, root.MouseY);

        RetailCursorTargetMode targetMode = ModeFromInteraction(_itemInteraction);
        UiItemSlot? hoveredItem = FindHoveredItemSlot(hover);
        uint hoverTarget = hoveredItem is not null
            ? hoveredItem.ItemId
            : FindRepresentedObject(hover) is { } represented
                ? represented
                : _worldTargetProvider?.Invoke() ?? 0u;
        bool? hoverTargetCompatible = targetMode == RetailCursorTargetMode.UseTarget
            && hoverTarget != 0
                ? _itemInteraction?.IsCurrentTargetCompatible(hoverTarget)
                : null;

        var snapshot = new CursorFeedbackSnapshot(
            DragPayload: root.DragPayload,
            DragAccept: FindHoveredItemSlot(hover)?.DragAcceptVisual ?? UiItemSlot.DragAcceptState.None,
            ActiveResizeEdges: root.ActiveResizeEdges,
            HoverResizeEdges: root.HoverResizeEdges,
            WindowMoveActive: root.IsWindowMoveActive,
            HoverWindowMove: root.HoverWindowMove,
            HoverUi: hover is not null,
            HoverTextEdit: hover?.IsEditControl == true,
            HoverTargetGuid: hoverTarget,
            HoverTargetCompatible: hoverTargetCompatible,
            BusyCount: _itemInteraction?.BusyCount ?? 0,
            TargetMode: targetMode,
            CombatMode: _combatModeProvider());

        var kind = ResolveKind(snapshot);
        UiCursorMedia authoredCursor = ResolveCursor(root.Captured, hover, root.UiLocked);
        UiCursorMedia cursor = ResolveEffectiveCursor(kind, snapshot, authoredCursor);
        Current = new CursorFeedback(kind, cursor, ResolveGlobalKind(snapshot));
        return Current;
    }

    public CursorFeedback Update(CursorFeedbackSnapshot snapshot)
    {
        Current = Resolve(snapshot);
        return Current;
    }

    public CursorFeedback Resolve(CursorFeedbackSnapshot snapshot)
    {
        CursorFeedbackKind kind = ResolveKind(snapshot);
        return new(
            kind,
            ResolveEffectiveCursor(kind, snapshot, authoredCursor: default),
            ResolveGlobalKind(snapshot));
    }

    private CursorFeedbackKind ResolveKind(CursorFeedbackSnapshot snapshot)
    {
        if (snapshot.DragPayload is not null)
        {
            return snapshot.DragAccept switch
            {
                UiItemSlot.DragAcceptState.Accept => CursorFeedbackKind.DragAccept,
                UiItemSlot.DragAcceptState.Reject => CursorFeedbackKind.DragReject,
                _ => CursorFeedbackKind.Drag,
            };
        }

        ResizeEdges resizeEdges = snapshot.ActiveResizeEdges != ResizeEdges.None
            ? snapshot.ActiveResizeEdges
            : snapshot.HoverResizeEdges;
        if (resizeEdges != ResizeEdges.None)
            return KindForResize(resizeEdges);

        if (snapshot.WindowMoveActive)
            return CursorFeedbackKind.WindowMove;

        RetailCursorTargetMode targetMode = EffectiveTargetMode(snapshot);
        if (targetMode == RetailCursorTargetMode.UseTarget)
        {
            if (snapshot.HoverTargetGuid != 0)
            {
                bool compatible = snapshot.HoverTargetCompatible
                    ?? _itemInteraction?.IsCurrentTargetCompatible(snapshot.HoverTargetGuid)
                    ?? false;
                return compatible
                        ? CursorFeedbackKind.TargetValid
                        : CursorFeedbackKind.TargetInvalid;
            }

            return CursorFeedbackKind.TargetPending;
        }

        if (snapshot.BusyCount > 0)
            return CursorFeedbackKind.Busy;

        if (targetMode == RetailCursorTargetMode.Use)
            return CursorFeedbackKind.Use;

        if (targetMode == RetailCursorTargetMode.Examine)
            return CursorFeedbackKind.Examine;

        if (snapshot.HoverWindowMove)
            return CursorFeedbackKind.WindowMove;

        if (snapshot.HoverTextEdit)
            return CursorFeedbackKind.Text;

        if (snapshot.CombatMode is CombatMode.Melee or CombatMode.Missile or CombatMode.Magic)
            return CursorFeedbackKind.Combat;

        return CursorFeedbackKind.Default;
    }

    internal RetailGlobalCursorKind ResolveGlobalKind(CursorFeedbackSnapshot snapshot)
    {
        bool found = snapshot.HoverTargetGuid != 0;

        if (snapshot.BusyCount > 0)
            return found ? RetailGlobalCursorKind.BusyFound : RetailGlobalCursorKind.Busy;

        switch (EffectiveTargetMode(snapshot))
        {
            case RetailCursorTargetMode.Use:
                return found ? RetailGlobalCursorKind.UseFound : RetailGlobalCursorKind.Use;
            case RetailCursorTargetMode.Examine:
                return found ? RetailGlobalCursorKind.ExamineFound : RetailGlobalCursorKind.Examine;
            case RetailCursorTargetMode.UseTarget:
                if (!found)
                    return RetailGlobalCursorKind.TargetPending;
                bool compatible = snapshot.HoverTargetCompatible
                    ?? _itemInteraction?.IsCurrentTargetCompatible(snapshot.HoverTargetGuid)
                    ?? false;
                return compatible
                    ? RetailGlobalCursorKind.TargetValid
                    : RetailGlobalCursorKind.TargetInvalid;
            case RetailCursorTargetMode.None:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(snapshot), snapshot.TargetMode, "Unknown retail target mode.");
        }

        return snapshot.CombatMode switch
        {
            CombatMode.Melee or CombatMode.Missile => found
                ? RetailGlobalCursorKind.MeleeOrMissileFound
                : RetailGlobalCursorKind.MeleeOrMissile,
            CombatMode.Magic => found
                ? RetailGlobalCursorKind.MagicFound
                : RetailGlobalCursorKind.Magic,
            _ => found ? RetailGlobalCursorKind.DefaultFound : RetailGlobalCursorKind.Default,
        };
    }

    private RetailCursorTargetMode EffectiveTargetMode(CursorFeedbackSnapshot snapshot)
        => snapshot.TargetMode == RetailCursorTargetMode.None
            ? ModeFromInteraction(_itemInteraction)
            : snapshot.TargetMode;

    private static RetailCursorTargetMode ModeFromInteraction(
        ItemInteractionController? interaction)
        => interaction?.InteractionState.Current.Kind switch
        {
            InteractionModeKind.Use => RetailCursorTargetMode.Use,
            InteractionModeKind.Examine => RetailCursorTargetMode.Examine,
            InteractionModeKind.UseItemOnTarget => RetailCursorTargetMode.UseTarget,
            _ => RetailCursorTargetMode.None,
        };

    private static CursorFeedbackKind KindForResize(ResizeEdges edges)
    {
        bool horizontal = (edges & (ResizeEdges.Left | ResizeEdges.Right)) != 0;
        bool vertical = (edges & (ResizeEdges.Top | ResizeEdges.Bottom)) != 0;

        if (horizontal && vertical)
        {
            bool nwse =
                ((edges & ResizeEdges.Left) != 0 && (edges & ResizeEdges.Top) != 0)
                || ((edges & ResizeEdges.Right) != 0 && (edges & ResizeEdges.Bottom) != 0);
            return nwse ? CursorFeedbackKind.ResizeDiagonalNwse : CursorFeedbackKind.ResizeDiagonalNesw;
        }

        if (horizontal) return CursorFeedbackKind.ResizeHorizontal;
        if (vertical) return CursorFeedbackKind.ResizeVertical;
        return CursorFeedbackKind.Default;
    }

    private static UiCursorMedia ResolveCursor(UiElement? captured, UiElement? hover, bool uiLocked)
    {
        var capturedCursor = AuthoredCursor(captured, uiLocked);
        if (capturedCursor.IsValid)
            return capturedCursor;

        return AuthoredCursor(hover, uiLocked);

        static UiCursorMedia AuthoredCursor(UiElement? element, bool locked)
            => element is null || (locked && element.WindowMoveHandle)
                ? default
                : element.ActiveCursor();
    }

    private static UiCursorMedia ResolveEffectiveCursor(
        CursorFeedbackKind kind,
        CursorFeedbackSnapshot snapshot,
        UiCursorMedia authoredCursor)
    {
        bool syntheticControlOwnsPointer = snapshot.ActiveResizeEdges != ResizeEdges.None
            || snapshot.HoverResizeEdges != ResizeEdges.None
            || snapshot.WindowMoveActive;

        if (syntheticControlOwnsPointer
            && RetailCursorCatalog.TryGetWindowControlCursor(kind, out UiCursorMedia capturedControl))
            return capturedControl;

        // Ordinary imported elements retain their authored MD_Data_Cursor exactly.
        if (authoredCursor.IsValid)
            return authoredCursor;

        return RetailCursorCatalog.TryGetWindowControlCursor(kind, out UiCursorMedia fallback)
            ? fallback
            : default;
    }

    private static UiItemSlot? FindHoveredItemSlot(UiElement? element)
    {
        while (element is not null)
        {
            if (element is UiItemSlot slot)
                return slot;
            element = element.Parent;
        }
        return null;
    }

    private static uint? FindRepresentedObject(UiElement? element)
    {
        while (element is not null)
        {
            if (element.FoundObjectGuidProvider is { } provider)
            {
                uint guid = provider();
                if (guid != 0u)
                    return guid;
            }
            element = element.Parent;
        }
        return null;
    }
}
