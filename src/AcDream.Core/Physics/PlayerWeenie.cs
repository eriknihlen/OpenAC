namespace AcDream.Core.Physics;

public sealed class PlayerWeenie : IWeenieObject
{
    public const float CanJumpLoadThreshold = 2.0f;

    private int _runSkill;
    private int _jumpSkill;
    private float _burden;

    private int? _playerKillerStatus;

    private float? _lastPkAttackTimestamp;

    private uint? _currentStamina;

    public PlayerWeenie(int runSkill = 0, int jumpSkill = 0, float burden = 0f)
    {
        _runSkill = runSkill;
        _jumpSkill = jumpSkill;
        _burden = burden;
    }

    public void SetSkills(int runSkill, int jumpSkill)
    {
        _runSkill = runSkill;
        _jumpSkill = jumpSkill;
    }

    public void SetBurden(float burden) => _burden = burden;

    public void SetStamina(uint? currentStamina) => _currentStamina = currentStamina;

    public void SetPlayerKillerStatus(int? playerKillerStatus, float? lastPkAttackTimestamp)
    {
        _playerKillerStatus = playerKillerStatus;
        _lastPkAttackTimestamp = lastPkAttackTimestamp;
    }

    public bool InqRunRate(out float rate)
    {
        int effectiveSkill = _currentStamina == 0 ? 0 : _runSkill;
        rate = MovementSystem.GetRunRate(_burden, effectiveSkill);
        return true;
    }

    public bool InqJumpVelocity(float extent, out float vz)
    {
        int effectiveSkill = _currentStamina == 0 ? 0 : _jumpSkill;
        float height = MovementSystem.GetJumpHeight(_burden, effectiveSkill, extent);
        vz = MathF.Sqrt(height * 19.6f);
        return true;
    }

    public bool CanJump(float extent) => _burden < CanJumpLoadThreshold;

    /// <summary>
    /// R3-W3 (W0-pins.md A3): the local player's weenie is THE player.
    /// Feeds W4's <c>apply_current_movement</c>/<c>ReportExhaustion</c>
    /// dual-dispatch gate.
    /// </summary>
    public bool IsThePlayer() => true;

    public bool JumpStaminaCost(float extent, out int cost)
    {
        bool pk = false;
        int pkStatus = _playerKillerStatus ?? 8;
        if ((pkStatus == 4 || pkStatus == 0x40) && _lastPkAttackTimestamp is { } ts)
        {
            float now = Environment.TickCount64 / 1000f;
            pk = (ts + 20.0f) >= now;
        }
        cost = MovementSystem.JumpStaminaCost(extent, _burden, pk);
        return true;
    }

    public static float GetRunRate(float burden, int runSkill) =>
        MovementSystem.GetRunRate(burden, runSkill);

    public static float GetJumpHeight(float burden, int jumpSkill, float extent) =>
        MovementSystem.GetJumpHeight(burden, jumpSkill, extent);

    public static float GetBurdenMod(float burden) => EncumbranceSystem.LoadMod(burden);
}
