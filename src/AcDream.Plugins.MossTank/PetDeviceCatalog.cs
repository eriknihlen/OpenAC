namespace AcDream.Plugins.MossTank;

internal static class PetDeviceCatalog
{
    public const uint EncapsulatedSpiritWeenieClassId = 49485u;

    public static MonsterDamageType DamageType(uint deviceWeenieClassId) =>
        deviceWeenieClassId switch
        {
            48878u or 48880u or 48882u or 48884u or 48886u or 48888u or 48890u => MonsterDamageType.Bludgeon,
            48972u or 49213u or 49214u or 49215u or 49216u or 49217u or 49218u or 49219u
                or 49234u or 49235u or 49236u or 49237u or 49238u or 49239u or 49261u or 49262u
                or 49263u or 49264u or 49265u or 49266u or 49267u or 49282u or 49283u or 49284u
                or 49285u or 49286u or 49287u or 49288u or 49310u or 49311u or 49312u or 49313u
                or 49314u or 49315u or 49316u or 49338u or 49339u or 49340u or 49341u or 49342u
                or 49343u or 49344u or 49366u or 49367u or 49368u or 49369u or 49370u or 49371u
                or 49372u or 49421u or 49422u or 49423u or 49424u or 49425u or 49426u or 49427u
                or 49524u or 49525u or 49526u or 49527u or 49528u or 49529u or 49530u => MonsterDamageType.Acid,
            48942u or 48944u or 48945u or 48946u or 48947u or 48948u or 48956u or 48957u
                or 48959u or 48961u or 48963u or 48965u or 48967u or 48969u or 49247u or 49248u
                or 49249u or 49250u or 49251u or 49252u or 49253u or 49296u or 49297u or 49298u
                or 49299u or 49300u or 49301u or 49302u or 49324u or 49325u or 49326u or 49327u
                or 49328u or 49329u or 49330u or 49352u or 49353u or 49354u or 49355u or 49356u
                or 49357u or 49358u or 49380u or 49381u or 49382u or 49383u or 49384u or 49385u
                or 49386u or 49435u or 49436u or 49437u or 49438u or 49439u or 49440u or 49441u
                or 49531u or 49532u or 49533u or 49534u or 49535u or 49536u or 49537u => MonsterDamageType.Fire,
            49212u or 49227u or 49228u or 49229u or 49230u or 49231u or 49232u or 49233u
                or 49254u or 49255u or 49256u or 49257u or 49258u or 49259u or 49260u or 49275u
                or 49276u or 49277u or 49278u or 49279u or 49280u or 49281u or 49303u or 49304u
                or 49305u or 49306u or 49307u or 49308u or 49309u or 49331u or 49332u or 49333u
                or 49334u or 49335u or 49336u or 49337u or 49359u or 49360u or 49361u or 49362u
                or 49363u or 49364u or 49365u or 49387u or 49388u or 49389u or 49390u or 49391u
                or 49392u or 49442u or 49443u or 49444u or 49445u or 49446u or 49447u or 49448u
                or 49538u or 49539u or 49540u or 49541u or 49542u or 49543u or 49544u => MonsterDamageType.Cold,
            49220u or 49221u or 49222u or 49223u or 49224u or 49225u or 49226u or 49240u
                or 49241u or 49242u or 49243u or 49244u or 49245u or 49246u or 49268u or 49269u
                or 49270u or 49271u or 49272u or 49273u or 49274u or 49289u or 49290u or 49291u
                or 49292u or 49293u or 49294u or 49295u or 49317u or 49318u or 49319u or 49320u
                or 49321u or 49322u or 49323u or 49345u or 49346u or 49347u or 49348u or 49349u
                or 49350u or 49351u or 49373u or 49374u or 49375u or 49376u or 49377u or 49378u
                or 49379u or 49428u or 49429u or 49430u or 49431u or 49432u or 49433u or 49434u
                or 49545u or 49546u or 49547u or 49548u or 49549u or 49550u or 49551u => MonsterDamageType.Electric,
            _ => MonsterDamageType.Auto,
        };
}
