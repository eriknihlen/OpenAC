namespace AcDream.App.UI;

internal static class RetailCursorCatalog
{
    public const uint CursorEnumTable = 6;

    public static bool TryGetGlobalCursor(RetailGlobalCursorKind kind, out RetailCursorSpec spec)
    {
        spec = kind switch
        {
            RetailGlobalCursorKind.Default => new RetailCursorSpec(0x01u, 0, 0),
            RetailGlobalCursorKind.DefaultFound => new RetailCursorSpec(0x02u, 0, 0),
            RetailGlobalCursorKind.MeleeOrMissile => new RetailCursorSpec(0x03u, 0, 0),
            RetailGlobalCursorKind.MeleeOrMissileFound => new RetailCursorSpec(0x04u, 0, 0),
            RetailGlobalCursorKind.Magic => new RetailCursorSpec(0x05u, 0, 0),
            RetailGlobalCursorKind.MagicFound => new RetailCursorSpec(0x06u, 0, 0),
            RetailGlobalCursorKind.Examine => new RetailCursorSpec(0x0Au, 0, 0),
            RetailGlobalCursorKind.ExamineFound => new RetailCursorSpec(0x0Bu, 0, 0),
            RetailGlobalCursorKind.Use => new RetailCursorSpec(0x0Cu, 14, 14),
            RetailGlobalCursorKind.UseFound => new RetailCursorSpec(0x0Du, 14, 14),
            RetailGlobalCursorKind.Busy => new RetailCursorSpec(0x0Eu, 0, 0),
            RetailGlobalCursorKind.BusyFound => new RetailCursorSpec(0x0Fu, 0, 0),
            RetailGlobalCursorKind.TargetPending => new RetailCursorSpec(0x27u, 14, 14),
            RetailGlobalCursorKind.TargetValid => new RetailCursorSpec(0x28u, 14, 14),
            RetailGlobalCursorKind.TargetInvalid => new RetailCursorSpec(0x29u, 14, 14),
            _ => default,
        };

        return spec.IsValid;
    }

    public static bool TryGetWindowControlCursor(
        CursorFeedbackKind kind,
        out UiCursorMedia cursor)
    {
        cursor = kind switch
        {
            CursorFeedbackKind.WindowMove => new UiCursorMedia(0x06006119u, 16, 16),
            CursorFeedbackKind.ResizeHorizontal => new UiCursorMedia(0x06006128u, 16, 16),
            CursorFeedbackKind.ResizeVertical => new UiCursorMedia(0x06005E66u, 16, 16),
            CursorFeedbackKind.ResizeDiagonalNwse => new UiCursorMedia(0x06006126u, 16, 16),
            CursorFeedbackKind.ResizeDiagonalNesw => new UiCursorMedia(0x06006127u, 16, 16),
            _ => default,
        };

        return cursor.IsValid;
    }
}

internal readonly record struct RetailCursorSpec(uint EnumId, int HotspotX, int HotspotY)
{
    public bool IsValid => EnumId != 0;
}
