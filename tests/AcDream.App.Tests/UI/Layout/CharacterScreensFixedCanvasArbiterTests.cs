using System.Numerics;
using AcDream.App.UI;
using AcDream.App.UI.Layout;
using AcDream.Core.CharGen;
using AcDream.Runtime;
using AcDream.Runtime.Session;

namespace AcDream.App.Tests.UI.Layout;

public sealed class CharacterScreensFixedCanvasArbiterTests
{
    private static readonly Vector2 AuthoredCanvas = new(800f, 600f);

    [Fact]
    public void CanvasStaysSetWhileEitherScreenIsActive_AndNullsOnlyWhenBothRevoke()
    {
        using var environment = new TwoControllerHarness();

        Assert.Equal(AuthoredCanvas, environment.Host.FixedCanvasSize);

        environment.Chargen.Controller.Open();
        Assert.Equal(AuthoredCanvas, environment.Host.FixedCanvasSize);

        environment.Chargen.Button(CharacterCreationUiController.ExitElementId)
            .OnClick!();
        environment.Chargen.ConfirmActiveDialog(confirmed: true);
        Assert.Equal(AuthoredCanvas, environment.Host.FixedCanvasSize);
        Assert.False(environment.Chargen.Controller.Root.Visible);

        environment.Management.Runtime.SetLifecycle(
            RuntimeCharacterSelectionLifecycle.InWorld);
        environment.Management.Controller.Tick();
        Assert.Null(environment.Host.FixedCanvasSize);
    }

    [Fact]
    public void CanvasNulls_WhenBothScreensRevokeAtWorldEntry()
    {
        using var environment = new TwoControllerHarness();
        environment.Chargen.Controller.Open();
        Assert.Equal(AuthoredCanvas, environment.Host.FixedCanvasSize);

        environment.Management.Runtime.SetLifecycle(
            RuntimeCharacterSelectionLifecycle.InWorld);
        environment.Management.Controller.Tick();
        environment.Chargen.Runtime.ProvideView = false;
        environment.Chargen.Controller.Tick();

        Assert.Null(environment.Host.FixedCanvasSize);
    }

    [Fact]
    public void CreateButtonClick_OpensChargen_AndExitConfirmReturnsToManagement()
    {
        using var environment = new TwoControllerHarness();

        Assert.True(environment.Management.Controller.Root.Visible);
        Assert.False(environment.Chargen.Controller.Root.Visible);

        UiButton create = environment.Management.Button(
            CharacterManagementUiController.CreateElementId);
        Assert.True(create.Enabled);
        create.OnClick!();
        environment.Chargen.Controller.Tick();

        Assert.True(environment.Chargen.Controller.Root.Visible);
        Assert.True(environment.Management.Controller.Root.Visible);
        Assert.False(environment.Chargen.Controller.Root.ClickThrough);
        Assert.True(
            environment.Chargen.Controller.Root.ZOrder
                > environment.Management.Controller.Root.ZOrder);

        environment.Chargen.Button(CharacterCreationUiController.ExitElementId)
            .OnClick!();
        environment.Chargen.ConfirmActiveDialog(confirmed: true);

        Assert.False(environment.Chargen.Controller.Root.Visible);
        // No separate "return" action was needed -- management was never
        // hidden, so it is simply what remains visible.
        Assert.True(environment.Management.Controller.Root.Visible);
    }


    private sealed class TwoControllerHarness : IDisposable
    {
        public TwoControllerHarness()
        {
            Host = new UiRoot { Width = 800f, Height = 600f };
            Management = new ManagementHarness(Host, () => Chargen!.Controller.Open());
            Chargen = new ChargenHarness(Host);
        }

        public UiRoot Host { get; }
        public ManagementHarness Management { get; }
        public ChargenHarness Chargen { get; }

        public void Dispose()
        {
            Chargen.Dispose();
            Management.Dispose();
        }
    }

    private sealed class ManagementHarness : IDisposable
    {
        private readonly RetailDialogFactory _dialogs;

        public ManagementHarness(UiRoot host, Action requestCreate)
        {
            Screen = BuildManagementScreen();
            Runtime = new ManagementFakeRuntime(requestCreate);
            _dialogs = new RetailDialogFactory(
                host,
                type => RetailDialogFactoryTests.BuildDialogLayout(type));
            Controller = Assert.IsType<CharacterManagementUiController>(
                CharacterManagementUiController.Bind(
                    host,
                    Screen,
                    static (_, _) => BuildRow(),
                    _dialogs,
                    Runtime.Bindings,
                    new CharacterManagementUiController.DialogStrings(
                        name => $"WARNING! {name}",
                        "DELETE",
                        "Please Wait",
                        "Entering World",
                        "Are you sure you want to leave?")));
        }

        public ImportedLayout Screen { get; }
        public ManagementFakeRuntime Runtime { get; }
        public CharacterManagementUiController Controller { get; }

        public UiButton Button(uint id) =>
            Assert.IsType<UiButton>(Screen.FindElement(id));

        public void Dispose()
        {
            Controller.Dispose();
            _dialogs.Dispose();
        }

        private static UiElement BuildRow() => LayoutImporter.Build(
            new ElementInfo { Id = 0x100003A5u, Type = 1u, Width = 160f, Height = 16f },
            _ => (0u, 0, 0),
            null).Root;

        private static ImportedLayout BuildManagementScreen()
        {
            var root = new ElementInfo
            {
                Id = CharacterManagementUiController.RootElementId,
                Type = 3u,
                Width = 800f,
                Height = 600f,
            };
            var list = new ElementInfo
            {
                Id = CharacterManagementUiController.ListElementId,
                Type = 5u,
                X = 42f,
                Y = 212f,
                Width = 160f,
                Height = 320f,
            };
            list.TemplateList.Add(new UiTemplateListEntry(0x21000004u, 0x100003A5u));
            root.Children.Add(list);
            root.Children.Add(new ElementInfo
            {
                Id = CharacterManagementUiController.WorldTextElementId,
                Type = 12u,
                Width = 193f,
                Height = 110f,
            });
            root.Children.Add(ButtonInfo(CharacterManagementUiController.CreateElementId));
            root.Children.Add(ButtonInfo(CharacterManagementUiController.EnterElementId));
            root.Children.Add(ButtonInfo(CharacterManagementUiController.DeleteElementId));
            root.Children.Add(ButtonInfo(CharacterManagementUiController.RestoreElementId));
            root.Children.Add(ButtonInfo(CharacterManagementUiController.CreditsElementId));
            root.Children.Add(ButtonInfo(CharacterManagementUiController.ExitElementId));
            return LayoutImporter.Build(root, _ => (0u, 0, 0), null);
        }
    }

    private sealed class ManagementFakeRuntime
    {
        private static readonly RuntimeGenerationToken Generation = new(11u);
        private readonly FakeManagementView _view = new();

        public ManagementFakeRuntime(Action requestCreate)
        {
            _view.Entries = [new RuntimeCharacterSelectionEntry(0, 0x50000001u, "Alpha", 0u)];
            _view.Snapshot = new RuntimeCharacterSelectionSnapshot(
                Generation,
                RuntimeCharacterSelectionLifecycle.AwaitingSelection,
                Revision: 1,
                AccountName: "account",
                SlotCount: 5,
                RosterCount: _view.Entries.Length,
                WorldName: "sawato",
                HighlightedCharacterId: 0x50000001u,
                HighlightedDisplayIndex: 0,
                PendingDeleteCharacterId: 0u,
                LastRestoreRequestedCharacterId: 0u,
                Operation: RuntimeCharacterSelectionOperation.None,
                Error: null,
                Buttons: new RuntimeCharacterSelectionButtons(true, true, false, true, false, true));
            Bindings = new CharacterSelectionRuntimeBindings(
                View: () => _view,
                Highlight: _ => Result(),
                Enter: Result,
                RequestDelete: Result,
                ConfirmDelete: Result,
                Restore: Result,
                Cancel: Result,
                RequestExit: () => { },
                RequestCreate: requestCreate);
        }

        public CharacterSelectionRuntimeBindings Bindings { get; }

        private static RuntimeCommandResult Result() =>
            new(RuntimeCommandStatus.Accepted, Generation);

        public void SetLifecycle(RuntimeCharacterSelectionLifecycle lifecycle)
        {
            RuntimeCharacterSelectionSnapshot current = _view.Snapshot;
            _view.Snapshot = current with { Lifecycle = lifecycle, Revision = current.Revision + 1 };
        }

        private sealed class FakeManagementView : IRuntimeCharacterSelectionView
        {
            public RuntimeCharacterSelectionEntry[] Entries { get; set; } = [];
            public RuntimeCharacterSelectionSnapshot Snapshot { get; set; }

            public bool TryGetAt(int displayIndex, out RuntimeCharacterSelectionEntry character)
            {
                if ((uint)displayIndex >= (uint)Entries.Length)
                {
                    character = default;
                    return false;
                }
                character = Entries[displayIndex];
                return true;
            }

            public bool TryGet(uint characterId, out RuntimeCharacterSelectionEntry character)
            {
                int index = Array.FindIndex(Entries, entry => entry.CharacterId == characterId);
                if (index < 0)
                {
                    character = default;
                    return false;
                }
                character = Entries[index];
                return true;
            }

            public void Visit(IRuntimeCharacterSelectionVisitor visitor)
            {
                foreach (RuntimeCharacterSelectionEntry character in Entries)
                    visitor.Visit(in character);
            }

            public IDisposable Subscribe(IRuntimeCharacterSelectionObserver observer) =>
                NullSubscription.Instance;

            private sealed class NullSubscription : IDisposable
            {
                public static readonly NullSubscription Instance = new();
                public void Dispose() { }
            }
        }
    }

    private sealed class ChargenHarness : IDisposable
    {
        private readonly List<ImportedLayout> _dialogLayouts = [];
        private readonly RetailDialogFactory _dialogs;

        public ChargenHarness(UiRoot host)
        {
            Screen = BuildChargenScreen();
            Runtime = new ChargenFakeRuntime();
            _dialogs = new RetailDialogFactory(host, type =>
            {
                ImportedLayout layout = RetailDialogFactoryTests.BuildDialogLayout(type);
                _dialogLayouts.Add(layout);
                return layout;
            });
            Controller = Assert.IsType<CharacterCreationUiController>(
                CharacterCreationUiController.CreateDetached(
                    host,
                    Screen,
                    static (_, _) => null,
                    _dialogs,
                    Runtime.Bindings,
                    new CharacterCreationUiController.DialogStrings(
                        "Are you sure you want to leave?",
                        "No name", "Unspent credits", "Randomize?", "Name too long")));
            Controller.AttachAndTick();
        }

        public ImportedLayout Screen { get; }
        public ChargenFakeRuntime Runtime { get; }
        public CharacterCreationUiController Controller { get; }

        public UiButton Button(uint id) =>
            Assert.IsType<UiButton>(Screen.FindElement(id));

        public void ConfirmActiveDialog(bool confirmed)
        {
            ImportedLayout dialog = _dialogLayouts[^1];
            uint buttonId = confirmed
                ? RetailConfirmationDialogView.AcceptButtonId
                : RetailConfirmationDialogView.RejectButtonId;
            UiButton button = Assert.IsType<UiButton>(dialog.FindElement(buttonId));
            button.OnClick!();
        }

        public void Dispose()
        {
            Controller.Dispose();
            _dialogs.Dispose();
        }

        private static ImportedLayout BuildChargenScreen()
        {
            var root = new ElementInfo
            {
                Id = CharacterCreationUiController.RootElementId,
                Type = 3u,
                Width = 800f,
                Height = 600f,
            };
            root.Children.Add(ContainerInfo(CharacterCreationUiController.ProgressBarElementId));
            root.Children.Add(ButtonInfo(CharacterCreationUiController.BackElementId));
            root.Children.Add(ButtonInfo(CharacterCreationUiController.NextElementId));
            root.Children.Add(ButtonInfo(CharacterCreationUiController.FinishElementId));
            root.Children.Add(ButtonInfo(CharacterCreationUiController.HelpElementId));
            root.Children.Add(ButtonInfo(CharacterCreationUiController.ExitElementId));
            root.Children.Add(ButtonInfo(CharacterCreationUiController.RandomElementId));
            root.Children.Add(ContainerInfo(CharacterCreationUiController.MasterPageElementId));
            root.Children.Add(ContainerInfo(CharacterCreationUiController.HeritagePageElementId));
            root.Children.Add(ContainerInfo(CharacterCreationUiController.ProfessionPageElementId));
            root.Children.Add(ContainerInfo(CharacterCreationUiController.SkillsPageElementId));
            root.Children.Add(ContainerInfo(CharacterCreationUiController.AppearancePageElementId));
            root.Children.Add(ContainerInfo(CharacterCreationUiController.TownPageElementId));
            root.Children.Add(ContainerInfo(CharacterCreationUiController.SummaryPageElementId));
            root.Children.Add(ButtonInfo(CharacterCreationUiController.HeritageTabElementId));
            root.Children.Add(ButtonInfo(CharacterCreationUiController.ProfessionTabElementId));
            root.Children.Add(ButtonInfo(CharacterCreationUiController.SkillsTabElementId));
            root.Children.Add(ButtonInfo(CharacterCreationUiController.AppearanceTabElementId));
            root.Children.Add(ButtonInfo(CharacterCreationUiController.TownTabElementId));
            root.Children.Add(ButtonInfo(CharacterCreationUiController.SummaryTabElementId));
            return LayoutImporter.Build(root, _ => (0u, 0, 0), null);
        }

        private static ElementInfo ContainerInfo(uint id) =>
            new() { Id = id, Type = 3u, Width = 200f, Height = 60f };
    }

    private sealed class ChargenFakeRuntime
    {
        private static readonly RuntimeGenerationToken Generation = new(13u);

        public ChargenFakeRuntime()
        {
            View = new FakeChargenView();
            Bindings = new CharacterCreationRuntimeBindings(
                () => ProvideView ? View : null,
                _ => Result(),
                _ => Result(),
                _ => Result(),
                (_, _) => Result(),
                (_, _) => Result(),
                _ => Result(),
                _ => Result(),
                _ => Result(),
                _ => Result(),
                _ => Result(),
                RequestExit: () => { },
                ResolveText: _ => null,
                OpenOnStart: false);
        }

        public FakeChargenView View { get; }
        public CharacterCreationRuntimeBindings Bindings { get; }
        public bool ProvideView { get; set; } = true;

        private static RuntimeCommandResult Result() =>
            new(RuntimeCommandStatus.Accepted, Generation);

        public sealed class FakeChargenView : IRuntimeCharacterCreationView
        {
            public RuntimeCharacterCreationSnapshot Snapshot { get; set; } =
                new(
                    Generation,
                    IsActive: true,
                    Revision: 1,
                    HeritageId: 0u,
                    GenderKey: 0u,
                    Appearance: RuntimeCharacterCreationAppearance.Default,
                    Template: RuntimeCharacterCreationSnapshot.TemplateUnset,
                    Attributes: default,
                    AttributeLockMask: 0u,
                    TotalAttributeCredits: 0u,
                    RemainingAttributeCredits: 0,
                    TotalSkillCredits: 0u,
                    RemainingSkillCredits: 0,
                    Name: string.Empty,
                    StartArea: -1,
                    Slot: 0u,
                    VerificationPending: false,
                    LastLocalRefusal: default,
                    LastRejection: null,
                    LastCreated: null);

            public ChargenOptions Options => ChargenOptions.Empty;

            public ChargenSkillAdvancementClass GetSkillLevel(uint skillId) =>
                ChargenSkillAdvancementClass.Inactive;

            public IDisposable Subscribe(IRuntimeCharacterCreationObserver observer) =>
                NullSubscription.Instance;

            private sealed class NullSubscription : IDisposable
            {
                public static readonly NullSubscription Instance = new();
                public void Dispose() { }
            }
        }
    }

    private static ElementInfo ButtonInfo(uint id) =>
        new() { Id = id, Type = 1u, Width = 100f, Height = 30f };
}
