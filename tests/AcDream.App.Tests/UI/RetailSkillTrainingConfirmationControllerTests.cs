using AcDream.App.UI;
using AcDream.App.UI.Layout;
using AcDream.App.Tests.UI.Layout;

namespace AcDream.App.Tests.UI;

public sealed class RetailSkillTrainingConfirmationControllerTests
{
    private const uint Healing = 21u;

    [Fact]
    public void AcceptSendsStoredSkillAndCreditsOnlyAfterConfirmation()
    {
        var root = new UiRoot { Width = 800f, Height = 600f };
        ImportedLayout? shown = null;
        var factory = new RetailDialogFactory(root, _ =>
            shown = FixtureLoader.LoadConfirmationDialog());
        var controller = new RetailSkillTrainingConfirmationController(factory);
        var sent = new List<CharacterStatController.RaiseRequest>();
        bool completed = false;

        controller.Request(Request(), Sheet(), sent.Add, () => completed = true);

        Assert.Empty(sent);
        Assert.False(completed);
        Assert.Equal(
            "Are you sure you want to spend 6 credits to train Healing?",
            string.Join(" ", Assert.IsType<UiText>(shown!.FindElement(
                RetailConfirmationDialogView.MessageElementId)).LinesProvider().Select(static line => line.Text)));

        Assert.IsType<UiButton>(shown.FindElement(
            RetailConfirmationDialogView.AcceptButtonId)).OnClick!();

        CharacterStatController.RaiseRequest request = Assert.Single(sent);
        Assert.Equal(CharacterStatController.RaiseTargetKind.TrainSkill, request.Kind);
        Assert.Equal(Healing, request.StatId);
        Assert.Equal(6L, request.Cost);
        Assert.Equal(1, request.Amount);
        Assert.True(completed);
        Assert.False(factory.IsOpen);
    }

    [Fact]
    public void RejectCompletesWithoutSendingTrainingRequest()
    {
        var root = new UiRoot { Width = 800f, Height = 600f };
        ImportedLayout? shown = null;
        var factory = new RetailDialogFactory(root, _ =>
            shown = FixtureLoader.LoadConfirmationDialog());
        var controller = new RetailSkillTrainingConfirmationController(factory);
        var sent = new List<CharacterStatController.RaiseRequest>();
        bool completed = false;

        controller.Request(Request(), Sheet(), sent.Add, () => completed = true);
        Assert.IsType<UiButton>(shown!.FindElement(
            RetailConfirmationDialogView.RejectButtonId)).OnClick!();

        Assert.Empty(sent);
        Assert.True(completed);
        Assert.False(factory.IsOpen);
    }

    private static CharacterStatController.RaiseRequest Request()
        => new(CharacterStatController.RaiseTargetKind.TrainSkill, Healing, 6L, 1);

    private static CharacterSheet Sheet()
        => new()
        {
            SkillCredits = 10,
            Skills =
            [
                new CharacterSkill(
                    Healing,
                    "Healing",
                    0x06000133u,
                    CharacterSkillAdvancementClass.Untrained,
                    BaseLevel: 10,
                    CurrentLevel: 10,
                    UsableUntrained: true,
                    TrainedCost: 6,
                    SpecializedCost: 10,
                    RaiseCost: 0L),
            ],
        };
}
