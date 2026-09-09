using System.Numerics;
using AcDream.Core.Items;
using AcDream.Runtime.Gameplay;

namespace AcDream.App.UI.Layout;

public sealed class SecureTradeUiController : IRetainedPanelController
{
    public const uint LayoutId = 0x2100000Du;
    public const uint RootId = 0x1000007Au;
    public const uint PartnerNameId = 0x1000007Eu;
    public const uint PartnerStatusId = 0x1000007Fu;
    public const uint PartnerCountId = 0x10000080u;
    public const uint PartnerListId = 0x10000081u;
    public const uint SelfNameId = 0x10000085u;
    public const uint TradeButtonId = 0x10000086u;
    public const uint SelfCountId = 0x10000087u;
    public const uint SelfListId = 0x10000088u;
    public const uint ClearAllButtonId = 0x1000008Au;
    public const uint CloseButtonId = 0x1000008Bu;

    private const string AcceptedState = "Highlight";

    private const uint TradeOverlaySpriteId = 0x06001DAEu;

    public sealed record Bindings(
        IRuntimeTradeView Trade,
        ClientObjectTable Objects,
        Func<ItemType, uint, uint, uint, uint, uint> ResolveIcon,
        Action<uint> OpenTrade,
        Action CloseTrade,
        Action<uint> AddToTrade,
        Action<bool /*selfAccepted*/, bool /*partnerAccepted*/, uint /*partner*/> AcceptTrade,
        Action DeclineTrade,
        Action ResetTrade,
        Action<bool> SetWindowVisible,
        uint SelfEmptySlotSprite = 0u,
        uint PartnerEmptySlotSprite = 0u,
        Func<int, string>? FormatTotalItems = null);

    private readonly Bindings _bindings;
    private readonly UiText? _partnerName;
    private readonly UiElement? _partnerStatus;
    private readonly UiText? _partnerCount;
    private readonly UiItemList? _partnerList;
    private readonly UiText? _selfCount;
    private readonly UiItemList? _selfList;
    private readonly UiButton? _tradeButton;

    private long _lastRevision = long.MinValue;
    private bool _wasOpen;
    private uint _pendingPartner;
    private uint _pendingStageItem;
    private bool _disposed;

    private SecureTradeUiController(
        ImportedLayout layout,
        Bindings bindings)
    {
        _bindings = bindings;
        _partnerName = layout.FindElement(PartnerNameId) as UiText;
        _partnerStatus = layout.FindElement(PartnerStatusId);
        _partnerCount = layout.FindElement(PartnerCountId) as UiText;
        _partnerList = layout.FindElement(PartnerListId) as UiItemList;
        _selfCount = layout.FindElement(SelfCountId) as UiText;
        _selfList = layout.FindElement(SelfListId) as UiItemList;
        _tradeButton = layout.FindElement(TradeButtonId) as UiButton;

        if (_tradeButton is not null)
        {
            _tradeButton.SuppressSelfToggle = true;
            _tradeButton.OnClick = () =>
            {
                RuntimeTradeSnapshot snapshot = _bindings.Trade.Snapshot;
                if (!snapshot.IsOpen) return;
                if (snapshot.SelfAccepted)
                    _bindings.DeclineTrade();
                else
                    _bindings.AcceptTrade(
                        true, snapshot.PartnerAccepted, snapshot.PartnerGuid);
            };
        }
        if (layout.FindElement(ClearAllButtonId) is UiButton clearAll)
            clearAll.OnClick = () =>
            {
                if (_bindings.Trade.Snapshot.IsOpen) _bindings.ResetTrade();
            };
        if (layout.FindElement(CloseButtonId) is UiButton close)
            close.OnClick = () =>
            {
                if (_bindings.Trade.Snapshot.IsOpen) _bindings.CloseTrade();
            };

        _selfList?.RegisterDragHandler(new SelfGridDropHandler(this));

        ConfigureGrid(_selfList, bindings.SelfEmptySlotSprite);
        ConfigureGrid(_partnerList, bindings.PartnerEmptySlotSprite);

        _bindings.SetWindowVisible(false);
    }

    private static void ConfigureGrid(UiItemList? list, uint emptySlotSprite)
    {
        if (list is null) return;
        list.Columns = 1;
        list.SingleRow = true;
        list.HorizontalScroll = true;
        list.CellWidth = 32f;
        list.CellHeight = 32f;
        list.FillVisibleEmptySlots = true;
        if (emptySlotSprite != 0u)
            list.CellEmptySprite = emptySlotSprite;
        list.EmptySlotFactory = () => new UiItemSlot
        {
            SpriteResolve = list.SpriteResolve,
            AllowDragSource = false,
        };
    }

    public static SecureTradeUiController? Bind(
        ImportedLayout layout, Bindings bindings)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(bindings);
        if (layout.FindElement(SelfListId) is not UiItemList
            || layout.FindElement(PartnerListId) is not UiItemList)
            return null;
        return new SecureTradeUiController(layout, bindings);
    }

    public void RequestSecureTrade(uint partnerGuid, uint itemGuid)
    {
        if (_disposed || partnerGuid == 0u) return;
        RuntimeTradeSnapshot snapshot = _bindings.Trade.Snapshot;
        if (snapshot.IsOpen && snapshot.PartnerGuid == partnerGuid)
        {
            if (itemGuid != 0u) _bindings.AddToTrade(itemGuid);
            return;
        }
        _pendingPartner = partnerGuid;
        _pendingStageItem = itemGuid;
        _bindings.OpenTrade(partnerGuid);
    }

    /// <summary>Applies the latest owner snapshot (revision-gated).</summary>
    public void Tick()
    {
        if (_disposed) return;
        RuntimeTradeSnapshot snapshot = _bindings.Trade.Snapshot;

        if (snapshot.IsOpen && !_wasOpen)
        {
            _wasOpen = true;
            _bindings.SetWindowVisible(true);
            // AttemptToTradeItem's queued item — stage it now that the
            // window registered, if the register matched the request.
            if (_pendingStageItem != 0u
                && (_pendingPartner == 0u
                    || snapshot.PartnerGuid == _pendingPartner))
            {
                _bindings.AddToTrade(_pendingStageItem);
            }
            _pendingStageItem = 0u;
            _pendingPartner = 0u;
        }
        else if (!snapshot.IsOpen && _wasOpen)
        {
            _wasOpen = false;
            _bindings.SetWindowVisible(false);
        }

        if (snapshot.Revision == _lastRevision) return;
        _lastRevision = snapshot.Revision;

        if (_partnerName is not null)
        {
            string name = _bindings.Objects.Get(snapshot.PartnerGuid)
                ?.GetAppropriateName() ?? string.Empty;
            _partnerName.LinesProvider =
                () => [new UiText.Line(name, Vector4.One)];
        }
        if (_partnerStatus is UiDatElement status)
            status.ActiveState = snapshot.PartnerAccepted ? AcceptedState : "";
        if (_tradeButton is not null)
            _tradeButton.Selected = snapshot.SelfAccepted;

        SetCount(_selfCount, snapshot.SelfItemCount);
        SetCount(_partnerCount, snapshot.PartnerItemCount);
        Populate(_selfList, RuntimeTradeSide.Self);
        Populate(_partnerList, RuntimeTradeSide.Partner);
    }

    public void SyncVisibility()
    {
        _wasOpen = !_bindings.Trade.Snapshot.IsOpen; // force re-evaluate
        Tick();
    }

    public void OnShown() => Tick();

    private void SetCount(UiText? text, int count)
    {
        if (text is null) return;
        string line = _bindings.FormatTotalItems?.Invoke(count)
            ?? count.ToString();
        text.LinesProvider = () => [new UiText.Line(line, Vector4.One)];
    }

    private void Populate(UiItemList? list, RuntimeTradeSide side)
    {
        if (list is null) return;
        using (list.DeferLayout())
        {
            list.Flush();
            foreach (uint guid in _bindings.Trade.GetItems(side))
            {
                ClientObject? item = _bindings.Objects.Get(guid);
                uint icon = item is null ? 0u : _bindings.ResolveIcon(
                    item.Type,
                    item.IconId,
                    item.IconUnderlayId,
                    item.IconOverlayId,
                    item.Effects);
                var cell = new UiItemSlot
                {
                    SpriteResolve = list.SpriteResolve,
                    SlotIndex = list.GetNumUIItems(),
                    AllowDragSource = false,
                    ShowTradeOverlay = side == RuntimeTradeSide.Self,
                    TradeOverlaySprite = TradeOverlaySpriteId,
                    TooltipTextResolve = g => _bindings.Objects.Get(g)?.GetTooltipDisplayName(),
                };
                cell.SetItem(guid, icon);
                list.AddItem(cell);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_tradeButton is not null) _tradeButton.OnClick = null;
    }

    private sealed class SelfGridDropHandler(SecureTradeUiController owner)
        : IItemListDragHandler
    {
        public void OnDragLift(
            UiItemList sourceList, UiItemSlot sourceCell, ItemDragPayload payload)
        {
        }

        public ItemDragAcceptance OnDragOver(
            UiItemList targetList, UiItemSlot targetCell, ItemDragPayload payload)
            => payload.SourceKind == ItemDragSource.Inventory
                ? ItemDragAcceptance.Accept
                : ItemDragAcceptance.Reject;

        public void HandleDropRelease(
            UiItemList targetList, UiItemSlot targetCell, ItemDragPayload payload)
        {
            if (payload.SourceKind != ItemDragSource.Inventory) return;
            if (owner._bindings.Trade.Snapshot.IsOpen)
                owner._bindings.AddToTrade(payload.ObjId);
        }
    }
}
