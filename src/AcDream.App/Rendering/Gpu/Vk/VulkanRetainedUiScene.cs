using System.Numerics;
using AcDream.App.Rendering;
using AcDream.App.UI;

namespace AcDream.App.Rendering.Gpu.Vk;

internal sealed class VulkanRetainedUiScene : IDisposable
{
    internal const int HeaderLeft = 24;
    internal const int HeaderTop = 18;
    internal const int HeaderWidth = 420;
    internal const int HeaderHeight = 96;

    private readonly MutableFrameSource _frames = new();
    private readonly UiHost _host;
    private readonly DebugLineRenderer _lines;
    private readonly BitmapFont? _font;
    private readonly List<IGpuTexture> _textures = [];

    private bool _disposed;

    internal VulkanRetainedUiScene(IGpuDevice device, string shaderDirectory)
    {
        ArgumentNullException.ThrowIfNull(device);

        byte[]? ttf = BitmapFont.TryLoadSystemMonospaceFont();
        _font = ttf is null ? null : new BitmapFont(device, ttf, pixelHeight: 18f);

        _host = new UiHost(device, _frames, shaderDirectory, _font);
        _lines = new DebugLineRenderer(device, _frames, shaderDirectory);

        uint chrome = CreateTexture(device, "vk-ui-chrome", BuildChromeTile(), 16, 16, nearest: false);
        uint icon = CreateTexture(device, "vk-ui-icon", BuildIcon(), 32, 32, nearest: true);

        BuildTree(chrome, icon);
    }

    /// <summary>True when a system font was found; without one no glyph draws happen.</summary>
    internal bool HasFont => _font is not null;

    internal void Render(IGpuFrame frame, uint width, uint height, double seconds)
    {
        ArgumentNullException.ThrowIfNull(frame);
        _frames.CurrentFrame = frame;
        try
        {
            _lines.Begin();
            float sweep = (float)Math.Sin(seconds) * 0.05f;
            _lines.AddLine(new Vector3(-0.92f, -0.30f, 0f), new Vector3(-0.30f, -0.68f, 0f), new Vector3(1f, 0.85f, 0.2f));
            _lines.AddLine(new Vector3(-0.30f, -0.68f, 0f), new Vector3(0.34f, -0.36f + sweep, 0f), new Vector3(0.2f, 1f, 0.6f));
            _lines.Flush(Matrix4x4.Identity, Matrix4x4.Identity);

            _host.Tick(0.016);
            _host.Draw(new Vector2(width, height));
        }
        finally
        {
            _frames.CurrentFrame = null;
        }
    }

    private void BuildTree(uint chrome, uint icon)
    {
        var header = new UiPanel
        {
            Name = "vk-header",
            Left = HeaderLeft,
            Top = HeaderTop,
            Width = HeaderWidth,
            Height = HeaderHeight,
            BackgroundSprite = 1,
            SpriteResolve = _ => (chrome, 16, 16),
        };
        header.AddChild(new UiLabel
        {
            Name = "vk-title",
            Left = 12,
            Top = 10,
            Width = 380,
            Height = 22,
            Text = "acdream - retained UI on Vulkan",
            TextColor = new Vector4(1f, 0.94f, 0.72f, 1f),
        });
        header.AddChild(new UiLabel
        {
            Name = "vk-subtitle",
            Left = 12,
            Top = 34,
            Width = 380,
            Height = 22,
            Text = "top-left origin - slice V6d",
            TextColor = new Vector4(0.75f, 0.9f, 1f, 1f),
        });

        var inner = new UiPanel
        {
            Name = "vk-inner",
            Left = 12,
            Top = 58,
            Width = 260,
            Height = 28,
            BackgroundColor = new Vector4(0.05f, 0.08f, 0.16f, 0.85f),
            BorderColor = new Vector4(0.6f, 0.75f, 1f, 1f),
            BorderThickness = 2f,
        };
        inner.AddChild(new UiLabel
        {
            Left = 8,
            Top = 6,
            Width = 240,
            Height = 18,
            Text = "fill + border + glyphs",
            TextColor = new Vector4(1f, 1f, 1f, 1f),
        });
        header.AddChild(inner);

        // Off to one side and near the bottom, so nothing about the layout is
        // mirror-symmetric in either axis.
        var badge = new UiTextureElement
        {
            Name = "vk-icon",
            Left = HeaderLeft + HeaderWidth + 16,
            Top = HeaderTop + 40,
            Width = 64,
            Height = 64,
            Texture = icon,
        };

        _host.Root.AddChild(header);
        _host.Root.AddChild(badge);
    }

    private uint CreateTexture(IGpuDevice device, string name, byte[] rgba, int width, int height, bool nearest)
    {
        IGpuTexture texture = device.CreateTexture(new GpuTextureDescription(
            name,
            GpuTextureKind.Texture2D,
            GpuTextureFormat.Rgba8Unorm,
            width,
            height,
            LayerCount: 1,
            MipLevelCount: 1));
        _textures.Add(texture);
        texture.Upload(0, 0, rgba);
        IGpuSampler sampler = device.CreateSampler(nearest
            ? GpuSamplerDescription.UiNearest
            : GpuSamplerDescription.WorldRepeat);
        return UiTextureTableHandle.FromSlot(device.RegisterTexture(texture, sampler));
    }

    private static byte[] BuildChromeTile()
    {
        const int extent = 16;
        var pixels = new byte[extent * extent * 4];
        for (int y = 0; y < extent; y++)
        {
            for (int x = 0; x < extent; x++)
            {
                bool edge = x == 0 || y == 0;
                byte r = edge ? (byte)0xC8 : (byte)0x2A;
                byte g = edge ? (byte)0xA0 : (byte)0x24;
                byte b = edge ? (byte)0x40 : (byte)0x1C;
                int offset = ((y * extent) + x) * 4;
                pixels[offset + 0] = r;
                pixels[offset + 1] = g;
                pixels[offset + 2] = b;
                pixels[offset + 3] = 0xF0;
            }
        }

        return pixels;
    }

    /// <summary>A 32x32 badge: red at the top, blue at the bottom, opaque ring.</summary>
    private static byte[] BuildIcon()
    {
        const int extent = 32;
        var pixels = new byte[extent * extent * 4];
        for (int y = 0; y < extent; y++)
        {
            for (int x = 0; x < extent; x++)
            {
                float dx = (x - 15.5f) / 15.5f;
                float dy = (y - 15.5f) / 15.5f;
                bool inside = (dx * dx) + (dy * dy) <= 1f;
                int offset = ((y * extent) + x) * 4;
                pixels[offset + 0] = (byte)(inside ? 255 - (y * 6) : 0);
                pixels[offset + 1] = (byte)(inside ? 60 : 0);
                pixels[offset + 2] = (byte)(inside ? y * 7 : 0);
                pixels[offset + 3] = (byte)(inside ? 255 : 0);
            }
        }

        return pixels;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _lines.Dispose();
        _host.Dispose();
        _font?.Dispose();
        for (int i = _textures.Count - 1; i >= 0; i--)
            _textures[i].Dispose();
        _textures.Clear();
    }

    private sealed class MutableFrameSource : ICurrentGpuFrameSource
    {
        public IGpuFrame? CurrentFrame { get; set; }
    }
}
