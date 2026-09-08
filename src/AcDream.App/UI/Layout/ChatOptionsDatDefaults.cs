using AcDream.Content;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Types;

namespace AcDream.App.UI.Layout;

public static class ChatOptionsDatDefaults
{
    private const uint EnumCategory = 2u;

    /// <summary>DAT enum-value key for the SECOND (sub-map) lookup.</summary>
    private const uint EnumValue = 0x16u;

    /// <summary><c>Option_DefaultOpacity_Property</c> — the unfocused-window opacity
    /// slider's <c>m_propName</c>.</summary>
    public const uint DefaultOpacityPropertyId = 0x10000080u;

    /// <summary><c>Option_ActiveOpacity_Property</c> — the focused-window opacity
    /// slider's <c>m_propName</c>.</summary>
    public const uint ActiveOpacityPropertyId = 0x10000081u;

    public const float FallbackDefaultOpacity = 0.5f;

    public const float FallbackActiveOpacity = 1.0f;

    public static bool TryRead(
        IDatReaderWriter dats, out float defaultOpacity, out float activeOpacity)
    {
        defaultOpacity = FallbackDefaultOpacity;
        activeOpacity = FallbackActiveOpacity;

        uint did = RetailDataIdResolver.Resolve(dats, EnumValue, EnumCategory);
        if (did == 0u || !dats.Portal.TryGet<DBProperties>(did, out DBProperties? props) || props is null)
            return false;

        bool ok = true;
        if (props.Properties.TryGetValue(DefaultOpacityPropertyId, out BaseProperty? d)
            && d is FloatBaseProperty defaultProp)
            defaultOpacity = defaultProp.Value;
        else
            ok = false;

        if (props.Properties.TryGetValue(ActiveOpacityPropertyId, out BaseProperty? a)
            && a is FloatBaseProperty activeProp)
            activeOpacity = activeProp.Value;
        else
            ok = false;

        return ok;
    }
}
