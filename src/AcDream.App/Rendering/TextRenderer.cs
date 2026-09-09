using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using AcDream.App.Rendering.Gpu;

namespace AcDream.App.Rendering;

public sealed class TextRenderer : IDisposable
{
    internal const int FloatsPerVertex = 8;
    private const int VertexStrideBytes = FloatsPerVertex * sizeof(float);

    internal static readonly GpuVertexLayout SpriteVertexLayout = GpuVertexLayout.Interleaved(
        strideBytes: VertexStrideBytes,
        [
            new GpuVertexAttribute(0, GpuVertexFormat.Float2, 0),
            new GpuVertexAttribute(1, GpuVertexFormat.Float2, 8),
            new GpuVertexAttribute(2, GpuVertexFormat.Float4, 16),
        ]);

    private readonly ICurrentGpuFrameSource _frameSource;
    private readonly IGpuPipeline _pipeline;

    private sealed class SpriteSeg { public uint Texture; public readonly List<float> Verts = new(256); }

    private readonly List<float> _textBuf = new(8192);
    private readonly List<float> _rectBuf = new(1024);
    private readonly List<SpriteSeg> _spriteSegs = new();
    private int _segUsed;
    private int _textVerts;
    private int _rectVerts;
    private Vector2 _screenSize;

    internal long DynamicBufferCapacityBytes => 0;

    internal IReadOnlyList<(uint Texture, int VertexCount, float Alpha)> DebugSpriteSegments
    {
        get
        {
            var result = new List<(uint, int, float)>(_segUsed);
            for (int i = 0; i < _segUsed; i++)
            {
                SpriteSeg seg = _spriteSegs[i];
                float alpha = seg.Verts.Count > 0 ? seg.Verts[7] : 0f;
                result.Add((seg.Texture, seg.Verts.Count / FloatsPerVertex, alpha));
            }
            return result;
        }
    }

    internal IReadOnlyList<(uint Texture, IReadOnlyList<float> Verts)> DebugSpriteSegmentVerts
    {
        get
        {
            var result = new List<(uint, IReadOnlyList<float>)>(_segUsed);
            for (int i = 0; i < _segUsed; i++)
            {
                SpriteSeg seg = _spriteSegs[i];
                result.Add((seg.Texture, seg.Verts.ToArray()));
            }
            return result;
        }
    }

    internal (int VertexCount, float Alpha) DebugTextBuffer
        => (_textVerts, _textBuf.Count > 0 ? _textBuf[7] : 0f);

    internal int DebugRectVertexCount => _rectVerts;

    private readonly List<float> _overlayTextBuf = new(1024);
    private readonly List<float> _overlayRectBuf = new(256);
    private readonly List<SpriteSeg> _overlaySpriteSegs = new();
    private int _overlaySegUsed;
    private int _overlayTextVerts;
    private int _overlayRectVerts;

    public bool OverlayMode { get; set; }

    internal TextRenderer(IGpuDevice device, ICurrentGpuFrameSource frameSource, string shaderDir)
    {
        ArgumentNullException.ThrowIfNull(device);
        _frameSource = frameSource ?? throw new ArgumentNullException(nameof(frameSource));
        ArgumentException.ThrowIfNullOrWhiteSpace(shaderDir);

        _pipeline = device.CreatePipeline(new GpuPipelineDescription
        {
            Name = "ui-text",
            Shaders = new GpuShaderSet("ui_text"),
            VertexLayout = SpriteVertexLayout,
            Topology = GpuPrimitiveTopology.TriangleList,
            Blend = GpuBlendMode.StraightAlpha,
            Depth = GpuDepthState.Disabled,
            Cull = GpuCullMode.None,
            AlphaToCoverage = false,
            ColorWrite = true,
            SampleCount = 1,
        });
    }

    internal Vector2 CanvasScale = Vector2.One;

    internal Func<uint, uint>? LinearTwinResolver { get; set; }

    public void Begin(Vector2 screenSize)
    {
        _screenSize = screenSize;
        _textBuf.Clear();
        _rectBuf.Clear();
        _segUsed = 0; // pool the SpriteSeg objects across frames
        _textVerts = 0;
        _rectVerts = 0;
        _overlayTextBuf.Clear();
        _overlayRectBuf.Clear();
        _overlaySegUsed = 0;
        _overlayTextVerts = 0;
        _overlayRectVerts = 0;
        OverlayMode = false;
    }

    public void DrawRect(float x, float y, float w, float h, Vector4 color)
    {
        if (OverlayMode) { AppendQuad(_overlayRectBuf, x, y, w, h, 0, 0, 0, 0, color); _overlayRectVerts += 6; }
        else             { AppendQuad(_rectBuf,        x, y, w, h, 0, 0, 0, 0, color); _rectVerts += 6; }
    }

    public void DrawFill(float x, float y, float w, float h, Vector4 color)
        => DrawSprite(UiTextureTableHandle.None, x, y, w, h, 0f, 0f, 1f, 1f, color);

    public void DrawRectOutline(float x, float y, float w, float h, Vector4 color, float thickness = 1f)
    {
        // top, bottom, left, right
        DrawRect(x, y, w, thickness, color);
        DrawRect(x, y + h - thickness, w, thickness, color);
        DrawRect(x, y, thickness, h, color);
        DrawRect(x + w - thickness, y, thickness, h, color);
    }

    /// <summary>
    /// Draw a single line of text at (x,y) where (x,y) is the top-left of the
    /// typographic block. Handles '\n' as a line break.
    /// </summary>
    public void DrawString(BitmapFont font, string text, float x, float y, Vector4 color)
        => DrawStringCore(
            font, text, x, y, color,
            clip: false, 0f, 0f, 0f, 0f);

    internal void DrawStringClipped(
        BitmapFont font,
        string text,
        float x,
        float y,
        Vector4 color,
        float clipLeft,
        float clipTop,
        float clipRight,
        float clipBottom)
        => DrawStringCore(
            font, text, x, y, color,
            clip: true, clipLeft, clipTop, clipRight, clipBottom);

    private void DrawStringCore(
        BitmapFont font,
        string text,
        float x,
        float y,
        Vector4 color,
        bool clip,
        float clipLeft,
        float clipTop,
        float clipRight,
        float clipBottom)
    {
        float cursorX = x;
        float baseline = y + font.Ascent;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\n')
            {
                cursorX = x;
                baseline += font.LineHeight;
                continue;
            }
            if (!font.TryGetGlyph(c, out var g))
            {
                // Unknown glyph — skip its advance width if '?' exists.
                if (font.TryGetGlyph('?', out var q))
                    cursorX += q.Advance;
                continue;
            }

            float gx = cursorX + g.OffsetX;
            float gy = baseline + g.OffsetY;
            float gw = g.Width;
            float gh = g.Height;
            float u0 = g.UvMinX;
            float v0 = g.UvMinY;
            float u1 = g.UvMaxX;
            float v1 = g.UvMaxY;

            if (gw > 0 && gh > 0
                && (!clip || QuadClipper.TryClip(
                    clipLeft, clipTop, clipRight, clipBottom,
                    ref gx, ref gy, ref gw, ref gh,
                    ref u0, ref v0, ref u1, ref v1)))
            {
                if (OverlayMode) { AppendQuad(_overlayTextBuf, gx, gy, gw, gh, u0, v0, u1, v1, color); _overlayTextVerts += 6; }
                else             { AppendQuad(_textBuf,        gx, gy, gw, gh, u0, v0, u1, v1, color); _textVerts += 6; }
            }
            cursorX += g.Advance;
        }
    }

    public void DrawSprite(uint texture, float x, float y, float w, float h,
        float u0, float v0, float u1, float v1, Vector4 tint)
    {
        if (CanvasScale != Vector2.One && LinearTwinResolver is { } resolve)
            texture = resolve(texture);

        SpriteSeg seg = OverlayMode
            ? NextSpriteSeg(_overlaySpriteSegs, ref _overlaySegUsed, texture)
            : NextSpriteSeg(_spriteSegs,        ref _segUsed,        texture);
        AppendQuad(seg.Verts, x, y, w, h, u0, v0, u1, v1, tint);
    }

    internal static uint ResolveExternalTextureSlot(GpuTextureSlot slot) =>
        UiTextureTableHandle.FromSlot(slot);

    private static SpriteSeg NextSpriteSeg(List<SpriteSeg> segs, ref int used, uint texture)
    {
        if (used > 0 && segs[used - 1].Texture == texture)
            return segs[used - 1];
        if (used < segs.Count)
        {
            var s = segs[used++];
            s.Texture = texture;
            s.Verts.Clear();
            return s;
        }
        var ns = new SpriteSeg { Texture = texture };
        segs.Add(ns);
        used++;
        return ns;
    }

    private void AppendQuad(List<float> buf,
        float x, float y, float w, float h,
        float u0, float v0, float u1, float v1, Vector4 color)
    {
        if (CanvasScale != Vector2.One)
        {
            x *= CanvasScale.X;
            y *= CanvasScale.Y;
            w *= CanvasScale.X;
            h *= CanvasScale.Y;
        }
        void V(float px, float py, float pu, float pv)
        {
            buf.Add(px); buf.Add(py);
            buf.Add(pu); buf.Add(pv);
            buf.Add(color.X); buf.Add(color.Y); buf.Add(color.Z); buf.Add(color.W);
        }
        V(x,     y,     u0, v0);
        V(x + w, y + h, u1, v1);
        V(x + w, y,     u1, v0);
        V(x,     y,     u0, v0);
        V(x,     y + h, u0, v1);
        V(x + w, y + h, u1, v1);
    }

    /// <summary>Upload + draw accumulated rects + text. font may be null if only DrawRect was used.</summary>
    public void Flush(BitmapFont? font)
    {
        bool anyNormal  = _segUsed > 0 || _textVerts > 0 || _rectVerts > 0;
        bool anyOverlay = _overlaySegUsed > 0 || _overlayTextVerts > 0 || _overlayRectVerts > 0;
        if (!anyNormal && !anyOverlay) return;

        IGpuFrame frame = _frameSource.CurrentFrame
            ?? throw new InvalidOperationException(
                "TextRenderer.Flush requires an open IGpuFrame (see GpuDeviceFrameLifetime) — " +
                "the host must drive IGpuDevice.BeginFrame() before rendering the retained UI.");

        using IGpuPassEncoder encoder = frame.BeginPass(new GpuPassDescription
        {
            Name = "ui-text",
            Color = new GpuColorAttachment(
                Target: null,
                Load: GpuLoadOp.Load,
                Store: GpuStoreOp.Store,
                ClearColor: default),
            Depth = null,
            SampleCount = 1,
        });
        encoder.BindPipeline(_pipeline);

        DrawLayer(_spriteSegs, _segUsed, _rectBuf, _rectVerts, _textBuf, _textVerts, font, frame, encoder);
        DrawLayer(_overlaySpriteSegs, _overlaySegUsed, _overlayRectBuf, _overlayRectVerts, _overlayTextBuf, _overlayTextVerts, font, frame, encoder);
    }

    private void DrawLayer(
        List<SpriteSeg> spriteSegs, int segUsed,
        List<float> rectBuf, int rectVerts,
        List<float> textBuf, int textVerts, BitmapFont? font,
        IGpuFrame frame, IGpuPassEncoder encoder)
    {
        if (segUsed > 0)
        {
            for (int i = 0; i < segUsed; i++)
            {
                var seg = spriteSegs[i];
                if (seg.Verts.Count == 0) continue;
                SetTextures(encoder, colorHandle: seg.Texture, coverageHandle: UiTextureTableHandle.None);
                DrawRing(frame, encoder, seg.Verts);
            }
        }

        if (rectVerts > 0)
        {
            SetTextures(encoder, UiTextureTableHandle.None, UiTextureTableHandle.None);
            DrawRing(frame, encoder, rectBuf);
        }

        if (textVerts > 0 && font is not null)
        {
            SetTextures(encoder, UiTextureTableHandle.None, coverageHandle: font.TextureId);
            DrawRing(frame, encoder, textBuf);
        }
    }

    private void SetTextures(IGpuPassEncoder encoder, uint colorHandle, uint coverageHandle)
    {
        GpuPushConstants constants = GpuPushConstants.Default;
        constants.ParamA = _screenSize.X;
        constants.ParamB = _screenSize.Y;
        constants.TextureIndexA = UiTextureTableHandle.ToSlot(colorHandle).Index;
        constants.TextureIndexB = UiTextureTableHandle.ToSlot(coverageHandle).Index;
        encoder.SetPushConstants(constants);
    }

    private static void DrawRing(IGpuFrame frame, IGpuPassEncoder encoder, List<float> buf)
    {
        if (buf.Count == 0)
            return;
        GpuRingAllocation allocation = frame.AllocateRing(buf.Count * sizeof(float), GpuRingUsage.Vertex);
        CollectionsMarshal.AsSpan(buf).CopyTo(allocation.AsSpan<float>());
        encoder.BindVertexBuffer(0, allocation.Buffer, allocation.OffsetBytes);
        encoder.Draw((uint)(buf.Count / FloatsPerVertex), 1, 0, 0);
    }

    public void Dispose() => _pipeline.Dispose();
}
