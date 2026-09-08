using AcDream.Content;
using Chorizite.Core.Render;
using Chorizite.Core.Render.Enums;
using DatReaderWriter.Enums;
using System;
using System.Collections.Generic;
using AcDream.App.Rendering;

namespace AcDream.App.Rendering.Wb {
    internal sealed class TextureAtlasDisposeTransaction {
        private bool _running;

        public bool IsComplete { get; private set; }
        public bool IsRunning => _running;

        public void Advance(
            Action retryLayerRetirements,
            Action disposeTextureArray,
            Action commitLogicalDisposal) {
            ArgumentNullException.ThrowIfNull(retryLayerRetirements);
            ArgumentNullException.ThrowIfNull(disposeTextureArray);
            ArgumentNullException.ThrowIfNull(commitLogicalDisposal);
            if (IsComplete || _running)
                return;

            _running = true;
            try {
                retryLayerRetirements();
                disposeTextureArray();
                commitLogicalDisposal();
                IsComplete = true;
            }
            finally {
                _running = false;
            }
        }
    }

    public class TextureAtlasManager : IDisposable {
        private static uint _nextSlot = 1;
        private readonly int _textureWidth;
        private readonly int _textureHeight;
        private readonly TextureFormat _format;
        private readonly Dictionary<TextureKey, int> _textureIndices = new();
        private readonly Dictionary<int, int> _refCounts = new();
        private readonly TextureAtlasSlotAllocator _slots;
        private readonly TextureAtlasLayerRetirement _layerRetirement;
        private readonly TextureAtlasDisposeTransaction _disposeTransaction = new();
        private readonly Action<TextureAtlasManager>? _onGpuSafeEmpty;
        private bool _disposed;
        internal const long TargetArrayBytes = 8L * 1024 * 1024;
        internal const int MaximumArrayLayers = 32;

        public uint Slot { get; }

        internal IWorldTextureArray TextureArray { get; private set; } = null!;

        public int UsedSlots => _textureIndices.Count;
        public int TotalSlots => TextureArray?.Size ?? 0;
        public int AvailableSlots => _slots.AvailableCount;
        internal bool IsGpuSafeEmpty => UsedSlots == 0 && AvailableSlots == TotalSlots;
        internal long AllocatedBytes {
            get {
                return TextureArray.TotalSizeInBytes;
            }
        }
        internal bool IsPhysicalRetirementComplete =>
            TextureArray.IsPhysicalRetirementComplete;
        internal long LastUseSequence { get; set; }
        internal int Width => _textureWidth;
        internal int Height => _textureHeight;
        internal TextureFormat Format => _format;

        internal TextureAtlasManager(
            IWorldTextureArrayFactory arrays,
            int width,
            int height,
            TextureFormat format = TextureFormat.RGBA8,
            Action<TextureAtlasManager>? onGpuSafeEmpty = null) {
            ArgumentNullException.ThrowIfNull(arrays);
            Slot = _nextSlot++;
            _textureWidth = width;
            _textureHeight = height;
            _format = format;
            _onGpuSafeEmpty = onGpuSafeEmpty;
            _layerRetirement = new TextureAtlasLayerRetirement(arrays.Retirement);
            int capacity = CalculateInitialCapacity(width, height, format);
            TextureArray = arrays.CreateClampedArray(format, width, height, capacity);
            _slots = new TextureAtlasSlotAllocator(TextureArray.Size);
        }

        internal static int CalculateInitialCapacity(int width, int height, TextureFormat format) {
            ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
            ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);

            long bytesPerLayer = CalculateMipChainBytes(width, height, format);
            long targetLayers = Math.Max(1L, TargetArrayBytes / bytesPerLayer);
            return checked((int)Math.Min(targetLayers, MaximumArrayLayers));
        }

        internal static long CalculateMipChainBytes(int width, int height, TextureFormat format) {
            long total = 0;
            int w = width;
            int h = height;
            while (true) {
                total = checked(total + CalculateLevelBytes(w, h, format));
                if (w == 1 && h == 1) return total;
                w = Math.Max(1, w >> 1);
                h = Math.Max(1, h >> 1);
            }
        }

        internal static long CalculateArrayBytes(int width, int height, TextureFormat format) =>
            checked(CalculateMipChainBytes(width, height, format)
                * CalculateInitialCapacity(width, height, format));

        internal static long CalculateLevelBytes(int width, int height, TextureFormat format) => format switch {
            TextureFormat.RGBA8 => checked((long)width * height * 4L),
            TextureFormat.RGB8 => checked((long)width * height * 3L),
            TextureFormat.A8 => checked((long)width * height),
            TextureFormat.Rgba32f => checked((long)width * height * 16L),
            TextureFormat.DXT1 => checked((long)Math.Max(1, (width + 3) / 4) * Math.Max(1, (height + 3) / 4) * 8L),
            TextureFormat.DXT3 or TextureFormat.DXT5 => checked((long)Math.Max(1, (width + 3) / 4) * Math.Max(1, (height + 3) / 4) * 16L),
            _ => throw new NotSupportedException($"Unsupported texture-atlas format {format}.")
        };

        public int AddTexture(TextureKey key, byte[] data, UploadPixelFormat? uploadPixelFormat = null, UploadPixelType? uploadPixelType = null) {
            ObjectDisposedException.ThrowIf(_disposed || _disposeTransaction.IsRunning, this);
            _layerRetirement.RetryPendingPublications();
            if (_textureIndices.TryGetValue(key, out var existingIndex)) {
                _refCounts[existingIndex]++;
                return existingIndex;
            }

            int index = _slots.Rent();

            try {
                TextureArray.UpdateLayer(index, data, uploadPixelFormat, uploadPixelType);
                _textureIndices[key] = index;
                _refCounts[index] = 1;
                return index;
            }
            catch (Exception) {
                if (!_textureIndices.ContainsKey(key))
                    _slots.Return(index);
                throw;
            }
        }

        public void ReleaseTexture(TextureKey key) {
            ObjectDisposedException.ThrowIf(_disposed || _disposeTransaction.IsRunning, this);
            _layerRetirement.RetryPendingPublications();
            if (!_textureIndices.TryGetValue(key, out var index)) return;

            if (!_refCounts.ContainsKey(index)) return;

            _refCounts[index]--;
            if (_refCounts[index] <= 0) {
                _textureIndices.Remove(key);
                _refCounts.Remove(index);
                _layerRetirement.Retire(
                    () => {
                        if (!_disposed)
                            _slots.Return(index);
                    },
                    () => {
                        if (!_disposed && IsGpuSafeEmpty)
                            _onGpuSafeEmpty?.Invoke(this);
                    });
            }
        }

        internal void RetryPendingRetirements() =>
            _layerRetirement.RetryPendingPublications();

        public bool HasTexture(TextureKey key) => _textureIndices.ContainsKey(key);

        public int GetTextureIndex(TextureKey key) =>
            _textureIndices.TryGetValue(key, out var index) ? index : -1;

        public void Dispose() {
            if (_disposed) return;
            _disposeTransaction.Advance(
                _layerRetirement.RetryPendingPublications,
                () => {
                    TextureArray?.Dispose();
                    if (TextureArray is not null && !TextureArray.HasDurableDisposeOwnership)
                        throw new InvalidOperationException(
                            "Texture-array disposal returned without retaining or publishing its physical release.");
                },
                () => {
                    _textureIndices.Clear();
                    _refCounts.Clear();
                    _disposed = true;
                });
        }
    }

    internal sealed class TextureAtlasLayerRetirement
    {
        private readonly GpuRetirementLedger _ledger;

        public TextureAtlasLayerRetirement(IGpuResourceRetirementQueue queue) =>
            _ledger = new GpuRetirementLedger(queue);

        internal int AwaitingPublicationCount => _ledger.AwaitingPublicationCount;

        public void Retire(Action returnLayer, Action notifyGpuSafeEmpty)
        {
            ArgumentNullException.ThrowIfNull(returnLayer);
            ArgumentNullException.ThrowIfNull(notifyGpuSafeEmpty);
            _ledger.Retire(new RetryableGpuResourceRelease(
                returnLayer,
                notifyGpuSafeEmpty));
        }

        public void RetryPendingPublications() =>
            _ledger.RetryPendingPublications();
    }
}
