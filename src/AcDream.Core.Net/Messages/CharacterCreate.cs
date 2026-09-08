using AcDream.Core.Net.Packets;

namespace AcDream.Core.Net.Messages;

public static class CharacterCreate
{
    public const uint Opcode = 0xF656u;

    public const int SkillAdvancementClassCount = 55;

    public readonly record struct Appearance(
        uint EyesStrip,
        uint NoseStrip,
        uint MouthStrip,
        uint HairColor,
        uint EyeColor,
        uint HairStyle,
        uint HeadgearStyle,
        uint HeadgearColor,
        uint ShirtStyle,
        uint ShirtColor,
        uint TrousersStyle,
        uint TrousersColor,
        uint FootwearStyle,
        uint FootwearColor,
        double SkinShade,
        double HairShade,
        double HeadgearShade,
        double ShirtShade,
        double TrousersShade,
        double FootwearShade);

    public readonly record struct Attributes(
        uint Strength,
        uint Endurance,
        uint Coordination,
        uint Quickness,
        uint Focus,
        uint Self);

    public readonly record struct Request(
        uint Heritage,
        uint Gender,
        Appearance Appearance,
        uint Template,
        Attributes Attributes,
        uint Slot,
        uint ClassId,
        string Name,
        uint StartArea,
        bool IsAdmin,
        bool IsEnvoy);

    public static byte[] BuildRequestBody(
        string accountName,
        Request request,
        ReadOnlySpan<uint> skillAdvancementClasses)
    {
        ArgumentNullException.ThrowIfNull(accountName);
        ArgumentNullException.ThrowIfNull(request.Name);
        if (skillAdvancementClasses.Length != SkillAdvancementClassCount)
        {
            throw new ArgumentException(
                "retail's CG_Pack numSkills must be exactly "
                + $"{SkillAdvancementClassCount} — ACE terminates the session "
                + "(PlayerFactory.CreateResult.ClientServerSkillsMismatch) on "
                + $"any other count. Got {skillAdvancementClasses.Length}.",
                nameof(skillAdvancementClasses));
        }

        Appearance appearance = request.Appearance;
        Attributes attributes = request.Attributes;

        var w = new PacketWriter(
            256 + (skillAdvancementClasses.Length * 4) + (request.Name.Length * 2));
        w.WriteUInt32(Opcode);
        w.WriteString16L(accountName);

        w.WriteUInt32(1u);
        w.WriteUInt32(request.Heritage);
        w.WriteUInt32(request.Gender);
        w.WriteUInt32(appearance.EyesStrip);
        w.WriteUInt32(appearance.NoseStrip);
        w.WriteUInt32(appearance.MouthStrip);
        w.WriteUInt32(appearance.HairColor);
        w.WriteUInt32(appearance.EyeColor);
        w.WriteUInt32(appearance.HairStyle);
        w.WriteUInt32(appearance.HeadgearStyle);
        w.WriteUInt32(appearance.HeadgearColor);
        w.WriteUInt32(appearance.ShirtStyle);
        w.WriteUInt32(appearance.ShirtColor);
        w.WriteUInt32(appearance.TrousersStyle);
        w.WriteUInt32(appearance.TrousersColor);
        w.WriteUInt32(appearance.FootwearStyle);
        w.WriteUInt32(appearance.FootwearColor);
        w.WriteDouble(appearance.SkinShade);
        w.WriteDouble(appearance.HairShade);
        w.WriteDouble(appearance.HeadgearShade);
        w.WriteDouble(appearance.ShirtShade);
        w.WriteDouble(appearance.TrousersShade);
        w.WriteDouble(appearance.FootwearShade);
        w.WriteUInt32(request.Template);
        w.WriteUInt32(attributes.Strength);
        w.WriteUInt32(attributes.Endurance);
        w.WriteUInt32(attributes.Coordination);
        w.WriteUInt32(attributes.Quickness);
        w.WriteUInt32(attributes.Focus);
        w.WriteUInt32(attributes.Self);
        w.WriteUInt32(request.Slot);
        w.WriteUInt32(request.ClassId);
        w.WriteUInt32((uint)skillAdvancementClasses.Length);
        foreach (uint skill in skillAdvancementClasses)
            w.WriteUInt32(skill);
        w.WriteString16L(request.Name);
        w.WriteUInt32(request.StartArea);
        w.WriteUInt32(request.IsAdmin ? 1u : 0u);
        w.WriteUInt32(request.IsEnvoy ? 1u : 0u);
        w.WriteUInt32(ComputeChecksum(request));

        return w.ToArray();
    }

    public static uint ComputeChecksum(Request request)
    {
        Appearance a = request.Appearance;
        Attributes b = request.Attributes;
        return unchecked(
            request.Heritage
            + request.Gender
            + a.EyesStrip
            + a.NoseStrip
            + a.MouthStrip
            + a.HairColor
            + a.EyeColor
            + a.HairStyle
            + a.HeadgearStyle
            + a.ShirtStyle
            + a.TrousersStyle
            + a.FootwearStyle
            + request.Template
            + b.Strength
            + b.Endurance
            + b.Coordination
            + b.Quickness
            + b.Focus
            + b.Self);
    }
}
