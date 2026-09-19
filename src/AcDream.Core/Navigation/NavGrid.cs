using System.Diagnostics;
using System.Numerics;
using AcDream.Core.Physics;

namespace AcDream.Core.Navigation;

/// <summary>What building one grid produced, and what it cost.</summary>
public readonly record struct NavGridBuildReport(
    int Triangles,
    int Cylinders,
    int Spans,
    int WallPieces,
    int Nodes,
    int ClearNodes,
    double Milliseconds);

/// <summary>
/// Where a body can stand in a square region of the world, and where it can
/// step next. The region's collision geometry is rasterized into columns of
/// solid spans. A node is the top of a walkable span with headroom for the
/// whole body, and a link joins nodes in neighbouring columns whose heights
/// differ by no more than the body can step up or down. A node is clear to walk
/// through when no wall between step height and head height comes nearer its
/// centre than <see cref="NearestWall"/>, and when it is not on the very edge of
/// a ledge. The walls are kept, so straight walks and lines of sight can be
/// tested against them.
/// </summary>
public sealed class NavGrid
{
    public const float DefaultCellSize = 0.25f;
    public const int DirectionCount = 8;

    /// <summary>How high a standing body's eyes are above its feet.</summary>
    public const float EyeHeight = 1.6f;

    /// <summary>How high above an object's feet a body looks when it looks at the object.</summary>
    public const float TargetHeight = 1.2f;

    /// <summary>Terrain is a surface with this much solid ground under it.</summary>
    private const float TerrainThickness = 0.5f;

    /// <summary>How far above the highest cell polygon in a column terrain stands to be ground over the cell, such as over a cellar, rather than the building's.</summary>
    private const float TerrainAboveCells = 0.25f;

    /// <summary>Overlapping spans whose tops are this close share walkability.</summary>
    private const float FlagMergeHeight = 0.1f;

    /// <summary>A walkable polygon covering less of a column than this only touches its edge.</summary>
    private const float MinimumFloorArea = 1e-5f;

    /// <summary>How much nearer than the body's radius a wall may come, as a share of a column.</summary>
    private const float ClearanceToleranceColumns = 0.25f;

    /// <summary>A node must be at least this many steps from a node beside a ledge or a wall.</summary>
    private const int EdgeMargin = 1;

    /// <summary>An edge of a floor with a wall this many columns from a node is the wall's; with none that near, it is a ledge.</summary>
    private const float LedgeWallColumns = 2.5f;

    /// <summary>How near a straight walk may pass a wall's footprint before it counts as passing through it.</summary>
    private const float WalkMargin = 0.05f;

    /// <summary>How near a line of sight may pass a wall's footprint before the wall hides what is behind it.</summary>
    private const float SightMargin = 0.02f;

    /// <summary>How far a surface may reach into a body in the air, or a landing lie past its feet, and still count as clear.</summary>
    private const float AirTolerance = 0.05f;

    private const byte UnboundedDistance = byte.MaxValue;
    private const int ClipCapacity = 32;

    /// <summary>What a span was rasterized from, so a building's shell can be told from the rooms inside it.</summary>
    private const byte FromCell = 1;
    private const byte FromShell = 2;
    private const byte FromObject = 4;
    private const byte FromTerrain = 8;

    /// <summary>How much of a dome's radius a body stands on: the part of its top no steeper than a floor.</summary>
    private static readonly float DomeStandShare =
        MathF.Sqrt(MathF.Max(0f, 1f - (PhysicsGlobals.FloorZ * PhysicsGlobals.FloorZ)));

    // East, north, west and south, then the diagonals.
    private static readonly int[] StepX = [1, 0, -1, 0, 1, -1, -1, 1];
    private static readonly int[] StepY = [0, 1, 0, -1, 1, 1, -1, -1];

    private readonly NavColumnTiles<ColumnNodes> _columnNodes;
    private readonly int[] _nodeColumn;
    private readonly float[] _nodeZ;
    private readonly float[] _nodeCeiling;
    private readonly int[] _links;
    private readonly byte[] _borderDistance;
    private readonly float[] _wallDistance;
    private readonly bool[] _clear;
    private readonly WallPieces _walls;

    private NavGrid(
        NavGeometry geometry,
        float cellSize,
        int side,
        NavBody body,
        NavColumnTiles<ColumnNodes> columnNodes,
        int[] nodeColumn,
        float[] nodeZ,
        float[] nodeCeiling,
        int[] links,
        byte[] borderDistance,
        float[] wallDistance,
        bool[] clear,
        bool[] terrain,
        WallPieces walls,
        NavGridBuildReport report)
    {
        OriginX = geometry.OriginX;
        OriginY = geometry.OriginY;
        LandblockIds = geometry.LandblockIds;
        ObjectFingerprint = geometry.ObjectFingerprint;
        ObjectIds = geometry.ObjectIds;
        CellSize = cellSize;
        Side = side;
        Body = body;
        NearestWall = NearestWallFor(body, cellSize);
        _columnNodes = columnNodes;
        _nodeColumn = nodeColumn;
        _nodeZ = nodeZ;
        _nodeCeiling = nodeCeiling;
        _links = links;
        _borderDistance = borderDistance;
        _wallDistance = wallDistance;
        _clear = clear;
        _terrain = terrain;
        _walls = walls;
        Report = report;
    }

    /// <summary>The world position of the region's south-west corner.</summary>
    public float OriginX { get; }

    public float OriginY { get; }

    public float CellSize { get; }

    /// <summary>Columns along each edge of the region.</summary>
    public int Side { get; }

    /// <summary>The length of the region's sides in meters.</summary>
    public float Size => Side * CellSize;

    /// <summary>The resident landblocks whose geometry the grid was built from.</summary>
    public IReadOnlyList<uint> LandblockIds { get; }

    /// <summary>What the objects the server placed in the region came to when it was built.</summary>
    public ulong ObjectFingerprint { get; }

    /// <summary>The objects the server placed whose collision the grid holds.</summary>
    public IReadOnlySet<uint> ObjectIds { get; }

    public NavBody Body { get; }

    /// <summary>
    /// The nearest a wall may come to a body walking the grid: the body's radius,
    /// less a quarter of a column, because the body can stand anywhere in its
    /// column and slides along walls.
    /// </summary>
    public float NearestWall { get; }

    /// <summary>
    /// How near a wall a body passing along a path with <see cref="CanBrushAlong"/>
    /// keeps its centre: nearer than a walked leg keeps, since a wall the body
    /// touches slides it along rather than stopping it.
    /// </summary>
    public const float BrushMargin = 0.25f;

    public NavGridBuildReport Report { get; }

    public int NodeCount => _nodeZ.Length;

    /// <summary>The columns east and north one step in a direction moves.</summary>
    public static (int X, int Y) StepOf(int direction) => (StepX[direction], StepY[direction]);

    public static int DirectionOf(int stepX, int stepY)
    {
        for (int direction = 0; direction < DirectionCount; direction++)
        {
            if (StepX[direction] == stepX && StepY[direction] == stepY)
                return direction;
        }
        return -1;
    }

    /// <summary>Whether a point lies inside the region, at least <paramref name="margin"/> from its edges.</summary>
    public bool Contains(Vector3 point, float margin = 0f) =>
        point.X >= OriginX + margin
        && point.Y >= OriginY + margin
        && point.X <= OriginX + Size - margin
        && point.Y <= OriginY + Size - margin;

    /// <summary>The node a step from <paramref name="node"/> lands on, or -1.</summary>
    public int Link(int node, int direction) => _links[(node * DirectionCount) + direction];

    public bool IsClear(int node) => _clear[node];

    /// <summary>Whether a node's floor is bare terrain, rather than an object, a building or a room standing on or over it.</summary>
    public bool IsTerrain(int node) => _terrain[node];

    private readonly bool[] _terrain;

    /// <summary>
    /// Whether a body stands on a node at all: no wall crowds it, though its floor may
    /// be too small to stand clear of the edges. The top of a post or a signpost is
    /// standable without being clear: a body perches there, and never walks across it.
    /// </summary>
    public bool IsStandable(int node) => _wallDistance[node] >= NearestWall;

    /// <summary>Steps from <paramref name="node"/> to the nearest node beside a ledge or a wall.</summary>
    public int BorderDistance(int node) => _borderDistance[node];

    /// <summary>Horizontal distance from the node's centre to the nearest wall the body would touch, or infinity.</summary>
    public float WallDistance(int node) => _wallDistance[node];

    /// <summary>The height of the underside of whatever stands above a node, or infinity.</summary>
    public float Ceiling(int node) => _nodeCeiling[node];

    /// <summary>
    /// Whether a body in the air with its feet at <paramref name="feet"/> touches
    /// nothing: no wall nearer its centre than <see cref="NearestWall"/> overlaps its
    /// height, and no floor or ceiling in the columns under it cuts into it.
    /// </summary>
    public bool IsOpenAir(Vector3 feet)
    {
        float localX = feet.X - OriginX;
        float localY = feet.Y - OriginY;
        float top = feet.Z + Body.Height;
        int centreX = (int)MathF.Floor(localX / CellSize);
        int centreY = (int)MathF.Floor(localY / CellSize);
        int reach = (int)MathF.Ceiling(Body.Radius / CellSize);
        float underSquared = NearestWall * NearestWall;
        for (int y = Math.Max(0, centreY - reach); y <= Math.Min(Side - 1, centreY + reach); y++)
        {
            for (int x = Math.Max(0, centreX - reach); x <= Math.Min(Side - 1, centreX + reach); x++)
            {
                for (int piece = _walls.First(x, y); piece != -1; piece = _walls.Next[piece])
                {
                    if (_walls.High[piece] > feet.Z + AirTolerance
                        && _walls.Low[piece] < top
                        && _walls.Distance(piece, localX, localY) < NearestWall)
                    {
                        return false;
                    }
                }
                float dx = ((x + 0.5f) * CellSize) - localX;
                float dy = ((y + 0.5f) * CellSize) - localY;
                if ((dx * dx) + (dy * dy) > underSquared)
                    continue;
                (int first, int count) = NodesInColumn(x, y);
                int below = -1;
                for (int node = first; node < first + count; node++)
                {
                    if (_nodeZ[node] <= feet.Z + AirTolerance)
                        below = node;
                    else if (_nodeZ[node] < top)
                        return false;
                }
                // The ceiling over the floor below cuts into the body only where it lies between
                // its feet and its head: a body above a room's ceiling, as one flying over the
                // roof above the room, is not under it.
                if (below >= 0 && _nodeCeiling[below] < top && _nodeCeiling[below] > feet.Z + AirTolerance)
                    return false;
            }
        }
        return true;
    }

    /// <summary>
    /// The node in the column under a falling body whose top its feet crossed on the
    /// way down from <paramref name="fromHeight"/> to <paramref name="feet"/>, or -1.
    /// </summary>
    public int LandingUnder(Vector3 feet, float fromHeight)
    {
        int x = (int)MathF.Floor((feet.X - OriginX) / CellSize);
        int y = (int)MathF.Floor((feet.Y - OriginY) / CellSize);
        (int first, int count) = NodesInColumn(x, y);
        for (int node = first + count - 1; node >= first; node--)
        {
            if (_nodeZ[node] <= fromHeight + AirTolerance && _nodeZ[node] >= feet.Z - AirTolerance)
                return node;
        }
        return -1;
    }

    public Vector3 Position(int node)
    {
        int column = _nodeColumn[node];
        return new Vector3(
            OriginX + (((column % Side) + 0.5f) * CellSize),
            OriginY + (((column / Side) + 0.5f) * CellSize),
            _nodeZ[node]);
    }

    public (int X, int Y) ColumnOf(int node) =>
        (_nodeColumn[node] % Side, _nodeColumn[node] / Side);

    /// <summary>The nodes standing in one column, lowest first.</summary>
    public (int First, int Count) NodesInColumn(int x, int y)
    {
        if ((uint)x >= (uint)Side || (uint)y >= (uint)Side)
            return (0, 0);
        ColumnNodes nodes = _columnNodes.Get(x, y);
        return (nodes.First, nodes.Count);
    }

    /// <summary>
    /// The clear node nearest <paramref name="position"/> within a horizontal
    /// radius and a height tolerance, or -1 when there is none.
    /// </summary>
    public int FindNode(Vector3 position, float radius, float heightTolerance)
    {
        List<(int Node, float Score)> near = NodesNear(position, radius, heightTolerance);
        return near.Count == 0 ? -1 : near[0].Node;
    }

    /// <summary>
    /// The nearest node a body stands on that it could reach in a straight line without
    /// passing through a wall: a clear one where there is one, and otherwise a perch,
    /// such as the top of a post a body stands on without room to walk.
    /// </summary>
    public int FindStandingNode(Vector3 position, float radius, float heightTolerance)
    {
        int walkable = FindWalkableNode(position, radius, heightTolerance);
        if (walkable >= 0)
            return walkable;
        foreach ((int node, _) in NodesNear(position, radius, heightTolerance, standable: true))
        {
            if (IsOpenLine(position, Position(node)))
                return node;
        }
        return -1;
    }

    /// <summary>
    /// The clear node nearest <paramref name="position"/>, within a horizontal
    /// radius and a height tolerance, that a body standing there could walk to in
    /// a straight line without passing through a wall, or -1 when there is none.
    /// </summary>
    public int FindWalkableNode(Vector3 position, float radius, float heightTolerance)
    {
        foreach ((int node, _) in NodesNear(position, radius, heightTolerance))
        {
            if (IsOpenLine(position, Position(node)))
                return node;
        }
        return -1;
    }

    /// <summary>
    /// Whether a body walking straight between two points, standing at their
    /// heights, keeps at least <see cref="NearestWall"/> from every wall it would
    /// touch.
    /// </summary>
    public bool CanSweep(Vector3 from, Vector3 to) => !WallNear(from, to, NearestWall, sight: false);

    /// <summary>Whether a body walking straight between two points, standing at their heights, passes through no wall.</summary>
    public bool IsOpenLine(Vector3 from, Vector3 to) => !WallNear(from, to, WalkMargin, sight: false);

    /// <summary>
    /// Whether a body standing on <paramref name="node"/> can see an object whose
    /// feet are at <paramref name="target"/>: no wall crosses the line from the
    /// body's eyes to the object, the object is not above the node's ceiling, and
    /// it is not so far below the node that the node's own floor hides it.
    /// </summary>
    public bool CanSee(int node, Vector3 target)
    {
        Vector3 feet = Position(node);
        var eye = new Vector3(feet.X, feet.Y, feet.Z + EyeHeight);
        var seen = new Vector3(target.X, target.Y, target.Z + TargetHeight);
        return seen.Z < _nodeCeiling[node]
            && seen.Z >= feet.Z
            && !WallNear(eye, seen, SightMargin, sight: true);
    }

    /// <summary>
    /// The wall pieces that hide <paramref name="target"/> from a body standing on
    /// <paramref name="node"/>: for each, its column, its height range, and the
    /// sight line's height where they are nearest. For diagnostics.
    /// </summary>
    internal IReadOnlyList<(int X, int Y, float Low, float High, float LineHeight)> SightBlockers(int node, Vector3 target)
    {
        Vector3 feet = Position(node);
        var eye = new Vector3(feet.X, feet.Y, feet.Z + EyeHeight);
        var seen = new Vector3(target.X, target.Y, target.Z + TargetHeight);
        var start = new Vector2(eye.X - OriginX, eye.Y - OriginY);
        var end = new Vector2(seen.X - OriginX, seen.Y - OriginY);
        int x0 = Math.Max(0, (int)MathF.Floor((MathF.Min(start.X, end.X) - SightMargin) / CellSize));
        int x1 = Math.Min(Side - 1, (int)MathF.Floor((MathF.Max(start.X, end.X) + SightMargin) / CellSize));
        int y0 = Math.Max(0, (int)MathF.Floor((MathF.Min(start.Y, end.Y) - SightMargin) / CellSize));
        int y1 = Math.Min(Side - 1, (int)MathF.Floor((MathF.Max(start.Y, end.Y) + SightMargin) / CellSize));
        var blockers = new List<(int X, int Y, float Low, float High, float LineHeight)>();
        for (int y = y0; y <= y1; y++)
        {
            for (int x = x0; x <= x1; x++)
            {
                for (int piece = _walls.First(x, y); piece != -1; piece = _walls.Next[piece])
                {
                    (float distance, float along) = _walls.DistanceToSegment(piece, start, end);
                    if (distance >= SightMargin)
                        continue;
                    float height = eye.Z + ((seen.Z - eye.Z) * along);
                    if (_walls.Low[piece] <= height && _walls.High[piece] >= height)
                        blockers.Add((x, y, _walls.Low[piece], _walls.High[piece], height));
                }
            }
        }
        return blockers;
    }

    /// <summary>
    /// Whether a body can walk straight from one node to another: every column
    /// along the line between them holds a clear node linked to the last, and no
    /// wall comes nearer the line than <see cref="NearestWall"/>.
    /// </summary>
    public bool CanWalkStraight(int from, int to) => CanWalkStraight(from, to, NearestWall, EdgeMargin);

    /// <summary>
    /// Whether a body can walk straight from one node to another and keep at least
    /// <paramref name="clearance"/> from walls and <paramref name="borderSteps"/>
    /// steps from ledges: every column along the line between them holds a clear
    /// node, linked to the last, that is at least that far from both, and no wall
    /// comes nearer the line than <paramref name="clearance"/> or
    /// <see cref="NearestWall"/>, whichever is farther.
    /// </summary>
    /// <summary>
    /// The nodes a straight walk from one node to another steps onto, one for each
    /// column along the line after the first, following links; empty when a column
    /// along the line holds no node linked to the last.
    /// </summary>
    public IReadOnlyList<int> NodesAlong(int from, int to)
    {
        (int x, int y) = ColumnOf(from);
        (int targetX, int targetY) = ColumnOf(to);
        int distanceX = Math.Abs(targetX - x);
        int distanceY = Math.Abs(targetY - y);
        int signX = Math.Sign(targetX - x);
        int signY = Math.Sign(targetY - y);
        int error = distanceX - distanceY;
        int node = from;
        var nodes = new List<int>(Math.Max(distanceX, distanceY));
        while (x != targetX || y != targetY)
        {
            int moveX = 0;
            int moveY = 0;
            int doubled = error * 2;
            if (doubled > -distanceY)
            {
                error -= distanceY;
                x += signX;
                moveX = signX;
            }
            if (doubled < distanceX)
            {
                error += distanceX;
                y += signY;
                moveY = signY;
            }
            node = Link(node, DirectionOf(moveX, moveY));
            if (node < 0)
                return [];
            nodes.Add(node);
        }
        return nodes;
    }

    public bool CanWalkStraight(int from, int to, float clearance, int borderSteps)
    {
        (int x, int y) = ColumnOf(from);
        (int targetX, int targetY) = ColumnOf(to);
        int distanceX = Math.Abs(targetX - x);
        int distanceY = Math.Abs(targetY - y);
        int signX = Math.Sign(targetX - x);
        int signY = Math.Sign(targetY - y);
        int error = distanceX - distanceY;
        int node = from;
        while (x != targetX || y != targetY)
        {
            int moveX = 0;
            int moveY = 0;
            int doubled = error * 2;
            if (doubled > -distanceY)
            {
                error -= distanceY;
                x += signX;
                moveX = signX;
            }
            if (doubled < distanceX)
            {
                error += distanceX;
                y += signY;
                moveY = signY;
            }
            node = Link(node, DirectionOf(moveX, moveY));
            if (node < 0 || !IsClear(node) || WallDistance(node) < clearance || BorderDistance(node) < borderSteps)
                return false;
        }
        return node == to && !WallNear(Position(from), Position(to), MathF.Max(NearestWall, clearance), sight: false);
    }

    /// <summary>
    /// Whether a body can pass along a path of points brushing walls, as it does
    /// running around a corner: the first point stands on a node within a step of
    /// its height, every column along the path holds a node linked to the last and
    /// not at a ledge, and no wall comes nearer the path than <see cref="BrushMargin"/>.
    /// </summary>
    public bool CanBrushAlong(IReadOnlyList<Vector3> path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Count == 0)
            return false;
        int node = NodeAt(path[0], Body.StepUpHeight);
        if (node < 0 || AtLedge(node))
            return false;
        for (int index = 1; index < path.Count; index++)
        {
            int start = node;
            (int x, int y) = ColumnOf(node);
            int targetX = (int)MathF.Floor((path[index].X - OriginX) / CellSize);
            int targetY = (int)MathF.Floor((path[index].Y - OriginY) / CellSize);
            int distanceX = Math.Abs(targetX - x);
            int distanceY = Math.Abs(targetY - y);
            int signX = Math.Sign(targetX - x);
            int signY = Math.Sign(targetY - y);
            int error = distanceX - distanceY;
            while (x != targetX || y != targetY)
            {
                int moveX = 0;
                int moveY = 0;
                int doubled = error * 2;
                if (doubled > -distanceY)
                {
                    error -= distanceY;
                    x += signX;
                    moveX = signX;
                }
                if (doubled < distanceX)
                {
                    error += distanceX;
                    y += signY;
                    moveY = signY;
                }
                node = Link(node, DirectionOf(moveX, moveY));
                if (node < 0 || AtLedge(node))
                    return false;
            }
            if (WallNear(path[index - 1] with { Z = _nodeZ[start] }, path[index] with { Z = _nodeZ[node] }, BrushMargin, sight: false))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Whether a node stands at a ledge: on the very edge of its floor, with no wall
    /// within <see cref="LedgeWallColumns"/> columns to account for the edge.
    /// </summary>
    private bool AtLedge(int node) => BorderDistance(node) < EdgeMargin && WallDistance(node) > CellSize * LedgeWallColumns;

    /// <summary>The node in a point's column nearest its height, within <paramref name="heightTolerance"/>, or -1.</summary>
    private int NodeAt(Vector3 point, float heightTolerance)
    {
        (int first, int count) = NodesInColumn(
            (int)MathF.Floor((point.X - OriginX) / CellSize),
            (int)MathF.Floor((point.Y - OriginY) / CellSize));
        int nearest = -1;
        float nearestRise = heightTolerance;
        for (int node = first; node < first + count; node++)
        {
            float rise = MathF.Abs(_nodeZ[node] - point.Z);
            if (rise <= nearestRise)
            {
                nearest = node;
                nearestRise = rise;
            }
        }
        return nearest;
    }

    public static NavGrid Build(NavGeometry geometry, NavBody body, float cellSize = DefaultCellSize)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        if (!(cellSize > 0f) || cellSize > geometry.Size)
            throw new ArgumentOutOfRangeException(nameof(cellSize));

        var clock = Stopwatch.StartNew();
        int side = (int)MathF.Ceiling((geometry.Size / cellSize) - 0.001f);
        var spans = new SpanColumns(side);
        var walls = new WallPieces(side);
        var highestCell = new NavColumnTiles<float>(side, float.NegativeInfinity);
        var origin = new Vector3(geometry.OriginX, geometry.OriginY, 0f);

        foreach (NavTriangle triangle in geometry.CellTriangles)
            RasterizeTriangle(spans, walls, triangle, origin, cellSize, side, highestCell, FromCell);
        foreach (NavTriangle triangle in geometry.ShellTriangles)
            RasterizeTriangle(spans, walls, triangle, origin, cellSize, side, highestCell: null, FromShell);
        foreach (NavTriangle triangle in geometry.ObjectTriangles)
            RasterizeTriangle(spans, walls, triangle, origin, cellSize, side, highestCell: null, FromObject);
        foreach (NavCylinder cylinder in geometry.Cylinders)
            RasterizeCylinder(spans, walls, cylinder, origin, cellSize, side, FromObject);
        foreach (NavTerrain terrain in geometry.Terrains)
            RasterizeTerrain(spans, terrain, origin, cellSize, side, highestCell);

        var columnNodes = new NavColumnTiles<ColumnNodes>(side, default);
        var nodeColumns = new List<int>();
        var nodeHeights = new List<float>();
        var nodeCeilings = new List<float>();
        var nodeTerrain = new List<bool>();
        int tiles = spans.Head.TilesPerSide;
        int tileColumns = NavColumnTiles<int>.TileColumns;
        var column = new List<int>(8);
        var cellAbove = new List<bool>(8);
        for (int tileY = 0; tileY < tiles; tileY++)
        {
            for (int tileX = 0; tileX < tiles; tileX++)
            {
                if (!spans.Head.HasTile(tileX, tileY))
                    continue;
                int lastY = Math.Min((tileY + 1) * tileColumns, side);
                int lastX = Math.Min((tileX + 1) * tileColumns, side);
                for (int y = tileY * tileColumns; y < lastY; y++)
                {
                    for (int x = tileX * tileColumns; x < lastX; x++)
                    {
                        int first = nodeHeights.Count;
                        column.Clear();
                        for (int span = spans.Head.Get(x, y); span != -1; span = spans.Next[span])
                            column.Add(span);
                        cellAbove.Clear();
                        bool seenCell = false;
                        for (int index = column.Count - 1; index >= 0; index--)
                        {
                            cellAbove.Add(seenCell);
                            seenCell |= (spans.Source[column[index]] & FromCell) != 0;
                        }
                        cellAbove.Reverse();
                        for (int index = 0; index < column.Count; index++)
                        {
                            int span = column[index];
                            if (!spans.Walkable[span] || Shrouded(spans, span, cellAbove[index]))
                                continue;
                            float ceiling = float.PositiveInfinity;
                            for (int higher = index + 1; higher < column.Count; higher++)
                            {
                                if (Shrouded(spans, column[higher], cellAbove[higher]))
                                    continue;
                                ceiling = spans.Min[column[higher]];
                                break;
                            }
                            if (ceiling - spans.Max[span] < body.Height)
                                continue;
                            nodeColumns.Add((y * side) + x);
                            nodeHeights.Add(spans.Max[span]);
                            nodeCeilings.Add(ceiling);
                            nodeTerrain.Add(spans.Source[span] == FromTerrain);
                        }
                        if (nodeHeights.Count > first)
                            columnNodes.Slot(x, y) = new ColumnNodes(first, nodeHeights.Count - first);
                    }
                }
            }
        }

        int[] nodeColumn = nodeColumns.ToArray();
        float[] heights = nodeHeights.ToArray();
        float[] ceilings = nodeCeilings.ToArray();
        int[] links = new int[heights.Length * DirectionCount];
        Array.Fill(links, -1);
        LinkOrthogonalNeighbours(side, body, columnNodes, nodeColumn, heights, ceilings, links);
        LinkDiagonalNeighbours(heights.Length, links);
        byte[] borderDistance = MeasureBorderDistance(links, heights.Length);
        float[] wallDistance = MeasureWallDistance(side, cellSize, body, nodeColumn, heights, walls);

        float nearestWall = NearestWallFor(body, cellSize);
        var clear = new bool[heights.Length];
        int clearCount = 0;
        for (int node = 0; node < heights.Length; node++)
        {
            clear[node] = borderDistance[node] >= EdgeMargin && wallDistance[node] >= nearestWall;
            if (clear[node])
                clearCount++;
        }

        var report = new NavGridBuildReport(
            geometry.CellTriangles.Count + geometry.ShellTriangles.Count + geometry.ObjectTriangles.Count,
            geometry.Cylinders.Count,
            spans.Live,
            walls.Count,
            heights.Length,
            clearCount,
            clock.Elapsed.TotalMilliseconds);
        return new NavGrid(
            geometry,
            cellSize,
            side,
            body,
            columnNodes,
            nodeColumn,
            heights,
            ceilings,
            links,
            borderDistance,
            wallDistance,
            clear,
            nodeTerrain.ToArray(),
            walls,
            report);
    }

    /// <summary>
    /// Whether a span is a building's shell where the building's own cells stand above
    /// it. That is the shell's floor and roof over rooms the cells already hold, and a
    /// body inside walks those cells' floors and stairs instead, under their ceilings.
    /// </summary>
    private static bool Shrouded(SpanColumns spans, int span, bool cellAbove) =>
        cellAbove && (spans.Source[span] & ~FromShell) == 0;

    private static float NearestWallFor(NavBody body, float cellSize) =>
        body.Radius - (cellSize * ClearanceToleranceColumns);

    /// <summary>The clear nodes within a horizontal radius and a height tolerance of a point, nearest first.</summary>
    private List<(int Node, float Score)> NodesNear(
        Vector3 position,
        float radius,
        float heightTolerance,
        bool standable = false)
    {
        int centreX = (int)MathF.Floor((position.X - OriginX) / CellSize);
        int centreY = (int)MathF.Floor((position.Y - OriginY) / CellSize);
        int reach = (int)MathF.Ceiling(radius / CellSize);
        var near = new List<(int Node, float Score)>();
        for (int y = Math.Max(0, centreY - reach); y <= Math.Min(Side - 1, centreY + reach); y++)
        {
            for (int x = Math.Max(0, centreX - reach); x <= Math.Min(Side - 1, centreX + reach); x++)
            {
                (int first, int count) = NodesInColumn(x, y);
                for (int node = first; node < first + count; node++)
                {
                    if (standable ? !IsStandable(node) : !IsClear(node))
                        continue;
                    Vector3 at = Position(node);
                    float rise = MathF.Abs(at.Z - position.Z);
                    if (rise > heightTolerance)
                        continue;
                    float dx = at.X - position.X;
                    float dy = at.Y - position.Y;
                    float horizontal = (dx * dx) + (dy * dy);
                    if (horizontal > radius * radius)
                        continue;
                    near.Add((node, horizontal + (rise * rise)));
                }
            }
        }
        near.Sort(static (left, right) => left.Score.CompareTo(right.Score));
        return near;
    }

    /// <summary>
    /// Whether a wall piece comes within <paramref name="margin"/> of the straight
    /// line between two points. For a body the points are where it stands, and a
    /// piece counts when it rises into the body standing on the line where they
    /// are nearest. For a line of sight a piece counts when it spans the line's
    /// height there.
    /// </summary>
    private bool WallNear(Vector3 from, Vector3 to, float margin, bool sight)
    {
        var start = new Vector2(from.X - OriginX, from.Y - OriginY);
        var end = new Vector2(to.X - OriginX, to.Y - OriginY);
        float reach = margin / CellSize;
        float startX = start.X / CellSize;
        float startY = start.Y / CellSize;
        float endX = end.X / CellSize;
        float endY = end.Y / CellSize;
        int firstRow = Math.Max(0, (int)MathF.Floor(MathF.Min(startY, endY) - reach));
        int lastRow = Math.Min(Side - 1, (int)MathF.Floor(MathF.Max(startY, endY) + reach));
        for (int row = firstRow; row <= lastRow; row++)
        {
            float lowX;
            float highX;
            if (MathF.Abs(endY - startY) < 1e-6f)
            {
                lowX = MathF.Min(startX, endX);
                highX = MathF.Max(startX, endX);
            }
            else
            {
                float enter = Math.Clamp((row - reach - startY) / (endY - startY), 0f, 1f);
                float leave = Math.Clamp((row + 1 + reach - startY) / (endY - startY), 0f, 1f);
                float enterX = startX + ((endX - startX) * enter);
                float leaveX = startX + ((endX - startX) * leave);
                lowX = MathF.Min(enterX, leaveX);
                highX = MathF.Max(enterX, leaveX);
            }
            int firstColumn = Math.Max(0, (int)MathF.Floor(lowX - reach));
            int lastColumn = Math.Min(Side - 1, (int)MathF.Floor(highX + reach));
            for (int column = firstColumn; column <= lastColumn; column++)
            {
                for (int piece = _walls.First(column, row); piece != -1; piece = _walls.Next[piece])
                {
                    (float distance, float along) = _walls.DistanceToSegment(piece, start, end);
                    if (distance >= margin)
                        continue;
                    float height = from.Z + ((to.Z - from.Z) * along);
                    bool counts = sight
                        ? _walls.Low[piece] <= height && _walls.High[piece] >= height
                        : _walls.High[piece] > height + Body.StepUpHeight && _walls.Low[piece] < height + Body.Height;
                    if (counts)
                        return true;
                }
            }
        }
        return false;
    }

    private static void LinkOrthogonalNeighbours(
        int side,
        NavBody body,
        NavColumnTiles<ColumnNodes> columnNodes,
        int[] nodeColumn,
        float[] heights,
        float[] ceilings,
        int[] links)
    {
        for (int node = 0; node < heights.Length; node++)
        {
            int x = nodeColumn[node] % side;
            int y = nodeColumn[node] / side;
            for (int direction = 0; direction < 4; direction++)
            {
                int neighbourX = x + StepX[direction];
                int neighbourY = y + StepY[direction];
                if ((uint)neighbourX >= (uint)side || (uint)neighbourY >= (uint)side)
                    continue;
                ColumnNodes neighbour = columnNodes.Get(neighbourX, neighbourY);
                int best = -1;
                float bestRise = float.PositiveInfinity;
                int end = neighbour.First + neighbour.Count;
                for (int other = neighbour.First; other < end; other++)
                {
                    float rise = heights[other] - heights[node];
                    if (rise > body.StepUpHeight || rise < -body.StepDownHeight)
                        continue;
                    float headroom = MathF.Min(ceilings[node], ceilings[other])
                        - MathF.Max(heights[node], heights[other]);
                    if (headroom < body.Height)
                        continue;
                    if (MathF.Abs(rise) < bestRise)
                    {
                        bestRise = MathF.Abs(rise);
                        best = other;
                    }
                }
                links[(node * DirectionCount) + direction] = best;
            }
        }
    }

    /// <summary>A diagonal step needs both of the square steps around its corner.</summary>
    private static void LinkDiagonalNeighbours(int nodes, int[] links)
    {
        for (int node = 0; node < nodes; node++)
        {
            for (int direction = 4; direction < DirectionCount; direction++)
            {
                int alongX = StepX[direction] > 0 ? 0 : 2;
                int alongY = StepY[direction] > 0 ? 1 : 3;
                int viaX = links[(node * DirectionCount) + alongX];
                int viaY = links[(node * DirectionCount) + alongY];
                if (viaX == -1 || viaY == -1)
                    continue;
                int corner = links[(viaX * DirectionCount) + alongY];
                if (corner != -1 && corner == links[(viaY * DirectionCount) + alongX])
                    links[(node * DirectionCount) + direction] = corner;
            }
        }
    }

    private static byte[] MeasureBorderDistance(int[] links, int nodes)
    {
        var distance = new byte[nodes];
        Array.Fill(distance, UnboundedDistance);
        var frontier = new Queue<int>();
        for (int node = 0; node < nodes; node++)
        {
            for (int direction = 0; direction < DirectionCount; direction++)
            {
                if (links[(node * DirectionCount) + direction] == -1)
                {
                    distance[node] = 0;
                    frontier.Enqueue(node);
                    break;
                }
            }
        }
        while (frontier.TryDequeue(out int node))
        {
            int next = distance[node] + 1;
            if (next >= UnboundedDistance)
                continue;
            for (int direction = 0; direction < DirectionCount; direction++)
            {
                int other = links[(node * DirectionCount) + direction];
                if (other != -1 && distance[other] > next)
                {
                    distance[other] = (byte)next;
                    frontier.Enqueue(other);
                }
            }
        }
        return distance;
    }

    /// <summary>
    /// The horizontal distance from each node's centre to the nearest wall
    /// piece that rises into the body: above what the body steps over and below
    /// its head.
    /// </summary>
    private static float[] MeasureWallDistance(
        int side,
        float cellSize,
        NavBody body,
        int[] nodeColumn,
        float[] heights,
        WallPieces walls)
    {
        int reach = (int)MathF.Ceiling((body.Radius + cellSize) / cellSize);
        var distance = new float[heights.Length];
        for (int node = 0; node < heights.Length; node++)
        {
            int x = nodeColumn[node] % side;
            int y = nodeColumn[node] / side;
            int x0 = Math.Max(0, x - reach);
            int x1 = Math.Min(side - 1, x + reach);
            int y0 = Math.Max(0, y - reach);
            int y1 = Math.Min(side - 1, y + reach);
            if (!walls.AnyIn(x0, y0, x1, y1))
            {
                distance[node] = float.PositiveInfinity;
                continue;
            }

            float centreX = (x + 0.5f) * cellSize;
            float centreY = (y + 0.5f) * cellSize;
            float low = heights[node] + body.StepUpHeight;
            float high = heights[node] + body.Height;
            float nearest = float.PositiveInfinity;
            for (int neighbourY = y0; neighbourY <= y1; neighbourY++)
            {
                for (int neighbourX = x0; neighbourX <= x1; neighbourX++)
                {
                    for (int piece = walls.First(neighbourX, neighbourY); piece != -1; piece = walls.Next[piece])
                    {
                        if (walls.High[piece] <= low || walls.Low[piece] >= high)
                            continue;
                        nearest = MathF.Min(nearest, walls.Distance(piece, centreX, centreY));
                    }
                }
            }
            distance[node] = nearest;
        }
        return distance;
    }

    /// <summary>
    /// Adds the part of a triangle inside each column it crosses as a solid
    /// span, and remembers the part of a wall as a wall piece. A column owns
    /// its west and south edges and not its east and north ones, so a wall
    /// lying on a column boundary fills exactly one column.
    /// </summary>
    private static void RasterizeTriangle(
        SpanColumns spans,
        WallPieces walls,
        NavTriangle triangle,
        Vector3 origin,
        float cellSize,
        int side,
        NavColumnTiles<float>? highestCell,
        byte source)
    {
        Vector3 a = triangle.A - origin;
        Vector3 b = triangle.B - origin;
        Vector3 c = triangle.C - origin;
        Vector3 normal = Vector3.Cross(b - a, c - a);
        float length = normal.Length();
        if (!(length > 1e-6f))
            return;
        bool walkable = MathF.Abs(normal.Z) / length >= PhysicsGlobals.FloorZ;

        int x0 = Math.Max(0, (int)MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X)) / cellSize));
        int x1 = Math.Min(side - 1, (int)MathF.Floor(MathF.Max(a.X, MathF.Max(b.X, c.X)) / cellSize));
        int y0 = Math.Max(0, (int)MathF.Floor(MathF.Min(a.Y, MathF.Min(b.Y, c.Y)) / cellSize));
        int y1 = Math.Min(side - 1, (int)MathF.Floor(MathF.Max(a.Y, MathF.Max(b.Y, c.Y)) / cellSize));
        if (x0 > x1 || y0 > y1)
            return;

        Span<Vector3> polygon = stackalloc Vector3[ClipCapacity];
        Span<Vector3> scratch = stackalloc Vector3[ClipCapacity];
        for (int y = y0; y <= y1; y++)
        {
            float bottom = y * cellSize;
            float top = bottom + cellSize;
            for (int x = x0; x <= x1; x++)
            {
                float left = x * cellSize;
                float right = left + cellSize;
                polygon[0] = a;
                polygon[1] = b;
                polygon[2] = c;
                int count = Clip(polygon, 3, scratch, alongX: true, left, keepAbove: true);
                count = Clip(scratch, count, polygon, alongX: true, right, keepAbove: false);
                count = Clip(polygon, count, scratch, alongX: false, bottom, keepAbove: true);
                count = Clip(scratch, count, polygon, alongX: false, top, keepAbove: false);
                if (count == 0)
                    continue;
                if (walkable && AreaXY(polygon, count, left, bottom) < MinimumFloorArea)
                    continue;

                float low = float.PositiveInfinity;
                float high = float.NegativeInfinity;
                for (int index = 0; index < count; index++)
                {
                    low = MathF.Min(low, polygon[index].Z);
                    high = MathF.Max(high, polygon[index].Z);
                }
                spans.Add(x, y, low, high, walkable, source);
                if (!walkable)
                    walls.Add(x, y, polygon[..count], low, high);
                if (highestCell is not null)
                    highestCell.Slot(x, y) = MathF.Max(highestCell.Get(x, y), high);
            }
        }
    }

    /// <summary>
    /// Adds a cylinder or a dome as solid, with its top as floor a body stands on: the
    /// whole of a cylinder's flat top, and the part of a dome's top no steeper than a
    /// floor. Its side stays a wall, so a body cannot walk through it lower down.
    /// </summary>
    private static void RasterizeCylinder(
        SpanColumns spans,
        WallPieces walls,
        NavCylinder cylinder,
        Vector3 origin,
        float cellSize,
        int side,
        byte source)
    {
        if (!(cylinder.Radius > 0f) || !(cylinder.Height > 0f))
            return;
        Vector3 centre = cylinder.Base - origin;
        float reach = cylinder.Radius + (cellSize * 0.5f);
        int x0 = Math.Max(0, (int)MathF.Floor((centre.X - reach) / cellSize));
        int x1 = Math.Min(side - 1, (int)MathF.Floor((centre.X + reach) / cellSize));
        int y0 = Math.Max(0, (int)MathF.Floor((centre.Y - reach) / cellSize));
        int y1 = Math.Min(side - 1, (int)MathF.Floor((centre.Y + reach) / cellSize));
        Span<Vector3> square = stackalloc Vector3[4];
        float standWithin = cylinder.Radius * DomeStandShare;
        for (int y = y0; y <= y1; y++)
        {
            for (int x = x0; x <= x1; x++)
            {
                float dx = ((x + 0.5f) * cellSize) - centre.X;
                float dy = ((y + 0.5f) * cellSize) - centre.Y;
                float across = (dx * dx) + (dy * dy);
                if (across > reach * reach)
                    continue;
                float top = centre.Z + cylinder.Height;
                bool stands = true;
                if (cylinder.Dome)
                {
                    float middle = centre.Z + cylinder.Radius;
                    float inside = (cylinder.Radius * cylinder.Radius) - across;
                    top = middle + (inside > 0f ? MathF.Sqrt(inside) : 0f);
                    stands = across <= standWithin * standWithin;
                }
                spans.Add(x, y, centre.Z, top, stands, source);

                float left = MathF.Max(x * cellSize, centre.X - cylinder.Radius);
                float right = MathF.Min((x + 1) * cellSize, centre.X + cylinder.Radius);
                float bottom = MathF.Max(y * cellSize, centre.Y - cylinder.Radius);
                float upper = MathF.Min((y + 1) * cellSize, centre.Y + cylinder.Radius);
                if (left > right || bottom > upper)
                    continue;
                square[0] = new Vector3(left, bottom, centre.Z);
                square[1] = new Vector3(right, bottom, centre.Z);
                square[2] = new Vector3(right, upper, centre.Z);
                square[3] = new Vector3(left, upper, centre.Z);
                walls.Add(x, y, square, centre.Z, top);
            }
        }
    }

    /// <summary>
    /// Fills each column whose centre lies in a landblock with that landblock's terrain where it
    /// stands above every cell polygon in the column, as ground over a cellar does; terrain among
    /// or under a building's polygons is the building's. The open sea, a landblock every cell of
    /// which is under water, is no floor at all, since no body walks into it.
    /// </summary>
    private static void RasterizeTerrain(
        SpanColumns spans,
        NavTerrain terrain,
        Vector3 origin,
        float cellSize,
        int side,
        NavColumnTiles<float> highestCell)
    {
        if (terrain.Surface.IsEntirelyWater)
            return;
        float cornerX = terrain.OriginX - origin.X;
        float cornerY = terrain.OriginY - origin.Y;
        int x0 = Math.Max(0, (int)MathF.Ceiling((cornerX / cellSize) - 0.5f));
        int x1 = Math.Min(side, (int)MathF.Ceiling(((cornerX + NavGeometry.LandblockSize) / cellSize) - 0.5f));
        int y0 = Math.Max(0, (int)MathF.Ceiling((cornerY / cellSize) - 0.5f));
        int y1 = Math.Min(side, (int)MathF.Ceiling(((cornerY + NavGeometry.LandblockSize) / cellSize) - 0.5f));
        for (int y = y0; y < y1; y++)
        {
            float localY = ((y + 0.5f) * cellSize) - cornerY;
            for (int x = x0; x < x1; x++)
            {
                TerrainSurfacePolygon surface =
                    terrain.Surface.SampleSurfacePolygon(((x + 0.5f) * cellSize) - cornerX, localY);
                if (surface.Z < highestCell.Get(x, y) + TerrainAboveCells)
                    continue;
                spans.Add(
                    x,
                    y,
                    surface.Z - TerrainThickness,
                    surface.Z,
                    surface.Normal.Z >= PhysicsGlobals.FloorZ,
                    FromTerrain);
            }
        }
    }

    /// <summary>Clips a polygon to one side of an axis-aligned line; the lower bound is kept, the upper is not.</summary>
    private static int Clip(
        ReadOnlySpan<Vector3> input,
        int count,
        Span<Vector3> output,
        bool alongX,
        float bound,
        bool keepAbove)
    {
        int written = 0;
        for (int index = 0; index < count && written + 2 <= output.Length; index++)
        {
            Vector3 current = input[index];
            Vector3 next = input[index + 1 == count ? 0 : index + 1];
            float currentCoordinate = alongX ? current.X : current.Y;
            float nextCoordinate = alongX ? next.X : next.Y;
            float currentSide = keepAbove ? currentCoordinate - bound : bound - currentCoordinate;
            float nextSide = keepAbove ? nextCoordinate - bound : bound - nextCoordinate;
            bool currentInside = keepAbove ? currentSide >= 0f : currentSide > 0f;
            bool nextInside = keepAbove ? nextSide >= 0f : nextSide > 0f;
            if (currentInside)
                output[written++] = current;
            if (currentInside != nextInside)
                output[written++] = Vector3.Lerp(current, next, currentSide / (currentSide - nextSide));
        }
        return written;
    }

    private static float AreaXY(ReadOnlySpan<Vector3> polygon, int count, float left, float bottom)
    {
        float twice = 0f;
        for (int index = 0; index < count; index++)
        {
            Vector3 a = polygon[index];
            Vector3 b = polygon[index + 1 == count ? 0 : index + 1];
            twice += ((a.X - left) * (b.Y - bottom)) - ((b.X - left) * (a.Y - bottom));
        }
        return MathF.Abs(twice) * 0.5f;
    }

    /// <summary>Each column's solid spans as a list sorted by height, merged where they overlap.</summary>
    /// <summary>The nodes standing in one column: the index of the lowest and how many there are.</summary>
    private readonly record struct ColumnNodes(int First, int Count);

    private sealed class SpanColumns
    {
        private int _allocated;
        private int _free = -1;

        public SpanColumns(int side)
        {
            const int initialCapacity = 1 << 16;
            Head = new NavColumnTiles<int>(side, -1);
            Min = new float[initialCapacity];
            Max = new float[initialCapacity];
            Walkable = new bool[initialCapacity];
            Source = new byte[initialCapacity];
            Next = new int[initialCapacity];
        }

        public NavColumnTiles<int> Head { get; }

        public float[] Min;

        public float[] Max;

        public bool[] Walkable;

        /// <summary>What each span was rasterized from, as <see cref="FromCell"/> and its fellows.</summary>
        public byte[] Source;

        public int[] Next;

        public int Live { get; private set; }

        public void Add(int x, int y, float min, float max, bool walkable, byte source)
        {
            ref int head = ref Head.Slot(x, y);
            int previous = -1;
            int current = head;
            while (current != -1 && Max[current] < min)
            {
                previous = current;
                current = Next[current];
            }
            while (current != -1 && Min[current] <= max)
            {
                if (MathF.Abs(Max[current] - max) <= FlagMergeHeight)
                    walkable |= Walkable[current];
                else if (Max[current] > max)
                    walkable = Walkable[current];
                min = MathF.Min(min, Min[current]);
                max = MathF.Max(max, Max[current]);
                source |= Source[current];
                int following = Next[current];
                Release(current);
                current = following;
            }

            int span = Allocate();
            Min[span] = min;
            Max[span] = max;
            Walkable[span] = walkable;
            Source[span] = source;
            Next[span] = current;
            if (previous == -1)
                head = span;
            else
                Next[previous] = span;
        }

        private int Allocate()
        {
            Live++;
            if (_free != -1)
            {
                int reused = _free;
                _free = Next[reused];
                return reused;
            }
            if (_allocated == Min.Length)
            {
                int capacity = _allocated * 2;
                Array.Resize(ref Min, capacity);
                Array.Resize(ref Max, capacity);
                Array.Resize(ref Walkable, capacity);
                Array.Resize(ref Source, capacity);
                Array.Resize(ref Next, capacity);
            }
            return _allocated++;
        }

        private void Release(int span)
        {
            Live--;
            Next[span] = _free;
            _free = span;
        }
    }

    /// <summary>The footprints of walls in each column: the part of each wall polygon inside it, and its height range.</summary>
    private sealed class WallPieces
    {
        private readonly List<float> _points = new(1 << 16);

        private readonly NavColumnTiles<int> _head;

        /// <summary>How many wall pieces each tile of columns holds.</summary>
        private readonly int[] _tileCount;

        public WallPieces(int side)
        {
            _head = new NavColumnTiles<int>(side, -1);
            _tileCount = new int[_head.TilesPerSide * _head.TilesPerSide];
        }

        public List<int> Next { get; } = new(1 << 14);

        public List<float> Low { get; } = new(1 << 14);

        public List<float> High { get; } = new(1 << 14);

        private List<int> PointStart { get; } = new(1 << 14);

        private List<int> PointCount { get; } = new(1 << 14);

        public int Count => Next.Count;

        /// <summary>The first wall piece in a column, or -1.</summary>
        public int First(int x, int y) => _head.Get(x, y);

        /// <summary>Whether any column of a rectangle of columns may hold a wall piece.</summary>
        public bool AnyIn(int x0, int y0, int x1, int y1)
        {
            int columns = NavColumnTiles<int>.TileColumns;
            for (int tileY = y0 / columns; tileY <= y1 / columns; tileY++)
            {
                for (int tileX = x0 / columns; tileX <= x1 / columns; tileX++)
                {
                    if (_tileCount[(tileY * _head.TilesPerSide) + tileX] > 0)
                        return true;
                }
            }
            return false;
        }

        public void Add(int x, int y, ReadOnlySpan<Vector3> polygon, float low, float high)
        {
            ref int head = ref _head.Slot(x, y);
            int piece = Next.Count;
            Next.Add(head);
            Low.Add(low);
            High.Add(high);
            PointStart.Add(_points.Count / 2);
            PointCount.Add(polygon.Length);
            foreach (Vector3 point in polygon)
            {
                _points.Add(point.X);
                _points.Add(point.Y);
            }
            head = piece;
            int columns = NavColumnTiles<int>.TileColumns;
            _tileCount[((y / columns) * _head.TilesPerSide) + (x / columns)]++;
        }

        /// <summary>The horizontal distance from a point to a wall piece's footprint; zero inside it.</summary>
        public float Distance(int piece, float x, float y)
        {
            int start = PointStart[piece];
            int count = PointCount[piece];
            if (count >= 3 && Contains(start, count, x, y))
                return 0f;
            float nearest = float.PositiveInfinity;
            for (int index = 0; index < count; index++)
            {
                int next = index + 1 == count ? 0 : index + 1;
                nearest = MathF.Min(
                    nearest,
                    SegmentDistance(
                        x,
                        y,
                        _points[(start + index) * 2],
                        _points[((start + index) * 2) + 1],
                        _points[(start + next) * 2],
                        _points[((start + next) * 2) + 1]));
            }
            return nearest;
        }

        /// <summary>
        /// The horizontal distance between a segment and a wall piece's footprint,
        /// and how far along the segment, from 0 to 1, they come nearest.
        /// </summary>
        public (float Distance, float Along) DistanceToSegment(int piece, Vector2 start, Vector2 end)
        {
            int first = PointStart[piece];
            int count = PointCount[piece];
            if (count >= 3)
            {
                if (Contains(first, count, start.X, start.Y))
                    return (0f, 0f);
                if (Contains(first, count, end.X, end.Y))
                    return (0f, 1f);
            }

            float nearest = float.PositiveInfinity;
            float nearestAlong = 0f;
            for (int index = 0; index < count; index++)
            {
                int next = index + 1 == count ? 0 : index + 1;
                var edgeStart = new Vector2(_points[(first + index) * 2], _points[((first + index) * 2) + 1]);
                var edgeEnd = new Vector2(_points[(first + next) * 2], _points[((first + next) * 2) + 1]);
                if (Crosses(start, end, edgeStart, edgeEnd, out float crossing))
                    return (0f, crossing);
                (float fromCorner, float cornerAlong) = PointToSegment(edgeStart, start, end);
                if (fromCorner < nearest)
                {
                    nearest = fromCorner;
                    nearestAlong = cornerAlong;
                }
                float fromStart = PointToSegment(start, edgeStart, edgeEnd).Distance;
                if (fromStart < nearest)
                {
                    nearest = fromStart;
                    nearestAlong = 0f;
                }
                float fromEnd = PointToSegment(end, edgeStart, edgeEnd).Distance;
                if (fromEnd < nearest)
                {
                    nearest = fromEnd;
                    nearestAlong = 1f;
                }
            }
            return (nearest, nearestAlong);
        }

        private bool Contains(int start, int count, float x, float y)
        {
            bool positive = false;
            bool negative = false;
            for (int index = 0; index < count; index++)
            {
                int next = index + 1 == count ? 0 : index + 1;
                float ax = _points[(start + index) * 2];
                float ay = _points[((start + index) * 2) + 1];
                float bx = _points[(start + next) * 2];
                float by = _points[((start + next) * 2) + 1];
                float cross = ((bx - ax) * (y - ay)) - ((by - ay) * (x - ax));
                if (cross > 1e-6f)
                    positive = true;
                else if (cross < -1e-6f)
                    negative = true;
                if (positive && negative)
                    return false;
            }
            return positive != negative;
        }

        private static float SegmentDistance(float x, float y, float ax, float ay, float bx, float by)
        {
            float dx = bx - ax;
            float dy = by - ay;
            float lengthSquared = (dx * dx) + (dy * dy);
            float t = lengthSquared > 1e-12f
                ? Math.Clamp((((x - ax) * dx) + ((y - ay) * dy)) / lengthSquared, 0f, 1f)
                : 0f;
            float px = ax + (t * dx) - x;
            float py = ay + (t * dy) - y;
            return MathF.Sqrt((px * px) + (py * py));
        }

        private static (float Distance, float Along) PointToSegment(Vector2 point, Vector2 start, Vector2 end)
        {
            Vector2 along = end - start;
            float lengthSquared = along.LengthSquared();
            float t = lengthSquared > 1e-12f
                ? Math.Clamp(Vector2.Dot(point - start, along) / lengthSquared, 0f, 1f)
                : 0f;
            return (Vector2.Distance(point, start + (along * t)), t);
        }

        private static bool Crosses(Vector2 start, Vector2 end, Vector2 edgeStart, Vector2 edgeEnd, out float along)
        {
            Vector2 segment = end - start;
            Vector2 edge = edgeEnd - edgeStart;
            float denominator = (segment.X * edge.Y) - (segment.Y * edge.X);
            along = 0f;
            if (MathF.Abs(denominator) < 1e-9f)
                return false;
            Vector2 offset = edgeStart - start;
            along = ((offset.X * edge.Y) - (offset.Y * edge.X)) / denominator;
            float onEdge = ((offset.X * segment.Y) - (offset.Y * segment.X)) / denominator;
            return along >= 0f && along <= 1f && onEdge >= 0f && onEdge <= 1f;
        }
    }
}
