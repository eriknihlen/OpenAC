using AcDream.App.Input;

namespace AcDream.App.UI.Layout;

public sealed class JumpPowerbarController : IRetainedPanelController
{
    public const uint LayoutId = 0x21000072u;
    public const uint MeterId = 0x10000034u;
    public const uint JumpModeState = 0x10000042u;

    private readonly UiMeter _meter;
    private readonly Func<JumpChargeSnapshot> _snapshot;
    private readonly Action<bool> _setWindowVisible;
    private float _power;
    private bool _isCharging;
    private bool _hasSynced;
    private bool _disposed;

    private JumpPowerbarController(
        UiMeter meter,
        Func<JumpChargeSnapshot> snapshot,
        Action<bool> setWindowVisible)
    {
        _meter = meter;
        _snapshot = snapshot;
        _setWindowVisible = setWindowVisible;
        _meter.Fill = () => _power;
        SyncVisibility(force: true);
    }

    public static JumpPowerbarController? Bind(
        ImportedLayout layout,
        Func<JumpChargeSnapshot> snapshot,
        Action<bool> setWindowVisible)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(setWindowVisible);

        if (layout.FindElement(MeterId) is not UiMeter meter
            || !meter.TrySetRetailState(JumpModeState))
            return null;

        return new JumpPowerbarController(meter, snapshot, setWindowVisible);
    }

    public void Tick() => SyncVisibility(force: false);

    public void SyncVisibility() => SyncVisibility(force: true);

    private void SyncVisibility(bool force)
    {
        if (_disposed) return;

        JumpChargeSnapshot state = _snapshot();
        _power = state.IsCharging ? Math.Clamp(state.Power, 0f, 1f) : 0f;

        if (force || !_hasSynced || state.IsCharging != _isCharging)
        {
            _isCharging = state.IsCharging;
            _hasSynced = true;
            _setWindowVisible(_isCharging);
        }
    }

    public void OnShown() => SyncVisibility(force: false);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _power = 0f;
        _meter.Fill = () => null;
    }
}
