using System;
using System.Numerics;
using AcDream.Core.Items;

namespace AcDream.App.UI;

public class UiItemSlot : UiElement
{
    public UiItemSlot()
    {
        ClickThrough = false;
        AuthoredTooltipRootElementId = Layout.RetailTooltipPresenter.SharedPopupSkinRootElementId;
        AuthoredTooltipLayoutDid = Layout.RetailTooltipPresenter.SharedPopupSkinLayoutDid;
    }

    public override bool ConsumesDatChildren => true;

    public uint ItemId { get; private set; }

    public Func<uint, string?>? TooltipTextResolve { get; set; }

    public override string? GetTooltipText()
        => ItemId != 0 ? TooltipTextResolve?.Invoke(ItemId) : null;

    public uint IconTexture { get; private set; }

    public uint DragIconTexture { get; private set; }

    public ShortcutEntry? Shortcut { get; private set; }

    public int SlotIndex { get; set; } = -1;

    public ItemDragSource SourceKind { get; set; } = ItemDragSource.Inventory;

    public uint DragAcceptSprite { get; set; } = 0x060011F9u;
    public uint DragRejectSprite { get; set; } = 0x060011F8u;

    public bool IsOpenContainer { get; set; }
    public uint OpenContainerSprite { get; set; } = 0x06005D9Cu;

    public bool Selected { get; set; }
    public uint SelectedSprite { get; set; } = 0x06004D21u;

    public float CapacityFill { get; set; } = -1f;
    public uint CapacityBackSprite { get; set; } = 0x06004D22u;
    public uint CapacityFrontSprite { get; set; } = 0x06004D23u;

    public enum DragAcceptState { None, Accept, Reject }
    private DragAcceptState _dragAccept = DragAcceptState.None;
    internal DragAcceptState DragAcceptVisual => _dragAccept;

    private protected void SetDragAcceptVisual(DragAcceptState state) => _dragAccept = state;

    public uint EmptySprite { get; set; } = 0x060074CFu;

    public uint WaitingSprite { get; set; } = 0x0600109Au;

    private bool _waiting;
    internal bool WaitingVisual => _waiting;
    private bool _primaryPressConsumed;

    public Func<uint, (uint tex, int w, int h)>? SpriteResolve { get; set; }

    public bool ShowTradeOverlay { get; set; }

    public uint TradeOverlaySprite { get; set; }

    public IReadOnlyList<uint>? CooldownSprites { get; set; }

    public Func<uint, int>? CooldownStepProvider { get; set; }

    public void SetItem(
        uint itemId,
        uint iconTexture,
        ShortcutEntry? shortcut = null,
        uint dragIconTexture = 0)
    {
        ItemId = itemId;
        IconTexture = iconTexture;
        DragIconTexture = dragIconTexture;
        Shortcut = shortcut;
    }

    public void Clear()
    {
        ItemId = 0;
        IconTexture = 0;
        DragIconTexture = 0;
        Shortcut = null;
        _waiting = false;
        _primaryPressConsumed = false;
    }

    /// <inheritdoc/>
    public override object? GetDragPayload()
        => AllowDragSource && ItemId != 0 && !_primaryPressConsumed
            ? new ItemDragPayload(ItemId, SourceKind, SlotIndex, this, Shortcut)
            : null;

    /// <inheritdoc/>
    public override (uint tex, int w, int h)? GetDragGhost()
    {
        if (ItemId == 0) return null;
        if (DragIconTexture != 0) return (DragIconTexture, 32, 32);
        return IconTexture != 0 ? (IconTexture, (int)Width, (int)Height) : null;
    }

    /// <inheritdoc/>
    internal override void SetDragSourceActive(bool active, object? payload)
    {
        SetWaitingState(active && SourceKind != ItemDragSource.ShortcutBar);
    }

    internal void SetWaitingState(bool waiting)
        => _waiting = waiting && ItemId != 0;

    public bool AllowDragSource { get; set; } = true;

    public override bool IsDragSource => ItemId != 0 && AllowDragSource;

    public override bool HandlesClick => ItemId != 0;

    protected UiItemList? FindList()
    {
        UiElement? e = Parent;
        while (e is not null) { if (e is UiItemList l) return l; e = e.Parent; }
        return null;
    }


    public int ShortcutNum { get; private set; } = -1;

    public bool ShortcutGhosted { get; private set; }

    public uint[]? RegularDigits { get; set; }

    public uint[]? GhostedDigits { get; set; }

    public uint[]? EmptyDigits { get; set; }

    protected virtual bool IsShortcutOccupied => ItemId != 0;

    public virtual bool IsEmptySlot => ItemId == 0;

    public void SetShortcutNum(int index, bool ghosted)
    {
        ShortcutNum = index;
        ShortcutGhosted = ghosted;
    }

    public void ClearShortcutNum() { ShortcutNum = -1; }

    internal uint[]? ActiveDigitArray()
    {
        return IsShortcutOccupied
            ? (ShortcutGhosted ? GhostedDigits : RegularDigits)
            : EmptyDigits;
    }

    // ── Events / draw ─────────────────────────────────────────────────────────

    public Action? Clicked { get; set; }

    public Action? DoubleClicked { get; set; }

    /// <inheritdoc/>
    public override bool OnEvent(in UiEvent e)
    {
        switch (e.Type)
        {
            case UiEventType.MouseDown:
                _primaryPressConsumed = ItemId != 0
                    && FindList() is { PrimaryItemPressed: { } pressed }
                    && pressed(ItemId);
                return true;
            case UiEventType.Click:
                if (!_primaryPressConsumed)
                    Clicked?.Invoke();
                return true;
            case UiEventType.DoubleClick:
                if (!_primaryPressConsumed)
                    DoubleClicked?.Invoke();
                return true;
            case UiEventType.RightClick:
                if (ItemId != 0
                    && FindList() is { ExamineItemRequested: { } examine })
                    examine(ItemId);
                return true;

            case UiEventType.DragBegin:
                if (FindList() is { DragHandler: { } lh } liftList && e.Payload is ItemDragPayload lp)
                    lh.OnDragLift(liftList, this, lp);
                return true;

            case UiEventType.DragEnter:            // pointer entered me mid-drag → ask the list's handler
                _dragAccept = FindList() is { DragHandler: { } h } list
                              && e.Payload is ItemDragPayload p
                    ? h.OnDragOver(list, this, p) switch
                    {
                        ItemDragAcceptance.Accept => DragAcceptState.Accept,
                        ItemDragAcceptance.Reject => DragAcceptState.Reject,
                        _ => DragAcceptState.None,
                    }
                    : DragAcceptState.Reject;
                return true;

            case UiEventType.DragOver:             // UiRoot fires this on LEAVE → neutral
                _dragAccept = DragAcceptState.None;
                return true;

            case UiEventType.DropReleased:
                _dragAccept = DragAcceptState.None;
                if (FindList() is { DragHandler: { } dh } dl && e.Payload is ItemDragPayload dp)
                    dh.HandleDropRelease(dl, this, dp);
                return true;
        }
        return false;
    }

    protected override void OnDraw(UiRenderContext ctx)
    {
        // Draw the icon (filled slot) or the empty-slot border. Both paths fall through
        // to the digit draw below; the slot label always shows on top-row slots.
        if (ItemId != 0 && IconTexture != 0)
        {
            ctx.DrawSprite(IconTexture, 0f, 0f, Width, Height, 0f, 0f, 1f, 1f, Vector4.One);
        }
        else if (SpriteResolve is not null && EmptySprite != 0)
        {
            var (tex, _, _) = SpriteResolve(EmptySprite);
            if (tex != 0)
                ctx.DrawSprite(tex, 0f, 0f, Width, Height, 0f, 0f, 1f, 1f, Vector4.One);
        }

        if (ShowTradeOverlay
            && ItemId != 0
            && SpriteResolve is not null
            && TradeOverlaySprite != 0)
        {
            var (overlayTex, _, _) = SpriteResolve(TradeOverlaySprite);
            if (overlayTex != 0)
                ctx.DrawSprite(overlayTex, 0f, 0f, Width, Height, 0f, 0f, 1f, 1f, Vector4.One);
        }

        DrawShortcutOverlay(ctx);

        if (CapacityFill >= 0f && SpriteResolve is not null)
        {
            const float by = 1f, bw = 5f, bh = 30f;
            float bx = Width - bw;
            if (CapacityBackSprite != 0)
            {
                var (bt, _, _) = SpriteResolve(CapacityBackSprite);
                if (bt != 0) ctx.DrawSprite(bt, bx, by, bw, bh, 0f, 0f, 1f, 1f, Vector4.One);
            }
            float f = Math.Clamp(CapacityFill, 0f, 1f);
            if (f > 0f && CapacityFrontSprite != 0)
            {
                var (ft, _, _) = SpriteResolve(CapacityFrontSprite);
                if (ft != 0)
                {
                    float fh = bh * f;
                    ctx.DrawSprite(ft, bx, by + (bh - fh), bw, fh, 0f, 1f - f, 1f, 1f, Vector4.One);
                }
            }
        }

        if (_waiting && SpriteResolve is not null && WaitingSprite != 0)
        {
            var (tex, _, _) = SpriteResolve(WaitingSprite);
            if (tex != 0)
                ctx.DrawSprite(tex, 0f, 0f, Width, Height, 0f, 0f, 1f, 1f, Vector4.One);
        }

        if (IsOpenContainer && SpriteResolve is not null && OpenContainerSprite != 0)
        {
            var (tex, _, _) = SpriteResolve(OpenContainerSprite);
            if (tex != 0)
                ctx.DrawSprite(tex, 0f, 0f, Width, Height, 0f, 0f, 1f, 1f, Vector4.One);
        }
        if (Selected && SpriteResolve is not null && SelectedSprite != 0)
        {
            var (tex, _, _) = SpriteResolve(SelectedSprite);
            if (tex != 0)
                ctx.DrawSprite(tex, 0f, 0f, Width, Height, 0f, 0f, 1f, 1f, Vector4.One);
        }

        DrawDragAcceptOverlay(ctx);

        uint cooldownSprite = ActiveCooldownSprite();
        if (cooldownSprite != 0u && SpriteResolve is not null)
        {
            var (texture, _, _) = SpriteResolve(cooldownSprite);
            if (texture != 0u)
                ctx.DrawSprite(
                    texture,
                    0f,
                    0f,
                    Width,
                    Height,
                    0f,
                    0f,
                    1f,
                    1f,
                    Vector4.One);
        }
    }

    protected void DrawDragAcceptOverlay(UiRenderContext ctx)
    {
        if (_dragAccept == DragAcceptState.None || SpriteResolve is null)
            return;
        uint id = _dragAccept == DragAcceptState.Accept ? DragAcceptSprite : DragRejectSprite;
        if (id == 0)
            return;
        var (tex, _, _) = SpriteResolve(id);
        if (tex != 0)
            ctx.DrawSprite(tex, 0f, 0f, Width, Height, 0f, 0f, 1f, 1f, Vector4.One);
    }

    protected void DrawShortcutOverlay(UiRenderContext ctx)
    {
        if (ShortcutNum < 0 || SpriteResolve is null)
            return;

        uint[]? digits = ActiveDigitArray();
        if (digits is null || ShortcutNum >= digits.Length)
            return;

        uint did = digits[ShortcutNum];
        if (did == 0)
            return;

        var (texture, _, _) = SpriteResolve(did);
        if (texture != 0)
            ctx.DrawSprite(texture, 0f, 0f, Width, Height, 0f, 0f, 1f, 1f, Vector4.One);
    }

    internal uint ActiveCooldownSprite()
    {
        if (ItemId == 0u
            || CooldownSprites is null
            || CooldownStepProvider is null)
            return 0u;

        int step = CooldownStepProvider(ItemId);
        return step is >= 1 and <= 10 && step <= CooldownSprites.Count
            ? CooldownSprites[step - 1]
            : 0u;
    }
}
