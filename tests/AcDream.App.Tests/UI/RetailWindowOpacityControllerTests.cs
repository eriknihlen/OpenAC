using AcDream.App.UI;

namespace AcDream.App.Tests.UI;

public sealed class RetailWindowOpacityControllerTests
{
    private static UiRoot NewRoot() => new() { Width = 800f, Height = 600f };

    private static (RetailWindowHandle handle, UiElement child) RegisterWindow(UiRoot root, string name)
    {
        var frame = new UiPanel { Width = 100f, Height = 100f };
        var child = new UiPanel { Width = 10f, Height = 10f, AcceptsFocus = true };
        frame.AddChild(child);
        root.AddChild(frame);
        RetailWindowHandle handle = root.WindowManager.Register(name, frame);
        return (handle, child);
    }

    [Fact]
    public void Construction_AttachesToAlreadyRegisteredWindows_AtDefaultOpacity()
    {
        UiRoot root = NewRoot();
        (RetailWindowHandle handle, _) = RegisterWindow(root, WindowNames.Chat);

        var controller = new RetailWindowOpacityController(
            root.WindowManager, defaultOpacity: 0.5f, activeOpacity: 1.0f);

        Assert.Equal(0.5f, handle.Opacity);
        Assert.Equal(0.5f, controller.DefaultOpacity);
        Assert.Equal(1.0f, controller.ActiveOpacity);
    }

    [Fact]
    public void WindowRegisteredAfterConstruction_PicksUpLiveOpacityImmediately()
    {
        UiRoot root = NewRoot();
        var controller = new RetailWindowOpacityController(
            root.WindowManager, defaultOpacity: 0.3f, activeOpacity: 0.9f);

        (RetailWindowHandle handle, _) = RegisterWindow(root, WindowNames.ChatWindow1);

        Assert.Equal(0.3f, handle.Opacity);
    }

    [Fact]
    public void FocusEnteringAWindow_SwitchesToActiveOpacity_LeavingSwitchesBack()
    {
        UiRoot root = NewRoot();
        (RetailWindowHandle handle, UiElement child) = RegisterWindow(root, WindowNames.Chat);
        var controller = new RetailWindowOpacityController(
            root.WindowManager, defaultOpacity: 0.5f, activeOpacity: 1.0f);
        Assert.Equal(0.5f, handle.Opacity);

        root.SetKeyboardFocus(child);
        Assert.Equal(1.0f, handle.Opacity);

        root.SetKeyboardFocus(null);
        Assert.Equal(0.5f, handle.Opacity);
        GC.KeepAlive(controller);
    }

    [Fact]
    public void OpacityFade_AppliesOnlyToChatWindows_NeverOtherPanels()
    {
        UiRoot root = NewRoot();
        (RetailWindowHandle vitals, _) = RegisterWindow(root, WindowNames.Vitals);
        (RetailWindowHandle toolbar, UiElement toolbarChild) = RegisterWindow(root, WindowNames.Toolbar);
        (RetailWindowHandle chat, _) = RegisterWindow(root, WindowNames.Chat);
        (RetailWindowHandle floaty1, UiElement floaty1Child) = RegisterWindow(root, WindowNames.ChatWindow1);
        var controller = new RetailWindowOpacityController(
            root.WindowManager, defaultOpacity: 0.4f, activeOpacity: 1.0f);

        Assert.Equal(1.0f, vitals.Opacity);
        Assert.Equal(1.0f, toolbar.Opacity);
        Assert.Equal(0.4f, chat.Opacity);
        Assert.Equal(0.4f, floaty1.Opacity);

        root.SetKeyboardFocus(toolbarChild);

        Assert.Equal(1.0f, vitals.Opacity);
        Assert.Equal(1.0f, toolbar.Opacity);
        Assert.Equal(0.4f, chat.Opacity);
        Assert.Equal(0.4f, floaty1.Opacity);

        root.SetKeyboardFocus(floaty1Child);
        controller.SetActiveOpacity(0.6f);

        Assert.Equal(1.0f, vitals.Opacity);
        Assert.Equal(1.0f, toolbar.Opacity);
        Assert.Equal(0.4f, chat.Opacity);
        Assert.Equal(0.6f, floaty1.Opacity);
    }

    [Fact]
    public void SetDefaultOpacity_AboveCurrentActive_DragsActiveUp_AndReappliesEverywhere()
    {
        UiRoot root = NewRoot();
        (RetailWindowHandle unfocused, _) = RegisterWindow(root, WindowNames.Chat);
        (RetailWindowHandle focused, UiElement focusedChild) = RegisterWindow(root, WindowNames.ChatWindow1);
        var controller = new RetailWindowOpacityController(
            root.WindowManager, defaultOpacity: 0.3f, activeOpacity: 0.5f);
        root.SetKeyboardFocus(focusedChild);
        Assert.Equal(0.3f, unfocused.Opacity);
        Assert.Equal(0.5f, focused.Opacity);

        controller.SetDefaultOpacity(0.9f);

        Assert.Equal(0.9f, controller.DefaultOpacity);
        Assert.Equal(0.9f, controller.ActiveOpacity);
        Assert.Equal(0.9f, unfocused.Opacity);
        Assert.Equal(0.9f, focused.Opacity);
    }

    [Fact]
    public void SetActiveOpacity_BelowCurrentDefault_DragsDefaultDown_AndReappliesEverywhere()
    {
        UiRoot root = NewRoot();
        (RetailWindowHandle unfocused, _) = RegisterWindow(root, WindowNames.Chat);
        (RetailWindowHandle focused, UiElement focusedChild) = RegisterWindow(root, WindowNames.ChatWindow1);
        var controller = new RetailWindowOpacityController(
            root.WindowManager, defaultOpacity: 0.5f, activeOpacity: 0.7f);
        root.SetKeyboardFocus(focusedChild);

        controller.SetActiveOpacity(0.1f);

        Assert.Equal(0.1f, controller.DefaultOpacity);
        Assert.Equal(0.1f, controller.ActiveOpacity);
        Assert.Equal(0.1f, unfocused.Opacity);
        Assert.Equal(0.1f, focused.Opacity);
    }

    [Fact]
    public void SetOpacity_AppliesBothInRetailsUpdateFromPlayerModuleOrder()
    {
        UiRoot root = NewRoot();
        (RetailWindowHandle handle, _) = RegisterWindow(root, WindowNames.Chat);
        var controller = new RetailWindowOpacityController(
            root.WindowManager, defaultOpacity: 0.5f, activeOpacity: 1.0f);

        controller.SetOpacity(defaultOpacity: 0.2f, activeOpacity: 0.6f);

        Assert.Equal(0.2f, controller.DefaultOpacity);
        Assert.Equal(0.6f, controller.ActiveOpacity);
        Assert.Equal(0.2f, handle.Opacity);
    }

    [Fact]
    public void ConstructorSeed_EnforcesTheActiveGreaterThanOrEqualDefaultInvariant()
    {
        UiRoot root = NewRoot();
        var controller = new RetailWindowOpacityController(
            root.WindowManager, defaultOpacity: 0.8f, activeOpacity: 0.2f);

        Assert.True(controller.ActiveOpacity >= controller.DefaultOpacity);
        Assert.Equal(0.2f, controller.DefaultOpacity);
        Assert.Equal(0.2f, controller.ActiveOpacity);
    }

    [Fact]
    public void Dispose_UnsubscribesFromFocusChanges()
    {
        UiRoot root = NewRoot();
        (RetailWindowHandle handle, UiElement child) = RegisterWindow(root, WindowNames.Chat);
        var controller = new RetailWindowOpacityController(
            root.WindowManager, defaultOpacity: 0.5f, activeOpacity: 1.0f);

        controller.Dispose();
        root.SetKeyboardFocus(child);

        Assert.Equal(0.5f, handle.Opacity);
    }

    [Fact]
    public void WindowUnregistered_DetachesSubscription_AndForgetsFocusedState()
    {
        UiRoot root = NewRoot();
        (RetailWindowHandle handle, UiElement child) = RegisterWindow(root, WindowNames.Chat);
        var controller = new RetailWindowOpacityController(
            root.WindowManager, defaultOpacity: 0.5f, activeOpacity: 1.0f);

        root.SetKeyboardFocus(child);
        Assert.Equal(1.0f, handle.Opacity);

        root.WindowManager.Unregister(WindowNames.Chat);

        Assert.Equal(0.5f, handle.Opacity);

        controller.SetActiveOpacity(0.7f);

        handle.NotifyDescendantFocusChanged(child);

        Assert.Equal(0.5f, handle.Opacity);
    }

    [Fact]
    public void SetMutators_AfterDispose_AreNoOps()
    {
        UiRoot root = NewRoot();
        (RetailWindowHandle handle, _) = RegisterWindow(root, WindowNames.Chat);
        var controller = new RetailWindowOpacityController(
            root.WindowManager, defaultOpacity: 0.5f, activeOpacity: 1.0f);

        controller.Dispose();
        controller.SetDefaultOpacity(0.9f);
        controller.SetActiveOpacity(0.9f);
        controller.SetOpacity(0.2f, 0.3f);

        Assert.Equal(0.5f, controller.DefaultOpacity);
        Assert.Equal(1.0f, controller.ActiveOpacity);
        Assert.Equal(0.5f, handle.Opacity);
    }
}
