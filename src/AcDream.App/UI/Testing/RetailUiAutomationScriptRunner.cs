using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using AcDream.Core.Items;
using AcDream.UI.Abstractions.Input;

namespace AcDream.App.UI.Testing;

public enum RetailUiAutomationCheckpointStatus
{
    Pending,
    Succeeded,
    Failed,
    Cancelled,
}

public interface IRetailUiAutomationCheckpoint
{
    int Sequence { get; }
    string Name { get; }
    RetailUiAutomationCheckpointStatus Status { get; }
    string? Error { get; }
}

public enum RetailUiAutomationRenderPackState
{
    Retail,
    CandidatePending,
    Active,
    FailedToRetail,
}

public readonly record struct RetailUiAutomationRenderPackStatus(
    RetailUiAutomationRenderPackState State,
    string PackId,
    string PresetId,
    long ActivationGeneration,
    string? FailureReason)
{
    public static RetailUiAutomationRenderPackStatus Retail { get; } = new(
        RetailUiAutomationRenderPackState.Retail,
        "retail",
        "off",
        ActivationGeneration: 0,
        FailureReason: null);
}

public interface IRetailUiAutomationRuntime
{
    bool IsWorldReady { get; }
    bool IsWorldViewportVisible { get; }
    int PortalMaterializationCount { get; }
    int RenderPackPerformanceSampleCount => 0;
    bool RenderPackFailedToRetail => false;
    RetailUiAutomationRenderPackStatus RenderPackStatus =>
        RetailUiAutomationRenderPackStatus.Retail;
    int FramebufferWidth => 0;
    int FramebufferHeight => 0;
    bool TrySelectRenderPack(string presetId, out string error)
    {
        error = "render-pack selection automation is unavailable";
        return false;
    }
    bool TryDisableRenderPack(out string error)
    {
        error = "render-pack selection automation is unavailable";
        return false;
    }
    bool TryReenableRenderPack(out string error)
    {
        error = "render-pack selection automation is unavailable";
        return false;
    }
    bool TryResizeFramebuffer(int width, int height, out string error)
    {
        error = "framebuffer resize automation is unavailable";
        return false;
    }
    bool TryResetRenderPackPerformance(out string error)
    {
        error = "render-pack performance automation is unavailable";
        return false;
    }
    bool TryRequestClientClose(out string error)
    {
        error = "client-close automation is unavailable";
        return false;
    }
    bool TryRequestCheckpoint(
        string name,
        out IRetailUiAutomationCheckpoint? checkpoint,
        out string error);
    void CancelCheckpoint(IRetailUiAutomationCheckpoint checkpoint);
    bool TryRequestScreenshot(string name, out string error);
    bool IsScreenshotComplete(string name);
    bool TryIsAutomationSignalPublished(
        string name,
        out bool published,
        out string error)
    {
        published = false;
        error = "automation signals require ACDREAM_AUTOMATION_ARTIFACT_DIR";
        return false;
    }
}

public sealed class RetailUiAutomationScriptRunner : IDisposable
{
    private readonly RetailUiAutomationProbe _probe;
    private readonly Action<string> _log;
    private readonly Action<string>? _submitCommand;
    private readonly Func<InputAction, bool>? _pressInput;
    private readonly Func<InputAction, bool, bool>? _setInputHeld;
    private readonly IRetailUiAutomationRuntime? _runtime;
    private readonly Action<float, float>? _queueMouseLookDelta;
    private readonly List<ScriptCommand> _commands = new();
    private readonly HashSet<InputAction> _heldInputs = new();
    private readonly bool _dumpOnStart;
    private readonly string? _loadError;
    private IRetailUiAutomationCheckpoint? _checkpoint;
    private int _checkpointCommandIndex = -1;
    private int _index;
    private int _activeIndex = -1;
    private double _commandElapsedMs;
    private double _commandElapsedCompensationMs;
    private bool _started;
    private bool _completed;
    private bool _disposed;

    public RetailUiAutomationScriptRunner(
        RetailUiAutomationProbe probe,
        string? scriptPath,
        bool dumpOnStart,
        Action<string>? log = null,
        Action<string>? submitCommand = null,
        Func<InputAction, bool>? pressInput = null,
        Func<InputAction, bool, bool>? setInputHeld = null,
        IRetailUiAutomationRuntime? runtime = null,
        Action<float, float>? queueMouseLookDelta = null)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _log = log ?? (_ => { });
        _submitCommand = submitCommand;
        _pressInput = pressInput;
        _setInputHeld = setInputHeld;
        _runtime = runtime;
        _queueMouseLookDelta = queueMouseLookDelta;
        _dumpOnStart = dumpOnStart;

        if (!string.IsNullOrWhiteSpace(scriptPath))
        {
            try
            {
                int lineNumber = 0;
                foreach (var raw in File.ReadAllLines(scriptPath))
                {
                    lineNumber++;
                    var line = StripComment(raw).Trim();
                    if (line.Length == 0) continue;
                    _commands.Add(new ScriptCommand(lineNumber, line, Split(line)));
                }
            }
            catch (Exception ex)
            {
                _loadError = $"failed to load UI probe script '{scriptPath}': {ex.Message}";
            }
        }
    }

    public bool Completed => _completed;

    public void Tick(double deltaSeconds)
    {
        if (_completed || _disposed) return;

        // Preserve sub-millisecond frame deltas. Rounding every uncapped frame
        // up to one millisecond makes script time run several times faster than
        // wall/game-loop time in light scenes and portal space.
        if (double.IsFinite(deltaSeconds) && deltaSeconds > 0d)
        {
            double addMs = deltaSeconds * 1000d;
            double adjustedMs = addMs - _commandElapsedCompensationMs;
            double nextElapsedMs = _commandElapsedMs + adjustedMs;
            double nextCompensationMs = (nextElapsedMs - _commandElapsedMs) - adjustedMs;
            if (_activeIndex == _index &&
                double.IsFinite(addMs) &&
                double.IsFinite(nextElapsedMs) &&
                double.IsFinite(nextCompensationMs))
            {
                _commandElapsedMs = nextElapsedMs;
                _commandElapsedCompensationMs = nextCompensationMs;
            }
        }

        if (!_started)
        {
            _started = true;
            if (_loadError is not null)
            {
                _log(_loadError);
                _completed = true;
                return;
            }

            if (_dumpOnStart)
                _log(_probe.DumpText());

            if (_commands.Count == 0)
            {
                _completed = true;
                return;
            }

            _log($"running {_commands.Count} UI probe command(s)");
        }

        int guard = 0;
        while (!_completed && _index < _commands.Count && guard++ < 8)
        {
            if (_activeIndex != _index)
            {
                _activeIndex = _index;
                _commandElapsedMs = 0d;
                _commandElapsedCompensationMs = 0d;
            }

            var command = _commands[_index];
            bool finished = Execute(command);
            if (!finished) break;
            bool yieldAfterResize = command.Parts.Length > 0
                && string.Equals(
                    command.Parts[0],
                    "resize",
                    StringComparison.OrdinalIgnoreCase);
            _index++;
            _activeIndex = -1;
            if (yieldAfterResize)
                break;
        }

        if (_index >= _commands.Count && !_completed)
        {
            ReleaseHeldInputs();
            _completed = true;
            _log("UI probe script complete");
        }
    }

    private bool Execute(ScriptCommand command)
    {
        var p = command.Parts;
        if (p.Length == 0) return true;

        string verb = p[0].ToLowerInvariant();
        return verb switch
        {
            "dump" => DoDump(),
            "click" => DoClick(command),
            "hover" => DoHover(command),
            "mousemove" => DoMouseMove(command),
            "doubleclick" => DoDoubleClick(command),
            "drag" => DoDrag(command),
            "wait" => DoWait(command),
            "sleep" => DoSleep(command),
            "assert" => DoAssert(command),
            "command" => DoCommand(command),
            "input" => DoInput(command),
            "mouselook" => DoMouseLook(command),
            "checkpoint" => DoCheckpoint(command),
            "renderpack" => DoRenderPack(command),
            "resize" => DoResize(command),
            "screenshot" => DoScreenshot(command),
            "close-client" => DoCloseClient(command),
            _ => Stop(command, $"unknown command '{p[0]}'"),
        };
    }

    private bool DoDump()
    {
        _log(_probe.DumpText());
        return true;
    }

    private bool DoClick(ScriptCommand command)
    {
        var p = command.Parts;
        if (p.Length < 3) return Stop(command, "usage: click element <datId> | click item <guid> [source] | click at <x> <y>");
        string target = p[1].ToLowerInvariant();
        if (target == "element")
        {
            if (!TryParseUInt(p[2], out uint datId)) return Stop(command, $"bad element id '{p[2]}'");
            return _probe.ClickElement(datId) || Stop(command, "click element failed");
        }
        if (target == "item")
        {
            if (!TryParseUInt(p[2], out uint itemGuid)) return Stop(command, $"bad item guid '{p[2]}'");
            return _probe.ClickItem(itemGuid, ParseSource(p, 3)) || Stop(command, "click item failed");
        }
        if (target == "at")
        {
            if (p.Length < 4
                || !TryParseInt(p[2], out int x)
                || !TryParseInt(p[3], out int y))
                return Stop(command, "usage: click at <x> <y>");
            return _probe.ClickAtPoint(x, y) || Stop(command, "click at failed");
        }
        return Stop(command, "usage: click element <datId> | click item <guid> [source] | click at <x> <y>");
    }

    private bool DoHover(ScriptCommand command)
    {
        var p = command.Parts;
        if (p.Length < 3 || !string.Equals(p[1], "element", StringComparison.OrdinalIgnoreCase))
            return Stop(command, "usage: hover element <datId>");
        if (!TryParseUInt(p[2], out uint datId)) return Stop(command, $"bad element id '{p[2]}'");
        return _probe.HoverElement(datId) || Stop(command, "hover element failed");
    }

    private bool DoMouseMove(ScriptCommand command)
    {
        var p = command.Parts;
        if (p.Length != 3)
            return Stop(command, "usage: mousemove <x> <y>");
        if (!TryParseInt(p[1], out int x)) return Stop(command, $"bad x '{p[1]}'");
        if (!TryParseInt(p[2], out int y)) return Stop(command, $"bad y '{p[2]}'");
        _probe.MoveMouse(x, y);
        return true;
    }

    private bool DoDoubleClick(ScriptCommand command)
    {
        var p = command.Parts;
        if (p.Length < 3 || !string.Equals(p[1], "item", StringComparison.OrdinalIgnoreCase))
            return Stop(command, "usage: doubleclick item <guid> [source]");
        if (!TryParseUInt(p[2], out uint itemGuid)) return Stop(command, $"bad item guid '{p[2]}'");
        return _probe.DoubleClickItem(itemGuid, ParseSource(p, 3)) || Stop(command, "doubleclick item failed");
    }

    private bool DoDrag(ScriptCommand command)
    {
        var p = command.Parts;
        if (p.Length >= 6
            && string.Equals(p[1], "at", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryParseInt(p[2], out int x1) || !TryParseInt(p[3], out int y1)
                || !TryParseInt(p[4], out int x2) || !TryParseInt(p[5], out int y2))
                return Stop(command, "usage: drag at <x1> <y1> <x2> <y2>");
            return _probe.DragAtPoint(x1, y1, x2, y2)
                || Stop(command, "drag at failed");
        }
        if (p.Length < 5 || !string.Equals(p[1], "item", StringComparison.OrdinalIgnoreCase))
            return Stop(command, "usage: drag item <guid> element <datId> | drag item <guid> item <guid> | drag item <guid> outside <x> <y> | drag at <x1> <y1> <x2> <y2>");
        if (!TryParseUInt(p[2], out uint sourceGuid)) return Stop(command, $"bad item guid '{p[2]}'");

        string target = p[3].ToLowerInvariant();
        if (target == "element")
        {
            if (!TryParseUInt(p[4], out uint datId)) return Stop(command, $"bad element id '{p[4]}'");
            return _probe.DragItemToElement(sourceGuid, datId, ParseSource(p, 5))
                || Stop(command, "drag item to element failed");
        }

        if (target == "item")
        {
            if (!TryParseUInt(p[4], out uint targetGuid)) return Stop(command, $"bad target item guid '{p[4]}'");
            return _probe.DragItemToItem(sourceGuid, targetGuid, ParseSource(p, 5))
                || Stop(command, "drag item to item failed");
        }

        if (target == "outside")
        {
            if (p.Length < 6) return Stop(command, "usage: drag item <guid> outside <x> <y> [source]");
            if (!TryParseInt(p[4], out int x)) return Stop(command, $"bad x coordinate '{p[4]}'");
            if (!TryParseInt(p[5], out int y)) return Stop(command, $"bad y coordinate '{p[5]}'");
            return _probe.DragItemOutside(sourceGuid, x, y, ParseSource(p, 6))
                || Stop(command, "drag item outside failed");
        }

        return Stop(command, "usage: drag item <guid> element/item/outside ...");
    }

    private bool DoWait(ScriptCommand command)
    {
        var p = command.Parts;
        if (p.Length < 2) return Stop(command, "usage: wait item|element|ms|world-ready|world-visible|materialized|render-pack|render-pack-samples|framebuffer|signal ...");

        string target = p[1].ToLowerInvariant();
        if (target == "item")
        {
            if (p.Length < 3 || !TryParseUInt(p[2], out uint itemGuid)) return Stop(command, "usage: wait item <guid> [source] [timeoutMs]");
            if (_probe.FindByItemId(itemGuid, ParseSource(p, 3)) is not null) return true;
            return WaitOrTimeout(command, TimeoutMs(p, 3, 10000), $"item 0x{itemGuid:X8}");
        }

        if (target == "element")
        {
            if (p.Length < 3 || !TryParseUInt(p[2], out uint datId)) return Stop(command, "usage: wait element <datId> [timeoutMs]");
            if (_probe.FindByDatElementId(datId) is not null) return true;
            return WaitOrTimeout(command, TimeoutMs(p, 3, 10000), $"element 0x{datId:X8}");
        }

        if (target == "ms")
            return DoSleep(command);

        if (target == "world-ready")
        {
            if (_runtime is null) return Stop(command, "world lifecycle automation is unavailable");
            if (_runtime.IsWorldReady) return true;
            return WaitOrTimeout(command, TimeoutMs(p, 2, 60000), "world readiness");
        }

        if (target == "world-visible")
        {
            if (_runtime is null) return Stop(command, "world lifecycle automation is unavailable");
            if (_runtime.IsWorldViewportVisible) return true;
            return WaitOrTimeout(command, TimeoutMs(p, 2, 60000), "normal world viewport");
        }

        if (target == "materialized")
        {
            if (_runtime is null) return Stop(command, "world lifecycle automation is unavailable");
            if (p.Length < 3 || !TryParseInt(p[2], out int occurrence) || occurrence <= 0)
                return Stop(command, "usage: wait materialized <occurrence> [timeoutMs]");
            if (_runtime.PortalMaterializationCount >= occurrence) return true;
            return WaitOrTimeout(command, TimeoutMs(p, 3, 60000), $"portal materialization {occurrence}");
        }

        if (target == "render-pack-samples")
        {
            if (_runtime is null)
                return Stop(command, "render-pack performance automation is unavailable");
            if (p.Length < 3
                || !TryParseInt(p[2], out int required)
                || required <= 0)
            {
                return Stop(
                    command,
                    "usage: wait render-pack-samples <count> [timeoutMs]");
            }
            if (_runtime.RenderPackPerformanceSampleCount >= required)
                return true;
            if (_runtime.RenderPackFailedToRetail)
                return true;
            return WaitOrTimeout(
                command,
                TimeoutMs(p, 3, 300000),
                $"{required} render-pack performance samples");
        }

        if (target == "render-pack")
        {
            if (_runtime is null)
                return Stop(command, "render-pack selection automation is unavailable");
            if (p.Length < 3 || !TryNormalizeRenderPackPreset(p[2], out string preset))
            {
                return Stop(
                    command,
                    "usage: wait render-pack retail|low|medium|high|auto [timeoutMs]");
            }

            RetailUiAutomationRenderPackStatus status = _runtime.RenderPackStatus;
            bool expectRetail = string.Equals(
                preset,
                "retail",
                StringComparison.Ordinal);
            if (expectRetail
                && status.State == RetailUiAutomationRenderPackState.Retail
                && string.Equals(status.PackId, "retail", StringComparison.OrdinalIgnoreCase)
                && string.Equals(status.PresetId, "off", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (!expectRetail
                && status.State == RetailUiAutomationRenderPackState.Active
                && string.Equals(
                    status.PackId,
                    "acdream.atmospheric",
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(status.PresetId, preset, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (status.State == RetailUiAutomationRenderPackState.FailedToRetail)
            {
                return Stop(
                    command,
                    $"render pack '{preset}' failed to retail: "
                    + (status.FailureReason ?? "no failure reason was published"));
            }
            return WaitOrTimeout(
                command,
                TimeoutMs(p, 3, 90000),
                $"render pack '{preset}' activation");
        }

        if (target == "framebuffer")
        {
            if (_runtime is null)
                return Stop(command, "framebuffer resize automation is unavailable");
            if (p.Length < 4
                || !TryParseInt(p[2], out int width)
                || !TryParseInt(p[3], out int height)
                || width <= 0
                || height <= 0)
            {
                return Stop(
                    command,
                    "usage: wait framebuffer <width> <height> [timeoutMs]");
            }
            if (_runtime.FramebufferWidth == width
                && _runtime.FramebufferHeight == height)
            {
                return true;
            }
            return WaitOrTimeout(
                command,
                TimeoutMs(p, 4, 30000),
                $"framebuffer {width}x{height}");
        }

        if (target == "signal")
        {
            if (_runtime is null)
                return Stop(command, "automation signal runtime is unavailable");
            if (p.Length < 3)
                return Stop(command, "usage: wait signal <name> [timeoutMs]");
            if (!_runtime.TryIsAutomationSignalPublished(
                    p[2],
                    out bool published,
                    out string error))
            {
                return Stop(command, error);
            }
            if (published)
                return true;
            return WaitOrTimeout(
                command,
                TimeoutMs(p, 3, 120000),
                $"automation signal '{p[2]}'");
        }

        return Stop(command, "usage: wait item|element|ms|world-ready|world-visible|materialized|render-pack|render-pack-samples|framebuffer|signal ...");
    }

    private bool DoRenderPack(ScriptCommand command)
    {
        if (_runtime is null)
            return Stop(command, "render-pack automation is unavailable");
        var p = command.Parts;
        if (p.Length == 2
            && string.Equals(p[1], "reset-performance", StringComparison.OrdinalIgnoreCase))
        {
            return _runtime.TryResetRenderPackPerformance(out string error)
                || Stop(command, error);
        }
        if (p.Length == 3
            && string.Equals(p[1], "select", StringComparison.OrdinalIgnoreCase)
            && TryNormalizeRenderPackPreset(p[2], out string preset))
        {
            return _runtime.TrySelectRenderPack(preset, out string error)
                || Stop(command, error);
        }
        if (p.Length == 2
            && string.Equals(p[1], "disable", StringComparison.OrdinalIgnoreCase))
        {
            return _runtime.TryDisableRenderPack(out string error)
                || Stop(command, error);
        }
        if (p.Length == 2
            && string.Equals(p[1], "reenable", StringComparison.OrdinalIgnoreCase))
        {
            return _runtime.TryReenableRenderPack(out string error)
                || Stop(command, error);
        }
        return Stop(
            command,
            "usage: renderpack reset-performance | renderpack select retail|low|medium|high|auto | renderpack disable | renderpack reenable");
    }

    private bool DoResize(ScriptCommand command)
    {
        if (_runtime is null)
            return Stop(command, "framebuffer resize automation is unavailable");
        var p = command.Parts;
        if (p.Length != 3
            || !TryParseInt(p[1], out int width)
            || !TryParseInt(p[2], out int height)
            || width <= 0
            || height <= 0)
        {
            return Stop(command, "usage: resize <width> <height>");
        }
        return _runtime.TryResizeFramebuffer(width, height, out string error)
            || Stop(command, error);
    }

    private static bool TryNormalizeRenderPackPreset(
        string value,
        out string preset)
    {
        preset = value.ToLowerInvariant();
        if (preset == "off")
            preset = "retail";
        return preset is "retail" or "low" or "medium" or "high" or "auto";
    }

    private bool DoSleep(ScriptCommand command)
    {
        var p = command.Parts;
        string value = p.Length >= 3 && string.Equals(p[0], "wait", StringComparison.OrdinalIgnoreCase) ? p[2]
            : p.Length >= 2 ? p[1] : "";
        if (!TryParseInt(value, out int ms) || ms < 0)
            return Stop(command, "usage: sleep <milliseconds> | wait ms <milliseconds>");
        return HasElapsed(ms);
    }

    private bool DoAssert(ScriptCommand command)
    {
        var p = command.Parts;
        if (p.Length < 5 || !string.Equals(p[1], "item", StringComparison.OrdinalIgnoreCase))
            return Stop(command, "usage: assert item <guid> equip|container|slot <value>");
        if (!TryParseUInt(p[2], out uint itemGuid)) return Stop(command, $"bad item guid '{p[2]}'");

        string kind = p[3].ToLowerInvariant();
        RetailUiProbeAssertion result;
        if (kind == "equip")
        {
            if (!TryParseUInt(p[4], out uint mask)) return Stop(command, $"bad equip mask '{p[4]}'");
            result = _probe.AssertItem(itemGuid, equippedLocation: (EquipMask)mask);
        }
        else if (kind == "container")
        {
            if (!TryParseUInt(p[4], out uint containerId)) return Stop(command, $"bad container id '{p[4]}'");
            result = _probe.AssertItem(itemGuid, containerId: containerId);
        }
        else if (kind == "slot")
        {
            if (!TryParseInt(p[4], out int slot)) return Stop(command, $"bad slot '{p[4]}'");
            result = _probe.AssertItem(itemGuid, slot: slot);
        }
        else
        {
            return Stop(command, "usage: assert item <guid> equip|container|slot <value>");
        }

        return result.Success || Stop(command, result.Message);
    }

    private bool DoCommand(ScriptCommand command)
    {
        string text = command.Text[command.Parts[0].Length..].Trim();
        if (text.Length == 0)
            return Stop(command, "usage: command <chat-or-client-command>");
        if (_submitCommand is null)
            return Stop(command, "command submission is unavailable");

        _submitCommand(text);
        return true;
    }

    private bool DoInput(ScriptCommand command)
    {
        var p = command.Parts;
        if (p.Length != 3)
            return Stop(command, "usage: input press|down|up <InputAction>");
        if (!Enum.TryParse(p[2], ignoreCase: true, out InputAction action)
            || action == InputAction.None
            || !Enum.IsDefined(action))
            return Stop(command, $"unknown input action '{p[2]}'");

        switch (p[1].ToLowerInvariant())
        {
            case "press":
                if (_pressInput is null)
                    return Stop(command, "input press injection is unavailable");
                return _pressInput(action)
                    || Stop(command, $"input press {action} was captured");

            case "down":
                if (_setInputHeld is null)
                    return Stop(command, "input held injection is unavailable");
                if (_heldInputs.Contains(action))
                    return Stop(command, $"input {action} is already down");
                if (!_setInputHeld(action, true))
                    return Stop(command, $"input down {action} was captured");
                _heldInputs.Add(action);
                return true;

            case "up":
                if (_setInputHeld is null)
                    return Stop(command, "input held injection is unavailable");
                if (!_heldInputs.Contains(action))
                    return Stop(command, $"input {action} is not down");
                if (!_setInputHeld(action, false))
                    return Stop(command, $"input up {action} failed");
                _heldInputs.Remove(action);
                return true;

            default:
                return Stop(command, "usage: input press|down|up <InputAction>");
        }
    }

    private bool DoMouseLook(ScriptCommand command)
    {
        var p = command.Parts;
        if (p.Length != 3
            || !TryParseFloat(p[1], out float dx)
            || !TryParseFloat(p[2], out float dy))
            return Stop(command, "usage: mouselook <dx> <dy>");
        if (_queueMouseLookDelta is null)
            return Stop(command, "mouselook injection is unavailable");
        _queueMouseLookDelta(dx, dy);
        return true;
    }

    private bool DoCheckpoint(ScriptCommand command)
    {
        if (command.Parts.Length != 2)
            return Stop(command, "usage: checkpoint <name>");
        if (_runtime is null)
            return Stop(command, "world lifecycle automation is unavailable");

        if (_checkpoint is null)
        {
            if (!_runtime.TryRequestCheckpoint(
                    command.Parts[1],
                    out IRetailUiAutomationCheckpoint? checkpoint,
                    out string error))
            {
                return Stop(command, error);
            }

            if (checkpoint is null)
            {
                return Stop(
                    command,
                    "world lifecycle automation accepted a checkpoint without returning its acknowledgement");
            }

            _checkpoint = checkpoint;
            _checkpointCommandIndex = _index;
        }

        if (_checkpointCommandIndex != _index)
        {
            return Stop(
                command,
                "checkpoint acknowledgement belongs to a different script command");
        }

        RetailUiAutomationCheckpointStatus status = _checkpoint.Status;
        if (status == RetailUiAutomationCheckpointStatus.Pending)
            return false;

        string? terminalError = _checkpoint.Error;
        _checkpoint = null;
        _checkpointCommandIndex = -1;
        return status == RetailUiAutomationCheckpointStatus.Succeeded
            || Stop(
                command,
                terminalError
                ?? $"checkpoint '{command.Parts[1]}' ended with {status}");
    }

    private bool DoScreenshot(ScriptCommand command)
    {
        if (command.Parts.Length is < 2 or > 3)
            return Stop(command, "usage: screenshot <name> [timeoutMs]");
        if (_runtime is null)
            return Stop(command, "world lifecycle automation is unavailable");

        string name = command.Parts[1];
        if (!_runtime.TryRequestScreenshot(name, out string error))
            return Stop(command, error);
        if (_runtime.IsScreenshotComplete(name))
            return true;
        return WaitOrTimeout(command, TimeoutMs(command.Parts, 2, 10000), $"screenshot '{name}'");
    }

    private bool DoCloseClient(ScriptCommand command)
    {
        if (command.Parts.Length != 1)
            return Stop(command, "usage: close-client");
        if (_runtime is null)
            return Stop(command, "client-close automation is unavailable");
        return _runtime.TryRequestClientClose(out string error)
            || Stop(command, error);
    }

    private bool WaitOrTimeout(ScriptCommand command, int timeoutMs, string label)
    {
        if (!HasExceeded(timeoutMs)) return false;
        return Stop(command, $"timed out waiting for {label}");
    }

    private bool HasElapsed(int durationMs)
    {
        double elapsedMs = _commandElapsedMs;
        if (!double.IsFinite(elapsedMs)) return false;
        if (elapsedMs >= durationMs) return true;

        // Repeated fractional frame deltas can land a few representable
        // doubles below an exact integer-millisecond deadline (for example,
        // sixty 1/60-second frames). Treat only machine-scale drift as equal;
        // this tolerance is far below any observable script or frame interval.
        return durationMs - elapsedMs <= TimingToleranceMs(durationMs);
    }

    private bool HasExceeded(int durationMs)
    {
        double elapsedMs = _commandElapsedMs;
        if (!double.IsFinite(elapsedMs) || elapsedMs <= durationMs) return false;
        return elapsedMs - durationMs > TimingToleranceMs(durationMs);
    }

    private static double TimingToleranceMs(int durationMs)
        => Math.Max(1e-9d, Math.Abs(durationMs) * 1e-12d);

    private bool Stop(ScriptCommand command, string message)
    {
        _log($"line {command.LineNumber}: {message}; command: {command.Text}");
        ReleaseHeldInputs();
        _completed = true;
        return false;
    }

    private void ReleaseHeldInputs()
    {
        if (_heldInputs.Count == 0 || _setInputHeld is null)
            return;

        var snapshot = new InputAction[_heldInputs.Count];
        _heldInputs.CopyTo(snapshot);
        _heldInputs.Clear();
        foreach (InputAction action in snapshot)
            _setInputHeld(action, false);
    }

    public void Dispose()
    {
        if (_disposed) return;
        if (_checkpoint is not null)
        {
            _runtime?.CancelCheckpoint(_checkpoint);
            _checkpoint = null;
            _checkpointCommandIndex = -1;
        }
        ReleaseHeldInputs();
        _disposed = true;
    }

    private static string StripComment(string line)
    {
        int hash = line.IndexOf('#');
        int slashes = line.IndexOf("//", StringComparison.Ordinal);
        int cut = -1;
        if (hash >= 0) cut = hash;
        if (slashes >= 0) cut = cut >= 0 ? Math.Min(cut, slashes) : slashes;
        return cut >= 0 ? line[..cut] : line;
    }

    private static string[] Split(string line)
        => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static ItemDragSource? ParseSource(string[] parts, int start)
    {
        for (int i = start; i < parts.Length; i++)
            if (TryParseSource(parts[i], out var source))
                return source;
        return null;
    }

    private static bool TryParseSource(string value, out ItemDragSource source)
    {
        switch (value.ToLowerInvariant())
        {
            case "inventory":
            case "pack":
            case "backpack":
                source = ItemDragSource.Inventory;
                return true;
            case "shortcut":
            case "shortcutbar":
            case "toolbar":
                source = ItemDragSource.ShortcutBar;
                return true;
            case "equipment":
            case "equip":
            case "paperdoll":
                source = ItemDragSource.Equipment;
                return true;
            case "ground":
                source = ItemDragSource.Ground;
                return true;
            default:
                source = default;
                return false;
        }
    }

    private static int TimeoutMs(string[] parts, int start, int defaultMs)
    {
        for (int i = start; i < parts.Length; i++)
            if (TryParseInt(parts[i], out int ms) && ms >= 0)
                return ms;
        return defaultMs;
    }

    private static bool TryParseUInt(string value, out uint parsed)
    {
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return uint.TryParse(value[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out parsed);
        return uint.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed);
    }

    private static bool TryParseInt(string value, out int parsed)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed);

    private static bool TryParseFloat(string value, out float parsed)
        => float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed);

    private readonly record struct ScriptCommand(int LineNumber, string Text, string[] Parts);
}
