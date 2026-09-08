using System.Numerics;

namespace AcDream.App.UI.Layout;

internal sealed record CreditsUiResources(
    uint LayoutId,
    ImportedLayout PictureLayout,
    ImportedLayout TextLayout,
    IReadOnlyList<string> TextFragments,
    IReadOnlyList<uint> PictureIds,
    float SectionSeconds,
    string PleaseWait);

internal sealed class CreditsUiController : IDisposable
{
    internal const uint RootEnum = 0x10000004u;
    internal const uint PictureRootElementId = 0x10000413u;
    internal const uint TextRootElementId = 0x10000410u;
    internal const uint TextAreaElementId = 0x10000411u;
    internal const uint DynamicPictureElementId = 0x10000415u;

    private readonly UiRoot _host;
    private readonly ImportedLayout _pictureLayout;
    private readonly ImportedLayout _textLayout;
    private readonly UiText _textArea;
    private readonly IReadOnlyList<string> _textFragments;
    private readonly IReadOnlyList<uint> _pictureIds;
    private readonly float _sectionSeconds;
    private readonly RetailDialogFactory _dialogs;
    private readonly string _pleaseWait;
    private readonly Func<double> _nowSeconds;
    private readonly Func<uint, (uint tex, int w, int h)> _resolveSprite;
    private readonly Action _returnToCharacterManagement;
    private readonly CreditsActionSurface _actionSurface;
    private readonly List<UiPanel> _pictures = [];
    private readonly Vector2 _authoredCanvas;

    private UiText.Line[] _lines = [];
    private float _textHeight;
    private double _startTime;
    private double _duration;
    private float _lastProgress;
    private int _nextPicture;
    private long _tickSequence;
    private long _returnAtTick = long.MaxValue;
    private uint _waitContext;
    private bool _active;
    private bool _returnPending;
    private bool _disposed;

    private CreditsUiController(
        UiRoot host,
        ImportedLayout pictureLayout,
        ImportedLayout textLayout,
        UiText textArea,
        IReadOnlyList<string> textFragments,
        IReadOnlyList<uint> pictureIds,
        float sectionSeconds,
        RetailDialogFactory dialogs,
        string pleaseWait,
        Func<double> nowSeconds,
        Func<uint, (uint tex, int w, int h)> resolveSprite,
        Action returnToCharacterManagement)
    {
        _host = host;
        _pictureLayout = pictureLayout;
        _textLayout = textLayout;
        _textArea = textArea;
        _textFragments = textFragments;
        _pictureIds = pictureIds;
        _sectionSeconds = sectionSeconds;
        _dialogs = dialogs;
        _pleaseWait = pleaseWait;
        _nowSeconds = nowSeconds;
        _resolveSprite = resolveSprite;
        _returnToCharacterManagement = returnToCharacterManagement;

        float width = MathF.Max(
            PictureRoot.Left + PictureRoot.Width,
            TextRoot.Left + TextRoot.Width);
        float height = MathF.Max(
            PictureRoot.Top + PictureRoot.Height,
            TextRoot.Top + TextRoot.Height);
        _authoredCanvas = new Vector2(
            width > 0f ? width : 800f,
            height > 0f ? height : 600f);

        PictureRoot.Visible = false;
        TextRoot.Visible = false;
        _actionSurface = new CreditsActionSurface(BeginReturn)
        {
            Width = _authoredCanvas.X,
            Height = _authoredCanvas.Y,
            Visible = false,
            ZOrder = int.MaxValue,
        };
    }

    internal UiElement PictureRoot => _pictureLayout.Root;
    internal UiElement TextRoot => _textLayout.Root;
    internal UiText TextArea => _textArea;
    internal IReadOnlyList<UiPanel> Pictures => _pictures;
    internal bool IsActive => _active;
    internal double DurationSeconds => _duration;

    internal static CreditsUiController? CreateDetached(
        UiRoot host,
        CreditsUiResources resources,
        RetailDialogFactory dialogs,
        Func<double> nowSeconds,
        Func<uint, (uint tex, int w, int h)> resolveSprite,
        Action returnToCharacterManagement)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(dialogs);
        ArgumentNullException.ThrowIfNull(nowSeconds);
        ArgumentNullException.ThrowIfNull(resolveSprite);
        ArgumentNullException.ThrowIfNull(returnToCharacterManagement);

        if (resources.PictureLayout.Root.DatElementId != PictureRootElementId
            || resources.TextLayout.Root.DatElementId != TextRootElementId
            || resources.TextLayout.FindElement(TextAreaElementId) is not UiText textArea
            || resources.TextFragments.Count == 0
            || resources.PictureIds.Count == 0
            || !float.IsFinite(resources.SectionSeconds)
            || resources.SectionSeconds <= 0f)
        {
            Console.WriteLine(
                "[UI] credits: authored root/text/picture contract is incomplete.");
            return null;
        }

        return new CreditsUiController(
            host,
            resources.PictureLayout,
            resources.TextLayout,
            textArea,
            resources.TextFragments,
            resources.PictureIds,
            resources.SectionSeconds,
            dialogs,
            resources.PleaseWait,
            nowSeconds,
            resolveSprite,
            returnToCharacterManagement);
    }

    internal void Activate()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_active)
            return;

        AttachRoots();
        ResetRun();
        _active = true;
        PictureRoot.Visible = true;
        TextRoot.Visible = true;
        _actionSurface.Visible = true;
        _host.DeclareFixedCanvas(this, _authoredCanvas);
        _host.BringToFront(PictureRoot);
        _host.BringToFront(TextRoot);
        _host.BringToFront(_actionSurface);
        _host.SetKeyboardFocus(_actionSurface);

        Tick();
    }

    internal void Tick()
    {
        if (_disposed || !_active)
            return;

        _tickSequence++;
        if (_returnPending)
        {
            if (_tickSequence >= _returnAtTick)
                CompleteReturn();
            return;
        }

        double elapsed = Math.Max(0d, _nowSeconds() - _startTime);
        float progress = _duration <= 0d
            ? 1f
            : Math.Clamp((float)(elapsed / _duration), 0f, 1f);
        progress = MathF.Max(progress, _lastProgress);
        _lastProgress = progress;

        float fieldHeight = TextRoot.Height;
        int oldTop = (int)MathF.Round(_textArea.Top);
        int travel = (int)MathF.Round(
            (fieldHeight + _textHeight) * progress,
            MidpointRounding.ToEven);
        int newTop = (int)MathF.Round(fieldHeight) - travel;
        _textArea.Left = 0f;
        _textArea.Top = newTop;
        ScrollPictures(oldTop - newTop);

        if (progress >= 1f)
            BeginReturn();
    }

    internal void ResetSession()
    {
        if (_disposed || !_active)
            return;
        Deactivate();
        _returnToCharacterManagement();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        bool restoreCharacterManagement = _active;
        _disposed = true;
        Deactivate();
        _host.RemoveChild(PictureRoot);
        _host.RemoveChild(TextRoot);
        _host.RemoveChild(_actionSurface);
        if (restoreCharacterManagement)
            _returnToCharacterManagement();
    }

    private void AttachRoots()
    {
        if (PictureRoot.Parent is null)
            _host.AddChild(PictureRoot);
        if (TextRoot.Parent is null)
            _host.AddChild(TextRoot);
        if (_actionSurface.Parent is null)
            _host.AddChild(_actionSurface);
    }

    private void ResetRun()
    {
        CloseWait();
        ClearPictures();
        _returnPending = false;
        _returnAtTick = long.MaxValue;
        _lastProgress = 0f;
        _nextPicture = 0;

        _textArea.Width = TextRoot.Width;
        float maximumWidth = Math.Max(
            1f,
            _textArea.Width
                - (_textArea.Padding + _textArea.MarginLeft)
                - (_textArea.Padding + _textArea.MarginRight));
        Func<string, float> measure = _textArea.DatFont is { } font
            ? font.MeasureWidth
            : static value => value.Length * 8f;
        string allText = string.Concat(_textFragments);
        IReadOnlyList<string> wrapped = UiText.WrapWords(
            allText,
            measure,
            maximumWidth);
        if (wrapped.Count == 0)
            wrapped = [string.Empty];
        _lines = [.. wrapped.Select(
            line => new UiText.Line(line, _textArea.DefaultColor))];
        _textArea.LinesProvider = () => _lines;

        float lineHeight = _textArea.DatFont?.LineHeight ?? 16f;
        _textHeight = Math.Max(lineHeight, lineHeight * _lines.Length);
        _textArea.Left = 0f;
        _textArea.Top = TextRoot.Height;
        _textArea.Height = _textHeight;

        float terminatorIndex = _textFragments.Count + 1f;
        _duration = _sectionSeconds
            * (TextRoot.Height + _textHeight)
            / (TextRoot.Height + _textHeight / terminatorIndex);
        _startTime = _nowSeconds();
    }

    private void ScrollPictures(int deltaPixels)
    {
        if (deltaPixels != 0)
            foreach (UiPanel picture in _pictures)
                picture.Top -= deltaPixels;

        if (_pictures.Count > 0
            && _pictures[0].Top + _pictures[0].Height < 0f)
        {
            UiPanel expired = _pictures[0];
            _pictures.RemoveAt(0);
            PictureRoot.RemoveChild(expired);
        }

        if (_pictures.Count == 0)
            AddPicture();

        if (_pictures.Count > 0
            && _pictures[^1].Top < PictureRoot.Height)
        {
            AddPicture();
        }
    }

    private void AddPicture()
    {
        if (_pictureIds.Count == 0)
            return;

        uint pictureId = _pictureIds[_nextPicture];
        _nextPicture = (_nextPicture + 1) % _pictureIds.Count;
        (uint texture, int width, int height) = _resolveSprite(pictureId);
        if (texture == 0u || width <= 0 || height <= 0)
            return;

        float top = _pictures.Count == 0
            ? PictureRoot.Height + 1f
            : _pictures[^1].Top + _pictures[^1].Height + 1f;
        var picture = new UiPanel
        {
            DatElementId = DynamicPictureElementId,
            Left = 0f,
            Top = top,
            Width = width,
            Height = height,
            BackgroundColor = Vector4.Zero,
            BorderColor = Vector4.Zero,
            BorderThickness = 0f,
            BackgroundSprite = pictureId,
            SpriteResolve = _resolveSprite,
            ClickThrough = true,
        };
        PictureRoot.AddChild(picture);
        _pictures.Add(picture);
    }

    private void BeginReturn()
    {
        if (_disposed || !_active || _returnPending)
            return;

        _returnPending = true;
        _waitContext = _dialogs.MakeWait(_pleaseWait);
        _returnAtTick = _tickSequence + 2;
    }

    private void CompleteReturn()
    {
        if (!_active)
            return;
        Deactivate();
        _returnToCharacterManagement();
    }

    private void Deactivate()
    {
        _returnPending = false;
        _returnAtTick = long.MaxValue;
        _active = false;
        PictureRoot.Visible = false;
        TextRoot.Visible = false;
        _actionSurface.Visible = false;
        if (ReferenceEquals(_host.KeyboardFocus, _actionSurface))
            _host.SetKeyboardFocus(null);
        _host.RevokeFixedCanvas(this);
        CloseWait();
        ClearPictures();
    }

    private void CloseWait()
    {
        uint context = _waitContext;
        _waitContext = 0u;
        if (context != 0u)
            _dialogs.CloseDialog(context);
    }

    private void ClearPictures()
    {
        foreach (UiPanel picture in _pictures)
            PictureRoot.RemoveChild(picture);
        _pictures.Clear();
    }

    private sealed class CreditsActionSurface : UiElement
    {
        private readonly Action _onAction;

        public override bool HandlesClick => true;

        public CreditsActionSurface(Action onAction)
        {
            _onAction = onAction ?? throw new ArgumentNullException(nameof(onAction));
            AcceptsFocus = true;
            ClickThrough = false;
        }

        public override bool OnEvent(in UiEvent e)
        {
            if (!Enabled || !Visible)
                return false;
            if (e.Type is UiEventType.KeyDown
                or UiEventType.MouseDown
                or UiEventType.RightDown
                or UiEventType.MiddleDown
                or UiEventType.Scroll)
            {
                _onAction();
                return true;
            }
            return e.Type is UiEventType.KeyUp
                or UiEventType.MouseUp
                or UiEventType.RightUp
                or UiEventType.MiddleUp
                or UiEventType.Click
                or UiEventType.RightClick;
        }
    }
}
