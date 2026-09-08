using System.Collections.Generic;
using System.Numerics;

namespace AcDream.App.UI.Layout;

public enum UiPropertyKind : byte
{
    Enum,
    Bool,
    DataId,
    Float,
    Integer,
    StringInfo,
    Color,
    Array,
    Struct,
    Vector,
    Bitfield32,
    Bitfield64,
    InstanceId,
}

public readonly record struct UiColorValue(byte Blue, byte Green, byte Red, byte Alpha);

public readonly record struct UiStringInfoValue(
    byte Token,
    uint StringId,
    uint TableId,
    byte Override,
    byte English,
    byte Comment);

public sealed class UiPropertyValue
{
    public UiPropertyKind Kind;
    public uint MasterPropertyId;
    public ulong UnsignedValue;
    public int IntegerValue;
    public float FloatValue;
    public bool BoolValue;
    public UiStringInfoValue StringInfoValue;
    public UiColorValue ColorValue;
    public Vector3 VectorValue;
    public List<UiPropertyValue> ArrayValue = new();
    public Dictionary<uint, UiPropertyValue> StructValue = new();

    public UiPropertyValue Clone()
    {
        var clone = new UiPropertyValue
        {
            Kind = Kind,
            MasterPropertyId = MasterPropertyId,
            UnsignedValue = UnsignedValue,
            IntegerValue = IntegerValue,
            FloatValue = FloatValue,
            BoolValue = BoolValue,
            StringInfoValue = StringInfoValue,
            ColorValue = ColorValue,
            VectorValue = VectorValue,
        };

        foreach (var item in ArrayValue)
            clone.ArrayValue.Add(item.Clone());
        foreach (var (key, value) in StructValue)
            clone.StructValue[key] = value.Clone();
        return clone;
    }
}

public sealed class UiPropertyBag
{
    public Dictionary<uint, UiPropertyValue> Values = new();

    public bool TryGetValue(uint id, out UiPropertyValue value)
        => Values.TryGetValue(id, out value!);

    public UiPropertyBag Clone()
    {
        var clone = new UiPropertyBag();
        foreach (var (key, value) in Values)
            clone.Values[key] = value.Clone();
        return clone;
    }

    public static UiPropertyBag Merge(UiPropertyBag baseProperties, UiPropertyBag derivedProperties)
    {
        var merged = baseProperties.Clone();
        foreach (var (key, value) in derivedProperties.Values)
            merged.Values[key] = value.Clone();
        return merged;
    }
}

public readonly record struct UiImageMedia(uint File, int DrawMode);

/// <summary>What one entry of a state's media sequence does.</summary>
public enum UiMediaStepKind
{
    /// <summary>Anything we do not act on (sound, movie, message, ...).</summary>
    Other,

    /// <summary>Show this image.</summary>
    Image,

    Pause,

    /// <summary>Branch to another entry — what makes a sequence loop.</summary>
    Jump,

    /// <summary>Hand the element to another STATE when the sequence ends.</summary>
    State,
}

public readonly record struct UiMediaStep(
    UiMediaStepKind Kind,
    uint File,
    int DrawMode,
    float MinDuration,
    float MaxDuration,
    uint JumpIndex,
    float Probability,
    int RawType = 0);

public sealed class UiStateInfo
{
    public const uint DirectStateId = uint.MaxValue;

    public uint Id;
    public string Name = "";
    public bool PassToChildren;
    public uint IncorporationFlags;
    public UiImageMedia? Image;

    public IReadOnlyList<UiMediaStep> MediaSteps = Array.Empty<UiMediaStep>();
    public UiCursorMedia? Cursor;
    public UiPropertyBag Properties = new();

    public int MediaCount;

    public int ImageMediaCount;

    public UiStateInfo Clone()
        => new()
        {
            Id = Id,
            Name = Name,
            PassToChildren = PassToChildren,
            IncorporationFlags = IncorporationFlags,
            Image = Image,
            MediaSteps = MediaSteps,
            Cursor = Cursor,
            Properties = Properties.Clone(),
            MediaCount = MediaCount,
            ImageMediaCount = ImageMediaCount,
        };

    public static UiStateInfo Merge(UiStateInfo baseState, UiStateInfo derivedState)
        => new()
        {
            Id = derivedState.Id,
            Name = derivedState.Name,
            PassToChildren = derivedState.PassToChildren,
            IncorporationFlags = derivedState.IncorporationFlags,
            Image = derivedState.Image ?? baseState.Image,
            Cursor = derivedState.Cursor ?? baseState.Cursor,
            Properties = UiPropertyBag.Merge(baseState.Properties, derivedState.Properties),
            MediaCount = baseState.MediaCount + derivedState.MediaCount,
            ImageMediaCount = baseState.ImageMediaCount + derivedState.ImageMediaCount,
        };
}
