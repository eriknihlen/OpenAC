using System.Numerics;
using AcDream.App.Rendering;
using AcDream.App.UI.Layout;
using AcDream.UI.Abstractions.Panels.SpewBox;

namespace AcDream.App.UI;

internal sealed class SpewBoxController : IDisposable
{
    internal const uint RetailFontId = 0x40000001u;

    private const float TopOffset = 0f;

    private const float SpewBoxWidth = 450f;
    private const float SpewBoxHeight = 72f;

    private static readonly Vector4 SpewBoxColor = new(1f, 1f, 0.247f, 1f);

    private readonly UiRoot _root;
    private readonly UiText _text;
    private readonly SpewBoxVM _vm;
    private readonly GlobalTimeSink _timeSink;
    private readonly Func<bool> _isGameplayActive;
    private UiText.Line[] _lines = Array.Empty<UiText.Line>();
    private bool _disposed;

    public SpewBoxController(
        UiRoot root, SpewBoxVM vm, UiDatFont? font = null, BitmapFont? debugFont = null,
        Func<bool>? isGameplayActive = null)
    {
        _root = root ?? throw new ArgumentNullException(nameof(root));
        _vm = vm ?? throw new ArgumentNullException(nameof(vm));
        _isGameplayActive = isGameplayActive ?? (static () => true);
        _text = new UiText
        {
            Name = "SpewBox",
            Left = (root.Width - SpewBoxWidth) / 2f,
            Top = TopOffset,
            Width = SpewBoxWidth,
            Height = SpewBoxHeight,
            Anchors = AnchorEdges.None,
            Centered = true,
            DatFont = font,
            Font = debugFont,
            OneLine = false,
            VerticalJustify = VJustify.Top,
            HonorVerticalJustification = true,
            ClickThrough = true,
            ZOrder = int.MaxValue,
            DefaultColor = SpewBoxColor,
            Outline = true,
            Visible = false,
        };
        _text.LinesProvider = () => _lines;
        _root.AddChild(_text);

        _timeSink = new GlobalTimeSink(Tick);
        _root.AddChild(_timeSink);
    }

    private void Tick(double nowSeconds)
    {
        _text.Left = (_root.Width - SpewBoxWidth) / 2f;

        IReadOnlyList<SpewBoxLine> lines = _vm.Lines(nowSeconds);
        bool gameplay = _isGameplayActive();
        if (!gameplay && lines.Count > 0)
            _vm.Clear();
        _text.Visible = gameplay && lines.Count > 0;
        if (!_text.Visible)
        {
            _lines = Array.Empty<UiText.Line>();
            return;
        }

        var result = new UiText.Line[lines.Count];
        for (int i = 0; i < lines.Count; i++)
            result[i] = new UiText.Line(lines[i].Text, SpewBoxColor);
        _lines = result;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _root.RemoveChild(_text);
        _root.RemoveChild(_timeSink);
        _disposed = true;
    }

    private sealed class GlobalTimeSink : UiElement, IUiGlobalTimeListener
    {
        private readonly Action<double> _onGlobalUiTime;
        public GlobalTimeSink(Action<double> onGlobalUiTime) => _onGlobalUiTime = onGlobalUiTime;
        public void OnGlobalUiTime(double nowSeconds) => _onGlobalUiTime(nowSeconds);
    }
}
