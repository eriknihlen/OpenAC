using System.Collections.Generic;
using System.Numerics;

namespace AcDream.App.Rendering;

public readonly record struct ClipViewSlice(
    int Slot, Vector4 NdcAabb, Vector4[] Planes, bool NothingVisible = false);

public sealed class ClipFrameAssembly
{
    public ClipFrame Frame { get; private set; } = null!;

    public Dictionary<uint, int> CellIdToSlot { get; } = new();

    public Dictionary<uint, int[]> CellIdToViewSlots { get; } = new();

    public Dictionary<uint, ClipViewSlice[]> CellIdToViewSlices { get; } = new();

    public ClipViewSlice[] OutsideViewSlices { get; private set; } = System.Array.Empty<ClipViewSlice>();

    public int OutdoorSlot { get; internal set; }
    public bool OutdoorVisible { get; internal set; }
    public bool HasOutsideView { get; internal set; }
    public Vector4 OutsideViewNdcAabb { get; internal set; }

    // Probe data.
    public int OutsidePlaneCount { get; internal set; }
    public Dictionary<uint, int> PerCellPlaneCounts { get; } = new();
    public int ScissorFallbacks { get; internal set; }

    private readonly Dictionary<int, Stack<ClipViewSlice[]>> _sliceArraysByLength = new();
    private readonly Dictionary<int, Stack<int[]>> _slotArraysByLength = new();
    internal List<ClipViewSlice> SliceScratch { get; } = new();
    internal int SliceArrayAllocationCount { get; private set; }
    internal int SlotArrayAllocationCount { get; private set; }
    internal const int MaxRetainedSliceItems = 4096;
    internal const int MaxRetainedSlotItems = 8192;
    internal const int MaxRetainedArraysPerPool = 128;
    internal int RetainedSliceItems { get; private set; }
    internal int RetainedSlotItems { get; private set; }
    internal int RetainedSliceArrays { get; private set; }
    internal int RetainedSlotArrays { get; private set; }

    internal void Reset(ClipFrame frame)
    {
        Frame = frame;
        foreach (ClipViewSlice[] slices in CellIdToViewSlices.Values)
            ReturnSlices(slices);
        foreach (int[] slots in CellIdToViewSlots.Values)
            ReturnSlots(slots);
        if (OutsideViewSlices.Length != 0)
            ReturnSlices(OutsideViewSlices);

        CellIdToSlot.Clear();
        CellIdToViewSlots.Clear();
        CellIdToViewSlices.Clear();
        PerCellPlaneCounts.Clear();
        OutsideViewSlices = System.Array.Empty<ClipViewSlice>();
        SliceScratch.Clear();
    }

    internal ClipViewSlice[] CopySlices(List<ClipViewSlice> source)
    {
        if (source.Count == 0)
            return System.Array.Empty<ClipViewSlice>();
        ClipViewSlice[] result = RentSlices(source.Count);
        source.CopyTo(result, 0);
        return result;
    }

    internal int[] CopySlots(ClipViewSlice[] slices)
    {
        if (slices.Length == 0)
            return System.Array.Empty<int>();
        int[] result = RentSlots(slices.Length);
        for (int i = 0; i < slices.Length; i++)
            result[i] = slices[i].Slot;
        return result;
    }

    internal void SetOutsideViewSlices(ClipViewSlice[] slices) => OutsideViewSlices = slices;

    internal void ReturnOutsideViewSlicesForReassembly()
    {
        if (OutsideViewSlices.Length != 0)
            ReturnSlices(OutsideViewSlices);
        OutsideViewSlices = System.Array.Empty<ClipViewSlice>();
    }

    private ClipViewSlice[] RentSlices(int length)
    {
        if (_sliceArraysByLength.TryGetValue(length, out Stack<ClipViewSlice[]>? pool)
            && pool.Count != 0)
        {
            ClipViewSlice[] result = pool.Pop();
            RetainedSliceItems -= result.Length;
            RetainedSliceArrays--;
            if (pool.Count == 0)
                _sliceArraysByLength.Remove(length);
            return result;
        }
        SliceArrayAllocationCount++;
        return new ClipViewSlice[length];
    }

    private int[] RentSlots(int length)
    {
        if (_slotArraysByLength.TryGetValue(length, out Stack<int[]>? pool)
            && pool.Count != 0)
        {
            int[] result = pool.Pop();
            RetainedSlotItems -= result.Length;
            RetainedSlotArrays--;
            if (pool.Count == 0)
                _slotArraysByLength.Remove(length);
            return result;
        }
        SlotArrayAllocationCount++;
        return new int[length];
    }

    private void ReturnSlices(ClipViewSlice[] array)
    {
        System.Array.Clear(array);
        if (array.Length > MaxRetainedSliceItems)
            return;
        while (RetainedSliceItems + array.Length > MaxRetainedSliceItems
               || RetainedSliceArrays >= MaxRetainedArraysPerPool)
        {
            if (!EvictOneSliceArray())
                break;
        }
        if (!_sliceArraysByLength.TryGetValue(array.Length, out Stack<ClipViewSlice[]>? pool))
        {
            pool = new Stack<ClipViewSlice[]>();
            _sliceArraysByLength.Add(array.Length, pool);
        }
        pool.Push(array);
        RetainedSliceItems += array.Length;
        RetainedSliceArrays++;
    }

    private void ReturnSlots(int[] array)
    {
        if (array.Length > MaxRetainedSlotItems)
            return;
        while (RetainedSlotItems + array.Length > MaxRetainedSlotItems
               || RetainedSlotArrays >= MaxRetainedArraysPerPool)
        {
            if (!EvictOneSlotArray())
                break;
        }
        if (!_slotArraysByLength.TryGetValue(array.Length, out Stack<int[]>? pool))
        {
            pool = new Stack<int[]>();
            _slotArraysByLength.Add(array.Length, pool);
        }
        pool.Push(array);
        RetainedSlotItems += array.Length;
        RetainedSlotArrays++;
    }

    private bool EvictOneSliceArray()
    {
        int selectedLength = -1;
        foreach ((int length, Stack<ClipViewSlice[]> pool) in _sliceArraysByLength)
        {
            if (pool.Count != 0 && length > selectedLength)
                selectedLength = length;
        }
        if (selectedLength < 0)
            return false;
        Stack<ClipViewSlice[]> selected = _sliceArraysByLength[selectedLength];
        ClipViewSlice[] evicted = selected.Pop();
        RetainedSliceItems -= evicted.Length;
        RetainedSliceArrays--;
        if (selected.Count == 0)
            _sliceArraysByLength.Remove(selectedLength);
        return true;
    }

    private bool EvictOneSlotArray()
    {
        int selectedLength = -1;
        foreach ((int length, Stack<int[]> pool) in _slotArraysByLength)
        {
            if (pool.Count != 0 && length > selectedLength)
                selectedLength = length;
        }
        if (selectedLength < 0)
            return false;
        Stack<int[]> selected = _slotArraysByLength[selectedLength];
        int[] evicted = selected.Pop();
        RetainedSlotItems -= evicted.Length;
        RetainedSlotArrays--;
        if (selected.Count == 0)
            _slotArraysByLength.Remove(selectedLength);
        return true;
    }
}

public static class ClipFrameAssembler
{
    public static ClipFrameAssembly BeginWalkFrame(
        ClipFrame frame,
        bool outdoorRoot,
        ClipFrameAssembly? reuseAssembly = null)
    {
        System.ArgumentNullException.ThrowIfNull(frame);
        frame.Reset();
        ClipFrameAssembly assembly = reuseAssembly ?? new ClipFrameAssembly();
        assembly.Reset(frame);

        if (outdoorRoot)
        {
            List<ClipViewSlice> slices = assembly.SliceScratch;
            slices.Clear();
            var fullScreen = new Vector4(-1f, -1f, 1f, 1f);
            slices.Add(new ClipViewSlice(0, fullScreen, System.Array.Empty<Vector4>()));
            assembly.SetOutsideViewSlices(assembly.CopySlices(slices));
            assembly.OutdoorSlot = 0;
            assembly.OutdoorVisible = true;
            assembly.HasOutsideView = true;
            assembly.OutsideViewNdcAabb = fullScreen;
            assembly.OutsidePlaneCount = 0;
            assembly.ScissorFallbacks = 1;
        }
        else
        {
            assembly.OutdoorSlot = 0;
            assembly.OutdoorVisible = false;
            assembly.HasOutsideView = false;
            assembly.OutsideViewNdcAabb = Vector4.Zero;
            assembly.OutsidePlaneCount = 0;
            assembly.ScissorFallbacks = 0;
        }

        return assembly;
    }

    public static void ReassembleOutsideViewFromWalk(
        ClipFrameAssembly assembly,
        Walk.WalkPortalView outsideView,
        float viewportWidth,
        float viewportHeight)
    {
        System.ArgumentNullException.ThrowIfNull(assembly);
        System.ArgumentNullException.ThrowIfNull(outsideView);
        if (viewportWidth <= 0f || viewportHeight <= 0f)
        {
            throw new System.ArgumentOutOfRangeException(
                nameof(viewportWidth),
                $"viewport {viewportWidth}x{viewportHeight} — the walk projected its "
                + "views through a real viewport; a non-positive extent here means the "
                + "caller handed a different frame's context (fail-loud rule).");
        }

        ClipFrame frame = assembly.Frame;
        int viewCount = outsideView.ViewCount;
        var polys = outsideView.View.Polys;
        var pool = outsideView.View.Vertices;
        if (polys.Count < viewCount)
        {
            throw new System.InvalidOperationException(
                $"walk outside_view holds {polys.Count} polys for ViewCount={viewCount} — "
                + "the view set's append bookkeeping desynchronized (fail-loud rule).");
        }

        assembly.ReturnOutsideViewSlicesForReassembly();

        List<ClipViewSlice> outsideSlicesList = assembly.SliceScratch;
        outsideSlicesList.Clear();
        int outsideMaxPlaneCount = 0;
        bool outsideHasScissorFallback = false;
        int scissorFallbacks = assembly.ScissorFallbacks;
        float unionMinX = float.MaxValue, unionMinY = float.MaxValue;
        float unionMaxX = float.MinValue, unionMaxY = float.MinValue;

        for (int v = 0; v < viewCount; v++)
        {
            Walk.WalkViewPoly walkPoly = polys[v];
            var vertices = new Vector2[walkPoly.VertexCount];
            for (int k = 0; k < walkPoly.VertexCount; k++)
            {
                Vector2 px = pool[walkPoly.VertexIndex + k].Point;
                vertices[k] = new Vector2(
                    px.X / viewportWidth * 2f - 1f,
                    1f - px.Y / viewportHeight * 2f);
            }
            var poly = new ViewPolygon(vertices);
            if (!poly.IsEmpty)
            {
                if (poly.MinX < unionMinX) unionMinX = poly.MinX;
                if (poly.MinY < unionMinY) unionMinY = poly.MinY;
                if (poly.MaxX > unionMaxX) unionMaxX = poly.MaxX;
                if (poly.MaxY > unionMaxY) unionMaxY = poly.MaxY;
            }

            bool appended = AppendOutsideSlice(
                frame,
                poly,
                outsideSlicesList,
                ref outsideMaxPlaneCount,
                ref outsideHasScissorFallback,
                ref scissorFallbacks);

            if (!appended)
            {
                outsideSlicesList.Add(
                    new ClipViewSlice(0, default, System.Array.Empty<Vector4>(), NothingVisible: true));
            }
        }

        ClipViewSlice[] outsideViewSlices = assembly.CopySlices(outsideSlicesList);
        bool outdoorVisible = outsideViewSlices.Length > 0;
        int outdoorSlot = outdoorVisible ? outsideViewSlices[0].Slot : 0;

        Vector4 outsideViewNdcAabb = outdoorVisible
            ? new Vector4(unionMinX, unionMinY, unionMaxX, unionMaxY)
            : Vector4.Zero;

        assembly.SetOutsideViewSlices(outsideViewSlices);
        assembly.OutdoorSlot = outdoorSlot;
        assembly.OutdoorVisible = outdoorVisible;
        assembly.HasOutsideView = outdoorVisible;
        assembly.OutsideViewNdcAabb = outsideViewNdcAabb;
        assembly.OutsidePlaneCount = outsideHasScissorFallback ? 0 : outsideMaxPlaneCount;
        assembly.ScissorFallbacks = scissorFallbacks;
    }

    private static bool AppendOutsideSlice(
        ClipFrame frame,
        in ViewPolygon poly,
        List<ClipViewSlice> outsideSlicesList,
        ref int maxPlaneCount,
        ref bool hasScissorFallback,
        ref int scissorFallbacks)
    {
        var cps = ClipPlaneSet.From(poly);
        if (cps.IsNothingVisible)
            return false;

        int slot;
        Vector4[] planes;
        if (cps.Count > 0)
        {
            planes = cps.PlaneArray;
            slot = frame.AppendSlot(planes);
            if (cps.Count > maxPlaneCount)
                maxPlaneCount = cps.Count;
        }
        else
        {
            planes = System.Array.Empty<Vector4>();
            slot = 0;
            hasScissorFallback = true;
            scissorFallbacks++;
        }

        outsideSlicesList.Add(new ClipViewSlice(slot, AabbOf(poly), planes));
        return true;
    }

    private static Vector4 AabbOf(ViewPolygon poly) =>
        new(poly.MinX, poly.MinY, poly.MaxX, poly.MaxY);

}
