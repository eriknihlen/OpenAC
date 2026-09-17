using AcDream.App.UI;
using AcDream.App.UI.Layout;
using AcDream.App.Tests.UI.Layout;
using AcDream.Core.Net.Messages;

namespace AcDream.App.Tests.UI;

public sealed class GameplayConfirmationControllerTests
{
    [Fact]
    public void SkillRequestAppendsContinueAndCloseNoticeSendsServerTuple()
    {
        var root = new UiRoot { Width = 800f, Height = 600f };
        ImportedLayout? shown = null;
        var factory = new RetailDialogFactory(root, _ =>
            shown = FixtureLoader.LoadConfirmationDialog());
        var responses = new List<(uint Type, uint Context, bool Accepted)>();
        using var controller = new GameplayConfirmationController(
            factory,
            (type, context, accepted) => responses.Add((type, context, accepted)));

        Assert.True(controller.HandleRequest(
            new GameEvents.CharacterConfirmationRequest(2u, 42u, "Raise this skill?")));
        Assert.Equal(
            "Raise this skill? Continue?",
            string.Join(" ", Assert.IsType<UiText>(shown!.FindElement(
                RetailConfirmationDialogView.MessageElementId)).LinesProvider().Select(static line => line.Text)));

        Assert.IsType<UiButton>(shown.FindElement(
            RetailConfirmationDialogView.AcceptButtonId)).OnClick!();

        Assert.Equal([(2u, 42u, true)], responses);
        Assert.Equal(0u, controller.ActiveDialogContext);
    }

    [Fact]
    public void FellowshipInviteRequest_Type4_OpensDialog_MessageVerbatim_AndSendsAcceptOnClose()
    {
        var root = new UiRoot { Width = 800f, Height = 600f };
        ImportedLayout? shown = null;
        var factory = new RetailDialogFactory(root, _ =>
            shown = FixtureLoader.LoadConfirmationDialog());
        var responses = new List<(uint Type, uint Context, bool Accepted)>();
        using var controller = new GameplayConfirmationController(
            factory,
            (type, context, accepted) => responses.Add((type, context, accepted)));

        Assert.True(controller.HandleRequest(
            new GameEvents.CharacterConfirmationRequest(4u, 7u, "Alice invites you to join their fellowship.")));

        Assert.Equal(
            "Alice invites you to join their fellowship.",
            string.Join(" ", Assert.IsType<UiText>(shown!.FindElement(
                RetailConfirmationDialogView.MessageElementId)).LinesProvider().Select(static line => line.Text)));

        Assert.IsType<UiButton>(shown.FindElement(
            RetailConfirmationDialogView.AcceptButtonId)).OnClick!();

        Assert.Equal([(4u, 7u, true)], responses);
        Assert.Equal(0u, controller.ActiveDialogContext);
    }

    [Fact]
    public void InjectedComposerWrapsTypes1And4_AndNeverTouchesContinueFamily()
    {
        var root = new UiRoot { Width = 800f, Height = 600f };
        ImportedLayout? shown = null;
        var factory = new RetailDialogFactory(root, _ =>
            shown = FixtureLoader.LoadConfirmationDialog());
        var composed = new List<uint>();
        using var controller = new GameplayConfirmationController(
            factory,
            (_, _, _) => { },
            (type, bareName) =>
            {
                composed.Add(type);
                return type == 4u
                    ? bareName
                        + " has invited you to join their fellowship. Do you accept?"
                    : null;
            });

        Assert.True(controller.HandleRequest(
            new GameEvents.CharacterConfirmationRequest(4u, 7u, "Alice")));
        Assert.Equal(
            "Alice has invited you to join their fellowship. Do you accept?",
            string.Join(" ", Assert.IsType<UiText>(shown!.FindElement(
                RetailConfirmationDialogView.MessageElementId)).LinesProvider()
                    .Select(static line => line.Text)));
        Assert.IsType<UiButton>(shown.FindElement(
            RetailConfirmationDialogView.AcceptButtonId)).OnClick!();

        Assert.True(controller.HandleRequest(
            new GameEvents.CharacterConfirmationRequest(1u, 8u, "Bob")));
        Assert.Equal(
            "Bob",
            string.Join(" ", Assert.IsType<UiText>(shown!.FindElement(
                RetailConfirmationDialogView.MessageElementId)).LinesProvider()
                    .Select(static line => line.Text)));
        Assert.IsType<UiButton>(shown.FindElement(
            RetailConfirmationDialogView.AcceptButtonId)).OnClick!();

        Assert.True(controller.HandleRequest(
            new GameEvents.CharacterConfirmationRequest(2u, 9u, "Raise this skill?")));
        Assert.Equal(
            "Raise this skill? Continue?",
            string.Join(" ", Assert.IsType<UiText>(shown!.FindElement(
                RetailConfirmationDialogView.MessageElementId)).LinesProvider()
                    .Select(static line => line.Text)));
        Assert.Equal([4u, 1u], composed);
    }

    [Fact]
    public void AllegianceSwearRequest_Type1_OpensDialog_MessageVerbatim_AndSendsAcceptOnClose()
    {
        var root = new UiRoot { Width = 800f, Height = 600f };
        ImportedLayout? shown = null;
        var factory = new RetailDialogFactory(root, _ =>
            shown = FixtureLoader.LoadConfirmationDialog());
        var responses = new List<(uint Type, uint Context, bool Accepted)>();
        using var controller = new GameplayConfirmationController(
            factory,
            (type, context, accepted) => responses.Add((type, context, accepted)));

        Assert.True(controller.HandleRequest(
            new GameEvents.CharacterConfirmationRequest(1u, 13u, "Bob")));

        Assert.Equal(
            "Bob",
            string.Join(" ", Assert.IsType<UiText>(shown!.FindElement(
                RetailConfirmationDialogView.MessageElementId)).LinesProvider().Select(static line => line.Text)));

        Assert.IsType<UiButton>(shown.FindElement(
            RetailConfirmationDialogView.AcceptButtonId)).OnClick!();

        Assert.Equal([(1u, 13u, true)], responses);
        Assert.Equal(0u, controller.ActiveDialogContext);
    }

    [Fact]
    public void MatchingConfirmationDoneClosesDialogAndUnmatchedTupleDoesNothing()
    {
        var root = new UiRoot { Width = 800f, Height = 600f };
        var factory = new RetailDialogFactory(root, _ =>
            FixtureLoader.LoadConfirmationDialog());
        var responses = new List<(uint Type, uint Context, bool Accepted)>();
        using var controller = new GameplayConfirmationController(
            factory,
            (type, context, accepted) => responses.Add((type, context, accepted)));
        controller.HandleRequest(new GameEvents.CharacterConfirmationRequest(7u, 99u, "Proceed?"));

        Assert.False(controller.HandleDone(
            new GameEvents.CharacterConfirmationDone(7u, 100u)));
        Assert.True(factory.IsOpen);
        Assert.True(controller.HandleDone(
            new GameEvents.CharacterConfirmationDone(7u, 99u)));

        Assert.False(factory.IsOpen);
        Assert.Equal([(7u, 99u, false)], responses);
    }

    [Fact]
    public void FactoryReset_CompletesResponseBeforeSessionTupleIsForgotten()
    {
        var root = new UiRoot { Width = 800f, Height = 600f };
        var factory = new RetailDialogFactory(root, _ =>
            FixtureLoader.LoadConfirmationDialog());
        var responses = new List<(uint Type, uint Context, bool Accepted)>();
        using var controller = new GameplayConfirmationController(
            factory,
            (type, context, accepted) => responses.Add((type, context, accepted)));
        controller.HandleRequest(
            new GameEvents.CharacterConfirmationRequest(7u, 99u, "Proceed?"));

        factory.Reset();
        controller.ResetSession();

        Assert.Equal(0u, controller.ActiveDialogContext);
        Assert.Equal([(7u, 99u, false)], responses);
        Assert.False(factory.IsOpen);
    }

    [Fact]
    public void TryAnswerProducesTheSameResponseAsAButtonDrivenClose()
    {
        // TryAnswer is the plugin-facing path (IDialogAutomation.Answer);
        // the button click is the human path. Both must end up sending the
        // exact same (type, context, accepted) tuple through _sendResponse.
        var rootButton = new UiRoot { Width = 800f, Height = 600f };
        ImportedLayout? shownButton = null;
        var buttonFactory = new RetailDialogFactory(rootButton, _ =>
            shownButton = FixtureLoader.LoadConfirmationDialog());
        var buttonResponses = new List<(uint Type, uint Context, bool Accepted)>();
        using var buttonController = new GameplayConfirmationController(
            buttonFactory,
            (type, context, accepted) =>
                buttonResponses.Add((type, context, accepted)));
        buttonController.HandleRequest(
            new GameEvents.CharacterConfirmationRequest(2u, 42u, "Raise this skill?"));
        Assert.IsType<UiButton>(shownButton!.FindElement(
            RetailConfirmationDialogView.AcceptButtonId)).OnClick!();

        var rootPlugin = new UiRoot { Width = 800f, Height = 600f };
        var pluginFactory = new RetailDialogFactory(rootPlugin, _ =>
            FixtureLoader.LoadConfirmationDialog());
        var pluginResponses = new List<(uint Type, uint Context, bool Accepted)>();
        using var pluginController = new GameplayConfirmationController(
            pluginFactory,
            (type, context, accepted) =>
                pluginResponses.Add((type, context, accepted)));
        pluginController.HandleRequest(
            new GameEvents.CharacterConfirmationRequest(2u, 42u, "Raise this skill?"));

        bool answered = pluginController.TryAnswer(42u, true);

        Assert.True(answered);
        Assert.Equal(buttonResponses, pluginResponses);
        Assert.Equal([(2u, 42u, true)], pluginResponses);
        Assert.Equal(0u, pluginController.ActiveDialogContext);
    }

    [Fact]
    public void TryAnswerWithTheWrongContextIdFailsAndSendsNothing()
    {
        var root = new UiRoot { Width = 800f, Height = 600f };
        var factory = new RetailDialogFactory(root, _ =>
            FixtureLoader.LoadConfirmationDialog());
        var responses = new List<(uint Type, uint Context, bool Accepted)>();
        using var controller = new GameplayConfirmationController(
            factory,
            (type, context, accepted) => responses.Add((type, context, accepted)));
        controller.HandleRequest(
            new GameEvents.CharacterConfirmationRequest(2u, 42u, "Raise this skill?"));

        bool answered = controller.TryAnswer(999u, true);

        Assert.False(answered);
        Assert.Empty(responses);
        Assert.NotEqual(0u, controller.ActiveDialogContext);
    }

    [Fact]
    public void SecondTryAnswerAfterTheDialogClosedFails()
    {
        var root = new UiRoot { Width = 800f, Height = 600f };
        var factory = new RetailDialogFactory(root, _ =>
            FixtureLoader.LoadConfirmationDialog());
        var responses = new List<(uint Type, uint Context, bool Accepted)>();
        using var controller = new GameplayConfirmationController(
            factory,
            (type, context, accepted) => responses.Add((type, context, accepted)));
        controller.HandleRequest(
            new GameEvents.CharacterConfirmationRequest(2u, 42u, "Raise this skill?"));

        Assert.True(controller.TryAnswer(42u, true));
        Assert.False(controller.TryAnswer(42u, true));

        Assert.Equal([(2u, 42u, true)], responses);
    }

    [Fact]
    public void TryAnswerWithNoDialogOpenFails()
    {
        var root = new UiRoot { Width = 800f, Height = 600f };
        var factory = new RetailDialogFactory(root, _ =>
            FixtureLoader.LoadConfirmationDialog());
        var responses = new List<(uint Type, uint Context, bool Accepted)>();
        using var controller = new GameplayConfirmationController(
            factory,
            (type, context, accepted) => responses.Add((type, context, accepted)));

        Assert.False(controller.TryAnswer(42u, true));
        Assert.Empty(responses);
    }

    [Fact]
    public void OverlappingRequestWhileADialogIsOpenDoesNotStealItsAnswerTuple()
    {
        // HandleRequest used to overwrite _serverType/_serverContext before
        // checking whether a dialog was already open. A second request that
        // arrived while the first dialog was still up would silently steal
        // the response tuple, so closing the FIRST dialog sent the SECOND
        // request's type/context.
        var root = new UiRoot { Width = 800f, Height = 600f };
        var factory = new RetailDialogFactory(root, _ =>
            FixtureLoader.LoadConfirmationDialog());
        var responses = new List<(uint Type, uint Context, bool Accepted)>();
        using var controller = new GameplayConfirmationController(
            factory,
            (type, context, accepted) => responses.Add((type, context, accepted)));

        Assert.True(controller.HandleRequest(
            new GameEvents.CharacterConfirmationRequest(2u, 42u, "Raise this skill?")));
        uint firstDialogContext = controller.ActiveDialogContext;

        // Rejected: a dialog is already open.
        Assert.False(controller.HandleRequest(
            new GameEvents.CharacterConfirmationRequest(4u, 7u, "Alice invites you.")));

        // The rejected second request must not have replaced the open
        // dialog's context -- a rejected request must never overwrite the answer route.
        Assert.Equal(firstDialogContext, controller.ActiveDialogContext);

        Assert.True(controller.TryAnswer(42u, true));
        Assert.Equal([(2u, 42u, true)], responses);
    }

}
