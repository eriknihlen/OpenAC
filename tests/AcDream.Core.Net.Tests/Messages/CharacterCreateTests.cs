using System.Buffers.Binary;
using System.Text;
using AcDream.Core.Net.Messages;

namespace AcDream.Core.Net.Tests.Messages;

public sealed class CharacterCreateTests
{
    private static uint[] MakeSkills(uint seed = 0)
    {
        var skills = new uint[CharacterCreate.SkillAdvancementClassCount];
        for (int i = 0; i < skills.Length; i++)
            skills[i] = seed + (uint)i;
        return skills;
    }

    private static CharacterCreate.Request MakeRequest() => new(
        Heritage: 1u,
        Gender: 0u,
        Appearance: new CharacterCreate.Appearance(
            EyesStrip: 2u,
            NoseStrip: 3u,
            MouthStrip: 4u,
            HairColor: 5u,
            EyeColor: 6u,
            HairStyle: 7u,
            HeadgearStyle: 8u,
            HeadgearColor: 9u,
            ShirtStyle: 10u,
            ShirtColor: 11u,
            TrousersStyle: 12u,
            TrousersColor: 13u,
            FootwearStyle: 14u,
            FootwearColor: 15u,
            SkinShade: 0.1,
            HairShade: 0.2,
            HeadgearShade: 0.3,
            ShirtShade: 0.4,
            TrousersShade: 0.5,
            FootwearShade: 0.6),
        Template: 16u,
        Attributes: new CharacterCreate.Attributes(
            Strength: 17u,
            Endurance: 18u,
            Coordination: 19u,
            Quickness: 20u,
            Focus: 21u,
            Self: 22u),
        Slot: 0u,
        ClassId: 1u,
        Name: "Testcdream",
        StartArea: 23u,
        IsAdmin: false,
        IsEnvoy: false);

    [Fact]
    public void BuildRequestBody_Layout_MatchesRetailCGPackFieldOrder()
    {
        CharacterCreate.Request request = MakeRequest();
        uint[] skills = MakeSkills();
        byte[] body = CharacterCreate.BuildRequestBody("testaccount", request, skills);

        int pos = 0;
        uint ReadU32()
        {
            uint v = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(pos));
            pos += 4;
            return v;
        }
        double ReadF64()
        {
            double v = BinaryPrimitives.ReadDoubleLittleEndian(body.AsSpan(pos));
            pos += 8;
            return v;
        }
        string ReadString16L()
        {
            ushort len = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(pos));
            pos += 2;
            string s = Encoding.ASCII.GetString(body, pos, len);
            pos += len;
            int recordSize = 2 + len;
            int padding = (4 - (recordSize & 3)) & 3;
            pos += padding;
            return s;
        }

        Assert.Equal(CharacterCreate.Opcode, ReadU32());
        Assert.Equal("testaccount", ReadString16L());
        Assert.Equal(1u, ReadU32());
        Assert.Equal(request.Heritage, ReadU32());
        Assert.Equal(request.Gender, ReadU32());
        Assert.Equal(request.Appearance.EyesStrip, ReadU32());
        Assert.Equal(request.Appearance.NoseStrip, ReadU32());
        Assert.Equal(request.Appearance.MouthStrip, ReadU32());
        Assert.Equal(request.Appearance.HairColor, ReadU32());
        Assert.Equal(request.Appearance.EyeColor, ReadU32());
        Assert.Equal(request.Appearance.HairStyle, ReadU32());
        Assert.Equal(request.Appearance.HeadgearStyle, ReadU32());
        Assert.Equal(request.Appearance.HeadgearColor, ReadU32());
        Assert.Equal(request.Appearance.ShirtStyle, ReadU32());
        Assert.Equal(request.Appearance.ShirtColor, ReadU32());
        Assert.Equal(request.Appearance.TrousersStyle, ReadU32());
        Assert.Equal(request.Appearance.TrousersColor, ReadU32());
        Assert.Equal(request.Appearance.FootwearStyle, ReadU32());
        Assert.Equal(request.Appearance.FootwearColor, ReadU32());
        Assert.Equal(request.Appearance.SkinShade, ReadF64());
        Assert.Equal(request.Appearance.HairShade, ReadF64());
        Assert.Equal(request.Appearance.HeadgearShade, ReadF64());
        Assert.Equal(request.Appearance.ShirtShade, ReadF64());
        Assert.Equal(request.Appearance.TrousersShade, ReadF64());
        Assert.Equal(request.Appearance.FootwearShade, ReadF64());
        Assert.Equal(request.Template, ReadU32());
        Assert.Equal(request.Attributes.Strength, ReadU32());
        Assert.Equal(request.Attributes.Endurance, ReadU32());
        Assert.Equal(request.Attributes.Coordination, ReadU32());
        Assert.Equal(request.Attributes.Quickness, ReadU32());
        Assert.Equal(request.Attributes.Focus, ReadU32());
        Assert.Equal(request.Attributes.Self, ReadU32());
        Assert.Equal(request.Slot, ReadU32());
        Assert.Equal(request.ClassId, ReadU32());
        uint numSkills = ReadU32();
        Assert.Equal((uint)CharacterCreate.SkillAdvancementClassCount, numSkills);
        for (int i = 0; i < skills.Length; i++)
            Assert.Equal(skills[i], ReadU32());
        Assert.Equal(request.Name, ReadString16L());
        Assert.Equal(request.StartArea, ReadU32());
        Assert.Equal(0u, ReadU32()); // isAdmin
        Assert.Equal(0u, ReadU32()); // isEnvoy
        uint checksum = ReadU32();
        Assert.Equal(CharacterCreate.ComputeChecksum(request), checksum);
        Assert.Equal(pos, body.Length);
    }

    [Fact]
    public void BuildRequestBody_ExactByteSequence_ShortAccountAndName()
    {
        CharacterCreate.Request request = new(
            Heritage: 1u,
            Gender: 0u,
            Appearance: default,
            Template: 0u,
            Attributes: default,
            Slot: 0u,
            ClassId: 1u,
            Name: "ab",
            StartArea: 0u,
            IsAdmin: false,
            IsEnvoy: false);
        uint[] skills = new uint[CharacterCreate.SkillAdvancementClassCount];

        byte[] body = CharacterCreate.BuildRequestBody("cd", request, skills);

        int pos = 0;
        Assert.Equal(CharacterCreate.Opcode, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(pos))); pos += 4;

        // String16L("cd") = u16(2) + 2 bytes, already 4-byte aligned.
        Assert.Equal(2, BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(pos))); pos += 2;
        Assert.Equal("cd", Encoding.ASCII.GetString(body, pos, 2)); pos += 2;

        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(pos))); pos += 4;
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(pos))); pos += 4; // heritage
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(pos))); pos += 4; // gender

        pos += 14 * 4;

        // 6 f64 shades, all zero (default).
        pos += 6 * 8;

        pos += 4; // template
        pos += 6 * 4; // attributes
        pos += 4; // slot
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(pos))); pos += 4;

        Assert.Equal(
            (uint)CharacterCreate.SkillAdvancementClassCount,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(pos)));
        pos += 4;
        pos += CharacterCreate.SkillAdvancementClassCount * 4;

        Assert.Equal(2, BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(pos))); pos += 2;
        Assert.Equal("ab", Encoding.ASCII.GetString(body, pos, 2)); pos += 2;

        pos += 4; // startArea
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(pos))); pos += 4; // isAdmin
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(pos))); pos += 4; // isEnvoy

        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(pos))); pos += 4;

        Assert.Equal(pos, body.Length);
    }

    [Fact]
    public void ComputeChecksum_ExactRetailAccumulationSet()
    {
        CharacterCreate.Request request = MakeRequest();
        uint expected = unchecked(
            request.Heritage
            + request.Gender
            + request.Appearance.EyesStrip
            + request.Appearance.NoseStrip
            + request.Appearance.MouthStrip
            + request.Appearance.HairColor
            + request.Appearance.EyeColor
            + request.Appearance.HairStyle
            + request.Appearance.HeadgearStyle
            + request.Appearance.ShirtStyle
            + request.Appearance.TrousersStyle
            + request.Appearance.FootwearStyle
            + request.Template
            + request.Attributes.Strength
            + request.Attributes.Endurance
            + request.Attributes.Coordination
            + request.Attributes.Quickness
            + request.Attributes.Focus
            + request.Attributes.Self);

        Assert.Equal(expected, CharacterCreate.ComputeChecksum(request));
        Assert.Equal(205u, expected);
    }

    [Fact]
    public void ComputeChecksum_ExcludesColorFieldsShadesSlotAndClassId()
    {
        CharacterCreate.Request baseline = MakeRequest();
        uint baselineChecksum = CharacterCreate.ComputeChecksum(baseline);

        CharacterCreate.Request mutated = baseline with
        {
            Appearance = baseline.Appearance with
            {
                HeadgearColor = baseline.Appearance.HeadgearColor + 1000u,
                ShirtColor = baseline.Appearance.ShirtColor + 1000u,
                TrousersColor = baseline.Appearance.TrousersColor + 1000u,
                FootwearColor = baseline.Appearance.FootwearColor + 1000u,
                SkinShade = baseline.Appearance.SkinShade + 5.0,
                HairShade = baseline.Appearance.HairShade + 5.0,
            },
            Slot = baseline.Slot + 7u,
            ClassId = baseline.ClassId + 7u,
        };

        Assert.Equal(baselineChecksum, CharacterCreate.ComputeChecksum(mutated));
    }

    [Fact]
    public void BuildRequestBody_SkillCountOtherThan55_Throws()
    {
        CharacterCreate.Request request = MakeRequest();

        Assert.Throws<ArgumentException>(() =>
            CharacterCreate.BuildRequestBody("testaccount", request, MakeSkills().AsSpan(0, 54)));
        Assert.Throws<ArgumentException>(() =>
            CharacterCreate.BuildRequestBody("testaccount", request, new uint[56]));
        Assert.Throws<ArgumentException>(() =>
            CharacterCreate.BuildRequestBody("testaccount", request, ReadOnlySpan<uint>.Empty));
    }

    [Fact]
    public void BuildRequestBody_NullAccountName_Throws()
    {
        CharacterCreate.Request request = MakeRequest();
        Assert.Throws<ArgumentNullException>(() =>
            CharacterCreate.BuildRequestBody(null!, request, MakeSkills()));
    }

    [Fact]
    public void BuildRequestBody_NullCharacterName_Throws()
    {
        CharacterCreate.Request request = MakeRequest() with { Name = null! };
        Assert.Throws<ArgumentNullException>(() =>
            CharacterCreate.BuildRequestBody("testaccount", request, MakeSkills()));
    }

    [Fact]
    public void BuildRequestBody_AdminAndEnvoyFlags_EncodeAsOneOrZero()
    {
        CharacterCreate.Request request = MakeRequest() with { IsAdmin = true, IsEnvoy = true };
        byte[] body = CharacterCreate.BuildRequestBody("testaccount", request, MakeSkills());

        uint isEnvoy = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(body.Length - 8));
        uint isAdmin = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(body.Length - 12));
        Assert.Equal(1u, isAdmin);
        Assert.Equal(1u, isEnvoy);
    }
}
