using System.Globalization;

namespace AcDream.Plugins.MossTank.Expressions;

internal enum ExpressionValueKind
{
    Number,
    String,
    Boolean,
    List,
    Dictionary,
    Coordinates,
    WorldObject,
    Stopwatch,
    UiControl,
}

internal readonly record struct ExpressionCoordinates(
    double EastWest,
    double NorthSouth,
    double Elevation = 0d)
{
    public override string ToString()
    {
        string ns = NorthSouth < 0d ? "S" : "N";
        string ew = EastWest < 0d ? "W" : "E";
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{Math.Abs(NorthSouth):0.0}{ns}, {Math.Abs(EastWest):0.0}{ew}");
    }
}

internal sealed class ExpressionList
{
    public List<ExpressionValue> Items { get; } = [];

    public ExpressionList()
    {
    }

    public ExpressionList(IEnumerable<ExpressionValue> values) =>
        Items.AddRange(values);

    public override string ToString() =>
        $"[{string.Join(",", Items.Select(static item => item.ToDisplayString()))}]";
}

internal sealed class ExpressionDictionary
{
    public Dictionary<string, ExpressionValue> Items { get; } =
        new(StringComparer.Ordinal);

    public override string ToString() =>
        $"[{string.Join(",", Items.Select(static pair =>
            pair.Key + "=>" + pair.Value.ToDisplayString()))}]";
}

internal sealed class ExpressionStopwatch
{
    private readonly System.Diagnostics.Stopwatch _clock = new();

    public bool IsRunning => _clock.IsRunning;
    public double ElapsedSeconds => _clock.Elapsed.TotalSeconds;
    public void Start() => _clock.Start();
    public void Stop() => _clock.Stop();
    public void Reset() => _clock.Reset();
    public void Restart() => _clock.Restart();
    public override string ToString() =>
        ElapsedSeconds.ToString("0.###", CultureInfo.InvariantCulture);
}

internal readonly record struct ExpressionWorldObject(uint ObjectId);
internal readonly record struct ExpressionUiControl(string View, string Control);

internal readonly struct ExpressionValue : IEquatable<ExpressionValue>
{
    private readonly double _number;
    private readonly object? _reference;

    private ExpressionValue(
        ExpressionValueKind kind,
        double number,
        object? reference)
    {
        Kind = kind;
        _number = number;
        _reference = reference;
    }

    public ExpressionValueKind Kind { get; }

    public static ExpressionValue Zero => Number(0d);
    public static ExpressionValue One => Number(1d);
    public static ExpressionValue Number(double value) =>
        new(ExpressionValueKind.Number, value, null);
    public static ExpressionValue String(string? value) =>
        new(ExpressionValueKind.String, 0d, value ?? string.Empty);
    public static ExpressionValue Boolean(bool value) =>
        new(ExpressionValueKind.Boolean, value ? 1d : 0d, null);
    public static ExpressionValue List(ExpressionList value) =>
        new(ExpressionValueKind.List, 0d, value);
    public static ExpressionValue Dictionary(ExpressionDictionary value) =>
        new(ExpressionValueKind.Dictionary, 0d, value);
    public static ExpressionValue Coordinates(ExpressionCoordinates value) =>
        new(ExpressionValueKind.Coordinates, 0d, value);
    public static ExpressionValue WorldObject(uint objectId) =>
        new(ExpressionValueKind.WorldObject, objectId, null);
    public static ExpressionValue Stopwatch(ExpressionStopwatch value) =>
        new(ExpressionValueKind.Stopwatch, 0d, value);
    public static ExpressionValue UiControl(ExpressionUiControl value) =>
        new(ExpressionValueKind.UiControl, 0d, value);

    public double AsNumber(string? operation = null)
    {
        if (Kind is ExpressionValueKind.Number or ExpressionValueKind.Boolean)
            return _number;
        throw TypeError(operation ?? "operation", "number");
    }

    public int AsInt32(string? operation = null) =>
        Convert.ToInt32(AsNumber(operation), CultureInfo.InvariantCulture);

    public string AsString(string? operation = null)
    {
        if (Kind == ExpressionValueKind.String)
            return (string)_reference!;
        throw TypeError(operation ?? "operation", "string");
    }

    public ExpressionList AsList(string? operation = null) =>
        Kind == ExpressionValueKind.List
            ? (ExpressionList)_reference!
            : throw TypeError(operation ?? "operation", "list");

    public ExpressionDictionary AsDictionary(string? operation = null) =>
        Kind == ExpressionValueKind.Dictionary
            ? (ExpressionDictionary)_reference!
            : throw TypeError(operation ?? "operation", "dictionary");

    public ExpressionCoordinates AsCoordinates(string? operation = null) =>
        Kind == ExpressionValueKind.Coordinates
            ? (ExpressionCoordinates)_reference!
            : throw TypeError(operation ?? "operation", "coordinates");

    public ExpressionStopwatch AsStopwatch(string? operation = null) =>
        Kind == ExpressionValueKind.Stopwatch
            ? (ExpressionStopwatch)_reference!
            : throw TypeError(operation ?? "operation", "stopwatch");

    public ExpressionUiControl AsUiControl(string? operation = null) =>
        Kind == ExpressionValueKind.UiControl
            ? (ExpressionUiControl)_reference!
            : throw TypeError(operation ?? "operation", "UI control");

    public uint AsObjectId(string? operation = null) => Kind switch
    {
        ExpressionValueKind.WorldObject => checked((uint)_number),
        ExpressionValueKind.Number => checked((uint)_number),
        _ => throw TypeError(operation ?? "operation", "world object"),
    };

    public bool IsTruthy => Kind switch
    {
        ExpressionValueKind.Number or ExpressionValueKind.Boolean =>
            _number != 0d,
        ExpressionValueKind.String => ((string)_reference!).Length != 0,
        _ => true,
    };

    public string ToDisplayString() => Kind switch
    {
        ExpressionValueKind.Number =>
            _number.ToString("G15", CultureInfo.InvariantCulture),
        ExpressionValueKind.Boolean => _number != 0d ? "True" : "False",
        ExpressionValueKind.String => (string)_reference!,
        ExpressionValueKind.List => _reference!.ToString()!,
        ExpressionValueKind.Dictionary => _reference!.ToString()!,
        ExpressionValueKind.Coordinates => _reference!.ToString()!,
        ExpressionValueKind.WorldObject =>
            checked((uint)_number).ToString(CultureInfo.InvariantCulture),
        ExpressionValueKind.Stopwatch => _reference!.ToString()!,
        ExpressionValueKind.UiControl => _reference!.ToString()!,
        _ => string.Empty,
    };

    public bool Equals(ExpressionValue other)
    {
        if (Kind == ExpressionValueKind.String)
        {
            return other.Kind == ExpressionValueKind.String
                && string.Equals(
                    (string)_reference!,
                    (string)other._reference!,
                    StringComparison.OrdinalIgnoreCase);
        }
        if (Kind is ExpressionValueKind.Number or ExpressionValueKind.Boolean
            && other.Kind is ExpressionValueKind.Number
                or ExpressionValueKind.Boolean)
        {
            return _number.Equals(other._number);
        }
        return Kind == other.Kind && ReferenceEquals(_reference, other._reference);
    }

    public override bool Equals(object? obj) =>
        obj is ExpressionValue other && Equals(other);

    public override int GetHashCode() => Kind switch
    {
        ExpressionValueKind.String => StringComparer.OrdinalIgnoreCase.GetHashCode(
            (string)_reference!),
        ExpressionValueKind.Number or ExpressionValueKind.Boolean =>
            _number.GetHashCode(),
        _ => HashCode.Combine(Kind, _reference),
    };

    public override string ToString() => ToDisplayString();

    private ExpressionEvaluationException TypeError(
        string operation,
        string expected) => new(
        $"{operation} expects {expected}, but received {Kind}.");
}

internal sealed class ExpressionParseException : Exception
{
    public ExpressionParseException(string message, int offset)
        : base($"{message} at offset {offset}.") => Offset = offset;

    public int Offset { get; }
}

internal sealed class ExpressionEvaluationException : Exception
{
    public ExpressionEvaluationException(string message, int offset = -1)
        : base(offset < 0 ? message : $"{message} at offset {offset}.") =>
        Offset = offset;

    public int Offset { get; }
}
