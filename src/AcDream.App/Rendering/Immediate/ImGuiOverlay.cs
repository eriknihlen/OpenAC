using System.Numerics;
using System.Runtime.InteropServices;
using AcDream.App.Input;
using AcDream.App.Rendering.Gpu;
using ImGuiNET;
using Silk.NET.Input;

namespace AcDream.App.Rendering.Immediate;

/// <summary>
/// One Dear ImGui context drawn over the finished game frame. Owns the
/// context, the font atlas and the renderer; runs the registered drawers each
/// frame between <see cref="BeginFrame"/> and <see cref="Render"/>; and
/// reports ImGui's capture wishes so the game and the retained UI yield the
/// mouse and keyboard while a tool window is being used.
/// </summary>
internal sealed unsafe class ImGuiOverlay : IDevToolsFrameLifecycle, IExternalInputCapture, IDisposable
{
    private readonly ICurrentGpuFrameSource _frames;
    private readonly ImGuiDrawerRegistry _drawers;
    private readonly Action<string> _log;
    private readonly nint _context;
    private readonly ImGuiRenderer _renderer;
    private readonly ImGuiInputBridge _input;
    private readonly nint _iniPath;
    private readonly nint _fontData;
    private bool _frameOpen;
    private bool _disposed;

    internal ImGuiOverlay(
        IGpuDevice device,
        ICurrentGpuFrameSource frames,
        IInputContext input,
        ImGuiDrawerRegistry drawers,
        string? iniPath,
        Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(device);
        _frames = frames ?? throw new ArgumentNullException(nameof(frames));
        ArgumentNullException.ThrowIfNull(input);
        _drawers = drawers ?? throw new ArgumentNullException(nameof(drawers));
        _log = log ?? throw new ArgumentNullException(nameof(log));

        _context = ImGui.CreateContext();
        ImGui.SetCurrentContext(_context);
        ImGuiIOPtr io = ImGui.GetIO();
        io.ConfigFlags |= ImGuiConfigFlags.NavEnableKeyboard;
        io.BackendFlags |= ImGuiBackendFlags.RendererHasVtxOffset;
        if (iniPath is not null)
        {
            _iniPath = Marshal.StringToCoTaskMemUTF8(iniPath);
            io.NativePtr->IniFilename = (byte*)_iniPath;
        }
        else
        {
            io.NativePtr->IniFilename = null;
        }

        _fontData = LoadFont(io);
        ApplyTheme();

        _renderer = new ImGuiRenderer(device);
        try
        {
            _renderer.CreateFontTexture(io);
            _input = new ImGuiInputBridge(input);
        }
        catch
        {
            _renderer.Dispose();
            ImGui.DestroyContext(_context);
            throw;
        }
    }

    public bool WantCaptureMouse => !_disposed && ImGui.GetIO().WantCaptureMouse;

    public bool WantCaptureKeyboard => !_disposed && ImGui.GetIO().WantCaptureKeyboard;

    public void BeginFrame(float deltaSeconds, int viewportWidth, int viewportHeight)
    {
        if (_disposed || _frameOpen)
            return;
        if (viewportWidth <= 0 || viewportHeight <= 0)
            return;
        ImGuiIOPtr io = ImGui.GetIO();
        io.DisplaySize = new Vector2(viewportWidth, viewportHeight);
        io.DeltaTime = deltaSeconds > 0f && float.IsFinite(deltaSeconds) ? deltaSeconds : 1f / 60f;
        ImGui.NewFrame();
        _frameOpen = true;
    }

    public void Render(double deltaSeconds, int viewportWidth, int viewportHeight)
    {
        if (_disposed || !_frameOpen)
            return;
        _frameOpen = false;
        _drawers.DrawAll((name, error) =>
            _log($"immediate ui: drawer '{name}' threw and was removed: {error}"));
        ImGui.Render();
        IGpuFrame? frame = _frames.CurrentFrame;
        if (frame is null)
            return;
        _renderer.Draw(frame, ImGui.GetDrawData(), viewportWidth, viewportHeight);
    }

    public void AbortFrame()
    {
        if (_disposed || !_frameOpen)
            return;
        _frameOpen = false;
        ImGui.EndFrame();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_frameOpen)
        {
            _frameOpen = false;
            ImGui.EndFrame();
        }
        _input.Dispose();
        _renderer.Dispose();
        ImGui.DestroyContext(_context);
        if (_fontData != nint.Zero)
            Marshal.FreeHGlobal(_fontData);
        if (_iniPath != nint.Zero)
            Marshal.FreeCoTaskMem(_iniPath);
    }

    /// <summary>A readable UI face from the system when one exists; ImGui's built-in otherwise.</summary>
    private static nint LoadFont(ImGuiIOPtr io)
    {
        byte[]? ttf = TryLoadSystemUiFont();
        if (ttf is null)
        {
            io.Fonts.AddFontDefault();
            return nint.Zero;
        }
        nint data = Marshal.AllocHGlobal(ttf.Length);
        Marshal.Copy(ttf, 0, data, ttf.Length);
        ImFontConfigPtr config = ImGuiNative.ImFontConfig_ImFontConfig();
        try
        {
            // The atlas must not free memory it did not allocate.
            config.FontDataOwnedByAtlas = false;
            io.Fonts.AddFontFromMemoryTTF(data, ttf.Length, 16f, config);
        }
        finally
        {
            config.Destroy();
        }
        return data;
    }

    private static byte[]? TryLoadSystemUiFont()
    {
        string[] candidates =
        [
            @"C:\Windows\Fonts\segoeui.ttf",
            @"C:\Windows\Fonts\tahoma.ttf",
            @"C:\Windows\Fonts\arial.ttf",
            "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
            "/usr/share/fonts/TTF/DejaVuSans.ttf",
            "/usr/share/fonts/truetype/noto/NotoSans-Regular.ttf",
        ];
        foreach (string path in candidates)
        {
            try
            {
                if (File.Exists(path))
                    return File.ReadAllBytes(path);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
        return null;
    }

    /// <summary>Dark, low-saturation palette that sits quietly over the game.</summary>
    private static void ApplyTheme()
    {
        ImGui.StyleColorsDark();
        ImGuiStylePtr style = ImGui.GetStyle();
        style.WindowRounding = 6f;
        style.FrameRounding = 4f;
        style.GrabRounding = 4f;
        style.WindowBorderSize = 1f;
        style.FrameBorderSize = 0f;
        style.WindowPadding = new Vector2(10f, 10f);
        style.FramePadding = new Vector2(6f, 4f);
        style.ItemSpacing = new Vector2(8f, 6f);

        RangeAccessor<Vector4> colors = style.Colors;
        colors[(int)ImGuiCol.WindowBg] = new Vector4(0.05f, 0.07f, 0.09f, 0.94f);
        colors[(int)ImGuiCol.ChildBg] = new Vector4(0.06f, 0.08f, 0.11f, 0.60f);
        colors[(int)ImGuiCol.PopupBg] = new Vector4(0.05f, 0.07f, 0.09f, 0.98f);
        colors[(int)ImGuiCol.Border] = new Vector4(0.15f, 0.25f, 0.35f, 0.80f);
        colors[(int)ImGuiCol.TitleBg] = new Vector4(0.04f, 0.06f, 0.08f, 1.00f);
        colors[(int)ImGuiCol.TitleBgActive] = new Vector4(0.06f, 0.12f, 0.18f, 1.00f);
        colors[(int)ImGuiCol.FrameBg] = new Vector4(0.09f, 0.14f, 0.19f, 1.00f);
        colors[(int)ImGuiCol.FrameBgHovered] = new Vector4(0.13f, 0.21f, 0.29f, 1.00f);
        colors[(int)ImGuiCol.FrameBgActive] = new Vector4(0.16f, 0.27f, 0.37f, 1.00f);
        colors[(int)ImGuiCol.Button] = new Vector4(0.10f, 0.20f, 0.30f, 1.00f);
        colors[(int)ImGuiCol.ButtonHovered] = new Vector4(0.15f, 0.31f, 0.45f, 1.00f);
        colors[(int)ImGuiCol.ButtonActive] = new Vector4(0.18f, 0.40f, 0.58f, 1.00f);
        colors[(int)ImGuiCol.Header] = new Vector4(0.10f, 0.20f, 0.30f, 1.00f);
        colors[(int)ImGuiCol.HeaderHovered] = new Vector4(0.15f, 0.31f, 0.45f, 1.00f);
        colors[(int)ImGuiCol.HeaderActive] = new Vector4(0.18f, 0.40f, 0.58f, 1.00f);
        colors[(int)ImGuiCol.Tab] = new Vector4(0.08f, 0.14f, 0.20f, 1.00f);
        colors[(int)ImGuiCol.TabHovered] = new Vector4(0.15f, 0.31f, 0.45f, 1.00f);
        colors[(int)ImGuiCol.TabSelected] = new Vector4(0.13f, 0.26f, 0.38f, 1.00f);
        colors[(int)ImGuiCol.CheckMark] = new Vector4(0.15f, 0.85f, 0.90f, 1.00f);
        colors[(int)ImGuiCol.SliderGrab] = new Vector4(0.15f, 0.65f, 0.75f, 1.00f);
        colors[(int)ImGuiCol.SliderGrabActive] = new Vector4(0.15f, 0.85f, 0.90f, 1.00f);
        colors[(int)ImGuiCol.Text] = new Vector4(0.85f, 0.90f, 0.95f, 1.00f);
        colors[(int)ImGuiCol.TextDisabled] = new Vector4(0.50f, 0.58f, 0.66f, 1.00f);
    }
}
