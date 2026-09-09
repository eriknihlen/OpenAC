using System.Numerics;
using DatReaderWriter.Types;

namespace AcDream.Core.Physics;

public interface IAnimationHookSink
{
    void OnHook(uint entityId, Vector3 entityWorldPosition, AnimationHook hook);
}

public sealed class NullAnimationHookSink : IAnimationHookSink
{
    public static readonly NullAnimationHookSink Instance = new();
    private NullAnimationHookSink() { }
    public void OnHook(uint entityId, Vector3 entityWorldPosition, AnimationHook hook) { }
}
