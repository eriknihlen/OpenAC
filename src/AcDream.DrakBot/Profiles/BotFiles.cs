using System.Diagnostics;
using AcDream.Plugin.Abstractions;

namespace AcDream.DrakBot.Profiles;

/// <summary>
/// Every file the bot reads or writes, in one folder a player can find:
/// the plugin's own storage, laid out by kind. VTank-format files a player
/// already has in the client's shared <c>vtank</c> folder (its root, or its
/// <c>metas</c> and <c>navs</c> subfolders) are still found, as a fallback;
/// anything the bot saves goes into its own folder.
/// <code>
/// plugins/acdream.drakbot/
///   profiles/   name.json          bot profiles
///   routes/     name.json, .nav    bot routes and VTank .nav routes
///   loot/       name.utl           VTank loot profiles
///   metas/      name.af, .met      VTank metas
///   hazards/    XXXX.json          marked hazard cells per landblock
///   meta/       gvars, pvars       meta variables
///   logs/       drakbot-log.txt    log dumps
/// </code>
/// </summary>
public sealed class BotFiles(IPluginStorage storage, IPluginStorage? vtank = null)
{
    public const string ProfilesFolder = "profiles";
    public const string RoutesFolder = "routes";
    public const string LootFolder = "loot";
    public const string MetasFolder = "metas";
    public const string HazardsFolder = "hazards";
    public const string LogsFolder = "logs";

    private readonly IPluginStorage _vtank = vtank ?? NoOpPluginStorage.Instance;

    /// <summary>The bot's folder on disk, or null on a host without file storage.</summary>
    public string? Directory => storage.Directory;

    /// <summary>The client's shared VTank folder on disk, or null.</summary>
    public string? VtankDirectory => _vtank.Directory;

    /// <summary>A subfolder's path, or null on a host without file storage.</summary>
    public string? PathOf(string folder) => Directory is { } root ? Path.Combine(root, folder) : null;

    /// <summary>Creates the folders so a player opening the bot's directory sees where things go.</summary>
    public void EnsureLayout()
    {
        if (Directory is not { } root)
            return;
        foreach (string folder in new[] { ProfilesFolder, RoutesFolder, LootFolder, MetasFolder, LogsFolder })
        {
            try
            {
                System.IO.Directory.CreateDirectory(Path.Combine(root, folder));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // A read-only install: the bot still runs, the folder just is not pre-made.
            }
        }
    }

    /// <summary>Opens a subfolder (or the bot's folder) in the system's file browser. False when there is no folder or it could not be opened.</summary>
    public bool TryOpen(string? folder, out string path)
    {
        path = folder is null ? Directory ?? string.Empty : PathOf(folder) ?? string.Empty;
        if (path.Length == 0)
            return false;
        try
        {
            System.IO.Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    // ── loot profiles ────────────────────────────────────────────────────

    /// <summary>A VTank <c>.utl</c> by name: the bot's loot folder first, then the client's vtank folder.</summary>
    public string? ReadUtl(string name)
    {
        string file = BotStore.SanitizeName(name) + ".utl";
        return storage.ReadText($"{LootFolder}/{file}") ?? FindInVtank(file, static (store, key) => store.ReadText(key));
    }

    /// <summary>The <c>.utl</c> files on offer, by file name, the bot's folder first.</summary>
    public IReadOnlyList<string> UtlFileNames() => Names(LootFolder, ".utl");

    // ── metas ────────────────────────────────────────────────────────────

    /// <summary>A meta's text (<c>.af</c>) or bytes (<c>.met</c>) by name, with where it was found.</summary>
    public bool TryReadMeta(string name, out string? text, out byte[]? bytes, out string path)
    {
        string safe = BotStore.SanitizeName(name);
        text = null;
        bytes = null;
        path = safe + ".af";
        text = storage.ReadText($"{MetasFolder}/{path}") ?? FindInVtank(path, static (store, key) => store.ReadText(key));
        if (text is not null)
            return true;
        path = safe + ".met";
        bytes = storage.ReadBytes($"{MetasFolder}/{path}") ?? FindInVtank(path, static (store, key) => store.ReadBytes(key));
        if (bytes is not null)
            return true;
        path = string.Empty;
        return false;
    }

    /// <summary>Writes a meta as <c>metas/&lt;name&gt;.af</c> in the bot's folder.</summary>
    public void WriteMeta(string name, string text) =>
        storage.WriteText($"{MetasFolder}/{BotStore.SanitizeName(name)}.af", text);

    public IReadOnlyList<string> MetaFileNames() => Names(MetasFolder, ".af", ".met");

    // ── VTank routes ─────────────────────────────────────────────────────

    /// <summary>A VTank <c>.nav</c> by name: the bot's routes folder first, then the client's vtank folder.</summary>
    public string? ReadNav(string name)
    {
        string file = BotStore.SanitizeName(name) + ".nav";
        return storage.ReadText($"{RoutesFolder}/{file}") ?? FindInVtank(file, static (store, key) => store.ReadText(key));
    }

    public IReadOnlyList<string> NavFileNames() => Names(RoutesFolder, ".nav");

    // ── logs ─────────────────────────────────────────────────────────────

    public const string LogDumpFile = "drakbot-log.txt";

    /// <summary>Writes the log dump; returns its path for the message, or the relative name without file storage.</summary>
    public string WriteLogDump(string text)
    {
        string key = $"{LogsFolder}/{LogDumpFile}";
        if (storage.IsAvailable)
            storage.WriteText(key, text);
        return PathOf(LogsFolder) is { } folder ? Path.Combine(folder, LogDumpFile) : key;
    }

    /// <summary>Writes a dungeon dump (`dungeons/<landblock>.json`); returns its path for the message.</summary>
    public string WriteDungeonDump(string landblock, string json)
    {
        string key = $"dungeons/{landblock}.json";
        if (storage.IsAvailable)
            storage.WriteText(key, json);
        return PathOf("dungeons") is { } folder ? Path.Combine(folder, $"{landblock}.json") : key;
    }

    // ── shared ───────────────────────────────────────────────────────────

    /// <summary>VTank kept everything in one folder; RynthAi and players sort into <c>metas</c> and <c>navs</c>. All three are looked in.</summary>
    private static readonly string[] VtankFolders = ["", "metas/", "navs/"];

    private T? FindInVtank<T>(string file, Func<IPluginStorage, string, T?> read) where T : class
    {
        if (!_vtank.IsAvailable)
            return null;
        foreach (string folder in VtankFolders)
        {
            T? found = read(_vtank, folder + file);
            if (found is not null)
                return found;
        }
        return null;
    }

    /// <summary>File names with one of the extensions: the bot's subfolder, then anywhere under the vtank folder; the bot's copy wins a name clash.</summary>
    private IReadOnlyList<string> Names(string folder, params string[] extensions)
    {
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(IEnumerable<string> keys)
        {
            foreach (string key in keys)
            {
                string file = key.Replace('\\', '/').Split('/')[^1];
                if (extensions.Any(extension => file.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) && seen.Add(file))
                    names.Add(file);
            }
        }
        if (storage.IsAvailable)
            Add(storage.List(folder));
        if (_vtank.IsAvailable)
            Add(_vtank.List(string.Empty));
        names.Sort(StringComparer.OrdinalIgnoreCase);
        return names;
    }
}
