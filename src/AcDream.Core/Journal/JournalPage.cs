namespace AcDream.Core.Journal;

public sealed record JournalPage(
    string Label = "",
    string Title = "",
    string Notes = "",
    int TimerDays = 0,
    int TimerHours = 0,
    int TimerMinutes = 0,
    float LocationX = 0f,
    float LocationY = 0f,
    bool HasLocation = false,
    double RunningTimerSeconds = 0d)
{
    /// <summary>Authored <c>0x1E</c> on the label edit box.</summary>
    public const int MaxLabelLength = 16;

    /// <summary>Authored <c>0x1E</c> on the title edit box.</summary>
    public const int MaxTitleLength = 32;

    /// <summary>Authored <c>0x1E</c> on the notes edit box.</summary>
    public const int MaxNotesLength = 2048;

    /// <summary>An untouched page — what "New" produces.</summary>
    public static readonly JournalPage Empty = new();

    /// <summary>Whether the timer fields describe any duration at all.</summary>
    public bool HasTimer =>
        TimerDays != 0 || TimerHours != 0 || TimerMinutes != 0;

    /// <summary>The timer fields as a single duration.</summary>
    public TimeSpan TimerDuration =>
        new(TimerDays, TimerHours, TimerMinutes, 0);

    public bool IsTimerRunning => RunningTimerSeconds > 0d;

    public JournalPage Clipped() => this with
    {
        Label = Clip(Label, MaxLabelLength),
        Title = Clip(Title, MaxTitleLength),
        Notes = Clip(Notes, MaxNotesLength),
    };

    private static string Clip(string value, int max) =>
        value.Length <= max ? value : value[..max];
}
