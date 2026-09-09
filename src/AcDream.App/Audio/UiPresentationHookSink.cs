using System;
using System.Numerics;
using AcDream.Core.Physics;
using DatReaderWriter.Types;

namespace AcDream.App.Audio;

public sealed class UiPresentationHookSink : IAnimationHookSink
{
    private readonly IAnimationHookSink _router;
    private readonly AudioHookSink? _audio;

    public UiPresentationHookSink(IAnimationHookSink router, AudioHookSink? audio)
    {
        _router = router ?? throw new ArgumentNullException(nameof(router));
        _audio = audio;
    }

    public void OnHook(uint entityId, Vector3 entityWorldPosition, AnimationHook hook)
    {
        if (hook is SoundHook or SoundTableHook or SoundTweakedHook)
        {
            // With no audio graph (headless/driver-less) the sound hook is
            // simply dropped — forwarding it to the router would put it back
            // on the world 3-D path this sink exists to bypass.
            _audio?.OnUiHook(entityId, hook);
            return;
        }

        _router.OnHook(entityId, entityWorldPosition, hook);
    }
}
