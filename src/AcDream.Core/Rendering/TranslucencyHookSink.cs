using System;
using System.Numerics;
using AcDream.Core.Physics;
using DatReaderWriter.Types;

namespace AcDream.Core.Rendering;

public sealed class TranslucencyHookSink : IAnimationHookSink
{
    private readonly TranslucencyFadeManager _fades;

    public TranslucencyHookSink(TranslucencyFadeManager fades)
    {
        _fades = fades ?? throw new ArgumentNullException(nameof(fades));
    }

    public void OnHook(uint entityId, Vector3 entityWorldPosition, AnimationHook hook)
    {
        if (hook is not TransparentPartHook tph) return;
        _fades.StartPartFade(entityId, tph.PartIndex, tph.Start, tph.End, tph.Time);
    }
}
