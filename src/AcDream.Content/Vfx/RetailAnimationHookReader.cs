using AcDream.Core.Vfx;
using DatReaderWriter.Enums;
using DatReaderWriter.Lib.IO;
using DatReaderWriter.Types;

namespace AcDream.Content.Vfx;

public static class RetailAnimationHookReader
{
    public static AnimationHook Read(DatBinReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        var type = (AnimationHookType)reader.ReadUInt32();
        reader.Skip(-sizeof(uint));

        AnimationHook? hook = type is AnimationHookType.CreateBlockingParticle
            ? new RetailCreateBlockingParticleHook()
            : AnimationHook.Unpack(reader, type);

        if (hook is null)
            throw new InvalidDataException(
                $"Unsupported animation hook type 0x{(uint)type:X8} at byte {reader.Offset}.");

        if (type is AnimationHookType.CreateBlockingParticle)
            hook.Unpack(reader);

        return hook;
    }
}
