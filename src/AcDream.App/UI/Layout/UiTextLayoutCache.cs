using System.Numerics;
using AcDream.App.Rendering;

namespace AcDream.App.UI.Layout;

internal sealed class UiTextLayoutCache<T>
{
    private readonly UiText _target;
    private readonly Func<UiText, T, IReadOnlyList<UiText.Line>> _shape;
    private readonly IEqualityComparer<T> _comparer;
    private readonly Func<T>? _source;
    private T _value = default!;
    private bool _hasValue;
    private bool _shaped;
    private IReadOnlyList<UiText.Line> _lines = Array.Empty<UiText.Line>();
    private float _width;
    private float _padding;
    private Vector4 _defaultColor;
    private UiDatFont? _datFont;
    private BitmapFont? _bitmapFont;
    private IReadOnlyList<Vector4>? _fontColorPalette;

    public UiTextLayoutCache(
        UiText target,
        Func<UiText, T, IReadOnlyList<UiText.Line>> shape,
        T initialValue,
        IEqualityComparer<T>? comparer = null)
        : this(target, shape, source: null, comparer, initialize: true)
    {
        SetValue(initialValue);
    }

    public UiTextLayoutCache(
        UiText target,
        Func<UiText, T, IReadOnlyList<UiText.Line>> shape,
        Func<T> source,
        IEqualityComparer<T>? comparer = null)
        : this(
            target,
            shape,
            source ?? throw new ArgumentNullException(nameof(source)),
            comparer,
            initialize: true)
    {
    }

    private UiTextLayoutCache(
        UiText target,
        Func<UiText, T, IReadOnlyList<UiText.Line>> shape,
        Func<T>? source,
        IEqualityComparer<T>? comparer,
        bool initialize)
    {
        _ = initialize;
        _target = target ?? throw new ArgumentNullException(nameof(target));
        _shape = shape ?? throw new ArgumentNullException(nameof(shape));
        _source = source;
        _comparer = comparer ?? EqualityComparer<T>.Default;
    }

    public Func<IReadOnlyList<UiText.Line>> Provider => GetLines;

    public void SetValue(T value)
    {
        if (_hasValue && _comparer.Equals(_value, value))
            return;

        _value = value;
        _hasValue = true;
        _shaped = false;
    }

    public void Invalidate() => _shaped = false;

    public IReadOnlyList<UiText.Line> GetLines()
    {
        if (_source is not null)
            SetValue(_source());

        if (!_hasValue)
            return Array.Empty<UiText.Line>();

        if (!_shaped || LayoutChanged())
        {
            CaptureLayout();
            _lines = _shape(_target, _value);
            _shaped = true;
        }

        return _lines;
    }

    private bool LayoutChanged()
        => _width != _target.Width
           || _padding != _target.Padding
           || _defaultColor != _target.DefaultColor
           || !ReferenceEquals(_datFont, _target.DatFont)
           || !ReferenceEquals(_bitmapFont, _target.Font)
           || !ReferenceEquals(_fontColorPalette, _target.FontColorPalette);

    private void CaptureLayout()
    {
        _width = _target.Width;
        _padding = _target.Padding;
        _defaultColor = _target.DefaultColor;
        _datFont = _target.DatFont;
        _bitmapFont = _target.Font;
        _fontColorPalette = _target.FontColorPalette;
    }
}
