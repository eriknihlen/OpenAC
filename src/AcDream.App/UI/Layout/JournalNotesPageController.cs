using System;
using System.Globalization;
using AcDream.Core.Journal;
using AcDream.Core.Ui;
using AcDream.Runtime.Gameplay;

namespace AcDream.App.UI.Layout;

public sealed class JournalNotesPageController
{
    private const uint PreviousButtonId = 0x10000565u;
    private const uint NextButtonId = 0x10000566u;
    private const uint NewButtonId = 0x10000567u;

    private const uint LabelFieldId = 0x10000569u;
    private const uint TitleFieldId = 0x1000056Bu;
    private const uint NotesFieldId = 0x1000056Du;

    private const uint FirstButtonId = 0x1000056Fu;
    private const uint PageNumberId = 0x10000570u;
    private const uint LastButtonId = 0x10000571u;

    private const uint LocationTextId = 0x10000573u;
    private const uint RecordButtonId = 0x10000574u;

    private const uint TimerDaysFieldId = 0x10000576u;
    private const uint TimerHoursFieldId = 0x10000578u;
    private const uint TimerMinutesFieldId = 0x1000057Au;

    private const uint TimerDaysLabelId = 0x10000577u;
    private const uint TimerHoursLabelId = 0x10000579u;
    private const uint TimerMinutesLabelId = 0x1000057Bu;
    private const uint RunningTimerTextId = 0x1000057Cu;
    private const uint StartButtonId = 0x1000057Du;

    public sealed record Bindings(
        IRuntimeJournalView Journal,
        RuntimeJournalState Commands,
        Func<uint> PlayerCell,
        Func<DateTime> Now);

    private readonly Bindings _bindings;
    private readonly UiField? _label;
    private readonly UiField? _title;
    private readonly UiField? _notes;
    private readonly UiField? _timerDays;
    private readonly UiField? _timerHours;
    private readonly UiField? _timerMinutes;
    private readonly UiText? _pageNumber;

    private readonly UiField? _location;
    private readonly UiText? _runningTimer;
    private readonly UiElement? _timerDaysLabel;
    private readonly UiElement? _timerHoursLabel;
    private readonly UiElement? _timerMinutesLabel;
    private readonly UiButton? _start;

    private long _renderedRevision = -1;

    public JournalNotesPageController(UiElement page, Bindings bindings)
    {
        ArgumentNullException.ThrowIfNull(page);
        _bindings = bindings ?? throw new ArgumentNullException(nameof(bindings));

        _label = UiElement.FindDescendant(page, LabelFieldId) as UiField;
        _title = UiElement.FindDescendant(page, TitleFieldId) as UiField;
        _notes = UiElement.FindDescendant(page, NotesFieldId) as UiField;
        _timerDays = UiElement.FindDescendant(page, TimerDaysFieldId) as UiField;
        _timerHours = UiElement.FindDescendant(page, TimerHoursFieldId) as UiField;
        _timerMinutes = UiElement.FindDescendant(page, TimerMinutesFieldId) as UiField;
        _pageNumber = UiElement.FindDescendant(page, PageNumberId) as UiText;
        _location = UiElement.FindDescendant(page, LocationTextId) as UiField;
        _runningTimer = UiElement.FindDescendant(page, RunningTimerTextId) as UiText;
        _timerDaysLabel = UiElement.FindDescendant(page, TimerDaysLabelId);
        _timerHoursLabel = UiElement.FindDescendant(page, TimerHoursLabelId);
        _timerMinutesLabel = UiElement.FindDescendant(page, TimerMinutesLabelId);
        _start = UiElement.FindDescendant(page, StartButtonId) as UiButton;

        // The three timer boxes take digits only. Their authored 0x1E is 2, so
        // the width is already handled; this stops a letter reaching an int
        // parse that would silently read as zero.
        foreach (UiField? field in new[] { _timerDays, _timerHours, _timerMinutes })
        {
            if (field is not null)
                field.CharacterFilter = static c => char.IsAsciiDigit(c);
        }

        foreach (UiField? field in new[] { _label, _title, _notes })
        {
            if (field is not null)
                field.OnFocusLost = _ => CommitText();
        }

        Bind(page, NewButtonId, () =>
        {
            CommitText();
            _bindings.Commands.NewPage();
            Refresh();
        });
        Bind(page, FirstButtonId, () => Navigate(1));
        Bind(page, LastButtonId, () => Navigate(_bindings.Journal.Snapshot.PageCount));
        Bind(page, PreviousButtonId,
            () => Navigate(_bindings.Journal.Snapshot.CurrentPage - 1));
        Bind(page, NextButtonId,
            () => Navigate(_bindings.Journal.Snapshot.CurrentPage + 1));
        Bind(page, RecordButtonId, RecordLocation);
        Bind(page, StartButtonId, ToggleTimer);

        Refresh();
    }

    public void CommitText()
    {
        if (_bindings.Journal.Snapshot.CurrentPage == 0)
            return;

        _bindings.Commands.UpdateCurrent(
            _label?.Text ?? string.Empty,
            _title?.Text ?? string.Empty,
            _notes?.Text ?? string.Empty);

        _bindings.Commands.SetTimer(
            ParseField(_timerDays),
            ParseField(_timerHours),
            ParseField(_timerMinutes));
    }

    public void Tick()
    {
        if (_bindings.Journal.Snapshot.Revision != _renderedRevision)
            Refresh();
        else
            RefreshTimer();
    }

    public void OnHidden() => CommitText();

    public void Refresh()
    {
        RuntimeJournalSnapshot snapshot = _bindings.Journal.Snapshot;
        _renderedRevision = snapshot.Revision;

        JournalPage page = _bindings.Journal.Current;

        _label?.SetText(page.Label);
        _title?.SetText(page.Title);
        _notes?.SetText(page.Notes);
        _timerDays?.SetText(Field(page.TimerDays));
        _timerHours?.SetText(Field(page.TimerHours));
        _timerMinutes?.SetText(Field(page.TimerMinutes));

        SetText(_pageNumber, snapshot.CurrentPage == 0
            ? string.Empty
            : $"~ {snapshot.CurrentPage.ToString(CultureInfo.InvariantCulture)} ~");

        _location?.SetText(page.HasLocation
            ? FormatLocation(page.LocationX, page.LocationY)
            : string.Empty);

        RefreshTimer();
    }

    private void RefreshTimer()
    {
        double remaining = _bindings.Journal.RemainingTimerSeconds(_bindings.Now());
        bool running = remaining > 0d;

        if (_timerDays is not null) _timerDays.Visible = !running;
        if (_timerHours is not null) _timerHours.Visible = !running;
        if (_timerMinutes is not null) _timerMinutes.Visible = !running;
        if (_timerDaysLabel is not null) _timerDaysLabel.Visible = !running;
        if (_timerHoursLabel is not null) _timerHoursLabel.Visible = !running;
        if (_timerMinutesLabel is not null) _timerMinutesLabel.Visible = !running;
        if (_runningTimer is not null) _runningTimer.Visible = running;

        SetText(_runningTimer, running
            ? RetailDurationText.Format(remaining)
            : string.Empty);

        if (_start is not null)
            _start.Label = running ? "Stop" : "Start";
    }

    private void Navigate(int pageNumber)
    {
        CommitText();
        _bindings.Commands.GotoPage(pageNumber);
        Refresh();
    }

    private void RecordLocation()
    {
        uint cell = _bindings.PlayerCell();
        if (cell == 0u)
            return;

        if (!AcDream.Core.Ui.RadarCoordinates.TryFromCell(cell, out var coordinates))
            return;

        _bindings.Commands.RecordLocation((float)coordinates.X, (float)coordinates.Y);
        Refresh();
    }

    private void ToggleTimer()
    {
        if (_bindings.Journal.RemainingTimerSeconds(_bindings.Now()) > 0d)
        {
            _bindings.Commands.ResetTimer();
            Refresh();
            return;
        }

        CommitText();
        _bindings.Commands.StartTimer(_bindings.Now());
        Refresh();
    }

    private static string FormatLocation(float x, float y)
    {
        var coordinates = new AcDream.Core.Ui.RadarCoordinates(x, y);
        return $"{coordinates.YText}, {coordinates.XText}";
    }

    private void Bind(UiElement page, uint elementId, Action action)
    {
        if (UiElement.FindDescendant(page, elementId) is UiButton button)
            button.OnClick = action;
    }

    private static int ParseField(UiField? field) =>
        int.TryParse(
            field?.Text,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out int value) && value >= 0
            ? value
            : 0;

    private static string Field(int value) =>
        value == 0 ? string.Empty : value.ToString(CultureInfo.InvariantCulture);

    private static void SetText(UiText? text, string value)
    {
        if (text is null) return;
        text.LinesProvider = () => [new UiText.Line(value, text.DefaultColor)];
    }
}
