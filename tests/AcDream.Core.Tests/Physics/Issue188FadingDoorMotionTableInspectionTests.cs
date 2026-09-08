using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using DatReaderWriter;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Options;
using DatReaderWriter.Types;
using Xunit;
using Xunit.Abstractions;
using Env = System.Environment;

namespace AcDream.Core.Tests.Physics;

[Trait("Purpose", "Diagnostic")]
public class Issue188FadingDoorMotionTableInspectionTests
{
    private readonly ITestOutputHelper _out;
    public Issue188FadingDoorMotionTableInspectionTests(ITestOutputHelper output) => _out = output;

    private static string? DatDir()
    {
        var d = Env.GetEnvironmentVariable("ACDREAM_DAT_DIR")
            ?? Path.Combine(Env.GetFolderPath(Env.SpecialFolder.UserProfile), "Documents", "Asheron's Call");
        return Directory.Exists(d) ? d : null;
    }

    [Fact]
    public void Reflect_DatReaderWriter_HookTypes()
    {
        var asm = typeof(MotionTable).Assembly;
        _out.WriteLine($"assembly: {asm.FullName}");

        foreach (var t in asm.GetTypes().Where(t => t.Name.Contains("Hook", StringComparison.OrdinalIgnoreCase)))
        {
            _out.WriteLine($"=== type {t.FullName} (base={t.BaseType?.Name}) ===");
            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                _out.WriteLine($"   [prop]  {p.PropertyType.Name} {p.Name}");
            foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                _out.WriteLine($"   [field] {f.FieldType.Name} {f.Name}");
        }

        void DumpMembers(Type t, string label)
        {
            _out.WriteLine($"=== {label} ({t.FullName}) ===");
            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                _out.WriteLine($"   [prop]  {p.PropertyType} {p.Name}");
            foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
                _out.WriteLine($"   [field] {f.FieldType} {f.Name}");
        }

        DumpMembers(typeof(MotionData), "MotionData");
        DumpMembers(asm.GetTypes().First(t => t.Name == "AnimData"), "AnimData");
        DumpMembers(typeof(Animation), "Animation");
        var frameType = asm.GetTypes().FirstOrDefault(t => t.Name is "AnimationFrame" or "AnimFrame");
        if (frameType is not null) DumpMembers(frameType, frameType.Name);
        var qdiType = asm.GetTypes().FirstOrDefault(t => t.Name.StartsWith("QualifiedDataId"));
        if (qdiType is not null) DumpMembers(qdiType, qdiType.Name);
    }

    [Fact]
    [Trait("Lane", "InstalledDat")]
    public void Dump_PedestalWeakSpot_MotionTable_HookContents()
    {
        var datDir = DatDir();
        if (datDir is null) { _out.WriteLine("SKIP: no dat dir"); Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md."); }
        using var dats = new DatCollection(datDir, DatAccessType.Read);

        const uint MotionTableId = 0x090000F9u;
        var mtable = dats.Get<MotionTable>(MotionTableId);
        if (mtable is null) { _out.WriteLine($"MotionTable 0x{MotionTableId:X8} NOT FOUND"); return; }

        _out.WriteLine($"MotionTable 0x{MotionTableId:X8}: DefaultStyle=0x{(uint)mtable.DefaultStyle:X8}");

        var allMotionData = new List<(string source, MotionData md)>();
        foreach (var kv in mtable.Cycles) allMotionData.Add(($"Cycles[{kv.Key:X}]", kv.Value));
        foreach (var kv in mtable.Modifiers) allMotionData.Add(($"Modifiers[{kv.Key:X}]", kv.Value));
        foreach (var linkKv in mtable.Links)
            foreach (var innerKv in linkKv.Value.MotionData)
                allMotionData.Add(($"Links[{linkKv.Key:X}][{innerKv.Key:X}]", innerKv.Value));

        _out.WriteLine($"total MotionData entries: {allMotionData.Count}");

        var animIdsSeen = new HashSet<uint>();
        var hookTypesSeen = new SortedSet<string>();

        foreach (var (source, md) in allMotionData)
        {
            if (md?.Anims is null) continue;
            foreach (var animData in md.Anims)
            {
                uint animId = GetAnimId(animData);
                if (animId == 0 || !animIdsSeen.Add(animId)) continue;

                var anim = dats.Get<Animation>(animId);
                if (anim is null) { _out.WriteLine($"  [{source}] anim 0x{animId:X8} NOT FOUND"); continue; }

                int frameCount = 0, hookCount = 0;
                int frameIdx = 0;
                foreach (var frame in GetFrames(anim))
                {
                    frameCount++;
                    foreach (var hook in GetHooks(frame))
                    {
                        hookCount++;
                        hookTypesSeen.Add(hook.GetType().Name);
                        var fieldDump = string.Join(" ", hook.GetType()
                            .GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                            .Select(f => $"{f.Name}={f.GetValue(hook)}"));
                        _out.WriteLine($"    frame[{frameIdx}] {hook.GetType().Name}: {fieldDump}");
                    }
                    frameIdx++;
                }
                _out.WriteLine($"  [{source}] anim 0x{animId:X8}: frames={frameCount} hooks={hookCount}");
            }
        }

        _out.WriteLine($"=== DISTINCT HOOK TYPES ACROSS ENTIRE TABLE: {(hookTypesSeen.Count == 0 ? "(none)" : string.Join(", ", hookTypesSeen))} ===");
    }

    private static object? GetMember(object obj, string name)
    {
        var t = obj.GetType();
        var prop = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        if (prop is not null) return prop.GetValue(obj);
        var field = t.GetField(name, BindingFlags.Public | BindingFlags.Instance);
        return field?.GetValue(obj);
    }

    private static uint GetAnimId(object animData)
    {
        var val = GetMember(animData, "AnimId") ?? GetMember(animData, "Id");
        if (val is null) return 0;
        var dataId = GetMember(val, "DataId") ?? GetMember(val, "Id");
        return dataId switch { uint u => u, int i => (uint)i, ulong ul => (uint)ul, _ => 0 };
    }

    private static IEnumerable<object> GetFrames(Animation anim)
    {
        var frames = (GetMember(anim, "PartFrames") ?? GetMember(anim, "Frames")) as IEnumerable;
        if (frames is null) yield break;
        foreach (var f in frames)
            if (f is not null) yield return f;
    }

    private static IEnumerable<object> GetHooks(object frame)
    {
        var hooks = GetMember(frame, "Hooks") as IEnumerable;
        if (hooks is null) yield break;
        foreach (var h in hooks)
            if (h is not null) yield return h;
    }
}
