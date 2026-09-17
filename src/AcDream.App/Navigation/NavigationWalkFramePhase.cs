using AcDream.App.Rendering;
using AcDream.App.Update;
using AcDream.Runtime.Navigation;

namespace AcDream.App.Navigation;

/// <summary>
/// Advances navigation walks every update frame, before gameplay input is read, and
/// keeps a grid built around the character while the navigation view is shown.
/// </summary>
internal sealed class NavigationWalkFramePhase : IGameplayInputFramePhase
{
    private readonly NavigationWalkController _walk;
    private readonly WorldSceneDebugState _debug;
    private readonly IGameplayInputFramePhase _input;

    public NavigationWalkFramePhase(
        NavigationWalkController walk,
        WorldSceneDebugState debug,
        IGameplayInputFramePhase input)
    {
        _walk = walk ?? throw new ArgumentNullException(nameof(walk));
        _debug = debug ?? throw new ArgumentNullException(nameof(debug));
        _input = input ?? throw new ArgumentNullException(nameof(input));
    }

    public void Tick(UpdateFrameTiming timing)
    {
        _walk.ShowGrid = _debug.NavMeshVisible;
        _walk.Tick(timing.SimulationDeltaSeconds);
        _input.Tick(timing);
    }
}
