using System;
using System.Globalization;

namespace AcDream.Core.Items;

public sealed class StackSplitQuantityState
{
    public uint Value { get; private set; } = 1u;
    public uint Maximum { get; private set; } = 1u;
    public float Ratio => Value / (float)Maximum;

    public event Action? Changed;

    public void Reset(uint stackSize, uint? initialValue = null)
    {
        uint maximum = Math.Max(stackSize, 1u);
        Set(initialValue ?? maximum, maximum);
    }

    public void SetFromText(string? text)
    {
        uint parsed = uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out uint value)
            ? value
            : 0u;
        SetValue(parsed);
    }

    public void SetFromSliderRatio(float ratio)
    {
        float clamped = Math.Clamp(ratio, 0f, 1f);
        uint positionMillis = (uint)MathF.Truncate(clamped * 1000f);
        ulong scaled = (ulong)positionMillis * Maximum;
        uint value = 1u + (uint)(scaled / 1000u);
        SetValue(value);
    }

    public void SetValue(uint value) => Set(value, Maximum);

    public uint GetObjectSplitSize(uint objectId, uint selectedObjectId, uint objectStackSize)
    {
        if (objectId == selectedObjectId)
            return Value;
        return Math.Max(objectStackSize, 1u);
    }

    private void Set(uint value, uint maximum)
    {
        maximum = Math.Max(maximum, 1u);
        value = Math.Clamp(value, 1u, maximum);
        if (Value == value && Maximum == maximum)
            return;

        Value = value;
        Maximum = maximum;
        Changed?.Invoke();
    }
}
