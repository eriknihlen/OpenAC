namespace AcDream.Core.Physics.Motion;

public static class ConstraintDistance
{
    private const float OutdoorStart = 10.0f;
    private const float IndoorStart = 5.0f;
    private const float OutdoorMax = 50.0f;
    private const float IndoorMax = 20.0f;

    public static bool IsIndoorCell(uint objCellId) => (objCellId & 0xFFFFu) >= 0x0100u;

    public static float GetStartConstraintDistance(uint objCellId) =>
        IsIndoorCell(objCellId) ? IndoorStart : OutdoorStart;

    public static float GetMaxConstraintDistance(uint objCellId) =>
        IsIndoorCell(objCellId) ? IndoorMax : OutdoorMax;
}
