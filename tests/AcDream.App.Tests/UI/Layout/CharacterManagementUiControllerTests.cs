using System.Numerics;
using System.Reflection;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Tests.Rendering.Gpu;
using AcDream.App.UI;
using AcDream.App.UI.Layout;
using AcDream.Runtime;
using AcDream.Runtime.Session;

namespace AcDream.App.Tests.UI.Layout;

public sealed class CharacterManagementUiControllerTests
{
    [Fact]
    public void DirectCharacterLaunchSkipsSelectionAndEntryPresentationButShowsSelectionAfterLogout()
    {
        using var environment = new EnvironmentHarness(directCharacterLaunch: true);
        Assert.False(environment.Controller.Root.Visible);
        Assert.Null(environment.Host.FixedCanvasSize);
        environment.Runtime.SetLifecycle(RuntimeCharacterSelectionLifecycle.EnteringWorld);
        environment.Controller.Tick();
        Assert.False(environment.Controller.Root.Visible);
        Assert.False(environment.Dialogs.IsOpen);
        environment.Runtime.SetLifecycle(RuntimeCharacterSelectionLifecycle.InWorld);
        environment.Controller.Tick(presentationBlocked: true);
        environment.Controller.ResetSession();
        environment.Runtime.SetLifecycle(RuntimeCharacterSelectionLifecycle.AwaitingSelection);
        environment.Controller.Tick();
        Assert.True(environment.Controller.Root.Visible);
        Assert.Equal(new Vector2(800f, 600f), environment.Host.FixedCanvasSize);

        environment.Runtime.SetLifecycle(RuntimeCharacterSelectionLifecycle.Connecting);
        environment.Controller.Tick();
        environment.Runtime.SetLifecycle(RuntimeCharacterSelectionLifecycle.EnteringWorld);
        environment.Controller.Tick();
        Assert.False(environment.Controller.Root.Visible);
    }

    [Fact]
    public void DirectCharacterEntryErrorRestoresSelectionAndItsErrorDialog()
    {
        using var environment = new EnvironmentHarness(directCharacterLaunch: true);
        environment.Runtime.SetLifecycle(RuntimeCharacterSelectionLifecycle.EnteringWorld);
        environment.Controller.Tick();
        environment.Runtime.SetError("That character is unavailable.");
        environment.Controller.Tick();
        Assert.True(environment.Controller.Root.Visible);
        Assert.NotEqual(0u, environment.Controller.ErrorDialogContext);
        Assert.Equal("That character is unavailable.", Message(environment.LastDialog(RetailDialogType.Message)));
    }

    [Fact]
    public void ConnectionPresentationBlockReleasesSelectionCanvasAndRestoresItWhenReady()
    {
        using var environment = new EnvironmentHarness();
        environment.Controller.Tick(presentationBlocked: true);
        Assert.False(environment.Controller.Root.Visible);
        Assert.Null(environment.Host.FixedCanvasSize);
        Assert.False(environment.Dialogs.IsOpen);
        environment.Controller.Tick();
        Assert.True(environment.Controller.Root.Visible);
        Assert.Equal(new Vector2(800f, 600f), environment.Host.FixedCanvasSize);
    }

    [Fact]
    public void VersionOverlay_UsesApplicationVersion_AtTheTopLeft_WithoutCapturingInput()
    {
        using var environment = new EnvironmentHarness();
        UiElement root = environment.Controller.Root;
        var version = Assert.IsType<UiText>(Assert.Single(root.Children,
            element => element.Name == "ClientVersion"));
        string expectedVersion = typeof(CharacterManagementUiController).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
            .InformationalVersion.Split('+')[0];

        Assert.Equal($"OpenAC {expectedVersion}", Assert.Single(version.LinesProvider()).Text);
        Assert.Equal(new Vector4(0.65f, 0.65f, 0.65f, 1f), Assert.Single(version.LinesProvider()).Color);
        Assert.Null(version.DatFont);
        Assert.Equal(new Vector2(8f, 6f), version.ScreenPosition);
        Assert.True(version.Left + version.Width <= root.Width);
        Assert.True(version.Top + version.Height <= root.Height);
        Assert.True(version.OneLine);
        Assert.True(version.Outline);
        Assert.False(version.HandlesClick);
        Assert.False(version.AcceptsFocus);
        Assert.Null(version.HitTest(1f, 1f));
        Assert.Same(root, version.Parent);
    }

    [Fact]
    [Trait("Lane", "SystemFont")]
    public void VersionOverlay_DrawsWithTheSuppliedMonospaceFontWithoutADefaultFont()
    {
        byte[] bytes = BitmapFont.TryLoadSystemMonospaceFont()
            ?? throw new InvalidOperationException("A system monospace font is required for this test.");
        using var device = new RecordingGpuDevice();
        using var font = new BitmapFont(device, bytes, 15f);
        using var environment = new EnvironmentHarness(versionFont: font);
        var text = Assert.IsType<UiText>(Assert.Single(environment.Controller.Root.Children,
            element => element.Name == "ClientVersion"));
        Assert.Same(font, text.Font);
        Assert.Null(text.DatFont);
        Assert.Equal(font.MeasureWidth("iii"), font.MeasureWidth("WWW"));
        using var renderer = new TextRenderer(device, new NullFrameSource(), "unused");
        renderer.Begin(new Vector2(800f, 600f));
        text.DrawSelfAndChildren(new UiRenderContext(renderer, new Vector2(800f, 600f)));
        Assert.True(renderer.DebugTextBuffer.VertexCount > 0);
        Assert.Equal(1f, renderer.DebugTextBuffer.Alpha);
    }

    private sealed class NullFrameSource : ICurrentGpuFrameSource
    {
        public IGpuFrame? CurrentFrame => null;
    }

    [Fact]
    public void VersionOverlay_FollowsScreenSuppression_WorldEntry_AndDisposal()
    {
        var environment = new EnvironmentHarness();
        UiElement root = environment.Controller.Root;
        UiElement version = Assert.Single(root.Children,
            element => element.Name == "ClientVersion");
        try
        {
            Assert.True(IsPresented(version));
            environment.Controller.SetPresentationSuppressed(true);
            Assert.False(IsPresented(version));
            environment.Controller.SetPresentationSuppressed(false);
            Assert.True(IsPresented(version));
            environment.Runtime.SetLifecycle(RuntimeCharacterSelectionLifecycle.InWorld);
            environment.Controller.Tick();
            Assert.False(IsPresented(version));
        }
        finally
        {
            environment.Dispose();
        }

        Assert.DoesNotContain(version, Descendants(environment.Host));

        static bool IsPresented(UiElement element)
        {
            for (UiElement? current = element; current is not null; current = current.Parent)
                if (!current.Visible)
                    return false;
            return true;
        }
    }

    [Fact]
    public void ActiveScreen_KeepsAuthoredRootExtent_AndSetsHostFixedCanvas()
    {
        var environment = new EnvironmentHarness();
        try
        {
            UiElement root = environment.Controller.Root;
            Assert.Equal(800f, root.Width);
            Assert.Equal(600f, root.Height);
            Assert.Equal(
                new Vector2(root.Width, root.Height),
                environment.Host.FixedCanvasSize);
        }
        finally
        {
            environment.Dispose();
        }

        Assert.Null(environment.Host.FixedCanvasSize);
    }

    [Fact]
    public void AuthoredChildContract_PreservesRuntimeOrderGreyTailHighlightAndButtonMatrix()
    {
        using var environment = new EnvironmentHarness();
        CharacterManagementUiController controller = environment.Controller;

        Assert.True(controller.Root.Visible);
        Assert.Equal(
            CharacterManagementUiController.RootElementId,
            controller.Root.DatElementId);
        Assert.IsType<UiTemplateListBox>(environment.Screen.FindElement(
            CharacterManagementUiController.ListElementId));
        UiButton create = environment.Button(
            CharacterManagementUiController.CreateElementId);
        UiButton enter = environment.Button(
            CharacterManagementUiController.EnterElementId);
        UiButton delete = environment.Button(
            CharacterManagementUiController.DeleteElementId);
        UiButton restore = environment.Button(
            CharacterManagementUiController.RestoreElementId);

        Assert.True(create.Visible);
        Assert.True(create.Enabled);
        Assert.NotNull(create.OnClick);
        create.OnClick!();
        Assert.Equal(1, environment.Runtime.RequestCreateCalls);
        Assert.True(enter.Enabled);
        Assert.True(delete.Visible);
        Assert.True(delete.Enabled);
        Assert.False(restore.Visible);
        Assert.False(restore.Enabled);

        Assert.Equal(
            ["Alpha", "Zulu", "Aaron (pending)"],
            controller.Rows.Select(static row => row.Label!).ToArray());
        Assert.All(controller.Rows, static row => Assert.Equal(64f, row.Height));
        Assert.Equal([0f, 64f, 128f],
            controller.Rows.Select(static row => row.Top).ToArray());
        Assert.True(controller.Rows[0].Selected);
        Assert.False(controller.Rows[1].Selected);
        Assert.Equal(Vector4.One, controller.Rows[0].LabelColor);
        Assert.Equal(new Vector4(1f, 0f, 0f, 1f), controller.Rows[2].LabelColor);
        Assert.DoesNotContain(
            Descendants(controller.Root),
            static element => element is UiViewport);

        controller.Rows[2].OnClick!();

        Assert.Equal(1, environment.Runtime.HighlightCalls);
        Assert.Equal(0x50000003u,
            environment.Runtime.View.Snapshot.HighlightedCharacterId);
        Assert.True(controller.Rows[2].Selected);
        Assert.False(enter.Enabled);
        Assert.False(delete.Visible);
        Assert.False(delete.Enabled);
        Assert.True(restore.Visible);
        Assert.True(restore.Enabled);
    }

    [Fact]
    public void CreateButton_GhostsWhenRosterReachesTheSlotCeiling_AndUnGhostsBelowIt()
    {
        using var environment = new EnvironmentHarness();
        CharacterManagementUiController controller = environment.Controller;
        UiButton create = environment.Button(
            CharacterManagementUiController.CreateElementId);

        RuntimeCharacterSelectionEntry[] full = Enumerable.Range(0, 5)
            .Select(index => new RuntimeCharacterSelectionEntry(
                index,
                (uint)(0x50000200 + index),
                $"Full {index:D2}",
                0u))
            .ToArray();
        environment.Runtime.ReplaceRoster(full, highlightedCharacterId: full[0].CharacterId);
        controller.Tick();

        Assert.True(create.Visible);
        Assert.False(create.Enabled);

        RuntimeCharacterSelectionEntry[] belowCeiling = full[..4];
        environment.Runtime.ReplaceRoster(
            belowCeiling,
            highlightedCharacterId: belowCeiling[0].CharacterId);
        controller.Tick();

        Assert.True(create.Enabled);
    }

    [Fact]
    public void WorldName_TicksFromSnapshot_IntoTheWorldTextElement()
    {
        using var environment = new EnvironmentHarness();
        CharacterManagementUiController controller = environment.Controller;
        var worldText = Assert.IsType<UiText>(environment.Screen.FindElement(
            CharacterManagementUiController.WorldTextElementId));

        Assert.Equal(
            "sawato",
            string.Join(" ", worldText.LinesProvider().Select(static line => line.Text)));

        environment.Runtime.SetWorldName("Frostfell");
        controller.Tick();

        Assert.Equal(
            "Frostfell",
            string.Join(" ", worldText.LinesProvider().Select(static line => line.Text)));
    }

    [Fact]
    public void CreditsButton_QueuesTheCreditsMode_AndPresentationCanBeSuppressedForIt()
    {
        int openCalls = 0;
        using var environment = new EnvironmentHarness(() => openCalls++);
        CharacterManagementUiController controller = environment.Controller;
        UiButton credits = environment.Button(
            CharacterManagementUiController.CreditsElementId);

        Assert.True(credits.Visible);
        Assert.True(credits.Enabled);
        Assert.NotNull(credits.OnClick);
        credits.OnClick!();
        Assert.Equal(1, openCalls);

        controller.SetPresentationSuppressed(true);
        Assert.False(controller.Root.Visible);
        Assert.Null(environment.Host.FixedCanvasSize);

        controller.SetPresentationSuppressed(false);
        Assert.True(controller.Root.Visible);
        Assert.Equal(new Vector2(800f, 600f), environment.Host.FixedCanvasSize);
        Assert.Equal(3, controller.Rows.Count);
    }

    [Fact]
    public void RowHeight_UsesAllowedSlotsAndClampsAtOneTenthForLargeRosters()
    {
        using var environment = new EnvironmentHarness();
        CharacterManagementUiController controller = environment.Controller;

        Assert.Equal(64, CharacterManagementUiController.ComputeRowHeight(
            320f,
            rosterCount: 3,
            allowedSlotCount: 5));
        Assert.Equal(63, CharacterManagementUiController.ComputeRowHeight(
            319f,
            rosterCount: 5,
            allowedSlotCount: 5));
        Assert.Equal(31, CharacterManagementUiController.ComputeRowHeight(
            319f,
            rosterCount: 11,
            allowedSlotCount: 5));

        RuntimeCharacterSelectionEntry[] large = Enumerable.Range(0, 12)
            .Select(index => new RuntimeCharacterSelectionEntry(
                index,
                (uint)(0x50000100 + index),
                $"Character {index:D2}",
                0u))
            .ToArray();
        environment.Runtime.ReplaceRoster(
            large,
            highlightedCharacterId: large[0].CharacterId);
        controller.Tick();

        Assert.Equal(12, controller.Rows.Count);
        Assert.All(controller.Rows, static row => Assert.Equal(32f, row.Height));
        Assert.Equal(
            Enumerable.Range(0, 12).Select(static index => index * 32f),
            controller.Rows.Select(static row => row.Top));
        UiTemplateListBox list = Assert.IsType<UiTemplateListBox>(
            environment.Screen.FindElement(
                CharacterManagementUiController.ListElementId));
        Assert.Equal(384, list.ContentHeight);
        Assert.Equal(32, list.LineHeight);
    }

    [Fact]
    public void MountCoordinator_RetriesCatalogRootAndStrings_ThenMountsOnce()
    {
        var host = new UiRoot();
        var runtime = new FakeRuntime();
        using var dialogs = new RetailDialogFactory(
            host,
            RetailDialogFactoryTests.BuildDialogLayout);
        bool catalogAvailable = false;
        bool rootAvailable = false;
        bool stringsAvailable = false;
        int dialogAttempts = 0;
        int resourceAttempts = 0;
        using var coordinator = new CharacterManagementUiMountCoordinator(
            host,
            runtime.Bindings,
            () =>
            {
                dialogAttempts++;
                return catalogAvailable ? dialogs : null;
            },
            () =>
            {
                resourceAttempts++;
                if (!rootAvailable || !stringsAvailable)
                    return null;
                return new CharacterManagementUiMountResources(
                    0x21000004u,
                    BuildScreen(),
                    static (layoutId, elementId) =>
                        layoutId == 0x21000004u
                        && elementId == 0x100003A5u
                            ? BuildRow()
                            : null,
                    TestStrings());
            });

        coordinator.Tick();
        Assert.Null(coordinator.Controller);
        Assert.Equal(1, dialogAttempts);
        Assert.Equal(0, resourceAttempts);

        catalogAvailable = true;
        coordinator.Tick();
        Assert.Null(coordinator.Controller);
        Assert.Equal(2, dialogAttempts);
        Assert.Equal(1, resourceAttempts);

        rootAvailable = true;
        coordinator.Tick();
        Assert.Null(coordinator.Controller);
        Assert.Equal(3, dialogAttempts);
        Assert.Equal(2, resourceAttempts);

        stringsAvailable = true;
        coordinator.Tick();
        CharacterManagementUiController controller = Assert.IsType<
            CharacterManagementUiController>(coordinator.Controller);
        Assert.Single(host.Children);
        Assert.Same(controller.Root, host.Children[0]);
        Assert.Equal(4, dialogAttempts);
        Assert.Equal(3, resourceAttempts);

        coordinator.Tick();
        Assert.Same(controller, coordinator.Controller);
        Assert.Single(host.Children);
        Assert.Equal(4, dialogAttempts);
        Assert.Equal(3, resourceAttempts);

        coordinator.Dispose();
        Assert.Empty(host.Children);
        coordinator.Tick();
        Assert.Null(coordinator.Controller);
        Assert.Equal(4, dialogAttempts);
        Assert.Equal(3, resourceAttempts);
    }

    [Fact]
    public void MountCoordinator_PostAttachFailuresDisposeBeforeRetryAndRecovery()
    {
        var host = new UiRoot { Width = 800f, Height = 600f };
        var runtime = new FakeRuntime();
        using var dialogs = new RetailDialogFactory(
            host,
            RetailDialogFactoryTests.BuildDialogLayout);
        var screens = new List<ImportedLayout>();
        int failingAttempts = 2;
        using var coordinator = new CharacterManagementUiMountCoordinator(
            host,
            runtime.Bindings,
            () => dialogs,
            () =>
            {
                ImportedLayout screen = BuildScreen();
                screens.Add(screen);
                bool throwAfterAttach = failingAttempts-- > 0;
                return new CharacterManagementUiMountResources(
                    0x21000004u,
                    screen,
                    (_, _) => throwAfterAttach
                        ? throw new InvalidOperationException(
                            "template failed after root attach")
                        : BuildRow(),
                    TestStrings());
            });

        coordinator.Tick();
        Assert.Null(coordinator.Controller);
        Assert.Empty(host.Children);
        Assert.Single(screens);
        AssertDetachedAndUnbound(screens[0]);

        coordinator.Tick();
        Assert.Null(coordinator.Controller);
        Assert.Empty(host.Children);
        Assert.Equal(2, screens.Count);
        Assert.All(screens, AssertDetachedAndUnbound);

        coordinator.Tick();
        CharacterManagementUiController mounted = Assert.IsType<
            CharacterManagementUiController>(coordinator.Controller);
        Assert.Equal(3, screens.Count);
        Assert.Single(host.Children);
        Assert.Same(mounted.Root, host.Children[0]);
        Assert.NotNull(Assert.IsType<UiButton>(screens[2].FindElement(
            CharacterManagementUiController.EnterElementId)).OnClick);

        coordinator.Tick();
        Assert.Equal(3, screens.Count);
        Assert.Single(host.Children);

        coordinator.Dispose();
        Assert.Empty(host.Children);
        Assert.Null(coordinator.Controller);
        Assert.All(screens, AssertDetachedAndUnbound);
    }

    [Fact]
    public void DeleteConfirmation_IsCaseInsensitive_ThenWaitsThroughAckUntilFreshRoster()
    {
        using var environment = new EnvironmentHarness();
        CharacterManagementUiController controller = environment.Controller;
        UiButton delete = environment.Button(
            CharacterManagementUiController.DeleteElementId);

        delete.OnClick!();
        ImportedLayout wrong = environment.LastDialog(
            RetailDialogType.ConfirmationTextInput);
        Assert.Contains("Alpha", Message(wrong));
        Assert.Contains("Type DELETE", Message(wrong));
        Input(wrong).SetText("not delete");
        DialogButton(
            wrong,
            RetailConfirmationTextInputDialogView.AcceptButtonId).OnClick!();
        Assert.Equal(1, environment.Runtime.CancelCalls);
        Assert.Equal(0, environment.Runtime.ConfirmDeleteCalls);
        Assert.Equal(0u, controller.DeleteDialogContext);
        Assert.False(environment.Dialogs.IsOpen);

        delete.OnClick!();
        ImportedLayout accepted = environment.LastDialog(
            RetailDialogType.ConfirmationTextInput);
        Input(accepted).SetText("delete");
        DialogButton(
            accepted,
            RetailConfirmationTextInputDialogView.AcceptButtonId).OnClick!();

        Assert.Equal(1, environment.Runtime.ConfirmDeleteCalls);
        Assert.Equal(
            RuntimeCharacterSelectionOperation.DeleteRequested,
            environment.Runtime.View.Snapshot.Operation);
        uint waitContext = controller.OperationWaitContext;
        Assert.NotEqual(0u, waitContext);
        Assert.Equal(
            RetailDialogType.Wait,
            environment.DialogLayouts[^1].Type);
        Assert.Equal("Please Wait", Message(environment.DialogLayouts[^1].Layout));

        environment.Runtime.SetOperation(
            RuntimeCharacterSelectionOperation.DeleteAcknowledged);
        controller.Tick();
        controller.Tick();
        Assert.Equal(waitContext, controller.OperationWaitContext);

        environment.Runtime.ReplaceRoster(
        [
            new RuntimeCharacterSelectionEntry(1, 0x50000002u, "Zulu", 0u),
            new RuntimeCharacterSelectionEntry(2, 0x50000003u, "Aaron (pending)", 1u),
        ],
            highlightedCharacterId: 0x50000002u);
        controller.Tick();

        Assert.Equal(0u, controller.OperationWaitContext);
        Assert.False(environment.Dialogs.IsOpen);
        Assert.Equal(
            ["Zulu", "Aaron (pending)"],
            controller.Rows.Select(static row => row.Label!).ToArray());
    }

    [Fact]
    public void ExitButton_OpenThenCancel_KeepsScreenActive_NoExitRequested()
    {
        using var environment = new EnvironmentHarness();
        CharacterManagementUiController controller = environment.Controller;
        UiButton exit = environment.Button(
            CharacterManagementUiController.ExitElementId);

        environment.Host.Width = 1920f;
        environment.Host.Height = 1080f;

        exit.OnClick!();

        Assert.NotEqual(0u, controller.ConfirmExitDialogContext);
        ImportedLayout dialog = environment.LastDialog(RetailDialogType.Confirmation);
        Assert.Equal(
            "Are you sure you want to leave?",
            Message(dialog));
        Assert.Equal(800f, dialog.Root.Width);
        Assert.Equal(600f, dialog.Root.Height);
        UiElement popup = Assert.Single(
            dialog.Root.Children,
            static child => child.Visible && child.Width > 0f);
        Assert.InRange(popup.Left + popup.Width * 0.5f, 399f, 401f);
        DialogButton(dialog, RetailConfirmationDialogView.RejectButtonId).OnClick!();

        Assert.Equal(0, environment.Runtime.RequestExitCalls);
        Assert.Equal(0u, controller.ConfirmExitDialogContext);
        Assert.False(environment.Dialogs.IsOpen);
        Assert.True(controller.Root.Visible);
    }

    [Fact]
    public void ExitButton_OpenThenConfirm_ReachesGracefulShutdownSeam()
    {
        using var environment = new EnvironmentHarness();
        CharacterManagementUiController controller = environment.Controller;
        UiButton exit = environment.Button(
            CharacterManagementUiController.ExitElementId);

        exit.OnClick!();
        ImportedLayout dialog = environment.LastDialog(RetailDialogType.Confirmation);
        DialogButton(dialog, RetailConfirmationDialogView.AcceptButtonId).OnClick!();

        Assert.Equal(1, environment.Runtime.RequestExitCalls);
        Assert.Equal(0u, controller.ConfirmExitDialogContext);
        Assert.False(environment.Dialogs.IsOpen);
    }

    [Fact]
    public void ExitButton_SecondClickWhileOpen_IsNoOp()
    {
        using var environment = new EnvironmentHarness();
        UiButton exit = environment.Button(
            CharacterManagementUiController.ExitElementId);

        exit.OnClick!();
        Assert.Equal(1, environment.DialogLayouts.Count(
            entry => entry.Type == RetailDialogType.Confirmation));

        exit.OnClick!();

        Assert.Equal(1, environment.DialogLayouts.Count(
            entry => entry.Type == RetailDialogType.Confirmation));
    }

    [Fact]
    public void AuthoredRowDoubleActivation_EntersTheHighlightedCharacter()
    {
        using var environment = new EnvironmentHarness();
        CharacterManagementUiController controller = environment.Controller;
        UiButton row = controller.Rows[1];

        Assert.True(row.OnEvent(new UiEvent(
            row.EventId,
            row,
            UiEventType.Click)));
        Assert.True(row.OnEvent(new UiEvent(
            row.EventId,
            row,
            UiEventType.DoubleClick)));

        Assert.Equal(1, environment.Runtime.HighlightCalls);
        Assert.Equal(0x50000002u,
            environment.Runtime.View.Snapshot.HighlightedCharacterId);
        Assert.Equal(1, environment.Runtime.EnterCalls);
        Assert.Equal(
            RuntimeCharacterSelectionLifecycle.EnteringWorld,
            environment.Runtime.View.Snapshot.Lifecycle);
        Assert.NotEqual(0u, controller.EnterWaitContext);
    }

    [Fact]
    public void RestoreSilenceExpires_EnterTransitions_AndErrorUsesMessageDialog()
    {
        using var environment = new EnvironmentHarness();
        CharacterManagementUiController controller = environment.Controller;

        controller.Rows[2].OnClick!();
        environment.Button(CharacterManagementUiController.RestoreElementId).OnClick!();
        uint restoreWait = controller.OperationWaitContext;
        Assert.NotEqual(0u, restoreWait);
        Assert.Equal(
            RuntimeCharacterSelectionOperation.RestoreRequested,
            environment.Runtime.View.Snapshot.Operation);

        controller.Tick();
        Assert.Equal(restoreWait, controller.OperationWaitContext);
        environment.Runtime.SetOperation(RuntimeCharacterSelectionOperation.None);
        controller.Tick();
        Assert.Equal(0u, controller.OperationWaitContext);
        Assert.False(environment.Dialogs.IsOpen);

        environment.Host.Width = 1920f;
        environment.Host.Height = 1080f;

        controller.Rows[0].OnClick!();
        environment.Button(CharacterManagementUiController.EnterElementId).OnClick!();
        Assert.Equal(1, environment.Runtime.EnterCalls);
        Assert.Equal(
            RuntimeCharacterSelectionLifecycle.EnteringWorld,
            environment.Runtime.View.Snapshot.Lifecycle);
        Assert.NotEqual(0u, controller.EnterWaitContext);
        ImportedLayout enterWait = environment.LastDialog(RetailDialogType.Wait);
        Assert.Equal("Entering World", Message(enterWait));
        Assert.Equal(800f, enterWait.Root.Width);
        Assert.Equal(600f, enterWait.Root.Height);
        UiElement enterWaitPopup = Assert.Single(
            enterWait.Root.Children,
            static child => child.Visible && child.Width > 0f);
        Assert.InRange(enterWaitPopup.Left + enterWaitPopup.Width * 0.5f, 399f, 401f);

        environment.Runtime.SetError("That character is unavailable.");
        controller.Tick();
        Assert.Equal(0u, controller.EnterWaitContext);
        Assert.NotEqual(0u, controller.ErrorDialogContext);
        ImportedLayout error = environment.LastDialog(RetailDialogType.Message);
        Assert.Equal("That character is unavailable.", Message(error));
        DialogButton(error, RetailMessageDialogView.OkButtonId).OnClick!();
        Assert.Equal(1, environment.Runtime.CancelCalls);
        Assert.Null(environment.Runtime.View.Snapshot.Error);
        Assert.False(environment.Dialogs.IsOpen);

        int createdBeforeSentinel = environment.DialogLayouts.Count;
        controller.Tick();
        Assert.Equal(createdBeforeSentinel, environment.DialogLayouts.Count);

        environment.Button(CharacterManagementUiController.EnterElementId).OnClick!();
        Assert.NotEqual(0u, controller.EnterWaitContext);
        environment.Runtime.SetLifecycle(
            RuntimeCharacterSelectionLifecycle.InWorld);
        controller.Tick();
        Assert.False(controller.Root.Visible);
        Assert.Equal(0u, controller.EnterWaitContext);
        Assert.False(environment.Dialogs.IsOpen);
    }

    [Fact]
    public void Restore_OpensWaitBeforeCommand_AndKeepsOneModalAcrossReentrantTick()
    {
        using var environment = new EnvironmentHarness();
        CharacterManagementUiController controller = environment.Controller;
        controller.Rows[2].OnClick!();
        uint observedContext = 0u;
        environment.Runtime.BeforeRestore = () =>
        {
            observedContext = controller.OperationWaitContext;
            Assert.NotEqual(0u, observedContext);
            Assert.True(environment.Dialogs.IsOpen);
            Assert.Same(
                environment.LastDialog(RetailDialogType.Wait).Root,
                environment.Host.Modal);

            controller.Tick();
            Assert.Equal(observedContext, controller.OperationWaitContext);
        };
        environment.Runtime.AfterRestoreProjection = () =>
        {
            controller.Tick();
            Assert.Equal(observedContext, controller.OperationWaitContext);
        };

        environment.Button(
            CharacterManagementUiController.RestoreElementId).OnClick!();

        Assert.Equal(1, environment.Runtime.RestoreCalls);
        Assert.Equal(observedContext, controller.OperationWaitContext);
        Assert.Equal(
            1,
            environment.DialogLayouts.Count(static entry =>
                entry.Type == RetailDialogType.Wait));
        Assert.Same(
            environment.LastDialog(RetailDialogType.Wait).Root,
            environment.Host.Modal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Restore_ImmediateRejectionOrFailure_ClosesPreopenedWait(
        bool throwFailure)
    {
        using var environment = new EnvironmentHarness();
        CharacterManagementUiController controller = environment.Controller;
        controller.Rows[2].OnClick!();
        environment.Runtime.RestoreStatus = RuntimeCommandStatus.Rejected;
        environment.Runtime.ThrowOnRestore = throwFailure;
        environment.Runtime.BeforeRestore = () =>
        {
            Assert.NotEqual(0u, controller.OperationWaitContext);
            Assert.NotNull(environment.Host.Modal);
        };

        Exception? error = Record.Exception(() => environment.Button(
            CharacterManagementUiController.RestoreElementId).OnClick!());

        Assert.Null(error);
        Assert.Equal(1, environment.Runtime.RestoreCalls);
        Assert.Equal(0u, controller.OperationWaitContext);
        Assert.False(environment.Dialogs.IsOpen);
        Assert.Null(environment.Host.Modal);
        Assert.Equal(
            RuntimeCharacterSelectionOperation.None,
            environment.Runtime.View.Snapshot.Operation);
    }

    [Fact]
    public void MissingOrDisposedBorrowedView_ClosesDialogsFlushesRowsAndDisposesSafely()
    {
        using var environment = new EnvironmentHarness();
        CharacterManagementUiController controller = environment.Controller;
        environment.Button(CharacterManagementUiController.DeleteElementId).OnClick!();
        Assert.NotEqual(0u, controller.DeleteDialogContext);

        environment.Runtime.ProvideView = false;
        controller.Tick();

        Assert.False(controller.Root.Visible);
        Assert.Empty(controller.Rows);
        Assert.False(environment.Dialogs.IsOpen);
        Assert.Equal(0, environment.Runtime.CancelCalls);

        controller.Dispose();
        Assert.Null(controller.Root.Parent);
        Assert.Null(environment.Button(
            CharacterManagementUiController.EnterElementId).OnClick);
        controller.Tick();
    }

    [Fact]
    public void SessionReset_ClosesOwnedContextsWithoutReentrantCancel()
    {
        using var environment = new EnvironmentHarness();
        CharacterManagementUiController controller = environment.Controller;
        environment.Button(CharacterManagementUiController.DeleteElementId).OnClick!();
        Assert.NotEqual(0u, controller.DeleteDialogContext);

        controller.ResetSession();

        Assert.False(controller.Root.Visible);
        Assert.Empty(controller.Rows);
        Assert.False(environment.Dialogs.IsOpen);
        Assert.Equal(0, environment.Runtime.CancelCalls);
    }

    [Fact]
    public void PreviewViewport_IsRejectedBeforeTheAuthoredScreenIsMounted()
    {
        var host = new UiRoot { Width = 800f, Height = 600f };
        ImportedLayout screen = BuildScreen(includePreview: true);
        using var dialogs = new RetailDialogFactory(
            host,
            RetailDialogFactoryTests.BuildDialogLayout);
        var runtime = new FakeRuntime();

        CharacterManagementUiController? controller =
            CharacterManagementUiController.Bind(
                host,
                screen,
                static (_, _) => BuildRow(),
                dialogs,
                runtime.Bindings,
                TestStrings());

        Assert.Null(controller);
        Assert.Empty(host.Children);
    }

    [Fact]
    public void TransientTemplateMiss_DoesNotConsumeTheRuntimeRevision()
    {
        var host = new UiRoot { Width = 800f, Height = 600f };
        ImportedLayout screen = BuildScreen();
        using var dialogs = new RetailDialogFactory(
            host,
            RetailDialogFactoryTests.BuildDialogLayout);
        var runtime = new FakeRuntime();
        int resolveCalls = 0;
        using CharacterManagementUiController controller =
            Assert.IsType<CharacterManagementUiController>(
                CharacterManagementUiController.Bind(
                    host,
                    screen,
                    (_, _) => ++resolveCalls == 1 ? null : BuildRow(),
                    dialogs,
                    runtime.Bindings,
                    TestStrings()));

        Assert.Empty(controller.Rows);
        controller.Tick();

        Assert.Equal(3, controller.Rows.Count);
        Assert.True(resolveCalls >= 4);
    }

    private static CharacterManagementUiController.DialogStrings TestStrings() =>
        new(
            name => $"WARNING! {name}\nType DELETE in the box below.",
            "DELETE",
            "Please Wait",
            "Entering World",
            "Are you sure you want to leave?");

    private static void AssertDetachedAndUnbound(ImportedLayout screen)
    {
        Assert.Null(screen.Root.Parent);
        Assert.Null(Assert.IsType<UiButton>(screen.FindElement(
            CharacterManagementUiController.EnterElementId)).OnClick);
        Assert.Null(Assert.IsType<UiButton>(screen.FindElement(
            CharacterManagementUiController.DeleteElementId)).OnClick);
        Assert.Null(Assert.IsType<UiButton>(screen.FindElement(
            CharacterManagementUiController.RestoreElementId)).OnClick);
    }

    private static ImportedLayout BuildScreen(bool includePreview = false)
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
        list.TemplateList.Add(new UiTemplateListEntry(
            0x21000004u,
            0x100003A5u));
        root.Children.Add(list);
        root.Children.Add(new ElementInfo
        {
            Id = CharacterManagementUiController.WorldTextElementId,
            Type = 12u,
            X = 21f,
            Y = 44f,
            Width = 193f,
            Height = 110f,
        });
        root.Children.Add(ButtonInfo(
            CharacterManagementUiController.CreateElementId));
        root.Children.Add(ButtonInfo(
            CharacterManagementUiController.EnterElementId));
        root.Children.Add(ButtonInfo(
            CharacterManagementUiController.DeleteElementId));
        root.Children.Add(ButtonInfo(
            CharacterManagementUiController.RestoreElementId));
        root.Children.Add(ButtonInfo(
            CharacterManagementUiController.CreditsElementId));
        root.Children.Add(ButtonInfo(
            CharacterManagementUiController.ExitElementId));
        if (includePreview)
        {
            root.Children.Add(new ElementInfo
            {
                Id = 0xDEADBEEFu,
                Type = 0xDu,
                Width = 100f,
                Height = 100f,
            });
        }
        return LayoutImporter.Build(root, _ => (0u, 0, 0), null);
    }

    private static ElementInfo ButtonInfo(uint id) => new()
    {
        Id = id,
        Type = 1u,
        Width = 100f,
        Height = 30f,
    };

    private static UiElement BuildRow() => LayoutImporter.Build(
        new ElementInfo
        {
            Id = 0x100003A5u,
            Type = 1u,
            Width = 160f,
            Height = 16f,
        },
        _ => (0u, 0, 0),
        null).Root;

    private static IEnumerable<UiElement> Descendants(UiElement root)
    {
        yield return root;
        foreach (UiElement child in root.Children)
            foreach (UiElement descendant in Descendants(child))
                yield return descendant;
    }

    private static UiButton DialogButton(ImportedLayout layout, uint id) =>
        Assert.IsType<UiButton>(layout.FindElement(id));

    private static UiField Input(ImportedLayout layout) =>
        Assert.IsType<UiField>(layout.FindElement(
            RetailConfirmationTextInputDialogView.InputElementId));

    private static string Message(ImportedLayout layout) => string.Join(
        " ",
        Assert.IsType<UiText>(layout.FindElement(0x3Eu))
            .LinesProvider()
            .Select(static line => line.Text));

    private sealed class EnvironmentHarness : IDisposable
    {
        public EnvironmentHarness(Action? openCredits = null, bool directCharacterLaunch = false,
            BitmapFont? versionFont = null)
        {
            Host = new UiRoot { Width = 800f, Height = 600f };
            Screen = BuildScreen();
            Runtime = new FakeRuntime();
            Dialogs = new RetailDialogFactory(Host, type =>
            {
                ImportedLayout layout =
                    RetailDialogFactoryTests.BuildDialogLayout(type);
                DialogLayouts.Add((type, layout));
                return layout;
            });
            Controller = Assert.IsType<CharacterManagementUiController>(
                CharacterManagementUiController.Bind(
                    Host,
                    Screen,
                    static (_, _) => BuildRow(),
                    Dialogs,
                    Runtime.Bindings with { DirectCharacterLaunch = directCharacterLaunch },
                    TestStrings(),
                    openCredits,
                    versionFont));
        }

        public UiRoot Host { get; }
        public ImportedLayout Screen { get; }
        public FakeRuntime Runtime { get; }
        public RetailDialogFactory Dialogs { get; }
        public List<(RetailDialogType Type, ImportedLayout Layout)> DialogLayouts { get; } = [];
        public CharacterManagementUiController Controller { get; }

        public UiButton Button(uint id) =>
            Assert.IsType<UiButton>(Screen.FindElement(id));

        public ImportedLayout LastDialog(RetailDialogType type) =>
            DialogLayouts.Last(entry => entry.Type == type).Layout;

        public void Dispose()
        {
            Controller.Dispose();
            Dialogs.Dispose();
        }
    }

    private sealed class FakeRuntime
    {
        private static readonly RuntimeGenerationToken Generation = new(7u);

        public FakeRuntime()
        {
            View.Entries =
            [
                new RuntimeCharacterSelectionEntry(0, 0x50000001u, "Alpha", 0u),
                new RuntimeCharacterSelectionEntry(1, 0x50000002u, "Zulu", 0u),
                new RuntimeCharacterSelectionEntry(2, 0x50000003u, "Aaron (pending)", 1u),
            ];
            View.Snapshot = Snapshot(
                RuntimeCharacterSelectionLifecycle.AwaitingSelection,
                revision: 1,
                highlightedCharacterId: 0x50000001u,
                buttons: ButtonsFor(0x50000001u));
            Bindings = new CharacterSelectionRuntimeBindings(
                () => ProvideView ? View : null,
                Highlight,
                Enter,
                RequestDelete,
                ConfirmDelete,
                Restore,
                Cancel,
                RequestExit,
                RequestCreate);
        }

        public FakeView View { get; } = new();
        public CharacterSelectionRuntimeBindings Bindings { get; }
        public bool ProvideView { get; set; } = true;
        public int HighlightCalls { get; private set; }
        public int EnterCalls { get; private set; }
        public int ConfirmDeleteCalls { get; private set; }
        public int CancelCalls { get; private set; }
        public int RestoreCalls { get; private set; }
        public int RequestExitCalls { get; private set; }
        public int RequestCreateCalls { get; private set; }

        private const int SlotCount = 5;
        public RuntimeCommandStatus RestoreStatus { get; set; } =
            RuntimeCommandStatus.Accepted;
        public bool ThrowOnRestore { get; set; }
        public Action? BeforeRestore { get; set; }
        public Action? AfterRestoreProjection { get; set; }

        public void SetOperation(RuntimeCharacterSelectionOperation operation)
        {
            RuntimeCharacterSelectionButtons buttons = operation is
                RuntimeCharacterSelectionOperation.DeleteRequested
                or RuntimeCharacterSelectionOperation.DeleteAcknowledged
                    ? RuntimeCharacterSelectionButtons.None with
                        { CanCreate = View.Entries.Length < SlotCount }
                    : ButtonsFor(View.Snapshot.HighlightedCharacterId);
            Update(snapshot => snapshot with
            {
                Operation = operation,
                Buttons = buttons,
            });
        }

        public void ReplaceRoster(
            RuntimeCharacterSelectionEntry[] entries,
            uint highlightedCharacterId)
        {
            View.Entries = entries;
            Update(snapshot => snapshot with
            {
                RosterCount = entries.Length,
                HighlightedCharacterId = highlightedCharacterId,
                HighlightedDisplayIndex = Array.FindIndex(
                    entries,
                    entry => entry.CharacterId == highlightedCharacterId),
                PendingDeleteCharacterId = 0u,
                Operation = RuntimeCharacterSelectionOperation.None,
                Buttons = ButtonsFor(highlightedCharacterId),
            });
        }

        public void SetLifecycle(RuntimeCharacterSelectionLifecycle lifecycle) =>
            Update(snapshot => snapshot with { Lifecycle = lifecycle });

        public void SetWorldName(string worldName) =>
            Update(snapshot => snapshot with { WorldName = worldName });

        public void SetError(string message) => Update(snapshot => snapshot with
        {
            Lifecycle = RuntimeCharacterSelectionLifecycle.AwaitingSelection,
            Error = new RuntimeCharacterSelectionError(
                1u,
                AcDream.Core.Net.Messages.CharacterError.Code.Logon,
                message),
            PendingDeleteCharacterId = 0u,
            Operation = RuntimeCharacterSelectionOperation.None,
        });

        private RuntimeCommandResult Highlight(uint characterId)
        {
            HighlightCalls++;
            int index = Array.FindIndex(
                View.Entries,
                entry => entry.CharacterId == characterId);
            if (index < 0)
                return Result(RuntimeCommandStatus.Rejected);
            Update(snapshot => snapshot with
            {
                HighlightedCharacterId = characterId,
                HighlightedDisplayIndex = index,
                Buttons = ButtonsFor(characterId),
            });
            return Result(RuntimeCommandStatus.Accepted, characterId);
        }

        private RuntimeCommandResult Enter()
        {
            EnterCalls++;
            Update(snapshot => snapshot with
            {
                Lifecycle = RuntimeCharacterSelectionLifecycle.EnteringWorld,
                Error = null,
            });
            return Result(
                RuntimeCommandStatus.Accepted,
                View.Snapshot.HighlightedCharacterId);
        }

        private RuntimeCommandResult RequestDelete()
        {
            uint id = View.Snapshot.HighlightedCharacterId;
            Update(snapshot => snapshot with
            {
                PendingDeleteCharacterId = id,
                Error = null,
            });
            return Result(RuntimeCommandStatus.Accepted, id);
        }

        private RuntimeCommandResult ConfirmDelete()
        {
            ConfirmDeleteCalls++;
            uint id = View.Snapshot.HighlightedCharacterId;
            Update(snapshot => snapshot with
            {
                PendingDeleteCharacterId = 0u,
                Operation = RuntimeCharacterSelectionOperation.DeleteRequested,
                Buttons = RuntimeCharacterSelectionButtons.None with
                    { CanCreate = View.Entries.Length < SlotCount },
            });
            return Result(RuntimeCommandStatus.Accepted, id);
        }

        private RuntimeCommandResult Restore()
        {
            RestoreCalls++;
            uint id = View.Snapshot.HighlightedCharacterId;
            BeforeRestore?.Invoke();
            if (ThrowOnRestore)
                throw new InvalidOperationException("restore transport failed");
            if (RestoreStatus != RuntimeCommandStatus.Accepted)
                return Result(RestoreStatus, id);
            Update(snapshot => snapshot with
            {
                LastRestoreRequestedCharacterId = id,
                Operation = RuntimeCharacterSelectionOperation.RestoreRequested,
                Buttons = new RuntimeCharacterSelectionButtons(
                    false,
                    false,
                    false,
                    false,
                    true,
                    View.Entries.Length < SlotCount),
            });
            AfterRestoreProjection?.Invoke();
            return Result(RuntimeCommandStatus.Accepted, id);
        }

        private RuntimeCommandResult Cancel()
        {
            CancelCalls++;
            Update(snapshot => snapshot with
            {
                PendingDeleteCharacterId = 0u,
                Error = null,
                Buttons = ButtonsFor(snapshot.HighlightedCharacterId),
            });
            return Result(RuntimeCommandStatus.Accepted);
        }

        private void RequestExit() => RequestExitCalls++;

        private void RequestCreate() => RequestCreateCalls++;

        private RuntimeCharacterSelectionButtons ButtonsFor(uint characterId)
        {
            bool canCreate = View.Entries.Length < SlotCount;
            RuntimeCharacterSelectionEntry? selected = View.Entries
                .Cast<RuntimeCharacterSelectionEntry?>()
                .FirstOrDefault(entry => entry?.CharacterId == characterId);
            if (selected is null)
                return RuntimeCharacterSelectionButtons.None with { CanCreate = canCreate };
            if (selected.Value.IsPendingDelete)
            {
                return new RuntimeCharacterSelectionButtons(
                    false,
                    false,
                    true,
                    false,
                    true,
                    canCreate);
            }
            return new RuntimeCharacterSelectionButtons(
                true,
                true,
                false,
                true,
                false,
                canCreate);
        }

        private void Update(
            Func<RuntimeCharacterSelectionSnapshot,
                RuntimeCharacterSelectionSnapshot> update)
        {
            RuntimeCharacterSelectionSnapshot current = View.Snapshot;
            RuntimeCharacterSelectionSnapshot next = update(current);
            View.Snapshot = next with { Revision = current.Revision + 1 };
        }

        private RuntimeCharacterSelectionSnapshot Snapshot(
            RuntimeCharacterSelectionLifecycle lifecycle,
            long revision,
            uint highlightedCharacterId,
            RuntimeCharacterSelectionButtons buttons) => new(
                Generation,
                lifecycle,
                revision,
                "account",
                SlotCount: 5,
                RosterCount: View.Entries.Length,
                WorldName: "sawato",
                highlightedCharacterId,
                HighlightedDisplayIndex: Array.FindIndex(
                    View.Entries,
                    entry => entry.CharacterId == highlightedCharacterId),
                PendingDeleteCharacterId: 0u,
                LastRestoreRequestedCharacterId: 0u,
                Operation: RuntimeCharacterSelectionOperation.None,
                Error: null,
                buttons);

        private static RuntimeCommandResult Result(
            RuntimeCommandStatus status,
            uint objectId = 0u) => new(status, Generation, objectId);
    }

    private sealed class FakeView : IRuntimeCharacterSelectionView
    {
        public RuntimeCharacterSelectionEntry[] Entries { get; set; } = [];
        public RuntimeCharacterSelectionSnapshot Snapshot { get; set; }

        public bool TryGetAt(
            int displayIndex,
            out RuntimeCharacterSelectionEntry character)
        {
            if ((uint)displayIndex >= (uint)Entries.Length)
            {
                character = default;
                return false;
            }
            character = Entries[displayIndex];
            return true;
        }

        public bool TryGet(
            uint characterId,
            out RuntimeCharacterSelectionEntry character)
        {
            int index = Array.FindIndex(
                Entries,
                entry => entry.CharacterId == characterId);
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
            NoopDisposable.Instance;
    }

    private sealed class NoopDisposable : IDisposable
    {
        public static NoopDisposable Instance { get; } = new();
        public void Dispose() { }
    }
}
