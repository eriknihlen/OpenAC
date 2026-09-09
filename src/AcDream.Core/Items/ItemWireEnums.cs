using System;

namespace AcDream.Core.Items;

[Flags]
public enum AmmoType : uint
{
    None            = 0x000,
    Arrow           = 0x001,
    Bolt            = 0x002,
    Atlatl          = 0x004,
    ArrowCrystal    = 0x008,
    BoltCrystal     = 0x010,
    AtlatlCrystal   = 0x020,
    ArrowChorizite  = 0x040,
    BoltChorizite   = 0x080,
    AtlatlChorizite = 0x100,
}

public enum CombatUse : uint
{
    None      = 0,
    Melee     = 1,
    Missile   = 2,
    Ammo      = 3,
    Shield    = 4,
    TwoHanded = 5,
}

[Flags]
public enum ItemUseable : uint
{
    Undef                    = 0x0,
    No                       = 0x1,
    Self                     = 0x2,
    Wielded                  = 0x4,
    Contained                = 0x8,
    Viewed                   = 0x10,
    Remote                   = 0x20,
    NeverWalk                = 0x40,
    ObjSelf                  = 0x80,

    ContainedViewed                    = 0x18,
    ViewedRemote                       = 0x30,
    ContainedViewedRemote              = 0x38,
    RemoteNeverWalk                    = 0x60,
    ViewedRemoteNeverWalk              = 0x70,
    ContainedViewedRemoteNeverWalk     = 0x78,

    SourceWieldedTargetWielded                 = 0x00040004,
    SourceWieldedTargetContained               = 0x00080004,
    SourceWieldedTargetViewed                  = 0x00100004,
    SourceWieldedTargetRemote                  = 0x00200004,
    SourceWieldedTargetRemoteNeverWalk         = 0x00600004,

    SourceContainedTargetWielded               = 0x00040008,
    SourceContainedTargetContained             = 0x00080008,
    SourceContainedTargetSelfOrContained       = 0x000A0008,
    SourceContainedTargetViewed                = 0x00100008,
    SourceContainedTargetRemote                = 0x00200008,
    SourceContainedTargetRemoteOrSelf          = 0x00220008,
    SourceContainedTargetRemoteNeverWalk       = 0x00600008,
    SourceContainedTargetObjSelfOrContained    = 0x00880008,

    SourceViewedTargetWielded                  = 0x00040010,
    SourceViewedTargetContained                = 0x00080010,
    SourceViewedTargetViewed                   = 0x00100010,
    SourceViewedTargetRemote                   = 0x00200010,

    SourceRemoteTargetWielded                  = 0x00040020,
    SourceRemoteTargetContained                = 0x00080020,
    SourceRemoteTargetViewed                   = 0x00100020,
    SourceRemoteTargetRemote                   = 0x00200020,
    SourceRemoteTargetRemoteNeverWalk          = 0x00600020,

    SourceMask = 0x0000FFFF,
    TargetMask = 0xFFFF0000,
}
