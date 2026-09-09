using AcDream.App.Combat;
using AcDream.App.Diagnostics;
using AcDream.App.UI;
using AcDream.Runtime;
using AcDream.UI.Abstractions.Input;

namespace AcDream.App.Input;

internal interface IRetainedGameplayWindowCommands
{
    void ToggleInventory();

    void ToggleFloatingChatWindow(int windowId);

    void ToggleOptionsPanel();

    void ToggleGameplayOptionsPage();

    void FocusChatEntry();

    void LogOutCharacter();
}

internal sealed class RetainedGameplayWindowCommands(RetailUiRuntime? runtime)
    : IRetainedGameplayWindowCommands
{
    private readonly RetailUiRuntime? _runtime = runtime;

    public void ToggleInventory() =>
        _runtime?.ToggleWindow(WindowNames.Inventory);

    public void ToggleFloatingChatWindow(int windowId) =>
        _runtime?.ToggleFloatingChatWindow(windowId);

    public void ToggleOptionsPanel() =>
        _runtime?.ToggleWindow(WindowNames.Options);

    public void ToggleGameplayOptionsPage() =>
        _runtime?.ToggleGameplayOptionsPage();

    public void FocusChatEntry() => _runtime?.FocusChatEntry();

    public void LogOutCharacter() => _runtime?.LogOutCharacter();
}

internal interface IPlayerModeGameplayCommands
{
    void ToggleFlyOrChase();

    void TogglePlayerMode();
}

internal sealed class PlayerModeGameplayCommands(PlayerModeController controller)
    : IPlayerModeGameplayCommands
{
    private readonly PlayerModeController _controller = controller
        ?? throw new ArgumentNullException(nameof(controller));

    public void ToggleFlyOrChase() => _controller.ToggleFlyOrChase();

    public void TogglePlayerMode() => _controller.Toggle();
}

internal interface IItemTargetModeCommands
{
    bool IsAnyTargetModeActive { get; }

    void CancelTargetMode();
}

internal sealed class ItemTargetModeCommands(ItemInteractionController items)
    : IItemTargetModeCommands
{
    private readonly ItemInteractionController _items = items
        ?? throw new ArgumentNullException(nameof(items));

    public bool IsAnyTargetModeActive => _items.IsAnyTargetModeActive;

    public void CancelTargetMode() => _items.CancelTargetMode();
}

internal interface IGameplayInputCommandTarget
{
    bool Handle(InputAction action);
}

internal sealed class GameplayInputCommandController : IGameplayInputCommandTarget
{
    private readonly IRetainedGameplayWindowCommands _retained;
    private readonly IRuntimeDiagnosticCommands _diagnostics;
    private readonly IPlayerModeGameplayCommands _playerMode;
    private readonly IItemTargetModeCommands _targetMode;
    private readonly IGameRuntimeView _runtimeView;
    private readonly IRuntimeCombatCommands _combat;
    private readonly Action? _toggleAudioMute;

    public GameplayInputCommandController(
        IRetainedGameplayWindowCommands retained,
        IRuntimeDiagnosticCommands diagnostics,
        IPlayerModeGameplayCommands playerMode,
        IItemTargetModeCommands targetMode,
        IGameRuntimeView runtimeView,
        IRuntimeCombatCommands combat,
        Action? toggleAudioMute = null)
    {
        _retained = retained ?? throw new ArgumentNullException(nameof(retained));
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        _playerMode = playerMode ?? throw new ArgumentNullException(nameof(playerMode));
        _targetMode = targetMode ?? throw new ArgumentNullException(nameof(targetMode));
        _runtimeView = runtimeView
            ?? throw new ArgumentNullException(nameof(runtimeView));
        _combat = combat ?? throw new ArgumentNullException(nameof(combat));
        _toggleAudioMute = toggleAudioMute;
    }

    public bool Handle(InputAction action)
    {
        if (_diagnostics.Handle(action))
            return true;

        switch (action)
        {
            case InputAction.ToggleInventoryPanel:
                _retained.ToggleInventory();
                return true;
            case InputAction.ToggleFloatingChatWindow1:
                _retained.ToggleFloatingChatWindow(1);
                return true;
            case InputAction.ToggleFloatingChatWindow2:
                _retained.ToggleFloatingChatWindow(2);
                return true;
            case InputAction.ToggleFloatingChatWindow3:
                _retained.ToggleFloatingChatWindow(3);
                return true;
            case InputAction.ToggleFloatingChatWindow4:
                _retained.ToggleFloatingChatWindow(4);
                return true;
            case InputAction.AcdreamToggleAudioMute:
                _toggleAudioMute?.Invoke();
                return true;
            case InputAction.AcdreamToggleDebugPanel:
                return true;
            case InputAction.AcdreamToggleFlyMode:
                _playerMode.ToggleFlyOrChase();
                return true;
            case InputAction.AcdreamTogglePlayerMode:
                _playerMode.TogglePlayerMode();
                return true;
            case InputAction.ToggleChatEntry:
            case InputAction.EnterChatMode:
                _retained.FocusChatEntry();
                return true;
            case InputAction.ToggleOptionsPanel:
                _retained.ToggleOptionsPanel();
                return true;
            case InputAction.CombatToggleCombat:
                _combat.Execute(
                    _runtimeView.Generation,
                    RuntimeCombatCommand.ToggleMode);
                return true;
            case InputAction.LOGOUT:
                _retained.LogOutCharacter();
                return true;
            case InputAction.EscapeKey:
                HandleEscape();
                return true;
            default:
                return false;
        }
    }

    private void HandleEscape()
    {
        if (_targetMode.IsAnyTargetModeActive)
            _targetMode.CancelTargetMode();
        else
            _retained.ToggleGameplayOptionsPage();
    }
}
