using AcDream.UI.Abstractions.Input;

namespace AcDream.App.Input;

internal static class RetailEmoteMotionTable
{
    private const uint EmoteInputMap = 0x10000006u;
    private const uint FirstEmoteAction = 0x10000098u;

    private static readonly uint[] Motions =
    [
        0x43000118u, // AFKState
        0x13000088u, // Akimbo
        0x420000F9u, // ATOYOT
        0x430000F2u, // AkimboState
        0x43000146u, // AtEaseState
        0x1300007Au, // Beckon
        0x1300007Bu, // BeSeeingYou
        0x1300007Cu, // BlowKiss
        0x1300007Du, // BowDeep
        0x430000ECu, // BowDeepState
        0x1300004Cu,
        0x1300007Eu,
        0x430000EDu,
        0x13000091u,
        0x430000EEu,
        0x1300007Fu, // Cry
        0x43000117u,
        0x1300014Eu, // DrudgeDance
        0x43000141u, // DrudgeDanceState
        0x1300014Fu, // HaveASeat
        0x43000145u, // HaveASeatState
        0x13000089u, // HeartyLaugh
        0x13000132u, // Helper
        0x13000092u, // Kneel
        0x430000F7u, // KneelState
        0x1300014Cu, // Knock
        0x13000080u, // Laugh
        0x430000F6u, // LeanState
        0x43000119u, // MeditateState
        0x13000082u, // MimeDrink
        0x13000081u, // MimeEat
        0x130000CBu, // Mock
        0x13000083u, // Nod
        0x13000147u, // NudgeLeft
        0x13000148u, // NudgeRight
        0x13000093u, // Plead
        0x430000F8u, // PleadState
        0x13000084u, // Point
        0x430000F0u, // PointState
        0x1300014Bu, // PointDown
        0x43000140u, // PointDownState
        0x13000149u, // PointLeft
        0x4300013Du, // PointLeftState
        0x1300014Au, // PointRight
        0x4300013Eu, // PointRightState
        0x43000142u, // PossumState
        0x130000CAu, // Pray
        0x430000EBu, // PrayState
        0x43000143u, // ReadState
        0x1300008Au, // Salute
        0x430000F3u, // SaluteState
        0x1300014Du, // ScanHorizon
        0x1300008Bu, // ScratchHead
        0x430000F4u, // ScratchHeadState
        0x13000079u, // ShakeFist
        0x430000EAu, // ShakeFistState
        0x13000085u, // ShakeHead
        0x13000094u, // Shiver
        0x430000EFu, // ShiverState
        0x13000095u, // Shoo
        0x13000086u, // Shrug
        0x4300013Au, // SitState
        0x4300013Cu, // SitBackState
        0x4300013Bu, // SitCrossleggedState
        0x13000096u, // Slouch
        0x430000FAu, // SlouchState
        0x1300008Cu, // SmackHead
        0x43000115u, // SnowAngelState
        0x13000097u, // Spit
        0x13000098u, // Surrender
        0x430000FBu, // SurrenderState
        0x4300013Fu, // TalktotheHandState
        0x1300008Du, // TapFoot
        0x430000F5u, // TapFootState
        0x130000CCu, // Teapot
        0x43000144u, // ThinkerState
        0x13000116u, // WarmHands
        0x13000087u, // Wave
        0x430000F1u, // WaveState
        0x1300008Fu, // WaveLow
        0x1300008Eu, // WaveHigh
        0x1300009Au, // Winded
        0x430000FDu, // WindedState
        0x13000099u, // Woah
        0x430000FCu, // WoahState
        0x13000090u, // YawnStretch
        0x1200009Bu, // YMCA
    ];

    public static int Count => Motions.Length;

    public static bool TryGetMotion(InputAction action, out uint motion)
    {
        motion = 0u;
        if (!RetailActionIdentityTable.TryGetRetailIdentity(
                action,
                out var identity)
            || identity.InputMapId != EmoteInputMap)
        {
            return false;
        }

        uint index = identity.ActionId - FirstEmoteAction;
        if (index >= Motions.Length)
            return false;

        motion = Motions[index];
        return true;
    }
}
