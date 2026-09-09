using System.Numerics;
using AcDream.App.UI;
using AcDream.App.UI.Layout;

namespace AcDream.App.Tests.UI.Layout;

public sealed class CreditsUiControllerTests
{
    [Fact]
    public void Activate_UsesAuthoredCanvas_TextTiming_AndCyclicPictureStrip()
    {
        using var environment = new EnvironmentHarness();
        CreditsUiController controller = environment.Controller;

        controller.Activate();

        Assert.True(controller.IsActive);
        Assert.Equal(new Vector2(800f, 600f), environment.Host.FixedCanvasSize);
        Assert.True(controller.PictureRoot.Visible);
        Assert.True(controller.TextRoot.Visible);
        Assert.Equal(600f, controller.TextArea.Top);
        Assert.Equal(48f, controller.TextArea.Height);
        Assert.Single(controller.Pictures);
        Assert.Equal(601f, controller.Pictures[0].Top);
        Assert.Equal(0x06000001u, controller.Pictures[0].BackgroundSprite);
        Assert.Equal(
            20d * (600d + 48d) / (600d + 48d / 3d),
            controller.DurationSeconds,
            precision: 5);

        environment.Now += controller.DurationSeconds * 0.5d;
        controller.Tick();

        Assert.Equal(276f, controller.TextArea.Top);
        Assert.True(controller.Pictures[0].Top < 600f);
        Assert.Equal(2, controller.Pictures.Count);
        Assert.Equal(0x06000002u, controller.Pictures[1].BackgroundSprite);
        Assert.Equal(
            controller.Pictures[0].Top + controller.Pictures[0].Height + 1f,
            controller.Pictures[1].Top);
    }

    [Fact]
    public void AnyKey_ShowsWaitForAFrame_ThenReturnsToCharacterManagement()
    {
        using var environment = new EnvironmentHarness();
        CreditsUiController controller = environment.Controller;
        controller.Activate();

        environment.Host.OnKeyDown(123);
        Assert.Equal(1, environment.Dialogs.ActiveCount);

        controller.Tick();
        Assert.True(controller.IsActive);
        Assert.Equal(0, environment.ReturnCalls);

        controller.Tick();
        Assert.False(controller.IsActive);
        Assert.Equal(1, environment.ReturnCalls);
        Assert.Equal(0, environment.Dialogs.ActiveCount);
        Assert.Null(environment.Host.FixedCanvasSize);
        Assert.False(controller.PictureRoot.Visible);
        Assert.False(controller.TextRoot.Visible);
    }

    [Fact]
    public void NaturalCompletion_UsesTheSameWaitAndReturnPath()
    {
        using var environment = new EnvironmentHarness();
        CreditsUiController controller = environment.Controller;
        controller.Activate();

        environment.Now += controller.DurationSeconds;
        controller.Tick();
        Assert.Equal(1, environment.Dialogs.ActiveCount);

        controller.Tick();
        Assert.True(controller.IsActive);
        controller.Tick();

        Assert.False(controller.IsActive);
        Assert.Equal(1, environment.ReturnCalls);
        Assert.Equal(0, environment.Dialogs.ActiveCount);
    }

    private sealed class EnvironmentHarness : IDisposable
    {
        public EnvironmentHarness()
        {
            Host = new UiRoot { Width = 1280f, Height = 720f };
            Dialogs = new RetailDialogFactory(
                Host,
                RetailDialogFactoryTests.BuildDialogLayout);
            CreditsUiResources resources = BuildResources();
            Controller = Assert.IsType<CreditsUiController>(
                CreditsUiController.CreateDetached(
                    Host,
                    resources,
                    Dialogs,
                    () => Now,
                    ResolveSprite,
                    () => ReturnCalls++));
        }

        public UiRoot Host { get; }
        public RetailDialogFactory Dialogs { get; }
        public CreditsUiController Controller { get; }
        public double Now { get; set; } = 100d;
        public int ReturnCalls { get; private set; }

        public void Dispose()
        {
            Controller.Dispose();
            Dialogs.Dispose();
        }

        private static (uint tex, int w, int h) ResolveSprite(uint id)
            => (id, 400, 300);

        private static CreditsUiResources BuildResources()
        {
            ImportedLayout picture = LayoutImporter.Build(
                new ElementInfo
                {
                    Id = CreditsUiController.PictureRootElementId,
                    Type = 3u,
                    X = 0f,
                    Y = 0f,
                    Width = 400f,
                    Height = 600f,
                },
                ResolveSprite,
                null);
            var textRoot = new ElementInfo
            {
                Id = CreditsUiController.TextRootElementId,
                Type = 3u,
                X = 400f,
                Y = 0f,
                Width = 400f,
                Height = 600f,
            };
            textRoot.Children.Add(new ElementInfo
            {
                Id = CreditsUiController.TextAreaElementId,
                Type = 12u,
                Width = 0f,
                Height = 0f,
                HJustify = HJustify.Center,
                VJustify = VJustify.Center,
            });
            ImportedLayout text = LayoutImporter.Build(
                textRoot,
                ResolveSprite,
                null);
            return new CreditsUiResources(
                0x21000003u,
                picture,
                text,
                ["One\n", "Two\n"],
                [0x06000001u, 0x06000002u],
                20f,
                "Please Wait");
        }
    }
}
