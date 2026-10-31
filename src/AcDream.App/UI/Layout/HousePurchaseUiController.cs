using System.Numerics;
using AcDream.Core.Items;
using AcDream.Core.Net.Messages;
using AcDream.Runtime.Gameplay;

namespace AcDream.App.UI.Layout;

/// <summary>
/// The slumlord window, LayoutDesc 0x2100000E: a Buy page and a Maintenance
/// page, each with the requirements, the owner, an item strip the player
/// drags payment onto, and a button that offers the staged items. Element
/// names in comments are the retail names from client_portal.dat.
/// </summary>
public sealed class HousePurchaseUiController : IRetainedPanelController, IItemListDragHandler
{
    public const uint LayoutId = 0x2100000Eu;
    public const uint RootId = 0x1000008Cu;                        // RootSlumlord_Field
    public const uint BuyTabId = 0x1000008Eu;                      // HouseBuyTab
    public const uint MaintenanceTabId = 0x1000008Fu;              // HouseMaintenanceTab
    public const uint BuyPageId = 0x10000090u;                     // HouseBuyPage
    public const uint BuyRequirementsTextId = 0x10000091u;         // HouseBuyRequirementsText
    public const uint BuyOwnerId = 0x10000093u;                    // HouseBuy_HouseOwner
    public const uint BuyButtonId = 0x10000094u;                   // HouseBuy_Button
    public const uint BuyItemsListId = 0x10000095u;                // HouseBuyItemsList
    public const uint BuyItemListScrollId = 0x10000096u;           // HouseBuy_ItemListScroll
    public const uint MaintenancePageId = 0x10000097u;             // HouseMaintenancePage
    public const uint MaintenanceRequirementsTextId = 0x10000098u; // HouseMaintenanceRequirementsText
    public const uint MaintenanceOwnerId = 0x1000009Au;            // HouseMaintenance_HouseOwner
    public const uint MaintenanceButtonId = 0x1000009Bu;           // HouseMaintenance_Button
    public const uint MaintenanceItemsListId = 0x1000009Cu;        // HouseMaintenanceItemsList
    public const uint MaintenanceItemListScrollId = 0x1000009Du;   // HouseMaintenance_ItemListScroll
    public const uint CloseButtonId = 0x1000009Eu;                 // CloseSlumlordButton
    private const uint TradeOverlaySprite = 0x06001DAEu;

    public sealed record Bindings(
        IRuntimeHousePurchaseView Purchase,
        ClientObjectTable Objects,
        Func<HousePurchaseTab, uint, HouseStageResult> Stage,
        Action<HousePurchaseTab, uint> Unstage,
        Action<HousePurchaseTab> ClearStaged,
        Action Revalidate,
        Action Close,
        Action<uint, IReadOnlyList<uint>> BuyHouse,
        Action<uint, IReadOnlyList<uint>> RentHouse,
        Func<ItemType, uint, uint, uint, uint, uint> ResolveIcon,
        Action<bool> SetWindowVisible,
        Action<string> Report,
        Func<uint> PlayerGuid,
        // Asks a yes/no question and passes on the answer.
        Action<string, Action<bool>> Confirm,
        // Composes an item's displayed name, material prefix included, as the
        // other item strips do.
        Func<ClientObject, string> ResolveAppropriateName,
        uint EmptySlotSprite = 0u);

    private sealed record Page(
        HousePurchaseTab Tab,
        UiElement? TabElement,
        UiElement? PageElement,
        UiText? Requirements,
        UiText? Owner,
        UiElement? Button,
        UiItemList List);

    private readonly Bindings _bindings;
    private readonly Page _buy;
    private readonly Page _rent;
    private readonly UiElement? _closeButton;
    private HousePurchaseTab _selected = HousePurchaseTab.Buy;
    private long _lastRevision = long.MinValue;
    private bool _wasOpen;
    private GameEvents.HouseProfile? _textProfile;
    private bool _disposed;

    private HousePurchaseUiController(ImportedLayout layout, Bindings bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings.ResolveAppropriateName);
        _bindings = bindings;
        _buy = BindPage(layout, HousePurchaseTab.Buy, BuyTabId, BuyPageId,
            BuyRequirementsTextId, BuyOwnerId, BuyButtonId, BuyItemsListId, BuyItemListScrollId);
        _rent = BindPage(layout, HousePurchaseTab.Rent, MaintenanceTabId, MaintenancePageId,
            MaintenanceRequirementsTextId, MaintenanceOwnerId, MaintenanceButtonId,
            MaintenanceItemsListId, MaintenanceItemListScrollId);
        _closeButton = layout.FindElement(CloseButtonId);
        RetailTabBinding.SetClick(_closeButton, () => _bindings.Close());

        bindings.Objects.ObjectRemoved += OnObjectRemoved;
        bindings.Objects.ObjectUpdated += OnObjectUpdated;
        bindings.Objects.ObjectMoved += OnObjectMoved;
        bindings.Objects.ContainerContentsReplaced += OnContainerContentsReplaced;

        Select(HousePurchaseTab.Buy);
        _bindings.SetWindowVisible(false);
    }

    public static HousePurchaseUiController? Bind(ImportedLayout layout, Bindings bindings)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(bindings);
        return layout.FindElement(BuyItemsListId) is UiItemList
            && layout.FindElement(MaintenanceItemsListId) is UiItemList
                ? new HousePurchaseUiController(layout, bindings)
                : null;
    }

    private Page BindPage(
        ImportedLayout layout, HousePurchaseTab tab, uint tabId, uint pageId,
        uint requirementsId, uint ownerId, uint buttonId, uint listId, uint scrollId)
    {
        var list = (UiItemList)layout.FindElement(listId)!;
        list.Columns = 1;
        list.SingleRow = true;
        list.HorizontalScroll = true;
        list.CellWidth = 32f;
        list.CellHeight = 32f;
        list.FillVisibleEmptySlots = true;
        if (_bindings.EmptySlotSprite != 0u)
            list.CellEmptySprite = _bindings.EmptySlotSprite;
        list.EmptySlotFactory = () => new UiItemSlot
        {
            SpriteResolve = list.SpriteResolve,
            AllowDragSource = false,
        };
        list.RegisterDragHandler(this);
        list.ExamineItemRequested = guid => _bindings.Unstage(tab, guid);
        if (layout.FindElement(scrollId) is UiScrollbar scrollbar)
        {
            scrollbar.Model = list.Scroll;
            scrollbar.Horizontal = true;
        }

        UiElement? button = layout.FindElement(buttonId);
        if (button is UiButton uiButton)
            uiButton.SuppressSelfToggle = true;
        RetailTabBinding.SetClick(button, () => Submit(tab));

        UiElement? tabElement = layout.FindElement(tabId);
        RetailTabBinding.SetClick(tabElement, () => Select(tab));

        return new Page(
            tab,
            tabElement,
            layout.FindElement(pageId),
            layout.FindElement(requirementsId) as UiText,
            layout.FindElement(ownerId) as UiText,
            button,
            list);
    }

    /// <summary>Applies the latest state (revision-gated).</summary>
    public void Tick()
    {
        if (_disposed) return;
        RuntimeHousePurchaseSnapshot snapshot = _bindings.Purchase.Snapshot;

        if (snapshot.IsOpen != _wasOpen)
        {
            _wasOpen = snapshot.IsOpen;
            _bindings.SetWindowVisible(snapshot.IsOpen);
        }

        if (snapshot.Revision == _lastRevision) return;
        _lastRevision = snapshot.Revision;
        // A profile arriving never changes the tab; only the player does.
        if (snapshot.Profile is not { } profile)
        {
            _textProfile = null;
            return;
        }

        // The text is rebuilt only when a new profile arrives, not as items
        // are staged. Each applied profile carries fresh payment arrays, so
        // it never equals the one before it.
        if (!_textProfile.Equals(profile))
        {
            _textProfile = profile;
            SetText(_buy, profile);
            SetText(_rent, profile);
        }

        Refresh(_buy, profile);
        Refresh(_rent, profile);
    }

    private static void SetText(Page page, GameEvents.HouseProfile profile)
    {
        SetLines(page.Requirements, [HousePurchaseText.RequirementText(profile, page.Tab)]);
        SetLines(page.Owner, [HousePurchaseText.OwnerLine(profile)]);
    }

    public void OnShown() => Tick();

    private void Select(HousePurchaseTab tab)
    {
        _selected = tab;
        foreach (Page page in new[] { _buy, _rent })
        {
            bool open = page.Tab == _selected;
            if (page.PageElement is not null)
                page.PageElement.Visible = open;
            RetailTabBinding.SetOpen(page.TabElement, open);
        }
    }

    private void Refresh(Page page, GameEvents.HouseProfile profile)
    {
        IReadOnlyList<uint> staged = _bindings.Purchase.GetStaged(page.Tab);

        using (page.List.DeferLayout())
        {
            page.List.Flush();
            foreach (uint guid in staged)
            {
                if (_bindings.Objects.Get(guid) is not { } item) continue;
                var slot = new UiItemSlot
                {
                    SlotIndex = page.List.GetNumUIItems(),
                    SpriteResolve = page.List.SpriteResolve,
                    AllowDragSource = true,
                    SourceKind = ItemDragSource.Vendor,
                    ShowTradeOverlay = true,
                    TradeOverlaySprite = TradeOverlaySprite,
                    TooltipTextResolve = id => ItemTooltipCaption.Resolve(
                        _bindings.Objects, id, _bindings.ResolveAppropriateName),
                };
                slot.SetItem(guid, _bindings.ResolveIcon(
                    item.Type, item.IconId, item.IconUnderlayId, item.IconOverlayId, item.Effects));
                page.List.AddItem(slot);
            }
        }

        if (page.Button is not null)
            page.Button.Enabled = CanSubmit(page.Tab, profile, staged.Count);
    }

    private bool CanSubmit(
        HousePurchaseTab tab, GameEvents.HouseProfile profile, int stagedCount) =>
        stagedCount > 0
        && !(tab == HousePurchaseTab.Buy && profile.OwnerGuid != 0u)
        && !(tab == HousePurchaseTab.Rent && profile.OwnerGuid == 0u)
        && !(tab == HousePurchaseTab.Rent && profile.MaintenanceFree)
        && (tab != HousePurchaseTab.Buy || BuyCovered(profile));

    // Rent may be part-paid; a purchase needs every line met by the staged
    // items alone.
    private bool BuyCovered(GameEvents.HouseProfile profile)
    {
        for (int i = 0; i < profile.Buy.Count; i++)
            if (_bindings.Purchase.GetStagedAmount(HousePurchaseTab.Buy, i) < profile.Buy[i].Num)
                return false;
        return true;
    }

    private void Submit(HousePurchaseTab tab)
    {
        if (_disposed || _bindings.Purchase.Snapshot.Profile is not { } profile) return;
        if (!CanSubmit(tab, profile, _bindings.Purchase.GetStaged(tab).Count)) return;

        string? question = HousePurchaseText.Confirmation(tab, profile, _bindings.PlayerGuid());
        if (question is null)
        {
            Send(tab, profile.SlumlordGuid);
            return;
        }
        uint slumlord = profile.SlumlordGuid;
        _bindings.Confirm(question, yes =>
        {
            if (yes) Send(tab, slumlord);
            else Decline();
        });
    }

    // A no closes the window, which empties both strips.
    private void Decline()
    {
        if (_disposed || !_bindings.Purchase.Snapshot.IsOpen) return;
        _bindings.Close();
    }

    // Re-checked on a yes: the window may have closed or moved on meanwhile.
    private void Send(HousePurchaseTab tab, uint slumlord)
    {
        if (_disposed
            || _bindings.Purchase.Snapshot.Profile is not { } profile
            || profile.SlumlordGuid != slumlord)
            return;
        uint[] staged = _bindings.Purchase.GetStaged(tab).ToArray();
        if (!CanSubmit(tab, profile, staged.Length)) return;
        if (tab == HousePurchaseTab.Buy)
            _bindings.BuyHouse(profile.SlumlordGuid, staged);
        else
            _bindings.RentHouse(profile.SlumlordGuid, staged);
        _bindings.ClearStaged(tab);
    }

    private static void SetLines(UiText? text, IReadOnlyList<string> lines)
    {
        if (text is null) return;
        UiText.Line[] rendered = lines
            .Select(line => new UiText.Line(line, Vector4.One))
            .ToArray();
        text.LinesProvider = () => rendered;
    }

    private Page? PageFor(UiItemList list) =>
        ReferenceEquals(list, _buy.List) ? _buy
        : ReferenceEquals(list, _rent.List) ? _rent
        : null;

    public void OnDragLift(UiItemList sourceList, UiItemSlot sourceCell, ItemDragPayload payload)
    {
        if (PageFor(sourceList) is { } page)
            _bindings.Unstage(page.Tab, payload.ObjId);
    }

    public ItemDragAcceptance OnDragOver(UiItemList targetList, UiItemSlot targetCell, ItemDragPayload payload)
        => PageFor(targetList) is not null && payload.SourceKind == ItemDragSource.Inventory
            ? ItemDragAcceptance.Accept
            : ItemDragAcceptance.Reject;

    // A drop from the ground, a chest or a corpse still reaches the state,
    // which refuses it with the one printed refusal. An alias (a shortcut,
    // or a vendor, house or salvage list cell) never does.
    public void HandleDropRelease(UiItemList targetList, UiItemSlot targetCell, ItemDragPayload payload)
    {
        if (PageFor(targetList) is not { } page
            || payload.SourceKind is not (ItemDragSource.Inventory or ItemDragSource.Ground))
            return;
        if (HousePurchaseText.Refusal(_bindings.Stage(page.Tab, payload.ObjId)) is { } refusal)
            _bindings.Report(refusal);
    }

    private void OnObjectRemoved(ClientObject item) => _bindings.Revalidate();
    // A staged item can change (a stack merging or splitting) without any
    // state change, so the revision gate would keep its slot as it was. What
    // it paid when staged stays as it was.
    private void OnObjectUpdated(ClientObject item)
    {
        if (_disposed || !_bindings.Purchase.Snapshot.IsOpen) return;
        if (!_bindings.Purchase.GetStaged(HousePurchaseTab.Buy).Contains(item.ObjectId)
            && !_bindings.Purchase.GetStaged(HousePurchaseTab.Rent).Contains(item.ObjectId))
            return;
        _bindings.Revalidate();
        _lastRevision = long.MinValue;
    }

    private void OnObjectMoved(ClientObjectMove move) => _bindings.Revalidate();
    private void OnContainerContentsReplaced(uint containerId) => _bindings.Revalidate();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _bindings.Objects.ObjectRemoved -= OnObjectRemoved;
        _bindings.Objects.ObjectUpdated -= OnObjectUpdated;
        _bindings.Objects.ObjectMoved -= OnObjectMoved;
        _bindings.Objects.ContainerContentsReplaced -= OnContainerContentsReplaced;
        foreach (Page page in new[] { _buy, _rent })
        {
            RetailTabBinding.SetClick(page.Button, null);
            RetailTabBinding.SetClick(page.TabElement, null);
            page.List.ExamineItemRequested = null;
        }
        RetailTabBinding.SetClick(_closeButton, null);
    }
}
