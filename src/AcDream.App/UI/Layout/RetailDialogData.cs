namespace AcDream.App.UI.Layout;

public static class RetailDialogProperty
{
    public const uint Priority = 0x8Du;
    public const uint Type = 0x8Eu;
    public const uint AcceptLabel = 0x90u;
    public const uint RejectLabel = 0x91u;
    public const uint ConfirmationResult = 0x92u;
    public const uint TextInputAcceptLabel = 0x9Au;
    public const uint TextInputRejectLabel = 0x9Bu;
    public const uint TextInputResult = 0x9Cu;
    public const uint MenuItems = 0xA6u;
    public const uint MenuItem = 0xA7u;
    public const uint MenuAcceptLabel = 0xA8u;
    public const uint MenuRejectLabel = 0xA9u;
    public const uint MenuSelection = 0xABu;
    public const uint ElementAttribute40 = 0xACu;
    public const uint QueueKey = 0xC3u;
    public const uint Message = 0xC5u;
    public const uint UsageObjectId = 0x1000003Du;
    public const uint TrainSkillId = 0x10000040u;
    public const uint TrainSkillCredits = 0x10000041u;
}

public enum RetailDialogType : uint
{
    Confirmation = 1,
    Wait = 2,
    Message = 3,
    TextInput = 4,
    ConfirmationTextInput = 5,
    Menu = 6,
    ConfirmationMenu = 7,
}

public sealed class RetailDialogData
{
    private readonly Dictionary<uint, object> _values = new();

    public IReadOnlyDictionary<uint, object> Values => _values;

    public RetailDialogData Set<T>(uint propertyId, T value) where T : notnull
    {
        _values[propertyId] = value;
        return this;
    }

    public bool Contains(uint propertyId) => _values.ContainsKey(propertyId);

    public bool TryGet<T>(uint propertyId, out T value)
    {
        if (_values.TryGetValue(propertyId, out object? raw) && raw is T typed)
        {
            value = typed;
            return true;
        }

        value = default!;
        return false;
    }

    public bool GetBoolean(uint propertyId, bool defaultValue = false)
        => _values.TryGetValue(propertyId, out object? raw)
            ? raw switch
            {
                bool value => value,
                byte value => value != 0,
                int value => value != 0,
                uint value => value != 0,
                _ => defaultValue,
            }
            : defaultValue;

    public uint GetUInt32(uint propertyId, uint defaultValue = 0u)
        => _values.TryGetValue(propertyId, out object? raw)
            ? raw switch
            {
                byte value => value,
                ushort value => value,
                int value when value >= 0 => (uint)value,
                uint value => value,
                Enum value => Convert.ToUInt32(value),
                _ => defaultValue,
            }
            : defaultValue;

    public int GetInt32(uint propertyId, int defaultValue = 0)
        => _values.TryGetValue(propertyId, out object? raw)
            ? raw switch
            {
                byte value => value,
                ushort value => value,
                int value => value,
                uint value when value <= int.MaxValue => (int)value,
                Enum value => Convert.ToInt32(value),
                _ => defaultValue,
            }
            : defaultValue;

    public string? GetString(uint propertyId)
        => _values.TryGetValue(propertyId, out object? raw) ? raw as string : null;

    public RetailDialogData Clone()
    {
        var clone = new RetailDialogData();
        foreach ((uint propertyId, object value) in _values)
            clone._values.Add(propertyId, value);
        return clone;
    }

    public static RetailDialogData Confirmation(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return new RetailDialogData()
            .Set(RetailDialogProperty.Type, RetailDialogType.Confirmation)
            .Set(RetailDialogProperty.ElementAttribute40, true)
            .Set(RetailDialogProperty.Message, message);
    }

    public static RetailDialogData Wait(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return new RetailDialogData()
            .Set(RetailDialogProperty.Type, RetailDialogType.Wait)
            .Set(RetailDialogProperty.ElementAttribute40, true)
            .Set(RetailDialogProperty.Message, message);
    }

    public static RetailDialogData Message(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return new RetailDialogData()
            .Set(RetailDialogProperty.Type, RetailDialogType.Message)
            .Set(RetailDialogProperty.Message, message);
    }

    public static RetailDialogData ConfirmationTextInput(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return new RetailDialogData()
            .Set(RetailDialogProperty.Type, RetailDialogType.ConfirmationTextInput)
            .Set(RetailDialogProperty.ElementAttribute40, true)
            .Set(RetailDialogProperty.Message, message);
    }

    public static RetailDialogData ConfirmationMenu(
        IReadOnlyList<string> items,
        int selectedIndex = 0)
    {
        ArgumentNullException.ThrowIfNull(items);
        return new RetailDialogData()
            .Set(RetailDialogProperty.Type, RetailDialogType.ConfirmationMenu)
            .Set(RetailDialogProperty.ElementAttribute40, true)
            .Set(RetailDialogProperty.MenuItems, items.ToArray())
            .Set(RetailDialogProperty.MenuSelection, selectedIndex);
    }
}
