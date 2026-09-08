using AcDream.Content;
using Chorizite.Core.Render.Enums;
using System;

namespace AcDream.App.Rendering.Wb {
    public static class TextureFormatExtensions {

        public static UploadPixelFormat ToPixelFormat(this Chorizite.Core.Render.Enums.TextureFormat format) {
            return format switch {
                Chorizite.Core.Render.Enums.TextureFormat.RGBA8 => UploadPixelFormat.Rgba,
                Chorizite.Core.Render.Enums.TextureFormat.RGB8 => UploadPixelFormat.Rgb,
                Chorizite.Core.Render.Enums.TextureFormat.A8 => UploadPixelFormat.Red,
                Chorizite.Core.Render.Enums.TextureFormat.Rgba32f => UploadPixelFormat.Rgba,
                _ => throw new NotSupportedException($"Texture format {format} is not supported."),
            };
        }

        public static UploadPixelType ToPixelType(this Chorizite.Core.Render.Enums.TextureFormat format) {
            return format switch {
                TextureFormat.RGBA8 => UploadPixelType.UnsignedByte,
                TextureFormat.RGB8 => UploadPixelType.UnsignedByte,
                TextureFormat.A8 => UploadPixelType.UnsignedByte,
                TextureFormat.Rgba32f => UploadPixelType.Float,
                _ => throw new NotSupportedException($"Texture format {format} is not supported."),
            };
        }
    }
}
