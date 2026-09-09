using System.Numerics;
using AcDream.Core.CharGen;
using AcDream.Core.Net.Messages;
using AcDream.Runtime;
using AcDream.Runtime.Session;

namespace AcDream.App.UI.Layout;

public sealed record CharacterCreationRuntimeBindings(
    Func<IRuntimeCharacterCreationView?> View,
    Func<uint, RuntimeCommandResult> SelectHeritage,
    Func<uint, RuntimeCommandResult> SelectGender,
    Func<uint, RuntimeCommandResult> SelectTemplate,
    Func<ChargenAttributeId, int, RuntimeCommandResult> SetAttribute,
    Func<ChargenAttributeId, bool, RuntimeCommandResult> SetAttributeLock,
    Func<uint, RuntimeCommandResult> TrainSkill,
    Func<uint, RuntimeCommandResult> SpecializeSkill,
    Func<uint, RuntimeCommandResult> UntrainSkill,
    Func<int, RuntimeCommandResult> SelectStartArea,
    Func<bool, RuntimeCommandResult> Finish,
    Action RequestExit,
    Func<ChargenAppearanceSlot, uint, RuntimeCommandResult>? SetAppearanceIndex = null,
    /// <summary>CC6b-MOUNT: the Appearance page's shade scrollbar.</summary>
    Func<ChargenShadeSlot, double, RuntimeCommandResult>? SetShade = null,
    Func<string, string?>? ResolveText = null,
    Func<string, RuntimeCommandResult>? SetName = null,
    Func<RuntimeCommandResult>? AcknowledgeRejection = null,
    Func<RuntimeCommandResult>? RandomizeCharacter = null,
    /// <summary>CC5: the Appearance page's Random button on its Face
    /// sub-tab.</summary>
    Func<RuntimeCommandResult>? RandomizeAppearance = null,
    Func<RuntimeCommandResult>? RandomizeClothing = null,
    Func<uint, ChargenAttributeValues, ChargenSkillAdvancementClass, uint>? GetSkillScore = null,
    bool OpenOnStart = false);

internal sealed class CharacterCreationUiController : IDisposable
{
    internal const uint RootEnum = 0x10000039u;
    internal const uint RootElementId = 0x100003CCu;
    internal const uint ProgressBarElementId = 0x100003CEu;
    internal const uint BackElementId = 0x100003C6u;
    internal const uint NextElementId = 0x100003C7u;
    internal const uint FinishElementId = 0x100003C8u;
    internal const uint HelpElementId = 0x100003C9u;
    internal const uint ExitElementId = 0x100003CAu;
    internal const uint RandomElementId = 0x100003CBu;
    internal const uint MasterPageElementId = 0x100003D0u;
    internal const uint HeritagePageElementId = 0x100003D1u;
    internal const uint ProfessionPageElementId = 0x100003D2u;
    internal const uint SkillsPageElementId = 0x100003D3u;
    internal const uint AppearancePageElementId = 0x100003D4u;
    internal const uint TownPageElementId = 0x100003D5u;
    internal const uint SummaryPageElementId = 0x100003D6u;
    internal const uint HeritageTabElementId = 0x100003EFu;
    internal const uint ProfessionTabElementId = 0x100003F0u;
    internal const uint SkillsTabElementId = 0x100003F1u;
    internal const uint AppearanceTabElementId = 0x100003F2u;
    internal const uint TownTabElementId = 0x100003F3u;
    internal const uint SummaryTabElementId = 0x100003F4u;

    internal enum Page
    {
        Heritage = 1,
        Profession = 2,
        Skills = 3,
        Appearance = 4,
        Town = 5,
        Summary = 6,
    }

    internal sealed record DialogStrings(
        string ExitWarning,
        string NoNameWarning,
        string CreditWarning,
        string RandomizeWarning,
        string NameTooLong);

    private readonly UiRoot _host;
    private readonly ImportedLayout _layout;
    private readonly UiElement _progressBar;
    private readonly UiButton _back;
    private readonly UiButton _next;
    private readonly UiButton _finish;
    private readonly UiButton _help;
    private readonly UiButton _exit;
    private readonly UiButton _random;
    private readonly UiElement _masterPage;
    private readonly UiElement _heritagePageRoot;
    private readonly UiElement _professionPageRoot;
    private readonly UiElement _skillsPageRoot;
    private readonly UiElement _appearancePageRoot;
    private readonly UiElement _townPageRoot;
    private readonly UiElement _summaryPageRoot;
    private readonly UiButton _heritageTab;
    private readonly UiButton _professionTab;
    private readonly UiButton _skillsTab;
    private readonly UiButton _appearanceTab;
    private readonly UiButton _townTab;
    private readonly UiButton _summaryTab;
    private readonly RetailDialogFactory _dialogs;
    private readonly CharacterCreationRuntimeBindings _bindings;
    private readonly DialogStrings _strings;
    private readonly CharacterCreationHeritagePage _heritagePage;
    private readonly CharacterCreationProfessionPage _professionPage;
    private readonly CharacterCreationSkillsPage _skillsPage;
    private readonly CharacterCreationTownPage _townPage;
    private readonly CharacterCreationAppearancePage _appearancePage;
    private readonly CharacterCreationSummaryPage _summaryPage;

    private Vector2 _authoredCanvas;
    private RuntimeGenerationToken _lastGeneration;
    private long _lastRevision = long.MinValue;
    private Page _currentPage = Page.Heritage;
    private bool _active;
    private bool _isOpen;
    private bool _openOnStartConsumed;
    private uint _exitDialogContext;
    private uint _creditWarningDialogContext;
    private uint _randomizeWarningDialogContext;
    private uint _noNameWarningDialogContext;
    private uint _errorMessageDialogContext;
    private RuntimeCharacterCreationRejection? _lastShownRejection;
    private bool _suppressDialogCallbacks;
    private bool _disposed;

    private CharacterCreationUiController(
        UiRoot host,
        ImportedLayout layout,
        UiElement progressBar,
        UiButton back,
        UiButton next,
        UiButton finish,
        UiButton help,
        UiButton exit,
        UiButton random,
        UiElement masterPage,
        UiElement heritagePageRoot,
        UiElement professionPageRoot,
        UiElement skillsPageRoot,
        UiElement appearancePageRoot,
        UiElement townPageRoot,
        UiElement summaryPageRoot,
        UiButton heritageTab,
        UiButton professionTab,
        UiButton skillsTab,
        UiButton appearanceTab,
        UiButton townTab,
        UiButton summaryTab,
        Func<uint, uint, UiElement?> templateResolver,
        RetailDialogFactory dialogs,
        CharacterCreationRuntimeBindings bindings,
        DialogStrings strings)
    {
        _host = host;
        _layout = layout;
        _progressBar = progressBar;
        _back = back;
        _next = next;
        _finish = finish;
        _help = help;
        _exit = exit;
        _random = random;
        _masterPage = masterPage;
        _heritagePageRoot = heritagePageRoot;
        _professionPageRoot = professionPageRoot;
        _skillsPageRoot = skillsPageRoot;
        _appearancePageRoot = appearancePageRoot;
        _townPageRoot = townPageRoot;
        _summaryPageRoot = summaryPageRoot;
        _heritageTab = heritageTab;
        _professionTab = professionTab;
        _skillsTab = skillsTab;
        _appearanceTab = appearanceTab;
        _townTab = townTab;
        _summaryTab = summaryTab;
        _dialogs = dialogs;
        _bindings = bindings;
        _strings = strings;

        Root.Left = 0f;
        Root.Top = 0f;
        Root.ClickThrough = false;
        Root.Visible = false;
        _authoredCanvas = new Vector2(
            Root.Width > 0f ? Root.Width : 800f,
            Root.Height > 0f ? Root.Height : 600f);

        _heritagePage = new CharacterCreationHeritagePage(heritagePageRoot, bindings, ApplyHeritageTabRestore);
        _professionPage = new CharacterCreationProfessionPage(professionPageRoot, bindings);
        _skillsPage = new CharacterCreationSkillsPage(skillsPageRoot, bindings, templateResolver);
        _townPage = new CharacterCreationTownPage(townPageRoot, bindings);
        _appearancePage = new CharacterCreationAppearancePage(appearancePageRoot, bindings);
        _summaryPage = new CharacterCreationSummaryPage(
            summaryPageRoot, bindings, dialogs, strings.NameTooLong, templateResolver);

        _back.OnClick = OnBack;
        _next.OnClick = OnNext;
        _finish.OnClick = OnFinish;
        _help.OnClick = null;
        _exit.OnClick = OnExit;
        _random.OnClick = OnRandom;
        _heritageTab.OnClick = () => ApplyProgressState(Page.Heritage);
        _professionTab.OnClick = () => ApplyProgressState(Page.Profession);
        _skillsTab.OnClick = () => ApplyProgressState(Page.Skills);
        _appearanceTab.OnClick = () => ApplyProgressState(Page.Appearance);
        _townTab.OnClick = () => ApplyProgressState(Page.Town);
        _summaryTab.OnClick = () => ApplyProgressState(Page.Summary);

    }

    internal UiElement Root => _layout.Root;

    internal UiViewport? AppearanceViewport => _appearancePage.Viewport;

    internal AcDream.App.Rendering.IChargenPreviewControl? AppearancePreviewControl
    {
        get => _appearancePage.PreviewControl;
        set => _appearancePage.PreviewControl = value;
    }

    internal IChargenPalSetSource? AppearancePalSetSource
    {
        get => _appearancePage.PalSetSource;
        set => _appearancePage.PalSetSource = value;
    }

    internal IChargenClothingTableSource? AppearanceClothingTableSource
    {
        get => _appearancePage.ClothingTableSource;
        set => _appearancePage.ClothingTableSource = value;
    }

    internal IChargenPaletteColorSource? AppearancePaletteColorSource
    {
        get => _appearancePage.PaletteColorSource;
        set => _appearancePage.PaletteColorSource = value;
    }

    internal IChargenSwatchTextureSource? AppearanceSwatchTextureSource
    {
        get => _appearancePage.SwatchTextureSource;
        set => _appearancePage.SwatchTextureSource = value;
    }

    internal bool IsAppearancePageVisible => Root.Visible && _appearancePageRoot.Visible;

    internal UiViewport? SummaryViewport => _summaryPage.Viewport;

    internal AcDream.App.Rendering.IChargenPreviewControl? SummaryPreviewControl
    {
        get => _summaryPage.PreviewControl;
        set => _summaryPage.PreviewControl = value;
    }

    internal bool IsSummaryPageVisible => Root.Visible && _summaryPageRoot.Visible;

    internal static CharacterCreationUiController? CreateDetached(
        UiRoot host,
        ImportedLayout layout,
        Func<uint, uint, UiElement?> templateResolver,
        RetailDialogFactory dialogs,
        CharacterCreationRuntimeBindings bindings,
        DialogStrings strings)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(templateResolver);
        ArgumentNullException.ThrowIfNull(dialogs);
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(strings);

        if (layout.Root.DatElementId != RootElementId
            || layout.FindElement(ProgressBarElementId) is not { } progressBar
            || layout.FindElement(BackElementId) is not UiButton back
            || layout.FindElement(NextElementId) is not UiButton next
            || layout.FindElement(FinishElementId) is not UiButton finish
            || layout.FindElement(HelpElementId) is not UiButton help
            || layout.FindElement(ExitElementId) is not UiButton exit
            || layout.FindElement(RandomElementId) is not UiButton random
            || layout.FindElement(MasterPageElementId) is not { } masterPage
            || layout.FindElement(HeritagePageElementId) is not { } heritagePageRoot
            || layout.FindElement(ProfessionPageElementId) is not { } professionPageRoot
            || layout.FindElement(SkillsPageElementId) is not { } skillsPageRoot
            || layout.FindElement(AppearancePageElementId) is not { } appearancePageRoot
            || layout.FindElement(TownPageElementId) is not { } townPageRoot
            || layout.FindElement(SummaryPageElementId) is not { } summaryPageRoot
            || layout.FindElement(HeritageTabElementId) is not UiButton heritageTab
            || layout.FindElement(ProfessionTabElementId) is not UiButton professionTab
            || layout.FindElement(SkillsTabElementId) is not UiButton skillsTab
            || layout.FindElement(AppearanceTabElementId) is not UiButton appearanceTab
            || layout.FindElement(TownTabElementId) is not UiButton townTab
            || layout.FindElement(SummaryTabElementId) is not UiButton summaryTab)
        {
            Console.WriteLine(
                "[UI] character creation: the authored root/master-shell contract is incomplete.");
            return null;
        }

        return new CharacterCreationUiController(
            host,
            layout,
            progressBar,
            back,
            next,
            finish,
            help,
            exit,
            random,
            masterPage,
            heritagePageRoot,
            professionPageRoot,
            skillsPageRoot,
            appearancePageRoot,
            townPageRoot,
            summaryPageRoot,
            heritageTab,
            professionTab,
            skillsTab,
            appearanceTab,
            townTab,
            summaryTab,
            templateResolver,
            dialogs,
            bindings,
            strings);
    }

    internal void AttachAndTick()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Root.Parent is null)
            _host.AddChild(Root);
        Tick();
    }

    internal void Tick()
    {
        if (_disposed)
            return;

        IRuntimeCharacterCreationView? view = _bindings.View();
        RuntimeCharacterCreationSnapshot snapshot = view?.Snapshot ?? default;
        if (view is null || !snapshot.IsActive)
        {
            Deactivate();
            _lastGeneration = snapshot.Generation;
            _lastRevision = snapshot.Revision;
            return;
        }

        if (!_active)
        {
            _active = true;
            if (_bindings.OpenOnStart && !_openOnStartConsumed)
            {
                _openOnStartConsumed = true;
                Open();
            }
        }

        if (_isOpen)
        {
            Root.Visible = true;
            _host.BringToFront(Root);
        }
        else
        {
            Root.Visible = false;
        }

        if (_lastGeneration != snapshot.Generation
            || _lastRevision != snapshot.Revision)
        {
            _heritagePage.Refresh(view, snapshot);
            _professionPage.Refresh(view, snapshot);
            _skillsPage.Refresh(view, snapshot);
            _townPage.Refresh(view, snapshot);
            _appearancePage.Refresh(view, snapshot);
            _summaryPage.Refresh(view, snapshot);
            _lastGeneration = snapshot.Generation;
            _lastRevision = snapshot.Revision;
        }

        ReconcileDialogs(snapshot);
    }

    internal void Open()
    {
        if (_disposed)
            return;
        _isOpen = true;
        _host.DeclareFixedCanvas(this, _authoredCanvas);
        RollOpeningCharacter();
        ApplyProgressState(Page.Heritage);
    }

    private void RollOpeningCharacter()
    {
        if (_bindings.RandomizeCharacter?.Invoke().Status != RuntimeCommandStatus.Accepted)
            return;

        uint gender = _bindings.View()?.Snapshot.GenderKey ?? 0u;
        if (gender == 1u)
            _bindings.SelectGender(2u);
        else if (gender == 2u)
            _bindings.SelectGender(1u);
    }

    private void Close()
    {
        if (!_isOpen)
            return;
        _isOpen = false;
        Root.Visible = false;
        _host.RevokeFixedCanvas(this);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        try
        {
            CloseAllDialogs(suppressCallbacks: true);
        }
        finally
        {
            _host.RevokeFixedCanvas(this);
            _back.OnClick = null;
            _next.OnClick = null;
            _finish.OnClick = null;
            _help.OnClick = null;
            _exit.OnClick = null;
            _random.OnClick = null;
            _heritageTab.OnClick = null;
            _professionTab.OnClick = null;
            _skillsTab.OnClick = null;
            _appearanceTab.OnClick = null;
            _townTab.OnClick = null;
            _summaryTab.OnClick = null;
            _heritagePage.Dispose();
            _professionPage.Dispose();
            _skillsPage.Dispose();
            _townPage.Dispose();
            _appearancePage.Dispose();
            _summaryPage.Dispose();
            _host.RemoveChild(Root);
        }
    }


    private void OnBack()
    {
        if (_disposed)
            return;
        if (_currentPage <= Page.Heritage)
        {
            OnExit();
            return;
        }
        ApplyProgressState(_currentPage - 1);
    }

    private void OnNext()
    {
        if (_disposed)
            return;
        if (_currentPage < Page.Summary)
            ApplyProgressState(_currentPage + 1);
    }

    private void OnExit()
    {
        if (_disposed)
            return;
        if (_exitDialogContext != 0u)
            return;

        _exitDialogContext = _dialogs.MakeConfirmation(
            _strings.ExitWarning,
            data =>
            {
                _exitDialogContext = 0u;
                if (_disposed || _suppressDialogCallbacks)
                    return;

                if (data.GetBoolean(RetailDialogProperty.ConfirmationResult))
                {
                    Close();
                    _bindings.RequestExit();
                }
            });
    }

    private void OnRandom()
    {
        if (_disposed)
            return;

        IRuntimeCharacterCreationView? view = _bindings.View();
        if (view is null)
            return;
        RuntimeCharacterCreationSnapshot snapshot = view.Snapshot;

        switch (_currentPage)
        {
            case Page.Heritage:
                _heritagePage.Randomize(snapshot);
                break;
            case Page.Profession:
                _professionPage.Randomize(snapshot);
                break;
            case Page.Appearance:
                _appearancePage.Randomize();
                break;
            case Page.Town:
                _townPage.Randomize(view);
                break;
            case Page.Summary:
                ShowRandomizeWarningDialog();
                break;
        }
    }

    private void ShowRandomizeWarningDialog()
    {
        if (_randomizeWarningDialogContext != 0u)
            return;

        _randomizeWarningDialogContext = _dialogs.MakeConfirmation(
            _strings.RandomizeWarning,
            data =>
            {
                _randomizeWarningDialogContext = 0u;
                if (_disposed || _suppressDialogCallbacks)
                    return;
                if (data.GetBoolean(RetailDialogProperty.ConfirmationResult))
                    _bindings.RandomizeCharacter?.Invoke();
            });
    }


    private void ApplyProgressState(Page target)
    {
        _heritagePageRoot.Visible = false;
        _professionPageRoot.Visible = false;
        _skillsPageRoot.Visible = false;
        _appearancePageRoot.Visible = false;
        _townPageRoot.Visible = false;
        _summaryPageRoot.Visible = false;
        _next.Visible = true;
        _finish.Visible = false;

        Page previous = _currentPage;
        _currentPage = target;
        _heritageTab.Selected = false;
        _professionTab.Selected = false;
        _skillsTab.Selected = false;
        _appearanceTab.Selected = false;
        _townTab.Selected = false;
        _summaryTab.Selected = false;

        uint heritageId = _bindings.View()?.Snapshot.HeritageId ?? 0u;
        bool isOlthoi = heritageId == (uint)ChargenHeritageGroup.Olthoi
            || heritageId == (uint)ChargenHeritageGroup.OlthoiAcid;
        if (isOlthoi)
        {
            _professionTab.Visible = false;
            _skillsTab.Visible = false;
            _townTab.Visible = false;
            if (_currentPage < previous)
            {
                if (_currentPage is Page.Profession or Page.Skills)
                    _currentPage = Page.Heritage;
                else if (_currentPage == Page.Town)
                    _currentPage = Page.Appearance;
            }
            else
            {
                if (_currentPage is Page.Profession or Page.Skills)
                    _currentPage = Page.Appearance;
                else if (_currentPage == Page.Town)
                    _currentPage = Page.Summary;
            }
        }
        else
        {
            _professionTab.Visible = true;
            _skillsTab.Visible = true;
            _townTab.Visible = true;
        }

        SetMasterPageState(0x10000025u + (uint)_currentPage - 1u);
        switch (_currentPage)
        {
            case Page.Heritage:
                _heritagePageRoot.Visible = true;
                _heritageTab.Selected = true;
                break;
            case Page.Profession:
                _professionPageRoot.Visible = true;
                _professionTab.Selected = true;
                break;
            case Page.Skills:
                _skillsPageRoot.Visible = true;
                _skillsTab.Selected = true;
                break;
            case Page.Appearance:
                _appearancePageRoot.Visible = true;
                _appearanceTab.Selected = true;
                break;
            case Page.Town:
                _townPageRoot.Visible = true;
                _townTab.Selected = true;
                break;
            case Page.Summary:
                _summaryPageRoot.Visible = true;
                _summaryTab.Selected = true;
                _next.Visible = false;
                _finish.Visible = true;
                break;
        }

        _random.Enabled = _currentPage is not Page.Skills;
        _finish.Enabled = _currentPage == Page.Summary;

        _lastRevision = long.MinValue;
        Tick();
    }

    private void SetMasterPageState(uint stateId)
    {
        if (_masterPage is IUiDatStateful stateful)
            stateful.TrySetRetailState(stateId);
    }


    private static readonly IReadOnlySet<uint> HeritageTabShowButtonIds = new HashSet<uint>
    {
        0x100003BFu, 0x100003C1u, 0x100003C2u, 0x100003C3u,
        0x10000590u, 0x10000591u, 0x100005A9u, 0x100005BFu,
        0x100005C4u, 0x100005E8u,
    };

    private static readonly IReadOnlySet<uint> HeritageTabHideButtonIds = new HashSet<uint>
    {
        0x100005C7u, 0x100005C8u,
    };

    private void ApplyHeritageTabRestore(uint buttonElementId)
    {
        if (HeritageTabShowButtonIds.Contains(buttonElementId))
        {
            _professionTab.Visible = true;
            _skillsTab.Visible = true;
            _townTab.Visible = true;
        }
        else if (HeritageTabHideButtonIds.Contains(buttonElementId))
        {
            _professionTab.Visible = false;
            _skillsTab.Visible = false;
            _townTab.Visible = false;
        }
    }


    private void OnFinish()
    {
        if (_disposed || _currentPage != Page.Summary)
            return;
        TryFinish(confirmedUnspentCredits: false);
    }

    private void TryFinish(bool confirmedUnspentCredits)
    {
        if (_bindings.Finish(confirmedUnspentCredits).Status != RuntimeCommandStatus.Rejected)
            return;

        RuntimeCharacterCreationLocalRefusal refusal =
            _bindings.View()?.Snapshot.LastLocalRefusal ?? default;
        if (refusal.NoName)
            ShowNoNameWarningDialog();
        else if (refusal.AttributeCreditsUnspent)
            ShowCreditWarningDialog();
    }

    private void ShowNoNameWarningDialog()
    {
        if (_noNameWarningDialogContext != 0u)
            return;
        _noNameWarningDialogContext = _dialogs.MakeMessage(
            _strings.NoNameWarning,
            data =>
            {
                _ = data;
                _noNameWarningDialogContext = 0u;
            });
    }

    private void ShowCreditWarningDialog()
    {
        if (_creditWarningDialogContext != 0u)
            return;
        _creditWarningDialogContext = _dialogs.MakeConfirmation(
            _strings.CreditWarning,
            data =>
            {
                _creditWarningDialogContext = 0u;
                if (_disposed || _suppressDialogCallbacks)
                    return;
                if (data.GetBoolean(RetailDialogProperty.ConfirmationResult))
                    TryFinish(confirmedUnspentCredits: true);
            });
    }


    private void ReconcileDialogs(RuntimeCharacterCreationSnapshot snapshot)
    {
        RuntimeCharacterCreationRejection? rejection = snapshot.LastRejection;
        if (rejection is null)
        {
            _lastShownRejection = null;
            return;
        }
        if (_lastShownRejection == rejection)
            return;
        _lastShownRejection = rejection;

        if (_errorMessageDialogContext != 0u)
            return;

        string key = rejection.Value.Code switch
        {
            CharGenVerificationResponse.Code.NameInUse => "ID_Character_Err_NameReserved",
            CharGenVerificationResponse.Code.NameBanned => "ID_Character_Err_NameBanned",
            CharGenVerificationResponse.Code.AdminPrivilegeDenied => "ID_Character_Err_NameAdminDenied",
            _ => "ID_Character_Err_NameDBDown",
        };
        string? message = _bindings.ResolveText?.Invoke(key);
        if (message is null)
            return;

        _errorMessageDialogContext = _dialogs.MakeMessage(message, data =>
        {
            _errorMessageDialogContext = 0u;
            _ = data;
            if (_disposed || _suppressDialogCallbacks)
                return;
            _bindings.AcknowledgeRejection?.Invoke();
        });
    }

    private void Deactivate()
    {
        if (_active)
        {
            _active = false;
            _openOnStartConsumed = false;
            Close();
        }
        CloseAllDialogs(suppressCallbacks: true);
    }

    private void CloseAllDialogs(bool suppressCallbacks)
    {
        bool previous = _suppressDialogCallbacks;
        _suppressDialogCallbacks |= suppressCallbacks;
        try
        {
            if (_exitDialogContext != 0u)
            {
                uint closing = _exitDialogContext;
                _exitDialogContext = 0u;
                _dialogs.CloseDialog(closing);
            }
            if (_creditWarningDialogContext != 0u)
            {
                uint closing = _creditWarningDialogContext;
                _creditWarningDialogContext = 0u;
                _dialogs.CloseDialog(closing);
            }
            if (_randomizeWarningDialogContext != 0u)
            {
                uint closing = _randomizeWarningDialogContext;
                _randomizeWarningDialogContext = 0u;
                _dialogs.CloseDialog(closing);
            }
            if (_noNameWarningDialogContext != 0u)
            {
                uint closing = _noNameWarningDialogContext;
                _noNameWarningDialogContext = 0u;
                _dialogs.CloseDialog(closing);
            }
            if (_errorMessageDialogContext != 0u)
            {
                uint closing = _errorMessageDialogContext;
                _errorMessageDialogContext = 0u;
                _dialogs.CloseDialog(closing);
            }
        }
        finally
        {
            _suppressDialogCallbacks = previous;
        }
    }
}
