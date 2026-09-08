using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using DatReaderWriter;
using AcDream.Core.Content;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Enums;
using DatReaderWriter.Types;

namespace AcDream.Core.World;

public sealed class SkyObjectData
{
    public float BeginTime;
    public float EndTime;
    public float BeginAngle;
    public float EndAngle;
    public float TexVelocityX;
    public float TexVelocityY;
    public uint  GfxObjId;
    public uint  PesObjectId;
    public uint  Properties;

    public uint  DefaultScriptId;

    public Vector3 AuthoredSortCenter;

    public bool IsWeather => (Properties & 0x04u) != 0u;

    public bool IsPostScene => (Properties & 0x01u) != 0u;

    public bool IsVisible(float t)
    {
        if (BeginTime == EndTime) return true;
        if (BeginTime < EndTime)  return t >= BeginTime && t <= EndTime;
        // Wrap around midnight.
        return t >= BeginTime || t <= EndTime;
    }

    /// <summary>
    /// Arc progress 0..1 through the visibility window; gives the angle
    /// interpolation for <c>BeginAngle</c>→<c>EndAngle</c> (r12 §2).
    /// </summary>
    public float AngleProgress(float t)
    {
        if (BeginTime == EndTime) return 0f;
        float duration;
        float progress;
        if (BeginTime < EndTime)
        {
            duration = EndTime - BeginTime;
            progress = (t - BeginTime) / duration;
        }
        else
        {
            duration = (1f - BeginTime) + EndTime;
            progress = (t >= BeginTime)
                ? (t - BeginTime) / duration
                : (t + (1f - BeginTime)) / duration;
        }
        return Math.Clamp(progress, 0f, 1f);
    }

    public float CurrentAngle(float t)
    {
        if (BeginTime == EndTime) return BeginAngle;
        return BeginAngle + (EndAngle - BeginAngle) * AngleProgress(t);
    }
}

public sealed class SkyObjectReplaceData
{
    public uint  ObjectIndex;
    public uint  GfxObjId;
    public float Rotate;
    public float Transparent;
    public float Luminosity;
    public float MaxBright;

    public Vector3 AuthoredSortCenter;
}

public sealed class DatSkyKeyframeData
{
    public SkyKeyframe Keyframe;
    public IReadOnlyList<SkyObjectReplaceData> Replaces = Array.Empty<SkyObjectReplaceData>();
}

public sealed class DayGroupData
{
    public float ChanceOfOccur;
    public string Name = "";
    public IReadOnlyList<SkyObjectData> SkyObjects = Array.Empty<SkyObjectData>();
    public IReadOnlyList<DatSkyKeyframeData> SkyTimes = Array.Empty<DatSkyKeyframeData>();
}

public sealed class LoadedSkyDesc
{
    public double TickSize;
    public double LightTickSize;
    public IReadOnlyList<DayGroupData> DayGroups = Array.Empty<DayGroupData>();

    public int SelectDayGroupIndex(int year, int secondsPerDay, int dayOfYear)
    {
        // Env-var override has absolute priority.
        var env = System.Environment.GetEnvironmentVariable("ACDREAM_DAY_GROUP");
        int? forcedIndex = null;
        if (int.TryParse(env, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var forced)
            && forced >= 0 && forced < DayGroups.Count)
        {
            forcedIndex = forced;
        }

        return SkyDayGroupSelector.SelectIndex(
            DayGroups.Count,
            year,
            secondsPerDay,
            dayOfYear,
            forcedIndex);
    }

    public DayGroupData? ActiveDayGroup(double serverTicks)
    {
        int absYear = DerethDateTime.AbsoluteYear(serverTicks);
        int dayOfYear = DerethDateTime.DayOfYear(serverTicks);
        int secondsPerDay = DerethDateTime.DaysInAMonth * DerethDateTime.MonthsInAYear;  // 360
        int idx = SelectDayGroupIndex(absYear, secondsPerDay, dayOfYear);
        return idx < DayGroups.Count ? DayGroups[idx] : null;
    }

    public DayGroupData? DefaultDayGroup
    {
        get
        {
            int idx = SelectDayGroupIndex(
                year: 0,
                secondsPerDay: DerethDateTime.DaysInAMonth * DerethDateTime.MonthsInAYear, // 360
                dayOfYear: 0);
            return DayGroups.Count > 0 ? DayGroups[idx] : null;
        }
    }

    public SkyStateProvider BuildDefaultProvider()
    {
        var grp = DefaultDayGroup;
        if (grp is null || grp.SkyTimes.Count == 0) return SkyStateProvider.Default();
        return new SkyStateProvider(grp.SkyTimes.Select(s => s.Keyframe).ToList());
    }

    public SkyStateProvider BuildProviderForDay(double serverTicks)
    {
        var grp = ActiveDayGroup(serverTicks);
        if (grp is null || grp.SkyTimes.Count == 0) return SkyStateProvider.Default();
        return new SkyStateProvider(grp.SkyTimes.Select(s => s.Keyframe).ToList());
    }
}

public static class SkyDescLoader
{
    public const uint RegionDatId = 0x13000000u;

    public static LoadedSkyDesc? LoadFromDat(IDatObjectSource dats)
    {
        ArgumentNullException.ThrowIfNull(dats);
        var region = dats.Get<Region>(RegionDatId);
        if (region is null) return null;
        return LoadFromRegion(region, dats);
    }

    public static LoadedSkyDesc? LoadFromRegion(
        Region region,
        IDatObjectSource? dats = null)
    {
        ArgumentNullException.ThrowIfNull(region);
        if (!region.PartsMask.HasFlag(PartsMask.HasSkyInfo) || region.SkyInfo is null)
            return null;

        var sky = region.SkyInfo;

        if (System.Environment.GetEnvironmentVariable("ACDREAM_DUMP_SKY") == "1")
            DumpRegionSkyDesc(region);

        var dayGroups = new List<DayGroupData>(sky.DayGroups.Count);

        foreach (var dg in sky.DayGroups)
        {
            var objs = dg.SkyObjects
                .Select(value => ConvertSkyObject(value, dats))
                .ToList();
            var times = dg.SkyTime
                .Select(value => ConvertTimeOfDay(value, dats))
                .ToList();

            dayGroups.Add(new DayGroupData
            {
                ChanceOfOccur = dg.ChanceOfOccur,
                Name          = dg.DayName?.ToString() ?? "",
                SkyObjects    = objs,
                SkyTimes      = times,
            });
        }

        return new LoadedSkyDesc
        {
            TickSize      = sky.TickSize,
            LightTickSize = sky.LightTickSize,
            DayGroups     = dayGroups,
        };
    }

    private static void DumpRegionSkyDesc(Region region)
    {
        var sky = region.SkyInfo;
        if (sky is null) return;

        Console.WriteLine("[sky-dump] ======== BEGIN SkyDesc dump ========");
        Console.WriteLine($"[sky-dump] Region Id={region.Id:X8} Number={region.RegionNumber} Name=\"{region.RegionName}\"");

        var gt = region.GameTime;
        if (gt is not null)
        {
            Console.WriteLine(
                $"[sky-dump] GameTime ZeroTimeOfYear={gt.ZeroTimeOfYear} ZeroYear={gt.ZeroYear} " +
                $"DayLength={gt.DayLength} DaysPerYear={gt.DaysPerYear} " +
                $"YearSpec=\"{gt.YearSpec}\" TimesOfDay.Count={gt.TimesOfDay?.Count ?? 0} " +
                $"DaysOfWeek.Count={gt.DaysOfWeek?.Count ?? 0} Seasons.Count={gt.Seasons?.Count ?? 0}");

            if (gt.TimesOfDay is not null)
            {
                for (int i = 0; i < gt.TimesOfDay.Count; i++)
                {
                    var t = gt.TimesOfDay[i];
                    Console.WriteLine(
                        $"[sky-dump]   TimeOfDay[{i}] Start={t.Start} IsNight={t.IsNight} Name=\"{t.Name}\"");
                }
            }
        }
        else
        {
            Console.WriteLine("[sky-dump] GameTime: null (no calendar info in this region)");
        }

        Console.WriteLine($"[sky-dump] SkyDesc TickSize={sky.TickSize} LightTickSize={sky.LightTickSize} DayGroups.Count={sky.DayGroups.Count}");

        for (int g = 0; g < sky.DayGroups.Count; g++)
        {
            var dg = sky.DayGroups[g];
            Console.WriteLine($"[sky-dump] DayGroup[{g}] Name=\"{dg.DayName}\" Chance={dg.ChanceOfOccur:F3} SkyObjects.Count={dg.SkyObjects.Count} SkyTime.Count={dg.SkyTime.Count}");

            for (int i = 0; i < dg.SkyObjects.Count; i++)
            {
                var o = dg.SkyObjects[i];
                uint gfxId = o.DefaultGfxObjectId?.DataId ?? 0u;
                uint pesId = o.DefaultPesObjectId?.DataId ?? 0u;
                Console.WriteLine(
                    $"[sky-dump]   SkyObject[{i}] GfxObjId=0x{gfxId:X8} PesObjectId=0x{pesId:X8} " +
                    $"Time=[{o.BeginTime:F4}..{o.EndTime:F4}] Angle=[{o.BeginAngle:F1}°..{o.EndAngle:F1}°] " +
                    $"TexVel=({o.TexVelocityX:F5},{o.TexVelocityY:F5}) Properties=0x{o.Properties:X8}");
            }

            for (int k = 0; k < dg.SkyTime.Count; k++)
            {
                var t = dg.SkyTime[k];
                string dirColor = t.DirColor is null ? "null" :
                    $"({t.DirColor.Red},{t.DirColor.Green},{t.DirColor.Blue},{t.DirColor.Alpha})";
                string ambColor = t.AmbColor is null ? "null" :
                    $"({t.AmbColor.Red},{t.AmbColor.Green},{t.AmbColor.Blue},{t.AmbColor.Alpha})";
                string fogColor = t.WorldFogColor is null ? "null" :
                    $"({t.WorldFogColor.Red},{t.WorldFogColor.Green},{t.WorldFogColor.Blue},{t.WorldFogColor.Alpha})";
                Console.WriteLine(
                    $"[sky-dump]   SkyTime[{k}] Begin={t.Begin:F4} " +
                    $"DirBright={t.DirBright:F4} DirHeading={t.DirHeading:F1}° DirPitch={t.DirPitch:F1}° " +
                    $"DirColor={dirColor} AmbBright={t.AmbBright:F4} AmbColor={ambColor} " +
                    $"Fog=[{t.MinWorldFog:F1}m..{t.MaxWorldFog:F1}m] FogColor={fogColor} FogMode={t.WorldFog}");

                for (int r = 0; r < t.SkyObjReplace.Count; r++)
                {
                    var rep = t.SkyObjReplace[r];
                    uint rGfx = rep.GfxObjId?.DataId ?? 0u;
                    Console.WriteLine(
                        $"[sky-dump]     Replace[{r}] ObjectIndex={rep.ObjectIndex} GfxObjId=0x{rGfx:X8} " +
                        $"Rotate={rep.Rotate:F3}° Transparent_raw={rep.Transparent:F6} " +
                        $"Luminosity_raw={rep.Luminosity:F6} MaxBright_raw={rep.MaxBright:F6}");
                }
            }
        }

        Console.WriteLine("[sky-dump] ======== END SkyDesc dump ========");
    }

    private static SkyObjectData ConvertSkyObject(
        SkyObject s,
        IDatObjectSource? dats) => new()
    {
        BeginTime    = s.BeginTime,
        EndTime      = s.EndTime,
        BeginAngle   = s.BeginAngle,
        EndAngle     = s.EndAngle,
        TexVelocityX = s.TexVelocityX,
        TexVelocityY = s.TexVelocityY,
        GfxObjId     = s.DefaultGfxObjectId?.DataId ?? 0u,
        PesObjectId  = s.DefaultPesObjectId?.DataId ?? 0u,
        Properties   = s.Properties,
        DefaultScriptId = ResolveSetupDefaultScript(
            s.DefaultGfxObjectId?.DataId ?? 0u,
            dats),
        AuthoredSortCenter = ResolveSortCenter(
            s.DefaultGfxObjectId?.DataId ?? 0u,
            dats),
    };

    private static uint ResolveSetupDefaultScript(
        uint gfxObjId,
        IDatObjectSource? dats)
    {
        if (dats is null || (gfxObjId & 0xFF000000u) != 0x02000000u)
            return 0u;

        try
        {
            return dats.TryGet<Setup>(gfxObjId, out var setup) && setup is not null
                ? setup.DefaultScript.DataId
                : 0u;
        }
        catch
        {
            return 0u;
        }
    }

    private static DatSkyKeyframeData ConvertTimeOfDay(
        SkyTimeOfDay s,
        IDatObjectSource? dats)
    {
        var replaces = s.SkyObjReplace.Select(r => new SkyObjectReplaceData
        {
            ObjectIndex = r.ObjectIndex,
            GfxObjId    = r.GfxObjId?.DataId ?? 0u,
            Rotate      = r.Rotate,
            Transparent = r.Transparent / 100f,
            Luminosity  = r.Luminosity  / 100f,
            MaxBright   = r.MaxBright   / 100f,
            AuthoredSortCenter = ResolveSortCenter(
                r.GfxObjId?.DataId ?? 0u,
                dats),
        }).ToList();

        var fogMode = s.WorldFog switch
        {
            1u => FogMode.Linear,
            2u => FogMode.Exp,
            3u => FogMode.Exp2,
            _  => FogMode.Off,
        };

        var kf = new SkyKeyframe(
            Begin:         s.Begin,
            SunHeadingDeg: s.DirHeading,
            SunPitchDeg:   s.DirPitch,
            DirColor:      ColorToVec3(s.DirColor),
            DirBright:     s.DirBright,
            AmbColor:      ColorToVec3(s.AmbColor),
            AmbBright:     s.AmbBright,
            FogColor:      ColorToVec3(s.WorldFogColor),
            FogDensity:    0f,
            FogStart:      s.MinWorldFog,
            FogEnd:        s.MaxWorldFog,
            FogMode:       fogMode);

        return new DatSkyKeyframeData
        {
            Keyframe = kf,
            Replaces = replaces,
        };
    }

    private static Vector3 ResolveSortCenter(
        uint gfxObjId,
        IDatObjectSource? dats)
    {
        if (dats is null || (gfxObjId & 0xFF000000u) != 0x01000000u)
            return Vector3.Zero;

        try
        {
            return dats.TryGet<GfxObj>(gfxObjId, out var gfx) && gfx is not null
                ? gfx.SortCenter
                : Vector3.Zero;
        }
        catch
        {
            return Vector3.Zero;
        }
    }

    public static Vector3 ColorToVec3(ColorARGB? c)
    {
        if (c is null) return Vector3.One;
        return new Vector3(c.Red / 255f, c.Green / 255f, c.Blue / 255f);
    }
}
