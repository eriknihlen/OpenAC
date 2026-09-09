using AcDream.Plugin.Abstractions.Rendering;
using AcDream.UI.Abstractions.Panels.Settings;

namespace AcDream.App.Rendering.Packs;

internal static class RenderPackSettingResolution
{
    internal static RenderPackValidationResult ValidateUserOverrides(
        RenderPackDescriptor descriptor,
        RenderPackSettingOverrides overrides)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (overrides is null)
            return Invalid($"Render pack '{descriptor.Id}' has a null user-setting override map.");

        Dictionary<string, RenderSettingDeclaration> settings = descriptor.Settings
            .ToDictionary(setting => setting.Id, StringComparer.OrdinalIgnoreCase);
        foreach ((string id, string value) in overrides)
        {
            if (!settings.TryGetValue(id, out RenderSettingDeclaration? setting))
            {
                return Invalid(
                    $"Render pack '{descriptor.Id}' has a user override for unknown "
                    + $"setting '{id}'.");
            }
            if (!RenderPackSettingValueCodec.TryEncode(setting, value, out _))
            {
                return Invalid(
                    $"Render pack '{descriptor.Id}' user override '{id}' has invalid "
                    + $"{setting.Kind} value '{value}'.");
            }
        }
        return RenderPackValidationResult.Valid();
    }

    internal static string Resolve(
        RenderSettingDeclaration setting,
        RenderQualityPreset preset,
        IReadOnlyDictionary<string, string>? userOverrides)
    {
        ArgumentNullException.ThrowIfNull(setting);
        ArgumentNullException.ThrowIfNull(preset);
        if (TryGet(userOverrides, setting.Id, out string? user))
            return user;
        RenderQualitySettingOverride? presetValue = preset.SettingOverrides
            .FirstOrDefault(value => string.Equals(
                value.SettingId,
                setting.Id,
                StringComparison.OrdinalIgnoreCase));
        return presetValue?.Value ?? setting.DefaultValue;
    }

    private static bool TryGet(
        IReadOnlyDictionary<string, string>? values,
        string id,
        out string value)
    {
        if (values is not null && values.TryGetValue(id, out value!))
            return true;
        if (values is not null)
        {
            foreach ((string key, string candidate) in values)
            {
                if (string.Equals(key, id, StringComparison.OrdinalIgnoreCase))
                {
                    value = candidate;
                    return true;
                }
            }
        }
        value = string.Empty;
        return false;
    }

    private static RenderPackValidationResult Invalid(string reason) =>
        RenderPackValidationResult.Invalid(reason);
}
