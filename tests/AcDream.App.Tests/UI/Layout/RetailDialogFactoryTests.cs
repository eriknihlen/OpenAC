using AcDream.App.UI;
using AcDream.App.UI.Layout;

namespace AcDream.App.Tests.UI.Layout;

public sealed class RetailDialogFactoryTests
{
    [Theory]
    [InlineData(RetailDialogType.Confirmation, 0x15u)]
    [InlineData(RetailDialogType.Wait, 0x31u)]
    [InlineData(RetailDialogType.Message, 0x24u)]
    [InlineData(RetailDialogType.TextInput, 0x28u)]
    [InlineData(RetailDialogType.ConfirmationTextInput, 0x2Cu)]
    [InlineData(RetailDialogType.Menu, 0x1Bu)]
    [InlineData(RetailDialogType.ConfirmationMenu, 0x1Fu)]
    public void DialogTypesMapToRetailCatalogRoots(
        RetailDialogType type,
        uint expectedRoot)
        => Assert.Equal(expectedRoot, RetailDialogFactory.RootElementId(type));

    [Fact]
    public void FixtureBuildsProductionConfirmationWidgets()
    {
        var layout = FixtureLoader.LoadConfirmationDialog();

        Assert.IsType<UiDialogRoot>(layout.Root);
        Assert.IsType<UiText>(layout.FindElement(RetailConfirmationDialogView.MessageElementId));
        Assert.IsType<UiButton>(layout.FindElement(RetailConfirmationDialogView.AcceptButtonId));
        Assert.IsType<UiButton>(layout.FindElement(RetailConfirmationDialogView.RejectButtonId));
    }

    [Fact]
    public void ConfirmationCreatesFreshCenteredRootAndReturnsPropertyResult()
    {
        var root = new UiRoot { Width = 1024f, Height = 768f };
        var layouts = new List<ImportedLayout>();
        var factory = CreateFactory(root, layouts);
        bool? result = null;

        factory.MakeConfirmation(
            "Do you really want to kill your character?",
            data => result = data.GetBoolean(RetailDialogProperty.ConfirmationResult));

        ImportedLayout layout = Assert.Single(layouts);
        Assert.Same(layout.Root, root.Modal);
        var popup = Assert.IsAssignableFrom<UiElement>(
            layout.FindElement(RetailConfirmationDialogView.PopupElementId));
        Assert.Equal(MathF.Round((1024f - popup.Width) * 0.5f), popup.Left);
        Assert.Equal(MathF.Round((768f - popup.Height) * 0.5f), popup.Top);

        Accept(layout);

        Assert.True(result);
        Assert.Null(root.Modal);
        Assert.False(factory.IsOpen);
    }

    [Fact]
    public void ConfirmationMenu_ReturnsSelectedIndex_AndRejectReturnsMinusOne()
    {
        var root = new UiRoot { Width = 800f, Height = 600f };
        var layouts = new List<ImportedLayout>();
        var factory = new RetailDialogFactory(root, type =>
        {
            ImportedLayout layout = BuildDialogLayout(type);
            layouts.Add(layout);
            return layout;
        });
        int? result = null;

        factory.MakeConfirmationMenu(
            new[] { "acdream.keymap", "friends.keymap" },
            selectedIndex: 1,
            data => result = data.GetInt32(RetailDialogProperty.MenuSelection));

        ImportedLayout first = Assert.Single(layouts);
        UiMenu menu = Assert.IsType<UiMenu>(
            first.FindElement(RetailConfirmationMenuDialogView.MenuElementId));
        Assert.Equal(1, menu.Selected);
        menu.Selected = 0;
        Button(first, RetailConfirmationMenuDialogView.AcceptButtonId).OnClick!();
        Assert.Equal(0, result);

        result = null;
        factory.MakeConfirmationMenu(
            new[] { "acdream.keymap" },
            selectedIndex: 0,
            data => result = data.GetInt32(RetailDialogProperty.MenuSelection));
        ImportedLayout second = layouts[^1];
        Button(second, RetailConfirmationMenuDialogView.RejectButtonId).OnClick!();
        Assert.Equal(-1, result);
    }

    [Fact]
    public void SameQueuePresentsFifoUsingFreshLiveRoots()
    {
        var root = new UiRoot { Width = 800f, Height = 600f };
        var layouts = new List<ImportedLayout>();
        var factory = CreateFactory(root, layouts);
        var results = new List<string>();

        factory.MakeConfirmation("first", data =>
            results.Add($"first:{data.GetBoolean(RetailDialogProperty.ConfirmationResult)}"));
        factory.MakeConfirmation("second", data =>
            results.Add($"second:{data.GetBoolean(RetailDialogProperty.ConfirmationResult)}"));

        Assert.Equal(1, factory.PendingCount);
        Reject(layouts[0]);
        Assert.Equal(["first:False"], results);
        Assert.Equal(2, layouts.Count);
        Assert.NotSame(layouts[0].Root, layouts[1].Root);
        Assert.Same(layouts[1].Root, root.Modal);
        Assert.Equal("second", Message(layouts[1]));

        Accept(layouts[1]);
        Assert.Equal(["first:False", "second:True"], results);
        Assert.False(factory.IsOpen);
    }

    [Fact]
    public void MakeConfirmation_OmittedQueueKey_SharesDefaultQueueKey()
    {
        var root = new UiRoot { Width = 800f, Height = 600f };
        var layouts = new List<ImportedLayout>();
        var factory = CreateFactory(root, layouts);

        factory.MakeConfirmation("uses the omitted-queueKey overload");
        factory.MakeConfirmation("explicit DefaultQueueKey", queueKey: RetailDialogFactory.DefaultQueueKey);

        Assert.Equal(1, factory.ActiveCount);
        Assert.Equal(1, factory.PendingCount);
    }

    [Fact]
    public void QueueGroupsAndNonQueuedDialogsCanBeActiveTogether()
    {
        var root = new UiRoot { Width = 800f, Height = 600f };
        var layouts = new List<ImportedLayout>();
        var factory = CreateFactory(root, layouts);

        uint first = factory.MakeConfirmation("queue two", queueKey: 2u);
        uint second = factory.MakeConfirmation("queue three", queueKey: 3u);
        uint third = factory.MakeConfirmation("nonqueued", queueKey: 1u);

        Assert.Equal(3, factory.ActiveCount);
        Assert.Equal(0, factory.PendingCount);
        Assert.Same(layouts[2].Root, root.Modal);

        Assert.True(factory.CloseDialog(third));
        Assert.Same(layouts[1].Root, root.Modal);
        Assert.True(factory.CloseDialog(second));
        Assert.Same(layouts[0].Root, root.Modal);
        Assert.True(factory.CloseDialog(first));
        Assert.Null(root.Modal);
    }

    [Fact]
    public void PriorityDialogSuspendsCurrentAndRestoresItBeforeOlderPendingWork()
    {
        var root = new UiRoot { Width = 800f, Height = 600f };
        var layouts = new List<ImportedLayout>();
        var factory = CreateFactory(root, layouts);

        factory.MakeConfirmation("current");
        factory.MakeConfirmation("ordinary pending");
        factory.MakeConfirmation("priority", priority: true);

        Assert.Equal(2, factory.PendingCount);
        Assert.Equal("priority", Message(layouts[1]));
        Assert.DoesNotContain(layouts[0].Root, root.Children);

        Reject(layouts[1]);

        Assert.Equal(3, layouts.Count);
        Assert.Equal("current", Message(layouts[2]));
        Assert.Equal(1, factory.PendingCount);
        Assert.NotSame(layouts[0].Root, layouts[2].Root);
    }

    [Fact]
    public void CallbackPrecedesCloseNoticeAndCustomLabelsAreApplied()
    {
        var root = new UiRoot { Width = 800f, Height = 600f };
        var layouts = new List<ImportedLayout>();
        var factory = CreateFactory(root, layouts);
        var order = new List<string>();
        factory.DialogClosed += (_, _) => order.Add("notice");
        RetailDialogData data = RetailDialogData.Confirmation("Proceed?")
            .Set(RetailDialogProperty.AcceptLabel, "Yes")
            .Set(RetailDialogProperty.RejectLabel, "No");

        factory.MakeDialog(data, _ => order.Add("callback"));

        Assert.Equal("Yes", Button(layouts[0], RetailConfirmationDialogView.AcceptButtonId).Label);
        Assert.Equal("No", Button(layouts[0], RetailConfirmationDialogView.RejectButtonId).Label);
        Accept(layouts[0]);
        Assert.Equal(["callback", "notice"], order);
    }

    [Fact]
    public void PendingContextCanBeClosedWithoutDisturbingActiveDialog()
    {
        var root = new UiRoot { Width = 800f, Height = 600f };
        var layouts = new List<ImportedLayout>();
        var factory = CreateFactory(root, layouts);
        var completed = new List<uint>();

        uint active = factory.MakeConfirmation("active");
        uint pending = 0u;
        pending = factory.MakeConfirmation("pending", _ => completed.Add(pending));

        Assert.True(factory.CloseDialog(pending));
        Assert.Equal([pending], completed);
        Assert.Equal(0, factory.PendingCount);
        Assert.Same(layouts[0].Root, root.Modal);
        Assert.True(factory.CloseDialog(active));
    }

    [Fact]
    public void ResetCompletesActiveAndPendingContexts()
    {
        var root = new UiRoot { Width = 800f, Height = 600f };
        var layouts = new List<ImportedLayout>();
        var factory = CreateFactory(root, layouts);
        var completed = new List<string>();
        var notices = new List<uint>();
        factory.DialogClosed += (context, _) => notices.Add(context);

        uint active = factory.MakeConfirmation("active", _ => completed.Add("active"));
        uint pending = factory.MakeConfirmation("pending", _ => completed.Add("pending"));
        factory.Reset();

        Assert.Equal(["active", "pending"], completed);
        Assert.Equal([active, pending], notices);
        Assert.False(factory.IsOpen);
        Assert.Equal(0, factory.PendingCount);
        Assert.Null(root.Modal);
    }

    [Fact]
    public void Reset_CompletesEveryDialogAndKeepsContextSequenceMonotonic()
    {
        var root = new UiRoot { Width = 800f, Height = 600f };
        var layouts = new List<ImportedLayout>();
        var factory = CreateFactory(root, layouts);
        int callbacks = 0;
        int notices = 0;
        factory.DialogClosed += (_, _) => notices++;

        uint first = factory.MakeConfirmation("active", _ => callbacks++);
        factory.MakeConfirmation("pending", _ => callbacks++);
        factory.Reset();

        Assert.Equal(1u, first);
        Assert.Equal(2, callbacks);
        Assert.Equal(2, notices);
        Assert.False(factory.IsOpen);
        Assert.Equal(0, factory.PendingCount);
        Assert.Null(root.Modal);
        Assert.Empty(root.Children);
        Assert.Equal(3u, factory.MakeConfirmation("new session"));
    }

    [Fact]
    public void Reset_AttemptsEveryDialogWhenOneCallbackThrows()
    {
        var root = new UiRoot { Width = 800f, Height = 600f };
        var layouts = new List<ImportedLayout>();
        var factory = CreateFactory(root, layouts);
        int completed = 0;

        factory.MakeConfirmation("active", _ => throw new InvalidOperationException("boom"));
        factory.MakeConfirmation("pending", _ => completed++);

        AggregateException error = Assert.Throws<AggregateException>(factory.Reset);

        Assert.Contains("boom", error.ToString());
        Assert.Equal(1, completed);
        Assert.False(factory.IsOpen);
        Assert.Equal(0, factory.PendingCount);
        Assert.Null(root.Modal);
        Assert.Empty(root.Children);
        factory.Reset();
    }

    [Fact]
    public void Reset_DrainsDialogCreatedReentrantlyByCompletionCallback()
    {
        var root = new UiRoot { Width = 800f, Height = 600f };
        var layouts = new List<ImportedLayout>();
        var factory = CreateFactory(root, layouts);
        int completed = 0;
        factory.MakeConfirmation("first", _ =>
        {
            completed++;
            factory.MakeConfirmation("reentrant", _ => completed++);
        });

        factory.Reset();

        Assert.Equal(2, completed);
        Assert.False(factory.IsOpen);
        Assert.Equal(0, factory.PendingCount);
        Assert.Null(root.Modal);
        Assert.Empty(root.Children);
    }

    [Fact]
    public void AcceptingChainedReentrantDialogDoesNotThrowAndDrainsQueueAfterChainCompletes()
    {
        var root = new UiRoot { Width = 800f, Height = 600f };
        var layouts = new List<ImportedLayout>();
        var factory = CreateFactory(root, layouts);
        var completed = new List<string>();

        factory.MakeConfirmation("stage1", _ =>
        {
            completed.Add("stage1");
            factory.MakeConfirmation("stage2", _ => completed.Add("stage2"));
        });
        factory.MakeConfirmation("queued behind chain", _ => completed.Add("queued"));

        Assert.Equal(1, factory.PendingCount);

        Exception? thrown = Record.Exception(() => Accept(layouts[0]));

        Assert.Null(thrown);
        Assert.Equal(["stage1"], completed);
        Assert.Equal(2, layouts.Count);
        Assert.Equal("stage2", Message(layouts[1]));
        Assert.Same(layouts[1].Root, root.Modal);
        Assert.Equal(1, factory.PendingCount);

        Accept(layouts[1]);

        Assert.Equal(["stage1", "stage2"], completed);
        Assert.Equal(3, layouts.Count);
        Assert.Equal("queued behind chain", Message(layouts[2]));
        Assert.Same(layouts[2].Root, root.Modal);
        Assert.Equal(0, factory.PendingCount);

        Accept(layouts[2]);

        Assert.Equal(["stage1", "stage2", "queued"], completed);
        Assert.False(factory.IsOpen);
    }

    [Fact]
    public void MakeWait_CreatesTextOnlyModal_ClosedByTheOpener()
    {
        var root = new UiRoot { Width = 1024f, Height = 768f };
        var layouts = new List<ImportedLayout>();
        var factory = new RetailDialogFactory(root, type =>
        {
            Assert.Equal(RetailDialogType.Wait, type);
            ImportedLayout layout = FixtureLoader.LoadConfirmationDialog();
            layouts.Add(layout);
            return layout;
        });

        uint context = factory.MakeWait(
            "The next key you press will be mapped.", queueKey: 0x10000001u);

        Assert.NotEqual(0u, context);
        ImportedLayout layout = Assert.Single(layouts);
        Assert.Same(layout.Root, root.Modal);
        Assert.Equal("The next key you press will be mapped.", Message(layout));

        Assert.True(factory.CloseDialog(context));
        Assert.Null(root.Modal);
        Assert.False(factory.IsOpen);
    }

    [Fact]
    public void WaitData_CarriesRetailsMapWarnPropertyShape()
    {
        RetailDialogData data = RetailDialogData.Wait("text");

        Assert.Equal(
            (uint)RetailDialogType.Wait,
            data.GetUInt32(RetailDialogProperty.Type));
        Assert.True(data.GetBoolean(RetailDialogProperty.ElementAttribute40));
        Assert.Equal("text", data.GetString(RetailDialogProperty.Message));
    }

    [Fact]
    public void MessageDialog_UsesAuthoredOkButtonAndReturnsThroughFactoryCallback()
    {
        var root = new UiRoot { Width = 800f, Height = 600f };
        var layouts = new List<(RetailDialogType Type, ImportedLayout Layout)>();
        using var factory = new RetailDialogFactory(root, type =>
        {
            ImportedLayout layout = BuildDialogLayout(type);
            layouts.Add((type, layout));
            return layout;
        });
        bool completed = false;

        factory.MakeMessage("Character selection failed.", _ => completed = true);

        (RetailDialogType type, ImportedLayout layout) = Assert.Single(layouts);
        Assert.Equal(RetailDialogType.Message, type);
        Assert.Equal("Character selection failed.", Message(layout));
        Button(layout, RetailMessageDialogView.OkButtonId).OnClick!();
        Assert.True(completed);
        Assert.False(factory.IsOpen);
        Assert.Null(root.Modal);
    }

    [Fact]
    public void ConfirmationTextInput_AcceptsTypedResultAndRejectsWithEmptyResult()
    {
        var root = new UiRoot { Width = 800f, Height = 600f };
        var layouts = new List<(RetailDialogType Type, ImportedLayout Layout)>();
        using var factory = new RetailDialogFactory(root, type =>
        {
            ImportedLayout layout = BuildDialogLayout(type);
            layouts.Add((type, layout));
            return layout;
        });
        var results = new List<string>();

        factory.MakeConfirmationTextInput(
            "Type DELETE.",
            data => results.Add(
                data.GetString(RetailDialogProperty.TextInputResult) ?? "<null>"));
        ImportedLayout accepted = layouts[^1].Layout;
        var field = Assert.IsType<UiField>(
            accepted.FindElement(RetailConfirmationTextInputDialogView.InputElementId));
        Assert.Same(field, root.KeyboardFocus);
        field.SetText("delete");
        Button(accepted, RetailConfirmationTextInputDialogView.AcceptButtonId).OnClick!();

        factory.MakeConfirmationTextInput(
            "Type DELETE.",
            data => results.Add(
                data.GetString(RetailDialogProperty.TextInputResult) ?? "<null>"));
        ImportedLayout rejected = layouts[^1].Layout;
        UiDialogRoot rejectedRoot = Assert.IsType<UiDialogRoot>(rejected.Root);
        Assert.NotNull(rejectedRoot.Cancel);
        rejectedRoot.Cancel!();

        Assert.Equal(["delete", ""], results);
        Assert.False(factory.IsOpen);
        Assert.Null(root.KeyboardFocus);
    }

    [Theory]
    [InlineData(RetailDialogType.Wait, 0)]
    [InlineData(RetailDialogType.Message, 1)]
    [InlineData(RetailDialogType.ConfirmationTextInput, 2)]
    public void CatalogFailure_DoesNotPoisonActiveQueue_AndTickRecovers(
        RetailDialogType type,
        int failureKind)
    {
        var root = new UiRoot { Width = 800f, Height = 600f };
        bool available = false;
        int attempts = 0;
        using var factory = new RetailDialogFactory(root, requested =>
        {
            Assert.Equal(type, requested);
            attempts++;
            if (!available)
            {
                return failureKind switch
                {
                    0 => throw new InvalidOperationException("catalog unavailable"),
                    1 => null,
                    _ => new ImportedLayout(
                        new UiDialogRoot(),
                        new Dictionary<uint, UiElement>()),
                };
            }
            return BuildDialogLayout(type);
        });
        RetailDialogData data = type switch
        {
            RetailDialogType.Wait => RetailDialogData.Wait("Please Wait"),
            RetailDialogType.Message => RetailDialogData.Message("Error"),
            _ => RetailDialogData.ConfirmationTextInput("Type DELETE"),
        };

        uint context = 0u;
        Exception? creationError = Record.Exception(
            () => context = factory.MakeDialog(data));

        Assert.Null(creationError);
        Assert.NotEqual(0u, context);
        Assert.Equal(0, factory.ActiveCount);
        Assert.Equal(0, factory.PendingCount);
        Assert.Equal(1, factory.RetryCount);
        Assert.Null(root.Modal);
        Assert.Empty(root.Children);

        available = true;
        factory.Tick();

        Assert.Equal(2, attempts);
        Assert.Equal(1, factory.ActiveCount);
        Assert.Equal(0, factory.PendingCount);
        Assert.Equal(0, factory.RetryCount);
        Assert.NotNull(root.Modal);
        Assert.True(factory.CloseDialog(context));
        Assert.False(factory.IsOpen);
    }

    [Fact]
    public void PendingCatalogFailure_MovesOutOfQueue_ThenRecoversBeforeLaterWork()
    {
        var root = new UiRoot { Width = 800f, Height = 600f };
        bool messageAvailable = false;
        var layouts = new List<(RetailDialogType Type, ImportedLayout Layout)>();
        using var factory = new RetailDialogFactory(root, type =>
        {
            if (type == RetailDialogType.Message && !messageAvailable)
                return null;
            ImportedLayout layout = BuildDialogLayout(type);
            layouts.Add((type, layout));
            return layout;
        });

        uint active = factory.MakeWait("active");
        uint failed = factory.MakeMessage("recover me");
        uint later = factory.MakeWait("later");
        Assert.Equal(2, factory.PendingCount);

        Assert.True(factory.CloseDialog(active));

        Assert.Equal(0, factory.ActiveCount);
        Assert.Equal(1, factory.PendingCount);
        Assert.Equal(1, factory.RetryCount);
        Assert.Null(root.Modal);

        messageAvailable = true;
        factory.Tick();

        Assert.Equal(1, factory.ActiveCount);
        Assert.Equal(1, factory.PendingCount);
        Assert.Equal(0, factory.RetryCount);
        Assert.Equal(
            "recover me",
            MessageFromAnyDialog(layouts.Last(static entry =>
                entry.Type == RetailDialogType.Message).Layout.Root));

        Assert.True(factory.CloseDialog(failed));
        Assert.Equal("later", MessageFromAnyDialog(root.Modal!));
        Assert.True(factory.CloseDialog(later));
    }

    [Fact]
    public void PriorityRequest_PreemptsOrdinaryRetryAndPreservesOrdinaryFifo()
    {
        var root = new UiRoot { Width = 800f, Height = 600f };
        bool waitsAvailable = false;
        using var factory = new RetailDialogFactory(root, type =>
            type == RetailDialogType.Wait && !waitsAvailable
                ? null
                : BuildDialogLayout(type));

        uint failed = factory.MakeWait("failed ordinary");
        uint later = factory.MakeWait("later ordinary");
        uint priority = factory.MakeDialog(
            Priority(RetailDialogData.Message("priority")));

        Assert.Equal(1, factory.ActiveCount);
        Assert.Equal(1, factory.RetryCount);
        Assert.Equal(1, factory.PendingCount);
        Assert.Equal("priority", MessageFromAnyDialog(root.Modal!));

        Assert.True(factory.CloseDialog(priority));
        Assert.Equal(0, factory.ActiveCount);
        Assert.Equal(1, factory.RetryCount);
        Assert.Equal(1, factory.PendingCount);

        waitsAvailable = true;
        factory.Tick();
        Assert.Equal("failed ordinary", MessageFromAnyDialog(root.Modal!));
        Assert.True(factory.CloseDialog(failed));
        Assert.Equal("later ordinary", MessageFromAnyDialog(root.Modal!));
        Assert.True(factory.CloseDialog(later));
    }

    [Fact]
    public void FailedPriority_RetriesAheadOfRestoredActiveAndQueuedDialog()
    {
        var root = new UiRoot { Width = 800f, Height = 600f };
        bool priorityAvailable = false;
        using var factory = new RetailDialogFactory(root, type =>
            type == RetailDialogType.Message && !priorityAvailable
                ? null
                : BuildDialogLayout(type));

        uint active = factory.MakeWait("active");
        uint queued = factory.MakeWait("queued");
        uint priority = factory.MakeDialog(
            Priority(RetailDialogData.Message("priority")));

        Assert.Equal(1, factory.ActiveCount);
        Assert.Equal(1, factory.RetryCount);
        Assert.Equal(1, factory.PendingCount);
        Assert.Equal("active", MessageFromAnyDialog(root.Modal!));

        priorityAvailable = true;
        factory.Tick();

        Assert.Equal(1, factory.ActiveCount);
        Assert.Equal(0, factory.RetryCount);
        Assert.Equal(2, factory.PendingCount);
        Assert.Equal("priority", MessageFromAnyDialog(root.Modal!));

        Assert.True(factory.CloseDialog(priority));
        Assert.Equal("active", MessageFromAnyDialog(root.Modal!));
        Assert.Equal(1, factory.PendingCount);
        Assert.True(factory.CloseDialog(active));
        Assert.Equal("queued", MessageFromAnyDialog(root.Modal!));
        Assert.True(factory.CloseDialog(queued));
    }

    [Fact]
    public void MultipleRetries_NewestPriorityFirstThenOlderPriorityThenOrdinaryFifo()
    {
        var root = new UiRoot { Width = 800f, Height = 600f };
        bool available = false;
        using var factory = new RetailDialogFactory(root, type =>
            !available ? null : BuildDialogLayout(type));

        uint ordinary = factory.MakeWait("ordinary");
        uint later = factory.MakeWait("later");
        uint olderPriority = factory.MakeDialog(
            Priority(RetailDialogData.Message("older priority")));
        uint newerPriority = factory.MakeDialog(Priority(
            RetailDialogData.ConfirmationTextInput("newer priority")));

        Assert.Equal(0, factory.ActiveCount);
        Assert.Equal(3, factory.RetryCount);
        Assert.Equal(1, factory.PendingCount);

        available = true;
        factory.Tick();

        Assert.Equal(1, factory.ActiveCount);
        Assert.Equal(2, factory.RetryCount);
        Assert.Equal("newer priority", MessageFromAnyDialog(root.Modal!));

        Assert.True(factory.CloseDialog(newerPriority));
        Assert.Equal("older priority", MessageFromAnyDialog(root.Modal!));
        Assert.True(factory.CloseDialog(olderPriority));
        Assert.Equal("ordinary", MessageFromAnyDialog(root.Modal!));
        Assert.True(factory.CloseDialog(ordinary));
        Assert.Equal("later", MessageFromAnyDialog(root.Modal!));
        Assert.True(factory.CloseDialog(later));
    }

    private static RetailDialogFactory CreateFactory(
        UiRoot root,
        List<ImportedLayout> layouts)
        => new(root, type =>
        {
            Assert.Equal(RetailDialogType.Confirmation, type);
            ImportedLayout layout = FixtureLoader.LoadConfirmationDialog();
            layouts.Add(layout);
            return layout;
        });

    private static UiButton Button(ImportedLayout layout, uint id)
        => Assert.IsType<UiButton>(layout.FindElement(id));

    private static void Accept(ImportedLayout layout)
        => Button(layout, RetailConfirmationDialogView.AcceptButtonId).OnClick!();

    private static void Reject(ImportedLayout layout)
        => Button(layout, RetailConfirmationDialogView.RejectButtonId).OnClick!();

    private static string Message(ImportedLayout layout)
        => string.Join(" ", Assert.IsType<UiText>(layout.FindElement(
            RetailConfirmationDialogView.MessageElementId)).LinesProvider().Select(static line => line.Text));

    private static string MessageFromAnyDialog(UiElement root)
        => string.Join(
            " ",
            Assert.IsType<UiText>(UiElement.FindDescendant(
                root,
                RetailConfirmationDialogView.MessageElementId))
                .LinesProvider()
                .Select(static line => line.Text));

    private static RetailDialogData Priority(RetailDialogData data) =>
        data.Set(RetailDialogProperty.Priority, true)
            .Set(RetailDialogProperty.QueueKey,
                RetailDialogFactory.DefaultQueueKey);

    internal static ImportedLayout BuildDialogLayout(RetailDialogType type)
    {
        uint rootId = RetailDialogFactory.RootElementId(type);
        uint rootType = type switch
        {
            RetailDialogType.Message => 0x17u,
            RetailDialogType.ConfirmationTextInput => 0x15u,
            RetailDialogType.ConfirmationMenu => 0x14u,
            RetailDialogType.Wait => 0x19u,
            _ => 0x13u,
        };
        var root = new ElementInfo
        {
            Id = rootId,
            Type = rootType,
            Width = 800f,
            Height = 600f,
        };
        var popup = new ElementInfo
        {
            Id = 0x3Du,
            Type = 3u,
            Width = 400f,
            Height = type == RetailDialogType.ConfirmationTextInput ? 125f : 95f,
        };
        if (type != RetailDialogType.ConfirmationMenu)
        {
            popup.Children.Add(new ElementInfo
            {
                Id = 0x3Eu,
                Type = 12u,
                X = 15f,
                Y = 15f,
                Width = 370f,
                Height = 18f,
            });
        }
        if (type == RetailDialogType.Message)
        {
            popup.Children.Add(new ElementInfo
            {
                Id = RetailMessageDialogView.OkButtonId,
                Type = 1u,
                X = 160f,
                Y = 48f,
                Width = 80f,
                Height = 32f,
            });
        }
        else if (type == RetailDialogType.Confirmation)
        {
            popup.Children.Add(new ElementInfo
            {
                Id = RetailConfirmationDialogView.AcceptButtonId,
                Type = 1u,
                X = 80f,
                Y = 48f,
                Width = 80f,
                Height = 32f,
            });
            popup.Children.Add(new ElementInfo
            {
                Id = RetailConfirmationDialogView.RejectButtonId,
                Type = 1u,
                X = 240f,
                Y = 48f,
                Width = 80f,
                Height = 32f,
            });
        }
        else if (type == RetailDialogType.ConfirmationTextInput)
        {
            var field = new ElementInfo
            {
                Id = RetailConfirmationTextInputDialogView.InputElementId,
                Type = 12u,
                X = 4f,
                Y = 43f,
                Width = 152f,
                Height = 16f,
            };
            var direct = new UiStateInfo { Id = UiStateInfo.DirectStateId };
            direct.Properties.Values[0x16u] = new UiPropertyValue
            {
                Kind = UiPropertyKind.Bool,
                BoolValue = true,
            };
            field.States.Add(UiStateInfo.DirectStateId, direct);
            popup.Children.Add(field);
            popup.Children.Add(new ElementInfo
            {
                Id = RetailConfirmationTextInputDialogView.AcceptButtonId,
                Type = 1u,
                X = 80f,
                Y = 78f,
                Width = 80f,
                Height = 32f,
            });
            popup.Children.Add(new ElementInfo
            {
                Id = RetailConfirmationTextInputDialogView.RejectButtonId,
                Type = 1u,
                X = 240f,
                Y = 78f,
                Width = 80f,
                Height = 32f,
            });
        }
        else if (type == RetailDialogType.ConfirmationMenu)
        {
            popup.Children.Add(new ElementInfo
            {
                Id = RetailConfirmationMenuDialogView.MenuElementId,
                Type = 6u,
                X = 80f,
                Y = 15f,
                Width = 240f,
                Height = 24f,
            });
            popup.Children.Add(new ElementInfo
            {
                Id = RetailConfirmationMenuDialogView.AcceptButtonId,
                Type = 1u,
                X = 80f,
                Y = 48f,
                Width = 80f,
                Height = 32f,
            });
            popup.Children.Add(new ElementInfo
            {
                Id = RetailConfirmationMenuDialogView.RejectButtonId,
                Type = 1u,
                X = 240f,
                Y = 48f,
                Width = 80f,
                Height = 32f,
            });
        }
        root.Children.Add(popup);
        return LayoutImporter.Build(root, _ => (0u, 0, 0), null);
    }
}
