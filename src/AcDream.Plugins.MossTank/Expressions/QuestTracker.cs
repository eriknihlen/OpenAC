using System.Globalization;
using System.Text.RegularExpressions;
using AcDream.Plugin.Abstractions;

namespace AcDream.Plugins.MossTank.Expressions;

internal sealed partial class QuestTracker(IPluginHost host)
{
    private const double CompletionSilenceSeconds = 1d;
    private const double RetrySeconds = 15d;
    private const int MaximumAttempts = 3;

    private readonly Dictionary<string, QuestFlag> _flags =
        new(StringComparer.OrdinalIgnoreCase);
    private ulong _chatSequence;
    private string _identity = string.Empty;
    private double _silenceSeconds;
    private int _attemptsRemaining;
    private bool _receivedFlag;

    public bool IsRefreshing { get; private set; }
    public int Count => _flags.Count;

    public void BindIdentity(string identity)
    {
        if (identity.Equals(_identity, StringComparison.Ordinal))
            return;
        _identity = identity;
        _flags.Clear();
        IsRefreshing = false;
        _receivedFlag = false;
        _silenceSeconds = 0d;
        if (!string.IsNullOrWhiteSpace(identity))
            Refresh();
    }

    public void Refresh()
    {
        if (IsRefreshing)
            return;
        _flags.Clear();
        _attemptsRemaining = MaximumAttempts;
        _receivedFlag = false;
        _silenceSeconds = 0d;
        IsRefreshing = true;
        SubmitRequest();
    }

    public void OnTick(double elapsedSeconds)
    {
        CaptureChat();
        if (!IsRefreshing)
            return;
        _silenceSeconds += elapsedSeconds;
        if (_receivedFlag && _silenceSeconds > CompletionSilenceSeconds)
        {
            IsRefreshing = false;
            return;
        }
        if (!_receivedFlag && _silenceSeconds > RetrySeconds)
            SubmitRequest();
    }

    public bool HasCompleted(string key) =>
        _flags.ContainsKey(Normalize(key));

    public bool IsReady(string key)
    {
        if (!_flags.TryGetValue(Normalize(key), out QuestFlag flag))
            return true;
        DateTimeOffset next = flag.CompletedOn.AddSeconds(flag.RepeatSeconds);
        if (next > DateTimeOffset.UtcNow)
            return false;
        return !(flag.MaxSolves == 1 && flag.Solves <= 1);
    }

    public int Progress(string key) =>
        _flags.TryGetValue(Normalize(key), out QuestFlag flag) ? flag.Solves : 0;

    public int Required(string key) =>
        _flags.TryGetValue(Normalize(key), out QuestFlag flag) ? flag.MaxSolves : 0;

    private void CaptureChat()
    {
        foreach (PluginChatMessage message in host.Automation.Chat
            .CaptureMessages(_chatSequence).OrderBy(static message => message.Sequence))
        {
            _chatSequence = Math.Max(_chatSequence, message.Sequence);
            string text = message.Text.Trim();
            if (text.Equals("Quest list is empty.", StringComparison.Ordinal)
                || text.Equals(
                    "The command \"myquests\" is not currently enabled on this server.",
                    StringComparison.Ordinal))
            {
                IsRefreshing = false;
                continue;
            }
            Match match = MyQuestLine().Match(text);
            if (!match.Success)
                continue;
            if (!int.TryParse(match.Groups["solves"].Value,
                    NumberStyles.Integer, CultureInfo.InvariantCulture, out int solves)
                || !long.TryParse(match.Groups["completedOn"].Value,
                    NumberStyles.Integer, CultureInfo.InvariantCulture, out long completed))
            {
                continue;
            }
            _ = int.TryParse(match.Groups["maxSolves"].Value,
                NumberStyles.Integer, CultureInfo.InvariantCulture, out int maximum);
            _ = long.TryParse(match.Groups["repeatTime"].Value,
                NumberStyles.Integer, CultureInfo.InvariantCulture, out long repeat);
            string key = Normalize(match.Groups["key"].Value);
            _flags[key] = new QuestFlag(
                solves,
                maximum,
                DateTimeOffset.FromUnixTimeSeconds(Math.Max(0L, completed)),
                Math.Max(0L, repeat));
            _receivedFlag = true;
            _silenceSeconds = 0d;
        }
    }

    private void SubmitRequest()
    {
        if (_attemptsRemaining <= 0)
        {
            IsRefreshing = false;
            return;
        }
        _attemptsRemaining--;
        _silenceSeconds = 0d;
        host.Automation.Chat.Submit("/myquests");
    }

    private static string Normalize(string key) => key.Trim().ToLowerInvariant();

    [GeneratedRegex(
        "(?<key>\\S+) \\- (?<solves>\\d+) solves \\((?<completedOn>\\d{0,11})\\)\"?((?<description>.*)\" (?<maxSolves>.*) (?<repeatTime>\\d{0,11}))?.*$",
        RegexOptions.CultureInvariant,
        100)]
    private static partial Regex MyQuestLine();

    private readonly record struct QuestFlag(
        int Solves,
        int MaxSolves,
        DateTimeOffset CompletedOn,
        long RepeatSeconds);
}
