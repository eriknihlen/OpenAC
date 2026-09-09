using AcDream.App.Settings;
using AcDream.UI.Abstractions.Panels.Settings;

namespace AcDream.App.Tests.Settings;

public sealed partial class RuntimeSettingsControllerTests
{
    [Fact]
    public void DirectLaunchUsesGameplaySizeWithoutEnablingLayoutPersistenceThenReturnsToPregameOnLogout()
    {
        var window = new SessionWindow();
        var target = new SessionDisplayWindowTarget(window, directCharacterLaunch: true);
        var preferred = DisplaySettings.Default with { Resolution = "1760x990", Fullscreen = true };
        target.Apply(preferred);
        target.SetGameplay(false);
        Assert.False(target.IsGameplay);
        Assert.Same(preferred, window.Applied[^1]);
        target.SetGameplay(true);
        Assert.True(target.IsGameplay);
        Assert.Same(preferred, window.Applied[^1]);
        target.SetGameplay(false);
        Assert.Equal(("800x600", false), (window.Applied[^1].Resolution, window.Applied[^1].Fullscreen));
    }

    [Fact]
    public void DirectLaunchFailureRestoresPregameSizeWithoutOverwritingPreferences()
    {
        var window = new SessionWindow();
        var target = new SessionDisplayWindowTarget(window, directCharacterLaunch: true);
        var preferred = DisplaySettings.Default with { Resolution = "1760x990", Fullscreen = true };
        target.Apply(preferred);
        target.EndDirectLaunch();
        Assert.False(target.IsGameplay);
        Assert.Equal(("800x600", false), (window.Applied[^1].Resolution, window.Applied[^1].Fullscreen));
        target.SetGameplay(true);
        Assert.Same(preferred, window.Applied[^1]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PregameAndLogoutKeepTheGameplayPreferenceWithoutSavingTheTemporaryViewport(bool fullscreen)
    {
        var preferred = DisplaySettings.Default with { Resolution = "1920x1080", Fullscreen = fullscreen };
        var storage = new FakeStorage { DisplayValue = preferred };
        var controller = new RuntimeSettingsController(storage, log: _ => { });
        var window = new SessionWindow();
        controller.BindDisplayWindow(window);
        controller.ApplyStartup(new SessionStartup(controller.DisplayWindowTarget!));

        Assert.False(controller.IsGameplayDisplay);
        Assert.Equal(("800x600", false), (window.Applied[^1].Resolution, window.Applied[^1].Fullscreen));
        Assert.Same(preferred, controller.Display);
        Assert.Equal(0, storage.DisplaySaves);

        controller.SetGameplayDisplay(true);
        Assert.True(controller.IsGameplayDisplay);
        Assert.Same(preferred, window.Applied[^1]);
        int calls = window.Applied.Count;
        controller.SetGameplayDisplay(true);
        Assert.Equal(calls, window.Applied.Count);

        controller.SetGameplayDisplay(false);
        Assert.Equal(("800x600", false), (window.Applied[^1].Resolution, window.Applied[^1].Fullscreen));
        controller.SetGameplayDisplay(true);
        Assert.Same(preferred, window.Applied[^1]);
        Assert.Equal(0, storage.DisplaySaves);
        Assert.Same(preferred, storage.DisplayValue);
    }

    [Fact]
    public void GameplayResolutionChangesSurviveReturningThroughCharacterSelection()
    {
        var window = new SessionWindow();
        var target = new SessionDisplayWindowTarget(window);
        target.Apply(DisplaySettings.Default with { Resolution = "1600x900" });
        target.SetGameplay(true);
        var changed = DisplaySettings.Default with { Resolution = "2560x1440", Fullscreen = true };
        target.Apply(changed);
        target.SetGameplay(false);
        Assert.Equal("800x600", window.Applied[^1].Resolution);
        target.SetGameplay(true);
        Assert.Same(changed, window.Applied[^1]);
    }

    [Fact]
    public void FixedAutomationViewportStillTracksGameplayWithoutChangingItsExtent()
    {
        var window = new SessionWindow();
        var target = new SessionDisplayWindowTarget(window, fixedAutomationViewport: true);
        var preferred = DisplaySettings.Default with { Resolution = "2560x1440" };
        target.Apply(preferred);
        Assert.False(target.IsGameplay);
        target.SetGameplay(true);
        target.SetGameplay(false);
        Assert.All(window.Applied, display => Assert.Same(preferred, display));
    }

    [Fact]
    public void FullscreenGameplayReturnsToWindowedPregameThenReentersTheSavedMode()
    {
        var surface = new FakeSizeSurface();
        var modes = new FakeModeSwitcher();
        var target = new SessionDisplayWindowTarget(
            new SilkRuntimeDisplayWindowTarget(surface, modes, _ => true));
        target.Apply(DisplaySettings.Default with { Resolution = "1920x1080", Fullscreen = true });
        Assert.Equal(new Silk.NET.Maths.Vector2D<int>(800, 600), surface.Size);
        Assert.False(modes.IsFullscreen);
        Assert.Empty(modes.Calls);

        target.SetGameplay(true);
        Assert.True(modes.IsFullscreen);
        target.SetGameplay(false);
        Assert.False(modes.IsFullscreen);
        target.SetGameplay(true);
        Assert.True(modes.IsFullscreen);
        Assert.Equal(["enter:1920x1080", "leave:800x600", "enter:1920x1080"], modes.Calls);
    }

    private sealed class SessionWindow : IRuntimeDisplayWindowTarget
    {
        public List<DisplaySettings> Applied { get; } = [];
        public RuntimeDisplayApplyResult Apply(DisplaySettings display)
        {
            Applied.Add(display);
            return new RuntimeDisplayApplyResult(display.Fullscreen);
        }
    }

    private sealed class SessionStartup(IRuntimeDisplayWindowTarget target) : IRuntimeSettingsStartupTarget
    {
        public RuntimeDisplayApplyResult ApplyDisplay(DisplaySettings display) => target.Apply(display);
        public void ApplyAudio(AudioSettings audio) { }
    }
}
