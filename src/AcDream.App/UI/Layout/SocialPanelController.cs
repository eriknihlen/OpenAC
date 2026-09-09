using System;
using AcDream.Core.Social;
using AcDream.Runtime;

namespace AcDream.App.UI.Layout;

public sealed class SocialPanelController : IRetainedPanelController
{
    public const uint HostLayoutId = 0x2100006Eu;

    public const uint SlotElementId = 0x1000018Fu;

    private const uint FriendsPageId = 0x10000513u;
    private const uint AllegiancePageId = 0x10000291u;
    private const uint FellowshipPageId = 0x10000292u;
    private const uint SquelchPageId = 0x1000054Au;

    private const uint CloseButtonId = 0x10000290u;

    public sealed record Callbacks(
        Action Toggle,
        SocialFellowshipPageController.Bindings Fellowship,
        SocialAllegiancePageController.Bindings Allegiance,
        FriendsState Friends,
        SquelchState Squelch,
        Func<uint, uint, UiElement?> TemplateResolver,
        SocialFriendsPageController.Actions? FriendsActions = null,
        SocialSquelchPageController.Actions? SquelchActions = null);

    private readonly UiTabPanel _tabPanel;
    private readonly SocialFellowshipPageController? _fellowship;
    private readonly SocialAllegiancePageController? _allegiance;
    private readonly SocialFriendsPageController? _friends;
    private readonly SocialSquelchPageController? _squelch;

    private readonly Action<uint, uint> _onActivePageChanged;

    private bool _disposed;

    public UiElement Root => _tabPanel;

    public UiTabPanel TabPanel => _tabPanel;

    private SocialPanelController(
        UiTabPanel tabPanel,
        SocialFellowshipPageController? fellowship,
        SocialAllegiancePageController? allegiance,
        SocialFriendsPageController? friends,
        SocialSquelchPageController? squelch)
    {
        _tabPanel = tabPanel;
        _fellowship = fellowship;
        _allegiance = allegiance;
        _friends = friends;
        _squelch = squelch;

        _onActivePageChanged = (_, _) =>
        {
            UpdateFellowshipPageVisibility();
            UpdateAllegiancePageVisibility();
        };
        _tabPanel.ActivePageChanged += _onActivePageChanged;
    }

    public static SocialPanelController? Bind(ImportedLayout layout, Callbacks callbacks)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(callbacks);

        if (layout.Root is not UiTabPanel tabPanel)
        {
            Console.WriteLine(
                "[UI] SocialPanelController.Bind: root did not build as UiTabPanel "
                + $"(actual type {layout.Root.GetType().Name}) — social panel will not open.");
            return null;
        }

        if (layout.FindElement(CloseButtonId) is UiButton close)
            close.OnClick = callbacks.Toggle;
        else
            Console.WriteLine(
                $"[UI] SocialPanelController: close button 0x{CloseButtonId:X8} "
                + "not found in the built layout — its handler was not wired.");

        UiElement? fellowshipPage = UiElement.FindDescendant(tabPanel, FellowshipPageId);
        UiElement? allegiancePage = UiElement.FindDescendant(tabPanel, AllegiancePageId);
        UiElement? friendsPage = UiElement.FindDescendant(tabPanel, FriendsPageId);
        UiElement? squelchPage = UiElement.FindDescendant(tabPanel, SquelchPageId);

        SocialFellowshipPageController? fellowship = fellowshipPage is null
            ? null
            : SocialFellowshipPageController.Bind(fellowshipPage, callbacks.Fellowship);
        SocialAllegiancePageController? allegiance = allegiancePage is null
            ? null
            : SocialAllegiancePageController.Bind(allegiancePage, callbacks.Allegiance);
        SocialFriendsPageController? friends = friendsPage is null
            ? null
            : SocialFriendsPageController.Bind(
                friendsPage, callbacks.Friends, callbacks.TemplateResolver, callbacks.FriendsActions);
        SocialSquelchPageController? squelch = squelchPage is null
            ? null
            : SocialSquelchPageController.Bind(
                squelchPage, callbacks.Squelch, callbacks.TemplateResolver, callbacks.SquelchActions);

        if (fellowshipPage is null)
            Console.WriteLine($"[UI] SocialPanelController: Fellowship page 0x{FellowshipPageId:X8} not found.");
        if (allegiancePage is null)
            Console.WriteLine($"[UI] SocialPanelController: Allegiance page 0x{AllegiancePageId:X8} not found.");
        if (friendsPage is null)
            Console.WriteLine($"[UI] SocialPanelController: Friends page 0x{FriendsPageId:X8} not found.");
        if (squelchPage is null)
            Console.WriteLine($"[UI] SocialPanelController: Squelch page 0x{SquelchPageId:X8} not found.");

        return new SocialPanelController(tabPanel, fellowship, allegiance, friends, squelch);
    }

    public void ActivateTabs() => _tabPanel.ActivateTabBehavior();

    public void ShowAllegiance() => _tabPanel.SwitchTo(AllegiancePageId);

    /// <summary>F4 <c>ToggleFellowshipPanel</c>'s tab-switch half.</summary>
    public void ShowFellowship() => _tabPanel.SwitchTo(FellowshipPageId);

    public void ShowFriends() => _tabPanel.SwitchTo(FriendsPageId);

    public bool IsShowingAllegiance => _tabPanel.ActivePageElementId == AllegiancePageId;

    /// <summary>True when the Fellowship tab is the active page.</summary>
    public bool IsShowingFellowship => _tabPanel.ActivePageElementId == FellowshipPageId;

    public bool IsShowingFriends => _tabPanel.ActivePageElementId == FriendsPageId;

    private bool _visible;

    public void OnShown()
    {
        _visible = true;
        UpdateFellowshipPageVisibility();
        UpdateAllegiancePageVisibility();
    }

    public void OnHidden()
    {
        _visible = false;
        UpdateFellowshipPageVisibility();
        UpdateAllegiancePageVisibility();
    }

    /// <summary>D4: the Fellowship page is "visible" (and therefore
    /// declares its panel-open state to Runtime) exactly when the social
    /// WINDOW is shown AND Fellowship is the active tab.</summary>
    private void UpdateFellowshipPageVisibility() =>
        _fellowship?.SetPageVisible(_visible && IsShowingFellowship);

    private void UpdateAllegiancePageVisibility() =>
        _allegiance?.SetPageVisible(_visible && IsShowingAllegiance);

    public void ResetSessionDeclaration()
    {
        _fellowship?.ResetPageVisibleLatch();
        _allegiance?.ResetPageVisibleLatch();
    }

    public void RedeclareAfterWorldEntry()
    {
        UpdateFellowshipPageVisibility();
        _allegiance?.RedeclareAfterWorldEntry();
    }

    public void Tick()
    {
        if (_disposed) return;
        _fellowship?.Tick();
        _allegiance?.Tick();
        if (_visible)
        {
            _friends?.Tick();
            _squelch?.Tick();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _tabPanel.ActivePageChanged -= _onActivePageChanged;
    }
}
