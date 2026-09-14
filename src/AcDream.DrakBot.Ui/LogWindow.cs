using AcDream.DrakBot.Profiles;
using System.Numerics;
using ImGuiNET;

namespace AcDream.DrakBot.Ui;

/// <summary>
/// The bot's log as it happens: the ring the engine, behaviors and
/// commands write to, newest at the bottom, with a level picker, a text
/// filter, pause, copy and a dump to the plugin folder. Warnings are red,
/// debug dim, trace dimmer.
/// </summary>
public sealed class LogWindow(BotController controller)
{
    private static readonly string[] LevelNames = ["Quiet", "Info", "Debug", "Trace"];
    private static readonly Vector4 ColWarn = new(1f, 0.4f, 0.35f, 1f);
    private static readonly Vector4 ColInfo = new(0.9f, 0.9f, 0.9f, 1f);
    private static readonly Vector4 ColDebug = new(0.6f, 0.68f, 0.75f, 1f);
    private static readonly Vector4 ColTrace = new(0.45f, 0.5f, 0.55f, 1f);

    private bool _open;
    private string _filter = string.Empty;
    private bool _paused;
    private bool _autoScroll = true;
    private long _seenSequence = -1;
    private IReadOnlyList<BotLogEntry> _shown = [];
    private string _status = string.Empty;
    private double _statusAt = double.NegativeInfinity;

    public bool IsOpen
    {
        get => _open;
        set => _open = value;
    }

    public void Draw()
    {
        if (!_open)
            return;
        BotLog log = controller.Log;
        ImGui.SetNextWindowSize(new Vector2(720f, 380f), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Log##drakbot", ref _open))
        {
            ImGui.End();
            return;
        }

        int level = (int)log.Level;
        ImGui.SetNextItemWidth(90f);
        if (ImGui.Combo("##level", ref level, LevelNames, LevelNames.Length))
            log.Level = (BotLogLevel)level;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Quiet: warnings only. Info: decisions. Debug: the reasons, a few lines a second. Trace: every tick.");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(200f);
        ImGui.InputTextWithHint("##filter", "filter (nav:, combat:, loot:, patrol:...)", ref _filter, 128);
        ImGui.SameLine();
        ImGui.Checkbox("Pause", ref _paused);
        ImGui.SameLine();
        ImGui.Checkbox("Follow", ref _autoScroll);
        ImGui.SameLine();
        if (ImGui.Button("Copy"))
        {
            ImGui.SetClipboardText(log.Dump());
            Status("copied to the clipboard");
        }
        ImGui.SameLine();
        if (ImGui.Button("Dump"))
            Status($"written to {controller.Files.WriteLogDump(log.Dump())}");
        ImGui.SameLine();
        if (ImGui.Button("Open folder"))
            controller.Files.TryOpen(BotFiles.LogsFolder, out _);
        ImGui.SameLine();
        if (ImGui.Button("Clear"))
            log.Clear();
        if (_status.Length > 0 && ImGui.GetTime() - _statusAt < 4d)
        {
            ImGui.SameLine();
            ImGui.TextDisabled(_status);
        }

        if (!_paused && log.Sequence != _seenSequence)
        {
            _seenSequence = log.Sequence;
            _shown = log.Snapshot();
        }

        ImGui.BeginChild("lines", new Vector2(0f, 0f), ImGuiChildFlags.Borders, ImGuiWindowFlags.HorizontalScrollbar);
        bool filtered = _filter.Length > 0;
        foreach (BotLogEntry entry in _shown)
        {
            if (filtered && !entry.Text.Contains(_filter, StringComparison.OrdinalIgnoreCase))
                continue;
            Vector4 color = entry.Level switch
            {
                BotLogLevel.Quiet => ColWarn,
                BotLogLevel.Info => ColInfo,
                BotLogLevel.Debug => ColDebug,
                _ => ColTrace,
            };
            ImGui.TextColored(color, $"{entry.At:HH:mm:ss.fff} {entry.Prefix} {entry.Text}");
        }
        if (_autoScroll && !_paused && ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 40f)
            ImGui.SetScrollHereY(1f);
        ImGui.EndChild();
        ImGui.End();
    }

    private void Status(string text)
    {
        _status = text;
        _statusAt = ImGui.GetTime();
    }
}
