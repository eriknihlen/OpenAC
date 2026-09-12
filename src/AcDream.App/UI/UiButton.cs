using System;
using System.Numerics;
using AcDream.App.UI.Layout;

namespace AcDream.App.UI;

public sealed class UiButton : UiElement, IUiGlobalTimeListener, IUiDatStateful
{
    private readonly ElementInfo _info;
    private readonly ElementInfo _mediaInfo;
    private readonly FaceSegment[] _faceSegments;
    private readonly Func<uint, (uint tex, int w, int h)> _resolve;
    private readonly string[] _segmentMediaStates;
    private string _faceMediaState = "";
    private string? _lastMediaCommitState;
    private readonly bool _hasCustomSelectionPair;
    private IReadOnlyDictionary<uint, Vector4>? _stateLabelColors;
    private IReadOnlyDictionary<uint, bool>? _stateLabelOutlines;
    private bool _pressed;
    private bool _pointerOver;
    private bool _selected;
    private bool _hotClicking;
    private bool _suppressNextClick;
    private int _pointerX;
    private int _pointerY;
    private double _nextHotClickTime = double.NaN;

    public Action? OnClick { get; set; }

    public Action? OnDoubleClick { get; set; }

    public Action? OnRightClick { get; set; }

    public Action? OnPressed { get; set; }
    public Action? OnReleased { get; set; }

    public Action<int, int>? OnClickAt { get; set; }

    public Func<ItemDragPayload, ItemDragAcceptance>? OnItemDragOver { get; set; }
    public Action<ItemDragPayload>? OnItemDrop { get; set; }
    public uint ItemDragAcceptSprite { get; set; }
    public uint ItemDragRejectSprite { get; set; }
    private ItemDragAcceptance _itemDragAcceptance;

    internal ItemDragAcceptance ItemDragAcceptanceForTest => _itemDragAcceptance;

    public uint ElementId => _info.Id;

    public string? Label { get; set; }

    public UiDatFont? LabelFont { get; set; }

    public Vector4 LabelColor { get; set; } = Vector4.One;

    /// <summary>
    /// Asked for the label's colour each frame when set, so a caption that has
    /// to dim and undim while its panel is open can do so without anything
    /// having to push a new colour in. Unset leaves <see cref="LabelColor"/>
    /// in charge.
    /// </summary>
    public Func<Vector4>? LabelColorProvider { get; set; }

    public string? TooltipText { get; set; }

    /// <inheritdoc />
    public override string? GetTooltipText() =>
        string.IsNullOrWhiteSpace(TooltipText) ? null : TooltipText;

    public bool Outline { get; set; }

    public Vector4 OutlineColor { get; set; } = UiRenderContext.DefaultOutlineColor;

    public float FaceLeft { get; set; }
    public float FaceTop { get; set; }
    public float FaceWidth { get; set; }
    public float FaceHeight { get; set; }

    public uint? FaceFileOverride { get; set; }

    public Vector4 Tint { get; set; } = Vector4.One;

    public Func<uint>? ColorKeyFaceResolver { get; set; }

    /// <summary>Additional left inset for left-aligned labels.</summary>
    public float LabelOffsetX { get; set; } = 3f;

    public LabelAlignment LabelAlign { get; set; } = LabelAlignment.Center;

    public (float X, float Y, float Width, float Height)? LabelBox { get; set; }

    public string? ValueLabel { get; set; }

    public UiDatFont? ValueFont { get; set; }

    public Vector4 ValueColor { get; set; } = Vector4.One;

    public (float X, float Y, float Width, float Height)? ValueBox { get; set; }

    public LabelAlignment ValueAlign { get; set; } = LabelAlignment.Center;

    public enum LabelAlignment { Center, Left, Right }

    public bool ToggleBehavior { get; }
    public bool RolloverEnabled { get; }
    public bool HotClickEnabled { get; }
    public float HotClickInitialDelay { get; }
    public float HotClickRepeatInterval { get; }

    public bool SuppressSelfToggle { get; set; }

    public bool ControllerOwnsVisualState { get; set; }

    public bool Selected
    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            _selected = value;
            UpdateVisualState();
        }
    }

    private string _activeState = "";
    private double _activeStateStartedAt;

    public string ActiveState
    {
        get => _activeState;
        set
        {
            if (string.Equals(_activeState, value, StringComparison.Ordinal))
                return;
            _activeState = value;
            // An authored media sequence is timed from the moment its state is
            // entered, so this is the only thing an element needs to remember.
            _activeStateStartedAt = Layout.UiMediaClock.Seconds;
        }
    }

    public uint ActiveRetailStateId
    {
        get
        {
            if (string.IsNullOrEmpty(ActiveState))
                return UiStateInfo.DirectStateId;
            foreach (var (id, state) in _mediaInfo.States)
                if (string.Equals(state.Name, ActiveState, StringComparison.Ordinal))
                    return id;
            return UiButtonStateMachine.TryStateId(ActiveState, out uint standard)
                ? standard
                : RetailUiStateIds.TryStateId(ActiveState, out uint custom) ? custom : 0u;
        }
    }

    /// <summary>Reads a resolved enum-valued DAT attribute from this button.</summary>
    public bool TryGetEnumAttribute(uint propertyId, out uint value)
    {
        if (_info.TryGetEffectiveProperty(propertyId, out var property)
            && property.Kind == UiPropertyKind.Enum)
        {
            value = checked((uint)property.UnsignedValue);
            return true;
        }

        value = 0;
        return false;
    }

    public override string ActiveCursorStateName => ActiveState;

    public bool TrySetRetailState(uint stateId)
    {
        if (ToggleBehavior && stateId is UiButtonStateMachine.Normal or UiButtonStateMachine.Highlight)
        {
            Selected = stateId == UiButtonStateMachine.Highlight;
            ApplyStateVisibility(stateId);
            return true;
        }
        if (stateId == UiButtonStateMachine.Ghosted)
        {
            Enabled = false;
            ApplyStateVisibility(stateId);
            return true;
        }
        if (!Enabled && stateId != UiButtonStateMachine.Ghosted)
            Enabled = true;

        if (stateId == UiStateInfo.DirectStateId)
        {
            if (!HasStateMedia(""))
                return false;
            ActiveState = "";
            ApplyStateVisibility(stateId);
            CascadeStateToChildren(stateId);
            return true;
        }
        if (TryFindState(stateId, out var state))
        {
            ActiveState = state.Name;
            ApplyStateVisibility(stateId);
            CascadeStateToChildren(stateId);
            return true;
        }
        string stateName = UiButtonStateMachine.StateName(stateId);
        if (string.IsNullOrEmpty(stateName))
            stateName = RetailUiStateIds.StateName(stateId);
        if (!string.IsNullOrEmpty(stateName) && HasStateMedia(stateName))
        {
            ActiveState = stateName;
            ApplyStateVisibility(stateId);
            CascadeStateToChildren(stateId);
            return true;
        }
        return false;
    }

    private void ApplyStateVisibility(uint stateId)
    {
        if (_info.States.TryGetValue(stateId, out var state)
            && state.Properties.Values.TryGetValue(0x3Bu, out var invisible)
            && invisible.Kind == UiPropertyKind.Bool)
        {
            Visible = !invisible.BoolValue;
        }
    }

    public UiButton(
        ElementInfo info,
        Func<uint, (uint tex, int w, int h)> resolve,
        ElementInfo? mediaInfo = null,
        IReadOnlyList<ElementInfo>? faceSegments = null)
    {
        _info = info;
        _mediaInfo = mediaInfo ?? info;
        _faceSegments = faceSegments is null
            ? []
            : faceSegments.Select(static segment => new FaceSegment(segment)).ToArray();
        _segmentMediaStates = new string[_faceSegments.Length];
        Array.Fill(_segmentMediaStates, "");
        _resolve = resolve;
        ClickThrough = false;

        _hasCustomSelectionPair = HasStateMedia(RetailUiStateIds.StateName(RetailUiStateIds.Unselected))
            && HasStateMedia(RetailUiStateIds.StateName(RetailUiStateIds.Selected));

        ToggleBehavior = info.TryGetEffectiveBool(0x0Bu, out bool toggle) && toggle;
        RolloverEnabled = info.TryGetEffectiveBool(0x13u, out bool rollover) && rollover;
        HotClickEnabled = info.TryGetEffectiveBool(0x0Fu, out bool hotClick) && hotClick;
        HotClickInitialDelay = info.TryGetEffectiveFloat(0x10u, out float initialDelay)
            ? initialDelay
            : 0f;
        HotClickRepeatInterval = info.TryGetEffectiveFloat(0x11u, out float repeatInterval)
            ? repeatInterval
            : 0f;
        _selected = info.TryGetEffectiveBool(0x0Eu, out bool selected) && selected;

        // State defaulting matches UiDatElement exactly:
        // DefaultStateName wins; else "Normal" if that state has a sprite; else DirectState ("").
        if (!string.IsNullOrEmpty(info.DefaultStateName))
            ActiveState = info.DefaultStateName;
        else if (HasStateMedia("Normal"))
            ActiveState = "Normal";
        // else ActiveState stays "" (DirectState)

        FaceWidth = mediaInfo?.Width ?? info.Width;
        FaceHeight = mediaInfo?.Height ?? info.Height;
        UpdateVisualState();
    }

    public override bool ConsumesDatChildren => true;

    public override bool HandlesClick => true;

    private void SyncMediaStates()
    {
        if (string.Equals(ActiveState, _lastMediaCommitState, StringComparison.Ordinal))
            return;
        uint committedId = ActiveRetailStateId;
        if (_faceSegments.Length == 0)
        {
            _faceMediaState = NextMediaState(
                _mediaInfo, committedId, ActiveState, _faceMediaState);
        }
        else
        {
            for (int i = 0; i < _faceSegments.Length; i++)
            {
                _segmentMediaStates[i] = NextMediaState(
                    _faceSegments[i].Info,
                    committedId,
                    ActiveState,
                    _segmentMediaStates[i]);
            }
        }
        _lastMediaCommitState = ActiveState;
    }

    private static string NextMediaState(
        ElementInfo info,
        uint committedId,
        string committedName,
        string current)
    {
        if (info.States.TryGetValue(committedId, out UiStateInfo? state))
            return HasImageMedia(info, state, committedName) ? committedName : current;
        if (info.StateMedia.ContainsKey(committedName))
            return committedName;
        if (info.States.TryGetValue(
                UiStateInfo.DirectStateId, out UiStateInfo? baseState))
            return HasImageMedia(info, baseState, "") ? "" : current;
        return info.StateMedia.ContainsKey("") ? "" : current;
    }

    private static bool HasImageMedia(
        ElementInfo info,
        UiStateInfo state,
        string stateName)
        => state.ImageMediaCount != 0 || info.StateMedia.ContainsKey(stateName);

    /// <summary>
    /// Returns the File id the media rule selected for this face; 0 draws
    /// nothing (an authored File=0 image reaches this as a media-state whose
    /// name has no drawable entry).
    /// </summary>
    private static uint ActiveFile(ElementInfo mediaInfo, string mediaState)
        => mediaInfo.StateMedia.TryGetValue(mediaState, out var m) ? m.File : 0u;

    /// <summary>
    /// The frame this element's active state is showing right now, and the
    /// state its sequence hands off to when it ends.
    /// </summary>
    /// <remarks>
    /// A state whose media is a single image is NOT routed through the player;
    /// it keeps the still-frame path, so the overwhelming majority of buttons
    /// are untouched by this.
    /// </remarks>
    private uint AnimatedFile(ElementInfo mediaInfo, string mediaState, out uint? handOff)
    {
        handOff = null;
        if (!TryFindStateNamed(mediaInfo, mediaState, out UiStateInfo? state)
            || !Layout.UiMediaSequence.IsAnimated(state!.MediaSteps))
        {
            return ActiveFile(mediaInfo, mediaState);
        }

        (uint file, uint? transition) = Layout.UiMediaSequence.Sample(
            state.MediaSteps,
            (float)(Layout.UiMediaClock.Seconds - _activeStateStartedAt));
        handOff = transition;
        return file != 0u ? file : ActiveFile(mediaInfo, mediaState);
    }

    private static bool TryFindStateNamed(
        ElementInfo mediaInfo, string mediaState, out UiStateInfo? state)
    {
        foreach (var (_, candidate) in mediaInfo.States)
        {
            if (string.Equals(candidate.Name, mediaState, StringComparison.Ordinal))
            {
                state = candidate;
                return true;
            }
        }
        state = null;
        return false;
    }

    protected override void OnDraw(UiRenderContext ctx)
    {
        SyncMediaStates();

        uint? pendingHandOff = null;
        if (_faceSegments.Length != 0)
        {
            for (int i = 0; i < _faceSegments.Length; i++)
            {
                FaceSegment segment = _faceSegments[i];
                uint frame = AnimatedFile(
                    segment.Info, _segmentMediaStates[i], out uint? segmentHandOff);
                DrawFace(ctx, frame, segment.Rect(Width, Height));
                pendingHandOff ??= segmentHandOff;
            }
        }
        else if (ColorKeyFaceResolver is { } colorKeyResolver)
        {
            uint bakedTexture = colorKeyResolver();
            if (bakedTexture != 0)
            {
                float faceWidth = FaceWidth > 0f ? FaceWidth : Width;
                float faceHeight = FaceHeight > 0f ? FaceHeight : Height;
                ctx.DrawSprite(bakedTexture, FaceLeft, FaceTop, faceWidth, faceHeight,
                    0f, 0f, 1f, 1f, Vector4.One);
            }
        }
        else
        {
            uint file = FaceFileOverride
                ?? AnimatedFile(_mediaInfo, _faceMediaState, out pendingHandOff);
            if (file != 0)
            {
                var (tex, tw, th) = _resolve(file);
                if (tex != 0 && tw != 0 && th != 0)
                {
                    float faceWidth = FaceWidth > 0f ? FaceWidth : Width;
                    float faceHeight = FaceHeight > 0f ? FaceHeight : Height;
                    ctx.DrawSprite(tex, FaceLeft, FaceTop, faceWidth, faceHeight,
                        0, 0, faceWidth / tw, faceHeight / th, Tint);
                }
            }
        }

        // The sequence has run out and asked for another state. Applied here,
        // after every face has drawn this frame.
        if (pendingHandOff is { } handOffState)
            TrySetRetailState(handOffState);

        if (Label is { Length: > 0 } label && LabelFont is { } lf)
        {
            // GF-11c: LabelBox null (every pre-existing button) reduces boxX/
            // boxY to 0 and boxWidth/boxHeight to the button's own Width/
            // Height — byte-identical to the prior unconditional math.
            float boxX = LabelBox?.X ?? 0f;
            float boxY = LabelBox?.Y ?? 0f;
            float boxWidth = LabelBox?.Width ?? Width;
            float boxHeight = LabelBox?.Height ?? Height;

            if (ValueBox is { X: var valueBoxX } && valueBoxX > boxX)
                boxWidth = MathF.Min(boxWidth, valueBoxX - boxX);

            DrawBlockLabel(
                ctx,
                label,
                lf,
                LabelColorProvider?.Invoke() ?? LabelColor,
                boxX,
                boxY,
                boxWidth,
                boxHeight,
                LabelAlign,
                LabelOffsetX);
        }

        if (ValueLabel is { Length: > 0 } value && ValueFont is { } vf)
        {
            float boxX = ValueBox?.X ?? 0f;
            float boxY = ValueBox?.Y ?? 0f;
            float boxWidth = ValueBox?.Width ?? Width;
            float boxHeight = ValueBox?.Height ?? Height;
            float valueWidth = vf.MeasureWidth(value);
            float vx = ValueAlign switch
            {
                LabelAlignment.Left => boxX + LabelOffsetX,
                LabelAlignment.Right => boxX + boxWidth - valueWidth,
                _ => boxX + (boxWidth - valueWidth) * 0.5f,
            };
            float vy = boxY + (boxHeight - vf.LineHeight) * 0.5f;
            ctx.DrawStringDat(vf, value, vx, vy, ValueColor, Outline, OutlineColor);
        }

        uint dragSprite = _itemDragAcceptance switch
        {
            ItemDragAcceptance.Accept => ItemDragAcceptSprite,
            ItemDragAcceptance.Reject => ItemDragRejectSprite,
            _ => 0u,
        };
        if (dragSprite != 0)
        {
            var (tex, _, _) = _resolve(dragSprite);
            if (tex != 0)
                ctx.DrawSprite(tex, 0f, 0f, Width, Height, 0f, 0f, 1f, 1f, Tint);
        }
    }

    private void DrawBlockLabel(
        UiRenderContext ctx,
        string text,
        UiDatFont font,
        Vector4 color,
        float boxX,
        float boxY,
        float boxWidth,
        float boxHeight,
        LabelAlignment align,
        float leftOffset)
    {
        IReadOnlyList<(string Text, float X, float Y)> lines = WrapBlockLines(
            text, font.MeasureWidth, font.LineHeight,
            boxX, boxY, boxWidth, boxHeight, align, leftOffset);

        bool clip = lines.Count > 1;
        if (clip)
            ctx.PushClip(boxX, boxY, boxWidth, boxHeight);
        try
        {
            foreach ((string line, float tx, float ty) in lines)
                ctx.DrawStringDat(font, line, tx, ty, color, Outline, OutlineColor);
        }
        finally
        {
            if (clip)
                ctx.PopClip();
        }
    }

    internal static IReadOnlyList<(string Text, float X, float Y)> WrapBlockLines(
        string text,
        Func<string, float> measureWidth,
        float lineHeight,
        float boxX,
        float boxY,
        float boxWidth,
        float boxHeight,
        LabelAlignment align,
        float leftOffset)
    {
        string[] lines = text.Split('\n');

        float totalHeight = lines.Length * lineHeight;
        float startY = boxY + (boxHeight - totalHeight) * 0.5f;

        var result = new List<(string, float, float)>(lines.Length);
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            float tx = align == LabelAlignment.Left
                ? boxX + leftOffset
                : boxX + (boxWidth - measureWidth(line)) * 0.5f;
            float ty = startY + i * lineHeight;
            result.Add((line, tx, ty));
        }
        return result;
    }

    private void DrawFace(UiRenderContext ctx, uint file, UiPixelRect rect)
    {
        if (file == 0 || rect.Width <= 0 || rect.Height <= 0)
            return;
        var (texture, textureWidth, textureHeight) = _resolve(file);
        if (texture == 0 || textureWidth == 0 || textureHeight == 0)
            return;

        ctx.DrawSprite(texture, rect.X0, rect.Y0, rect.Width, rect.Height,
            0f, 0f, (float)rect.Width / textureWidth, (float)rect.Height / textureHeight,
            Tint);
    }

    private bool HasStateMedia(string stateName)
    {
        if (_faceSegments.Length == 0)
            return _mediaInfo.StateMedia.ContainsKey(stateName);
        foreach (FaceSegment segment in _faceSegments)
            if (segment.Info.StateMedia.ContainsKey(stateName))
                return true;
        return false;
    }

    private bool TryFindState(uint stateId, out UiStateInfo state)
    {
        if (_faceSegments.Length == 0)
            return _mediaInfo.States.TryGetValue(stateId, out state!);
        foreach (FaceSegment segment in _faceSegments)
            if (segment.Info.States.TryGetValue(stateId, out state!))
                return true;
        state = null!;
        return false;
    }

    private sealed class FaceSegment
    {
        private readonly UiPixelRect _original;
        private readonly UiLayoutPolicy? _layout;

        public FaceSegment(ElementInfo info)
        {
            Info = info;
            _original = UiPixelRect.FromPositionAndSize(
                (int)info.X, (int)info.Y, (int)info.Width, (int)info.Height);
            if (info.HasOriginalParentSize)
                _layout = new UiLayoutPolicy(
                    info.Left, info.Top, info.Right, info.Bottom,
                    _original,
                    UiPixelRect.FromPositionAndSize(
                        0, 0, (int)info.OriginalParentWidth, (int)info.OriginalParentHeight));
        }

        public ElementInfo Info { get; }

        public UiPixelRect Rect(float parentWidth, float parentHeight)
        {
            if (_layout is null)
                return _original;
            return _layout.Apply(
                _original,
                UiPixelRect.FromPositionAndSize(0, 0, (int)parentWidth, (int)parentHeight));
        }
    }

    public override bool OnEvent(in UiEvent e)
    {
        switch (e.Type)
        {
            case UiEventType.HoverEnter:
                _pointerOver = true;
                UpdateVisualState();
                return true;
            case UiEventType.HoverLeave:
                _pointerOver = false;
                UpdateVisualState();
                return true;
            case UiEventType.MouseDown:
                _pointerX = e.Data1;
                _pointerY = e.Data2;
                _pointerOver = ContainsLocal(e.Data1, e.Data2);
                _pressed = true;
                UpdateVisualState();
                if (Enabled)
                    OnPressed?.Invoke();
                if (HotClickEnabled && Enabled)
                {
                    OnClick?.Invoke();
                    OnClickAt?.Invoke(_pointerX, _pointerY);
                    _hotClicking = true;
                    _nextHotClickTime = double.NaN;
                }
                return true;
            case UiEventType.MouseMove:
                if (_pressed)
                {
                    _pointerX = e.Data1;
                    _pointerY = e.Data2;
                    _pointerOver = ContainsLocal(e.Data1, e.Data2);
                    UpdateVisualState();
                    return true;
                }
                return false;
            case UiEventType.MouseUp:
                _pointerX = e.Data1;
                _pointerY = e.Data2;
                _pointerOver = ContainsLocal(e.Data1, e.Data2);
                _suppressNextClick = _hotClicking && _pointerOver;
                _hotClicking = false;
                _nextHotClickTime = double.NaN;
                if (_pressed && _pointerOver && Enabled && ToggleBehavior && !SuppressSelfToggle)
                    _selected = !_selected;
                if (_pressed && Enabled)
                    OnReleased?.Invoke();
                _pressed = false;
                UpdateVisualState();
                return true;
            case UiEventType.Click:
                if (!Enabled) return true;
                if (_suppressNextClick)
                {
                    _suppressNextClick = false;
                    return true;
                }
                OnClick?.Invoke();
                OnClickAt?.Invoke(e.Data1, e.Data2);
                return OnClick is not null || OnClickAt is not null;
            case UiEventType.DoubleClick:
                if (OnDoubleClick is null) return false;
                if (!Enabled) return true;
                OnDoubleClick.Invoke();
                return true;
            case UiEventType.RightClick:
                if (OnRightClick is null) return false;
                if (!Enabled) return true;
                OnRightClick.Invoke();
                return true;
            case UiEventType.DragEnter:
                _itemDragAcceptance = e.Payload is ItemDragPayload payload
                    ? OnItemDragOver?.Invoke(payload) ?? ItemDragAcceptance.None
                    : ItemDragAcceptance.None;
                return OnItemDragOver is not null;
            case UiEventType.DragOver:
                _itemDragAcceptance = ItemDragAcceptance.None;
                return OnItemDragOver is not null;
            case UiEventType.DropReleased:
                _itemDragAcceptance = ItemDragAcceptance.None;
                if (e.Payload is ItemDragPayload dropped)
                    OnItemDrop?.Invoke(dropped);
                return OnItemDrop is not null;
            default:
                return false;
        }
    }

    protected override void OnEnabledChanged()
    {
        if (!Enabled)
        {
            _pressed = false;
            _hotClicking = false;
            _nextHotClickTime = double.NaN;
        }
        UpdateVisualState();
    }

    internal int FaceSegmentCount => _faceSegments.Length;

    internal IReadOnlyList<UiPixelRect> FaceSegmentRectsForTest()
        => _faceSegments.Select(segment => segment.Rect(Width, Height)).ToArray();

    public void OnGlobalUiTime(double nowSeconds)
    {
        if (!_hotClicking || !HotClickEnabled)
            return;

        if (double.IsNaN(_nextHotClickTime))
            _nextHotClickTime = nowSeconds + HotClickInitialDelay;

        if (!_pointerOver && nowSeconds >= _nextHotClickTime)
        {
            _nextHotClickTime = nowSeconds;
            return;
        }

        if (_pointerOver && nowSeconds >= _nextHotClickTime)
        {
            OnClick?.Invoke();
            OnClickAt?.Invoke(_pointerX, _pointerY);
            _nextHotClickTime += HotClickRepeatInterval;
        }
    }

    private bool ContainsLocal(int x, int y)
        => x >= 0 && y >= 0 && x < Width && y < Height;

    private void UpdateVisualState()
    {
        if (ControllerOwnsVisualState)
            return;

        uint requested = ComputeRequestedStateId();
        if (_hasCustomSelectionPair)
        {
            ActiveState = RetailUiStateIds.StateName(requested);
            ApplyPerStateLabelStyle(requested);
            CascadeStateToChildren(requested);
            return;
        }

        string requestedName = UiButtonStateMachine.StateName(requested);
        bool authored = _info.States.TryGetValue(
            requested, out UiStateInfo? committed);
        if (!authored && !HasStateMedia(requestedName))
            return;

        ActiveState = authored && !string.IsNullOrEmpty(committed!.Name)
            ? committed.Name
            : requestedName;
        ApplyPerStateLabelStyle(requested);
        CascadeStateToChildren(requested);
    }

    private void CascadeStateToChildren(uint stateId)
    {
        if (Children.Count == 0)
            return;
        if (!TryFindState(stateId, out UiStateInfo state) || !state.PassToChildren)
            return;
        foreach (UiElement child in Children)
            if (child is IUiDatStateful stateful)
                stateful.TrySetRetailState(stateId);
    }

    private uint ComputeRequestedStateId()
        => _hasCustomSelectionPair
            ? (_selected ? RetailUiStateIds.Selected : RetailUiStateIds.Unselected)
            : UiButtonStateMachine.RequestedState(new UiButtonVisualInput(
                Disabled: !Enabled,
                Selected: _selected,
                RolloverEnabled: RolloverEnabled,
                Pressed: _pressed,
                PointerOver: _pointerOver));

    internal void SetPerStateLabelStyle(
        IReadOnlyDictionary<uint, Vector4>? colors,
        IReadOnlyDictionary<uint, bool>? outlines)
    {
        _stateLabelColors = colors;
        _stateLabelOutlines = outlines;
        ApplyPerStateLabelStyle(ComputeRequestedStateId());
    }

    private void ApplyPerStateLabelStyle(uint requestedStateId)
    {
        if (_stateLabelColors is { } colors && colors.TryGetValue(requestedStateId, out Vector4 color))
            LabelColor = color;
        if (_stateLabelOutlines is { } outlines && outlines.TryGetValue(requestedStateId, out bool outline))
            Outline = outline;
    }
}
