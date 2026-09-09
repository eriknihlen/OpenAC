using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text;
using AcDream.Core.Items;

namespace AcDream.App.UI.Testing;

public sealed record RetailUiProbeElement(
    int Index,
    int Depth,
    string Path,
    string TypeName,
    string? Name,
    uint EventId,
    uint DatElementId,
    float X,
    float Y,
    float Width,
    float Height,
    bool Visible,
    bool Enabled,
    uint ItemId,
    int SlotIndex,
    ItemDragSource? SourceKind)
{
    public int CenterX => (int)MathF.Round(X + Width * 0.5f);
    public int CenterY => (int)MathF.Round(Y + Height * 0.5f);
}

public sealed record RetailUiProbeAssertion(bool Success, string Message);

public sealed class RetailUiAutomationProbe
{
    private readonly UiRoot _root;
    private readonly ClientObjectTable _objects;
    private readonly Action<string>? _log;
    private long _nowMs = 1;

    public RetailUiAutomationProbe(
        UiRoot root,
        ClientObjectTable objects,
        Action<string>? log = null)
    {
        _root = root ?? throw new ArgumentNullException(nameof(root));
        _objects = objects ?? throw new ArgumentNullException(nameof(objects));
        _log = log;
    }

    public IReadOnlyList<RetailUiProbeElement> Snapshot(bool visibleOnly = true)
    {
        var rows = new List<RetailUiProbeElement>();
        Walk(_root, "root", depth: 0, ancestorsVisible: true, visibleOnly, rows);
        return rows;
    }

    public string DumpText(bool visibleOnly = true)
    {
        var sb = new StringBuilder();
        foreach (var e in Snapshot(visibleOnly))
        {
            sb.Append('[').Append(e.Index.ToString(CultureInfo.InvariantCulture)).Append("] ");
            sb.Append(new string(' ', Math.Max(0, e.Depth) * 2));
            sb.Append(e.TypeName);
            if (e.DatElementId != 0) sb.Append(" dat=0x").Append(e.DatElementId.ToString("X8", CultureInfo.InvariantCulture));
            if (e.EventId != 0) sb.Append(" event=0x").Append(e.EventId.ToString("X8", CultureInfo.InvariantCulture));
            if (!string.IsNullOrEmpty(e.Name)) sb.Append(" name=").Append(e.Name);
            sb.Append(" rect=(").Append(F(e.X)).Append(',').Append(F(e.Y)).Append(',')
              .Append(F(e.Width)).Append(',').Append(F(e.Height)).Append(')');
            if (!e.Visible) sb.Append(" hidden");
            if (!e.Enabled) sb.Append(" disabled");
            if (e.ItemId != 0)
            {
                sb.Append(" item=0x").Append(e.ItemId.ToString("X8", CultureInfo.InvariantCulture));
                sb.Append(" slot=").Append(e.SlotIndex.ToString(CultureInfo.InvariantCulture));
                if (e.SourceKind is { } source) sb.Append(" source=").Append(source);
            }
            sb.Append(" path=").Append(e.Path);
            sb.AppendLine();
        }
        return sb.ToString();
    }

    public RetailUiProbeElement? FindByDatElementId(uint datElementId, bool visibleOnly = true)
    {
        foreach (var e in Snapshot(visibleOnly))
            if (e.DatElementId == datElementId && HasArea(e))
                return e;
        return null;
    }

    public IReadOnlyList<RetailUiProbeElement> FindAllByDatElementId(uint datElementId, bool visibleOnly = true)
    {
        var matches = new List<RetailUiProbeElement>();
        foreach (var e in Snapshot(visibleOnly))
            if (e.DatElementId == datElementId)
                matches.Add(e);
        return matches;
    }

    public RetailUiProbeElement? FindByItemId(
        uint itemGuid,
        ItemDragSource? sourceKind = null,
        bool visibleOnly = true)
    {
        if (itemGuid == 0) return null;
        foreach (var e in Snapshot(visibleOnly))
        {
            if (e.ItemId != itemGuid) continue;
            if (sourceKind is { } source && e.SourceKind != source) continue;
            if (!HasArea(e)) continue;
            return e;
        }
        return null;
    }

    public bool ClickElement(uint datElementId)
    {
        var target = FindByDatElementId(datElementId);
        if (target is null) return Fail($"element 0x{datElementId:X8} not found");
        ClickAt(target.CenterX, target.CenterY);
        return true;
    }

    public bool ClickAtPoint(int x, int y)
    {
        ClickAt(x, y);
        return true;
    }

    public bool DragAtPoint(int startX, int startY, int endX, int endY)
    {
        DragAt(startX, startY, endX, endY);
        return true;
    }

    public bool HoverElement(uint datElementId)
    {
        var target = FindByDatElementId(datElementId);
        if (target is null) return Fail($"element 0x{datElementId:X8} not found");
        (int x, int y) = CanvasToWindow((int)target.CenterX, (int)target.CenterY);
        _root.OnMouseMove(x, y);
        return true;
    }

    public void MoveMouse(int x, int y) => _root.OnMouseMove(x, y);

    public bool ClickItem(uint itemGuid, ItemDragSource? sourceKind = null)
    {
        var target = FindByItemId(itemGuid, sourceKind);
        if (target is null) return Fail($"item 0x{itemGuid:X8} not found");
        ClickAt(target.CenterX, target.CenterY);
        return true;
    }

    public bool DoubleClickItem(uint itemGuid, ItemDragSource? sourceKind = null)
    {
        var target = FindByItemId(itemGuid, sourceKind);
        if (target is null) return Fail($"item 0x{itemGuid:X8} not found");
        ClickAt(target.CenterX, target.CenterY);
        Advance(80);
        ClickAt(target.CenterX, target.CenterY);
        return true;
    }

    public bool DragItemToElement(uint itemGuid, uint datElementId, ItemDragSource? sourceKind = null)
    {
        var source = FindByItemId(itemGuid, sourceKind);
        if (source is null) return Fail($"drag source item 0x{itemGuid:X8} not found");
        var target = FindByDatElementId(datElementId);
        if (target is null) return Fail($"drop target element 0x{datElementId:X8} not found");
        DragAt(source.CenterX, source.CenterY, target.CenterX, target.CenterY);
        return true;
    }

    public bool DragItemToItem(uint sourceGuid, uint targetGuid, ItemDragSource? sourceKind = null)
    {
        var source = FindByItemId(sourceGuid, sourceKind);
        if (source is null) return Fail($"drag source item 0x{sourceGuid:X8} not found");
        var target = FindByItemId(targetGuid);
        if (target is null) return Fail($"drop target item 0x{targetGuid:X8} not found");
        DragAt(source.CenterX, source.CenterY, target.CenterX, target.CenterY);
        return true;
    }

    public bool DragItemOutside(uint itemGuid, int x, int y, ItemDragSource? sourceKind = null)
    {
        var source = FindByItemId(itemGuid, sourceKind);
        if (source is null) return Fail($"drag source item 0x{itemGuid:X8} not found");
        DragAt(source.CenterX, source.CenterY, x, y);
        return true;
    }

    public RetailUiProbeAssertion AssertItem(
        uint itemGuid,
        EquipMask? equippedLocation = null,
        uint? containerId = null,
        int? slot = null)
    {
        var item = _objects.Get(itemGuid);
        if (item is null)
            return Result(false, $"item 0x{itemGuid:X8} missing from object table");

        if (equippedLocation is { } equip && item.CurrentlyEquippedLocation != equip)
            return Result(false,
                $"item 0x{itemGuid:X8} equip expected 0x{((uint)equip):X8}, got 0x{((uint)item.CurrentlyEquippedLocation):X8}");

        if (containerId is { } container && item.ContainerId != container)
            return Result(false,
                $"item 0x{itemGuid:X8} container expected 0x{container:X8}, got 0x{item.ContainerId:X8}");

        if (slot is { } slotValue && item.ContainerSlot != slotValue)
            return Result(false,
                $"item 0x{itemGuid:X8} slot expected {slotValue}, got {item.ContainerSlot}");

        return Result(true, $"item 0x{itemGuid:X8} matched object-table assertion");
    }

    private void Walk(
        UiElement element,
        string path,
        int depth,
        bool ancestorsVisible,
        bool visibleOnly,
        List<RetailUiProbeElement> rows)
    {
        bool effectiveVisible = ancestorsVisible && element.Visible;
        if (visibleOnly && !effectiveVisible) return;

        if (element is UiItemList list)
            list.LayoutCells();

        var pos = element.ScreenPosition;
        uint itemId = 0;
        int slotIndex = -1;
        ItemDragSource? sourceKind = null;
        if (element is UiItemSlot slot)
        {
            itemId = slot.ItemId;
            slotIndex = slot.SlotIndex;
            sourceKind = slot.SourceKind;
        }

        rows.Add(new RetailUiProbeElement(
            rows.Count,
            depth,
            path,
            element.GetType().Name,
            element.Name,
            element.EventId,
            element.DatElementId,
            pos.X,
            pos.Y,
            element.Width,
            element.Height,
            effectiveVisible,
            element.Enabled,
            itemId,
            slotIndex,
            sourceKind));

        var children = element.Children;
        for (int i = 0; i < children.Count; i++)
        {
            var child = children[i];
            var childPath = $"{path}/{child.GetType().Name}[{i}]";
            if (child.DatElementId != 0)
                childPath += $"#0x{child.DatElementId:X8}";
            Walk(child, childPath, depth + 1, effectiveVisible, visibleOnly, rows);
        }
    }

    private (int x, int y) CanvasToWindow(int x, int y)
    {
        Vector2 scale = _root.CanvasScale;
        return scale == Vector2.One
            ? (x, y)
            : ((int)MathF.Round(x * scale.X), (int)MathF.Round(y * scale.Y));
    }

    private void ClickAt(int x, int y)
    {
        (x, y) = CanvasToWindow(x, y);
        Advance(16);
        _root.OnMouseMove(x, y);
        _root.OnMouseDown(UiMouseButton.Left, x, y);
        Advance(50);
        _root.OnMouseUp(UiMouseButton.Left, x, y);
        Advance(16);
    }

    private void DragAt(int startX, int startY, int endX, int endY)
    {
        (startX, startY) = CanvasToWindow(startX, startY);
        (endX, endY) = CanvasToWindow(endX, endY);
        Advance(16);
        _root.OnMouseMove(startX, startY);
        _root.OnMouseDown(UiMouseButton.Left, startX, startY);
        Advance(16);
        _root.OnMouseMove(startX + 5, startY + 5);
        Advance(16);
        _root.OnMouseMove(endX, endY);
        Advance(16);
        _root.OnMouseUp(UiMouseButton.Left, endX, endY);
        Advance(16);
    }

    private void Advance(int ms)
    {
        _nowMs += ms;
        _root.Tick(ms / 1000.0, _nowMs);
    }

    private static bool HasArea(RetailUiProbeElement element)
        => element.Width > 0f && element.Height > 0f;

    private bool Fail(string message)
    {
        _log?.Invoke(message);
        return false;
    }

    private RetailUiProbeAssertion Result(bool success, string message)
    {
        _log?.Invoke(message);
        return new RetailUiProbeAssertion(success, message);
    }

    private static string F(float value)
        => value.ToString("0.##", CultureInfo.InvariantCulture);
}
