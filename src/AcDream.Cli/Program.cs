using System.Globalization;
using System.Diagnostics;
using AcDream.Cli;
using DatReaderWriter;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Enums;
using DatReaderWriter.Options;
using DatReaderWriter.Types;
using Env = System.Environment;

// ─── subcommand dispatch ────────────────────────────────────────────────────
if (args.Length >= 1 && args[0] == "summarize-frame-history")
{
    return PerformanceTools.SummarizeFrameHistory(args[1..]);
}

if (args.Length >= 1 && args[0] == "compare-screenshots")
{
    return PerformanceTools.CompareScreenshots(args[1..]);
}

if (args.Length >= 1 && args[0] == "dump-vitals-bars")
{
    string? dvbDatDir = args.ElementAtOrDefault(1) ?? Env.GetEnvironmentVariable("ACDREAM_DAT_DIR");
    if (string.IsNullOrWhiteSpace(dvbDatDir))
    {
        Console.Error.WriteLine("usage: AcDream.Cli dump-vitals-bars <dat-directory>");
        return 2;
    }
    return DumpVitalsBars(dvbDatDir);
}

if (args.Length >= 1 && args[0] == "dump-vitals-layout")
{
    string? dvlDatDir = args.ElementAtOrDefault(1) ?? Env.GetEnvironmentVariable("ACDREAM_DAT_DIR");
    string? dvlLayout = args.ElementAtOrDefault(2);
    if (string.IsNullOrWhiteSpace(dvlDatDir))
    {
        Console.Error.WriteLine("usage: AcDream.Cli dump-vitals-layout <dat-directory> [0xLayoutId]");
        return 2;
    }
    return VitalsLayoutDump.Run(dvlDatDir, dvlLayout);
}

if (args.Length >= 1 && args[0] == "list-ui-layouts")
{
    string? luiDatDir = args.ElementAtOrDefault(1) ?? Env.GetEnvironmentVariable("ACDREAM_DAT_DIR");
    string? luiRootType = args.ElementAtOrDefault(2);
    if (string.IsNullOrWhiteSpace(luiDatDir))
    {
        Console.Error.WriteLine("usage: AcDream.Cli list-ui-layouts <dat-directory> [0xRootType]");
        return 2;
    }
    return LayoutIndexDump.Run(luiDatDir, luiRootType);
}

if (args.Length >= 1 && args[0] == "render-vitals-mockup")
{
    string? rvmDatDir = args.ElementAtOrDefault(1) ?? Env.GetEnvironmentVariable("ACDREAM_DAT_DIR");
    string rvmOut = args.ElementAtOrDefault(2) ?? "vitals-mockup.png";
    if (string.IsNullOrWhiteSpace(rvmDatDir))
    {
        Console.Error.WriteLine("usage: AcDream.Cli render-vitals-mockup <dat-directory> [out.png]");
        return 2;
    }
    return VitalsMockup.Render(rvmDatDir, rvmOut);
}

if (args.Length >= 1 && args[0] == "dump-sprite-sheet")
{
    string? dssDir = args.ElementAtOrDefault(1) ?? Env.GetEnvironmentVariable("ACDREAM_DAT_DIR");
    string? dssIds = args.ElementAtOrDefault(2);
    string dssOut = args.ElementAtOrDefault(3) ?? "sprite-sheet.png";
    if (string.IsNullOrWhiteSpace(dssDir) || string.IsNullOrWhiteSpace(dssIds))
    {
        Console.Error.WriteLine("usage: AcDream.Cli dump-sprite-sheet <dat-directory> <0xId,0xId,...> [out.png]");
        return 2;
    }
    return VitalsMockup.ExportSheet(dssDir, dssIds, dssOut);
}

if (args.Length >= 1 && args[0] == "dump-font-atlas")
{
    string? dfaDir = args.ElementAtOrDefault(1) ?? Env.GetEnvironmentVariable("ACDREAM_DAT_DIR");
    string? dfaFont = args.ElementAtOrDefault(2);
    string? dfaSample = args.ElementAtOrDefault(3);   // sample string
    string dfaOut = args.ElementAtOrDefault(4) ?? "font-atlas";
    if (string.IsNullOrWhiteSpace(dfaDir))
    {
        Console.Error.WriteLine("usage: AcDream.Cli dump-font-atlas <dat-directory> [0xFontId] [sample] [outBase]");
        return 2;
    }
    return FontAtlasDump.Run(dfaDir, dfaFont, dfaSample, dfaOut);
}

if (args.Length >= 1 && args[0] == "probe")
{
    // probe <in.png> <x0> <y0> <x1> <y1>
    if (args.Length < 6) { Console.Error.WriteLine("usage: AcDream.Cli probe <in.png> <x0> <y0> <x1> <y1>"); return 2; }
    return VitalsMockup.Probe(args[1], int.Parse(args[2], CultureInfo.InvariantCulture), int.Parse(args[3], CultureInfo.InvariantCulture), int.Parse(args[4], CultureInfo.InvariantCulture), int.Parse(args[5], CultureInfo.InvariantCulture));
}

if (args.Length >= 1 && args[0] == "crop")
{
    if (args.Length < 8) { Console.Error.WriteLine("usage: AcDream.Cli crop <in.png> <x> <y> <w> <h> <zoom> <out.png>"); return 2; }
    return VitalsMockup.Crop(args[1],
        int.Parse(args[2], CultureInfo.InvariantCulture), int.Parse(args[3], CultureInfo.InvariantCulture), int.Parse(args[4], CultureInfo.InvariantCulture), int.Parse(args[5], CultureInfo.InvariantCulture), int.Parse(args[6], CultureInfo.InvariantCulture), args[7]);
}

if (args.Length >= 1 && args[0] == "dump-edges")
{
    string? deDir = args.ElementAtOrDefault(1) ?? Env.GetEnvironmentVariable("ACDREAM_DAT_DIR");
    string? deId = args.ElementAtOrDefault(2);
    if (string.IsNullOrWhiteSpace(deDir) || string.IsNullOrWhiteSpace(deId))
    {
        Console.Error.WriteLine("usage: AcDream.Cli dump-edges <dat-directory> <0xId>");
        return 2;
    }
    return VitalsMockup.DumpEdges(deDir, deId);
}

if (args.Length >= 1 && args[0] == "mock-selbar")
{
    string? msbDir = args.ElementAtOrDefault(1) ?? Env.GetEnvironmentVariable("ACDREAM_DAT_DIR");
    string msbOut = args.ElementAtOrDefault(2) ?? "selbar.png";
    if (string.IsNullOrWhiteSpace(msbDir))
    {
        Console.Error.WriteLine("usage: AcDream.Cli mock-selbar <dat-directory> [out.png]");
        return 2;
    }
    return VitalsMockup.MockSelBar(msbDir, msbOut);
}

if (args.Length >= 1 && args[0] == "export-ui-sprite")
{
    string? eusDatDir = args.ElementAtOrDefault(1) ?? Env.GetEnvironmentVariable("ACDREAM_DAT_DIR");
    string? eusId = args.ElementAtOrDefault(2);
    string eusOut = args.ElementAtOrDefault(3) ?? "sprite.png";
    if (string.IsNullOrWhiteSpace(eusDatDir) || string.IsNullOrWhiteSpace(eusId))
    {
        Console.Error.WriteLine("usage: AcDream.Cli export-ui-sprite <dat-directory> <0xId> [out.png]");
        return 2;
    }
    return VitalsMockup.ExportSprite(eusDatDir, eusId, eusOut);
}


string? datDir = args.FirstOrDefault() ?? Env.GetEnvironmentVariable("ACDREAM_DAT_DIR");
if (string.IsNullOrWhiteSpace(datDir))
{
    Console.Error.WriteLine("usage: AcDream.Cli <dat-directory>");
    Console.Error.WriteLine("   or: set ACDREAM_DAT_DIR and run with no args");
    return 2;
}

if (!Directory.Exists(datDir))
{
    Console.Error.WriteLine($"error: directory not found: {datDir}");
    return 2;
}

string[] required = ["client_portal.dat", "client_cell_1.dat", "client_highres.dat", "client_local_English.dat"];
var missing = required.Where(f => !File.Exists(Path.Combine(datDir, f))).ToArray();
if (missing.Length > 0)
{
    Console.Error.WriteLine($"error: missing dat files in {datDir}:");
    foreach (var f in missing) Console.Error.WriteLine($"  - {f}");
    return 2;
}

Console.WriteLine($"acdream asset dump");
Console.WriteLine($"dat dir: {datDir}");
Console.WriteLine();

var sw = Stopwatch.StartNew();
using var dats = new DatCollection(datDir, DatAccessType.Read);
sw.Stop();
Console.WriteLine($"opened 4 dats in {sw.ElapsedMilliseconds} ms");
Console.WriteLine();

// File sizes, just so we can see they're real
foreach (var f in required)
{
    var path = Path.Combine(datDir, f);
    var mb = new FileInfo(path).Length / 1024.0 / 1024.0;
    Console.WriteLine($"  {f,-30} {mb,8:F1} MB");
}
Console.WriteLine();

var sections = new (string Section, (string Name, Func<int> Count)[] Rows)[]
{
    ("visual — geometry", new (string, Func<int>)[]
    {
        ("GfxObj",            () => dats.GetAllIdsOfType<GfxObj>().Count()),
        ("GfxObjDegradeInfo", () => dats.GetAllIdsOfType<GfxObjDegradeInfo>().Count()),
        ("Setup",             () => dats.GetAllIdsOfType<Setup>().Count()),
        ("Scene",             () => dats.GetAllIdsOfType<Scene>().Count()),
        ("Environment",       () => dats.GetAllIdsOfType<DatReaderWriter.DBObjs.Environment>().Count()),
    }),
    ("visual — texturing / materials", new (string, Func<int>)[]
    {
        ("Surface",          () => dats.GetAllIdsOfType<Surface>().Count()),
        ("SurfaceTexture",   () => dats.GetAllIdsOfType<SurfaceTexture>().Count()),
        ("RenderSurface",    () => dats.GetAllIdsOfType<RenderSurface>().Count()),
        ("RenderTexture",    () => dats.GetAllIdsOfType<RenderTexture>().Count()),
        ("RenderMaterial",   () => dats.GetAllIdsOfType<RenderMaterial>().Count()),
        ("MaterialInstance", () => dats.GetAllIdsOfType<MaterialInstance>().Count()),
        ("MaterialModifier", () => dats.GetAllIdsOfType<MaterialModifier>().Count()),
        ("Palette",          () => dats.GetAllIdsOfType<Palette>().Count()),
        ("PalSet",           () => dats.GetAllIdsOfType<PalSet>().Count()),
    }),
    ("terrain / cells", CountCellByLow16(dats)),
    ("animation", new (string, Func<int>)[]
    {
        ("Animation",   () => dats.GetAllIdsOfType<Animation>().Count()),
        ("MotionTable", () => dats.GetAllIdsOfType<MotionTable>().Count()),
    }),
    ("particles & physics", new (string, Func<int>)[]
    {
        ("ParticleEmitter",    () => dats.GetAllIdsOfType<ParticleEmitter>().Count()),
        ("PhysicsScript",      () => dats.GetAllIdsOfType<PhysicsScript>().Count()),
        ("PhysicsScriptTable", () => dats.GetAllIdsOfType<PhysicsScriptTable>().Count()),
    }),
    ("audio", new (string, Func<int>)[]
    {
        ("SoundTable", () => dats.GetAllIdsOfType<SoundTable>().Count()),
        ("Wave",       () => dats.GetAllIdsOfType<Wave>().Count()),
    }),
    ("game data tables", new (string, Func<int>)[]
    {
        ("SpellTable",          () => dats.GetAllIdsOfType<SpellTable>().Count()),
        ("SpellComponentTable", () => dats.GetAllIdsOfType<SpellComponentTable>().Count()),
        ("SkillTable",          () => dats.GetAllIdsOfType<SkillTable>().Count()),
        ("CombatTable",         () => dats.GetAllIdsOfType<CombatTable>().Count()),
        ("VitalTable",          () => dats.GetAllIdsOfType<VitalTable>().Count()),
        ("ExperienceTable",     () => dats.GetAllIdsOfType<ExperienceTable>().Count()),
        ("ClothingTable",       () => dats.GetAllIdsOfType<ClothingTable>().Count()),
        ("CharGen",             () => dats.GetAllIdsOfType<CharGen>().Count()),
        ("ChatPoseTable",       () => dats.GetAllIdsOfType<ChatPoseTable>().Count()),
        ("ContractTable",       () => dats.GetAllIdsOfType<ContractTable>().Count()),
    }),
    ("ui / strings", new (string, Func<int>)[]
    {
        ("Font",           () => dats.GetAllIdsOfType<Font>().Count()),
        ("StringTable",    () => dats.GetAllIdsOfType<StringTable>().Count()),
        ("LanguageString", () => dats.GetAllIdsOfType<LanguageString>().Count()),
        ("LanguageInfo",   () => dats.GetAllIdsOfType<LanguageInfo>().Count()),
        ("LayoutDesc",     () => dats.GetAllIdsOfType<LayoutDesc>().Count()),
    }),
};

long grandTotal = 0;
foreach (var (section, rows) in sections)
{
    Console.WriteLine($"[{section}]");
    long sectionTotal = 0;
    foreach (var (name, count) in rows)
    {
        var n = count();
        sectionTotal += n;
        Console.WriteLine($"  {name,-22} {n,10:N0}");
    }
    Console.WriteLine($"  {"subtotal",-22} {sectionTotal,10:N0}");
    Console.WriteLine();
    grandTotal += sectionTotal;
}

Console.WriteLine($"grand total: {grandTotal:N0} indexed assets across tracked types");
return 0;

static (string Name, Func<int> Count)[] CountCellByLow16(DatCollection dats)
{
    int landBlocks = 0, landBlockInfos = 0, envCells = 0, other = 0;
    foreach (var file in dats.Cell.Tree)
    {
        var low = file.Id & 0xFFFFu;
        if (low == 0xFFFFu) landBlocks++;
        else if (low == 0xFFFEu) landBlockInfos++;
        else if (file.Id != 0) envCells++;
        else other++;
    }
    return new (string, Func<int>)[]
    {
        ("LandBlock",     () => landBlocks),
        ("LandBlockInfo", () => landBlockInfos),
        ("EnvCell",       () => envCells),
        ("Region",        () => dats.GetAllIdsOfType<Region>().Count()),
    };
}

static int DumpVitalsBars(string dvbDatDir)
{
    const uint HEALTH_ELEM_ID  = 0x100000E6u;
    const uint STAMINA_ELEM_ID = 0x100000ECu;
    const uint MANA_ELEM_ID    = 0x100000EEu;

    if (!Directory.Exists(dvbDatDir))
    {
        Console.Error.WriteLine($"error: directory not found: {dvbDatDir}");
        return 2;
    }

    using var dats = new DatCollection(dvbDatDir, DatAccessType.Read);

    Console.WriteLine("Scanning LayoutDescs for vitals window (element 0x100000E6 = Health meter)...");
    uint? vitalsId = null;
    LayoutDesc? vitalsLayout = null;
    foreach (var id in dats.GetAllIdsOfType<LayoutDesc>())
    {
        var ld = dats.Get<LayoutDesc>(id);
        if (ld is null) continue;
        if (VbContainsElementId(ld, HEALTH_ELEM_ID)) { vitalsId = id; vitalsLayout = ld; break; }
    }

    if (vitalsLayout is null)
    {
        Console.Error.WriteLine("ERROR: no LayoutDesc contains element 0x100000E6 (Health meter).");
        return 1;
    }
    Console.WriteLine($"Found vitals layout: 0x{vitalsId!.Value:X8}");
    Console.WriteLine();

    var meters = new[] { (HEALTH_ELEM_ID, "HEALTH"), (STAMINA_ELEM_ID, "STAMINA"), (MANA_ELEM_ID, "MANA") };
    foreach (var (eid, vitalName) in meters)
    {
        Console.WriteLine($"{vitalName} meter (element 0x{eid:X8}) in layout 0x{vitalsId!.Value:X8}:");
        var meterElem = VbFindElement(vitalsLayout!, eid);
        if (meterElem is null) { Console.WriteLine("  <element not found>"); continue; }

        var sprites = new List<(string Role, uint DataId, string DrawMode)>();
        VbCollectSprites(meterElem, sprites, 0);

        if (sprites.Count == 0)
        {
            Console.WriteLine("  <no sprites found in sub-tree>");
        }
        else
        {
            foreach (var (role, dataId, drawMode) in sprites)
                Console.WriteLine($"  {role,-35} 0x{dataId:X8}  ({drawMode})");
        }
        Console.WriteLine();
    }

    return 0;
}

// ─── dump-vitals-bars helpers ───────────────────────────────────────────────

static bool VbContainsElementId(LayoutDesc ld, uint targetId)
{
    var elems = ld.Elements;
    foreach (var kvp in elems)
    {
        if (kvp.Key == targetId) return true;
        if (VbChildContains(kvp.Value, targetId)) return true;
    }
    return false;
}

static bool VbChildContains(ElementDesc elem, uint targetId)
{
    foreach (var kvp in elem.Children)
    {
        if (kvp.Key == targetId) return true;
        if (VbChildContains(kvp.Value, targetId)) return true;
    }
    return false;
}

static ElementDesc? VbFindElement(LayoutDesc ld, uint targetId)
{
    foreach (var kvp in ld.Elements)
    {
        if (kvp.Key == targetId) return kvp.Value;
        var found = VbFindChild(kvp.Value, targetId);
        if (found is not null) return found;
    }
    return null;
}

static ElementDesc? VbFindChild(ElementDesc elem, uint targetId)
{
    foreach (var kvp in elem.Children)
    {
        if (kvp.Key == targetId) return kvp.Value;
        var found = VbFindChild(kvp.Value, targetId);
        if (found is not null) return found;
    }
    return null;
}

static void VbCollectSprites(ElementDesc elem, List<(string, uint, string)> out_, int depth)
{
    string indent = new string(' ', depth * 2);

    if (elem.StateDesc is not null)
        VbExtractMedia(elem.StateDesc, $"{indent}elem_0x{elem.ElementId:X8}.DirectState", out_);

    foreach (var kvp in elem.States)
        VbExtractMedia(kvp.Value, $"{indent}elem_0x{elem.ElementId:X8}.{kvp.Key}", out_);

    foreach (var kvp in elem.Children)
        VbCollectSprites(kvp.Value, out_, depth + 1);
}

static void VbExtractMedia(StateDesc sd, string role, List<(string, uint, string)> out_)
{
    foreach (var m in sd.Media)
    {
        if (m is MediaDescImage img && img.File != 0)
            out_.Add((role, img.File, img.DrawMode.ToString()));
    }
}
