using DatReaderWriter.Enums;
using DatReaderWriter.Types;

namespace AcDream.Core.Vfx;

public sealed class RetailCreateBlockingParticleHook : CreateParticleHook
{
    public override AnimationHookType HookType => AnimationHookType.CreateBlockingParticle;
}
