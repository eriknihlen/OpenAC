namespace AcDream.Plugin.Abstractions;

/// <summary>One owned item that can participate in VTank equipment policy.</summary>
public readonly record struct PluginEquipmentItem(
    uint ObjectId,
    string Name,
    uint ItemType,
    uint ValidLocations,
    uint EquippedLocation,
    uint ContainerObjectId,
    uint WielderObjectId,
    byte CombatUse,
    int DamageType,
    int WeaponSkill,
    int Damage,
    double DamageVariance)
{
    public bool IsEquipped => EquippedLocation != 0u;
    public uint AmmoType { get; init; }
    public int StackSize { get; init; } = 1;
    public int WeaponType { get; init; }
}

public enum PluginEquipmentCommandStatus
{
    Unavailable = 0,
    InvalidItem,
    Busy,
    AlreadyEquipped,
    Started,
    Refused,
}

public readonly record struct PluginEquipmentCommandResult(
    PluginEquipmentCommandStatus Status,
    string? Notice = null)
{
    public bool Accepted => Status is
        PluginEquipmentCommandStatus.AlreadyEquipped
        or PluginEquipmentCommandStatus.Started;
}

public interface IEquipmentAutomation
{
    bool IsAvailable => false;
    bool IsBusy => false;

    IReadOnlyList<PluginEquipmentItem> CaptureOwnedEquipment() =>
        Array.Empty<PluginEquipmentItem>();

    PluginEquipmentCommandResult Equip(
        uint objectId,
        uint requestedLocation = 0u) =>
        new(PluginEquipmentCommandStatus.Unavailable);
}
