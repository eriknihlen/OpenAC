namespace AcDream.App.UI;

public enum UiMarkupListColumnKind
{
    Text,
    Check,
    Icon,
}

public sealed class UiMarkupListColumn
{
    public UiMarkupListColumnKind Kind { get; internal init; }
    public float Width { get; internal init; }
    public bool IsAutoWidth { get; internal init; }

    public Func<IReadOnlyList<string>>? TextSource { get; internal init; }
    public Func<IReadOnlyList<uint>>? ColorsSource { get; internal init; }
    public Action<int>? TextClicked { get; internal init; }

    public Func<IReadOnlyList<bool>>? CheckSource { get; internal init; }
    public Action<int>? CheckChanged { get; internal init; }

    public Func<IReadOnlyList<uint>>? IconValuesSource { get; internal init; }
    public Func<uint, (uint tex, int w, int h)>? IconResolve { get; internal init; }
    public Action<int>? IconClicked { get; internal init; }

    public static UiMarkupListColumn Text(
        float width,
        Func<IReadOnlyList<string>> textSource,
        Func<IReadOnlyList<uint>>? colorsSource,
        Action<int>? onClick = null,
        bool isAutoWidth = false) => new()
    {
        Kind = UiMarkupListColumnKind.Text,
        Width = width,
        IsAutoWidth = isAutoWidth,
        TextSource = textSource,
        ColorsSource = colorsSource,
        TextClicked = onClick,
    };

    public static UiMarkupListColumn Check(
        float width,
        Func<IReadOnlyList<bool>> checkSource,
        Action<int> onChange,
        bool isAutoWidth = false) => new()
    {
        Kind = UiMarkupListColumnKind.Check,
        Width = width,
        IsAutoWidth = isAutoWidth,
        CheckSource = checkSource,
        CheckChanged = onChange,
    };

    public static UiMarkupListColumn Icon(
        float width,
        Func<IReadOnlyList<uint>> valuesSource,
        Func<uint, (uint tex, int w, int h)>? resolve,
        Action<int> onClick,
        bool isAutoWidth = false) => new()
    {
        Kind = UiMarkupListColumnKind.Icon,
        Width = width,
        IsAutoWidth = isAutoWidth,
        IconValuesSource = valuesSource,
        IconResolve = resolve,
        IconClicked = onClick,
    };

    public int RowCount() => Kind switch
    {
        UiMarkupListColumnKind.Text => TextSource?.Invoke().Count ?? 0,
        UiMarkupListColumnKind.Check => CheckSource?.Invoke().Count ?? 0,
        UiMarkupListColumnKind.Icon => IconValuesSource?.Invoke().Count ?? 0,
        _ => 0,
    };
}
