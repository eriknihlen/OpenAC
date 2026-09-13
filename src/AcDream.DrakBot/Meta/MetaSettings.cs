namespace AcDream.DrakBot.Meta;

/// <summary>
/// The meta's live state and the bridge from VTank/RynthAi option names to
/// the bot's profile. The expression engine reads and writes the state
/// (<c>setmetastate</c>) and the options (<c>vtsetsetting</c>,
/// <c>/vt opt set</c>) through this; the owner supplies the option map.
/// </summary>
public sealed class MetaSettings
{
    private readonly Func<Dictionary<string, (Func<string> Get, Action<string> Set)>> _map;

    public MetaSettings(Func<Dictionary<string, (Func<string> Get, Action<string> Set)>> map)
    {
        _map = map ?? throw new ArgumentNullException(nameof(map));
    }

    public string CurrentState { get; set; } = "Default";

    /// <summary>Set by anything that changes the state; the engine re-arms every rule on the next tick.</summary>
    public bool ForceStateReset { get; set; }

    public Dictionary<string, (Func<string> Get, Action<string> Set)> BuildMap() => _map();
}
