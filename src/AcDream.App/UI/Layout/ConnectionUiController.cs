using System.Numerics;
using System.Diagnostics;
using AcDream.Runtime.Session;

namespace AcDream.App.UI.Layout;

internal sealed class ConnectionUiController : IDisposable
{
    internal const uint RootEnum = 0x10000001u;
    internal const uint RootElementId = 0x1000041Au;
    internal const uint ConnectionMeterId = 0x1000041Eu;
    internal const uint UpdateMeterId = 0x1000041Fu;
    internal const uint ConnectionTextId = 0x10000420u;
    internal const uint UpdateTextId = 0x10000421u;
    internal const uint CancelId = 0x1000041Cu;

    private readonly UiRoot _host;
    private readonly ConnectionRuntimeBindings _bindings;
    private readonly UiMeter _connectionMeter;
    private readonly UiMeter _updateMeter;
    private readonly UiText _connectionText;
    private readonly UiText _updateText;
    private readonly UiButton _cancel;
    private readonly UiPanel _errorPanel;
    private readonly UiText _errorText;
    private readonly string _checkingText;
    private readonly string _completedText;
    private readonly Func<IReadOnlyList<UiText.Line>> _initialUpdateText;
    private RuntimeConnectionSnapshot _snapshot;
    private bool _disposed;
    private readonly Func<double> _nowSeconds;
    private readonly double _minimumVisibleSeconds;
    private double? _shownAt;
    private bool _presentationCompleted;

    private ConnectionUiController(UiRoot host, ImportedLayout layout,
        ConnectionRuntimeBindings bindings, string checkingText, string completedText,
        Func<double>? nowSeconds, double minimumVisibleSeconds)
    {
        _host = host;
        _bindings = bindings;
        _nowSeconds = nowSeconds ?? (() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
        _minimumVisibleSeconds = minimumVisibleSeconds;
        Root = layout.Root;
        Root.Left = Root.Top = 0f;
        Root.Visible = false;
        Root.ClickThrough = false;
        Root.AddChild(new UiPanel
        {
            Name = "ConnectionBackdrop",
            Left = 0f, Top = 0f, Width = Root.Width, Height = Root.Height,
            BackgroundColor = new Vector4(0f, 0f, 0f, 1f),
            BorderColor = Vector4.Zero, ZOrder = int.MinValue, ClickThrough = true,
        });
        _connectionMeter = (UiMeter)layout.FindElement(ConnectionMeterId)!;
        _updateMeter = (UiMeter)layout.FindElement(UpdateMeterId)!;
        _connectionText = (UiText)layout.FindElement(ConnectionTextId)!;
        _updateText = (UiText)layout.FindElement(UpdateTextId)!;
        _cancel = (UiButton)layout.FindElement(CancelId)!;
        _checkingText = checkingText;
        _completedText = completedText;
        _initialUpdateText = _updateText.LinesProvider;
        _connectionMeter.Fill = () => _snapshot.ConnectionProgress;
        _updateMeter.Fill = () => _snapshot.UpdateProgress;
        _cancel.OnClick = bindings.RequestExit;
        _errorPanel = new UiPanel
        {
            Left = 30f, Top = 352f, Width = 740f, Height = 104f,
            BackgroundColor = new Vector4(0f, 0f, 0f, 0.92f),
            BorderColor = Vector4.Zero, Visible = false, ClickThrough = true,
        };
        _errorText = new UiText
        {
            Left = 12f, Top = 12f, Width = 716f, Height = 80f,
            DatFont = _updateText.DatFont, Font = _updateText.Font,
            DefaultColor = Vector4.One, Outline = true, ClickThrough = true,
        };
        _errorPanel.AddChild(_errorText);
        Root.AddChild(_errorPanel);
        _host.AddChild(Root);
    }

    internal UiElement Root { get; }
    internal UiText ErrorText => _errorText;

    internal static ConnectionUiController? Bind(UiRoot host, ImportedLayout layout,
        ConnectionRuntimeBindings bindings, string checkingText, string completedText,
        Func<double>? nowSeconds = null, double minimumVisibleSeconds = 2d)
    {
        if (layout.Root.DatElementId != RootElementId
            || layout.FindElement(ConnectionMeterId) is not UiMeter
            || layout.FindElement(UpdateMeterId) is not UiMeter
            || layout.FindElement(ConnectionTextId) is not UiText
            || layout.FindElement(UpdateTextId) is not UiText
            || layout.FindElement(CancelId) is not UiButton)
            return null;
        return new ConnectionUiController(host, layout, bindings, checkingText, completedText,
            nowSeconds, minimumVisibleSeconds);
    }

    internal void Tick()
    {
        if (_disposed) return;
        RuntimeConnectionSnapshot snapshot = _bindings.View()?.Snapshot ?? default;
        if (snapshot.Status == RuntimeConnectionStatus.Inactive
            || snapshot.Status == RuntimeConnectionStatus.Connecting
                && _snapshot.Status != RuntimeConnectionStatus.Connecting)
        {
            _shownAt = null;
            _presentationCompleted = false;
        }
        bool failure = snapshot.Status is RuntimeConnectionStatus.Unsupported or RuntimeConnectionStatus.Failed;
        bool progress = _bindings.ShowProgress && !_presentationCompleted
            && snapshot.Status is RuntimeConnectionStatus.Connecting or RuntimeConnectionStatus.CheckingData
                or RuntimeConnectionStatus.Ready;
        if (progress)
        {
            _shownAt ??= _nowSeconds();
            if (snapshot.Status == RuntimeConnectionStatus.Ready
                && _nowSeconds() - _shownAt.Value >= _minimumVisibleSeconds)
            {
                _presentationCompleted = true;
                progress = false;
            }
        }
        bool visible = failure || progress;
        if (!visible)
        {
            Root.Visible = false;
            _host.RevokeFixedCanvas(this);
            _snapshot = snapshot;
            return;
        }

        if (!Root.Visible)
        {
            Root.Visible = true;
            _host.DeclareFixedCanvas(this, new Vector2(Root.Width, Root.Height));
            _host.BringToFront(Root);
        }
        if (_snapshot == snapshot) return;
        _snapshot = snapshot;
        _connectionText.TrySetRetailState(snapshot.ConnectionProgress >= 1f
            ? 0x1000003Cu : snapshot.Status == RuntimeConnectionStatus.Connecting
                ? 0x1000003Bu : 1u);
        if (snapshot.UpdateProgress >= 1f)
            SetText(_updateText, _completedText);
        else if (snapshot.Status == RuntimeConnectionStatus.CheckingData)
            SetText(_updateText, _checkingText);
        else
            _updateText.LinesProvider = _initialUpdateText;

        _errorPanel.Visible = snapshot.Status is RuntimeConnectionStatus.Unsupported
            or RuntimeConnectionStatus.Failed;
        string error = snapshot.Error ?? string.Empty;
        UiText.Line[] errorLines = UiText.WrapWords(error,
                text => _errorText.DatFont?.MeasureWidth(text)
                    ?? _errorText.Font?.MeasureWidth(text) ?? text.Length * 8f,
                _errorText.Width)
            .Select(text => new UiText.Line(text, _errorText.DefaultColor)).ToArray();
        _errorText.LinesProvider = () => errorLines;
    }

    private static void SetText(UiText element, string text)
    {
        UiText.Line[] lines = [new(text, element.DefaultColor)];
        element.LinesProvider = () => lines;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cancel.OnClick = null;
        _connectionMeter.Fill = static () => 0f;
        _updateMeter.Fill = static () => 0f;
        _host.RevokeFixedCanvas(this);
        _host.RemoveChild(Root);
    }
}
