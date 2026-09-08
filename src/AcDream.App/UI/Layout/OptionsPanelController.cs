using System;
using System.Collections.Generic;
using AcDream.App.UI;
using AcDream.UI.Abstractions.Panels.Settings;
using AcDream.Core.Chat;

namespace AcDream.App.UI.Layout;

public sealed class OptionsPanelController : IRetainedPanelController
{
    public const uint HostLayoutId = 0x2100006Eu;

    public const uint SlotElementId = 0x1000018Du;

    private const uint GameplayPageId = 0x10000212u;
    private const uint CharacterPageId = 0x10000211u;
    private const uint ChatPageId = 0x1000050Cu;
    private const uint ConfigPageId = 0x10000213u;

    private const uint CloseButtonId = 0x10000210u;

    private const uint ExitToCharacterSelectionId = 0x10000203u;
    private const uint ConfigureKeyboardId = 0x10000204u;
    private const uint InGameHelpFilesId = 0x10000205u;
    private const uint UrgentAssistanceId = 0x10000206u;
    private const uint ReportAbuseId = 0x10000207u;
    private const uint UseMouseTurningSettingsId = 0x100005CCu;
    private const uint ExitGameId = 0x10000617u;

    private const uint ApplyButtonId = 0x100001FCu;
    private const uint ResetButtonId = 0x100001FDu;
    private const uint DefaultsButtonId = 0x100001FEu;

    public sealed record Callbacks(
        Action Toggle,
        Action RequestExitToCharacterSelection,
        Action ExitGame,
        Action UseMouseTurningSettings,
        Action<string> DisplaySystemMessage,
        Action? AfterApply = null,
        Action? OpenConfigureKeyboard = null)
    {
        public string UrgentAssistanceMessage { get; init; } =
            OptionsPanelText.UrgentAssistanceUnavailable;

        public string ReportAbuseMessage { get; init; } =
            OptionsPanelText.ReportAbuseUnavailable;
    }

    private readonly UiTabPanel _tabPanel;
    private readonly Dictionary<uint, OptionPage> _pages = new();
    private bool _disposed;

    public UiElement Root => _tabPanel;

    public UiTabPanel TabPanel => _tabPanel;

    public IReadOnlyDictionary<uint, OptionPage> Pages => _pages;

    public OptionPage GameplayPage => _pages[GameplayPageId];

    public OptionPage CharacterPage => _pages[CharacterPageId];

    public OptionPage ChatPage => _pages[ChatPageId];

    public OptionPage ConfigPage => _pages[ConfigPageId];

    /// <summary>True when the authored Gameplay Options page is active.</summary>
    public bool IsShowingGameplay =>
        _tabPanel.ActivePageElementId == GameplayPageId;

    public bool IsShowingCharacter =>
        _tabPanel.ActivePageElementId == CharacterPageId;

    public bool IsShowingConfiguration =>
        _tabPanel.ActivePageElementId == ConfigPageId;

    public void ShowGameplay() => _tabPanel.SwitchTo(GameplayPageId);

    public void ShowCharacter() => _tabPanel.SwitchTo(CharacterPageId);

    public void ShowConfiguration() => _tabPanel.SwitchTo(ConfigPageId);

    private OptionsPanelController(UiTabPanel tabPanel, Action? afterApply)
    {
        _tabPanel = tabPanel;

        _pages.Add(GameplayPageId, new OptionPage { AfterApply = null });
        foreach (uint pageId in new[] { CharacterPageId, ChatPageId, ConfigPageId })
        {
            var page = new OptionPage { AfterApply = afterApply };
            _pages.Add(pageId, page);
        }

        _tabPanel.ActivePageChanged += OnActivePageChanged;
    }

    public static OptionsPanelController? Bind(
        ImportedLayout layout,
        Callbacks callbacks,
        Func<uint, (uint tex, int w, int h)>? resolveSprite = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(callbacks);

        if (layout.Root is not UiTabPanel tabPanel)
        {
            Console.WriteLine(
                "[D.2b] OptionsPanelController.Bind: root did not build as UiTabPanel "
                + $"(actual type {layout.Root.GetType().Name}) — Options panel will not open.");
            return null;
        }

        var controller = new OptionsPanelController(tabPanel, callbacks.AfterApply);

        if (layout.FindElement(CloseButtonId) is UiButton close)
            close.OnClick = callbacks.Toggle;
        else
            Console.WriteLine(
                $"[D.2b] OptionsPanelController: close button 0x{CloseButtonId:X8} "
                + "not found in the built layout — its handler was not wired.");

        BindButton(layout, ExitToCharacterSelectionId, callbacks.RequestExitToCharacterSelection);
        BindButton(layout, ConfigureKeyboardId, callbacks.OpenConfigureKeyboard);
        BindButton(layout, UseMouseTurningSettingsId, callbacks.UseMouseTurningSettings);
        BindButton(layout, ExitGameId, callbacks.ExitGame);
        BindButton(layout, UrgentAssistanceId,
            () => callbacks.DisplaySystemMessage(callbacks.UrgentAssistanceMessage));
        BindButton(layout, ReportAbuseId,
            () => callbacks.DisplaySystemMessage(callbacks.ReportAbuseMessage));

        foreach (uint pageId in new[] { CharacterPageId, ChatPageId, ConfigPageId })
        {
            OptionPage page = controller._pages[pageId];
            UiElement? pageRoot = UiElement.FindDescendant(tabPanel, pageId);
            if (pageRoot is null) continue;

            UiButton? apply = BindPageButton(pageRoot, ApplyButtonId, page.Apply);
            UiButton? reset = BindPageButton(pageRoot, ResetButtonId, page.Reset);
            UiButton? defaults = BindPageButton(pageRoot, DefaultsButtonId, page.Defaults);

            AddFooterBacking(pageRoot, apply, reset, defaults, resolveSprite);

            if (apply is not null && reset is not null)
            {
                page.OnOptionChanged = () =>
                {
                    uint state = page.Changed
                        ? UiButtonStateMachine.Normal
                        : UiButtonStateMachine.Ghosted;
                    apply.TrySetRetailState(state);
                    reset.TrySetRetailState(state);
                };
                page.OnOptionChanged();
            }
        }

        return controller;
    }

    private static UiButton? BindPageButton(UiElement pageRoot, uint elementId, Action onClick)
    {
        if (UiElement.FindDescendant(pageRoot, elementId) is UiButton button)
        {
            button.OnClick = onClick;
            return button;
        }

        Console.WriteLine(
            $"[D.2b] OptionsPanelController: page 0x{pageRoot.DatElementId:X8}'s button "
            + $"0x{elementId:X8} not found — its handler was not wired.");
        return null;
    }

    private const int FooterBackingZOrder = int.MinValue / 2;

    private static void AddFooterBacking(
        UiElement pageRoot,
        UiButton? apply,
        UiButton? reset,
        UiButton? defaults,
        Func<uint, (uint tex, int w, int h)>? resolveSprite)
    {
        UiButton? first = apply ?? reset ?? defaults;
        if (first is null)
        {
            Console.WriteLine(
                $"[D.2b] OptionsPanelController: page 0x{pageRoot.DatElementId:X8} has no "
                + "resolved Apply/Reset/Defaults buttons — footer backing field skipped.");
            return;
        }

        var backing = new UiSolidSpriteFill
        {
            SpriteId = RetailChromeSprites.CenterFill,
            SpriteResolve = resolveSprite,
            Left = 0f,
            Top = first.Top,
            Width = pageRoot.Width,
            Height = first.Height,
            ZOrder = FooterBackingZOrder,
        };
        pageRoot.AddChild(backing);
    }

    public void ActivateTabs() => _tabPanel.ActivateTabBehavior();

    private static void BindButton(ImportedLayout layout, uint elementId, Action? onClick)
    {
        if (onClick is null) return;
        if (layout.FindElement(elementId) is UiButton button)
        {
            button.OnClick = () =>
            {
                Console.WriteLine($"[options] gameplay button 0x{elementId:X8} clicked — handler invoked");
                onClick();
            };
        }
        else
            Console.WriteLine(
                $"[D.2b] OptionsPanelController: Gameplay-tab button 0x{elementId:X8} "
                + "not found in the built layout — its handler was not wired.");
    }

    private void OnActivePageChanged(uint previousPageElementId, uint newPageElementId)
    {
        if (previousPageElementId != 0 && _pages.TryGetValue(previousPageElementId, out OptionPage? previous))
            previous.OnHidden();

        if (_pages.TryGetValue(newPageElementId, out OptionPage? next))
            next.OnShown();
    }

    public void OnHidden()
    {
        if (_pages.TryGetValue(_tabPanel.ActivePageElementId, out OptionPage? page))
            page.OnHidden();
    }

    public void OnShown()
    {
        if (_pages.TryGetValue(_tabPanel.ActivePageElementId, out OptionPage? page))
            page.OnShown();
    }

    public void OnServerOptionsSeeded()
    {
        if (_pages.TryGetValue(_tabPanel.ActivePageElementId, out OptionPage? page))
            page.ReloadFromLive();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _tabPanel.ActivePageChanged -= OnActivePageChanged;
    }
}
