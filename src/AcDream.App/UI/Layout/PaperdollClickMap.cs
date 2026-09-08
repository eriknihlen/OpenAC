using AcDream.Core.Items;
using AcDream.Core.Textures;
using AcDream.Content;
using DatReaderWriter;
using DatReaderWriter.DBObjs;

namespace AcDream.App.UI.Layout;

public sealed class PaperdollClickMap
{
    public const uint ClickMapEnum = 0x1000000Cu;
    public const uint InterfaceEnumCategory = 7u;

    private readonly byte[] _rgba;

    public int Width { get; }
    public int Height { get; }

    public PaperdollClickMap(byte[] rgba, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(rgba);
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        if (rgba.Length < checked(width * height * 4))
            throw new ArgumentException("The RGBA buffer is smaller than the declared map.", nameof(rgba));

        _rgba = rgba;
        Width = width;
        Height = height;
    }

    public static PaperdollClickMap? Load(IDatReaderWriter dats)
    {
        ArgumentNullException.ThrowIfNull(dats);

        uint masterDid = (uint)dats.Portal.Db.Header.MasterMapId;
        if (masterDid == 0
            || !dats.Portal.TryGet<EnumIDMap>(masterDid, out var master)
            || master is null
            || !master.ClientEnumToID.TryGetValue(InterfaceEnumCategory, out uint subMapDid)
            || !dats.Portal.TryGet<EnumIDMap>(subMapDid, out var subMap)
            || subMap is null
            || !subMap.ClientEnumToID.TryGetValue(ClickMapEnum, out uint surfaceDid))
            return null;

        if (!dats.Portal.TryGet<RenderSurface>(surfaceDid, out var surface)
            && !dats.HighRes.TryGet<RenderSurface>(surfaceDid, out surface))
            return null;

        Palette? palette = surface.DefaultPaletteId != 0
            ? dats.Get<Palette>(surface.DefaultPaletteId)
            : null;
        DecodedTexture decoded = SurfaceDecoder.DecodeRenderSurface(surface, palette);
        return decoded.Width > 1 && decoded.Height > 1
            ? new PaperdollClickMap(decoded.Rgba8, decoded.Width, decoded.Height)
            : null;
    }

    public EquipMask GetBodyLocation(int x, int y)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height)
            return EquipMask.None;

        int pixel = (y * Width + x) * 4;
        byte r = _rgba[pixel];
        byte g = _rgba[pixel + 1];
        byte b = _rgba[pixel + 2];

        return (r, g, b) switch
        {
            (0x00, 0x00, 0xFF) => EquipMask.HeadWear,
            (0x00, 0xFF, 0x00) => EquipMask.ChestWear | EquipMask.ChestArmor,
            (0xFF, 0x00, 0x00) => EquipMask.AbdomenWear | EquipMask.AbdomenArmor,
            (0x00, 0xFF, 0xFF) => EquipMask.UpperArmWear | EquipMask.UpperArmArmor,
            (0xFF, 0x00, 0xFF) => EquipMask.LowerArmWear | EquipMask.LowerArmArmor,
            (0xFF, 0xFF, 0x00) => EquipMask.UpperLegWear | EquipMask.UpperLegArmor,
            (0x00, 0x00, 0x80) => EquipMask.LowerLegWear | EquipMask.LowerLegArmor,
            (0x00, 0x80, 0x00) => EquipMask.HandWear,
            (0x80, 0x00, 0x00) => EquipMask.FootWear,
            _ => EquipMask.None,
        };
    }
}
