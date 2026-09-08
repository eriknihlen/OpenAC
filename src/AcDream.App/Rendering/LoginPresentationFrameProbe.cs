using System;

namespace AcDream.App.Rendering;

internal static class RenderPresentationDiagnostics
{
    public static bool ProbeLoginFrames { get; } =
        Environment.GetEnvironmentVariable("ACDREAM_PROBE_LOGIN_FRAMES") == "1";
}

internal sealed class LoginPresentationFrameProbe : IRenderFramePostDiagnosticsPhase
{
    private readonly Func<bool> _tunnelSceneVisible;
    private readonly IRenderLoginStateSource _login;
    private readonly Action<string> _log;
    private long _frame;
    private double _elapsedSeconds;
    private string? _lastClass;

    public LoginPresentationFrameProbe(
        Func<bool> tunnelSceneVisible,
        IRenderLoginStateSource login,
        Action<string> log)
    {
        _tunnelSceneVisible = tunnelSceneVisible
            ?? throw new ArgumentNullException(nameof(tunnelSceneVisible));
        _login = login ?? throw new ArgumentNullException(nameof(login));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public void Process(RenderFrameInput input, RenderFrameOutcome outcome)
    {
        _frame++;
        _elapsedSeconds += input.DeltaSeconds;

        bool world = outcome.World.NormalWorldDrawn;
        bool tunnel = _tunnelSceneVisible();
        bool cover = outcome.Presentation.PortalViewportDrawn;
        bool waiting = _login.IsWaitingForLogin;

        string presentClass =
            world ? "world"
            : tunnel ? "tunnel"
            : cover ? "black"
            : "void";

        if (presentClass == _lastClass)
            return;
        _lastClass = presentClass;
        _log(
            $"[login-frames] frame={_frame} t={_elapsedSeconds:F3}s "
            + $"present={presentClass} waiting={(waiting ? 1 : 0)} "
            + $"cover={(cover ? 1 : 0)} tunnel={(tunnel ? 1 : 0)} "
            + $"world={(world ? 1 : 0)}");
    }
}
