using AcDream.Plugin.Abstractions;

namespace AcDream.Plugins.MossTank;

internal sealed class FellowshipManager
{
    private const int MaximumOtherMembers = 8;
    private const double RequestLifetimeSeconds = 300d;
    private const double VoteLifetimeSeconds = 120d;
    private const double VoteCallerCooldownSeconds = 240d;
    private const double RecruitRangeMeters = 10d;

    private readonly IPluginHost _host;
    private readonly List<WaitingPlayer> _waiting = [];
    private readonly HashSet<string> _banned =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> _voteCooldowns =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly List<FellowVote> _votes = [];
    private readonly Dictionary<string, Queue<double>> _tellRate =
        new(StringComparer.OrdinalIgnoreCase);
    private ulong _chatSequence;
    private double _now;
    private double _nextRecruitAt;
    private int _nextVoteId = 1;
    private bool _wasLeader;
    private bool _desiredOpen = true;

    public FellowshipManager(IPluginHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
    }

    public string Status { get; private set; } = "Fellow manager idle";
    public IReadOnlyList<string> WaitingNames =>
        _waiting.Select(static value => value.Name).ToArray();

    public void Tick(double elapsedSeconds, bool enabled)
    {
        _now += Math.Max(0d, elapsedSeconds);
        IReadOnlyList<PluginChatMessage> messages =
            _host.Automation.Chat.CaptureMessages(_chatSequence);
        foreach (PluginChatMessage message in messages)
        {
            _chatSequence = Math.Max(_chatSequence, message.Sequence);
            if (enabled && IsIncomingTell(message))
                HandleTell(message);
        }

        IFellowshipAutomation fellowship = _host.Automation.Fellowship;
        if (!enabled || !fellowship.IsInFellowship)
        {
            Status = enabled ? "Not in a fellowship" : "Fellow manager disabled";
            if (!fellowship.IsInFellowship)
                ResetSocialState();
            return;
        }

        bool isLeader = fellowship.LeaderObjectId == _host.Automation.Character.ObjectId;
        if (_wasLeader && !isLeader)
        {
            if (_votes.Count != 0)
                Fellow("[VT Fellow Manager] I am no longer the fellowship leader. All votes have been canceled.  -v-");
            _votes.Clear();
            _waiting.Clear();
            _banned.Clear();
        }
        _wasLeader = isLeader;

        RemoveJoinedPlayers(fellowship.CaptureRoster());
        ExpireVotes(isLeader);
        ExpireWaitingPlayers();
        if (isLeader)
            RecruitNext(fellowship);
        Status = isLeader
            ? $"Fellow leader — {_waiting.Count} waiting, {_votes.Count} vote(s)"
            : $"Fellow member — leader {LeaderName(fellowship)}";
    }

    public void Reset()
    {
        _chatSequence = 0u;
        _now = 0d;
        _nextRecruitAt = 0d;
        _nextVoteId = 1;
        _wasLeader = false;
        ResetSocialState();
        _tellRate.Clear();
        Status = "Fellow manager idle";
    }

    private void HandleTell(PluginChatMessage message)
    {
        string sender = message.Sender.Trim();
        string command = message.Text.Trim();
        if (sender.Length == 0 || command.Length == 0 || IsSpam(sender))
            return;

        IFellowshipAutomation fellowship = _host.Automation.Fellowship;
        IReadOnlyList<PluginFellowMember> roster = fellowship.CaptureRoster();
        bool isMember = roster.Any(member => member.Name.Equals(
            sender, StringComparison.OrdinalIgnoreCase));
        bool isLeader = fellowship.LeaderObjectId == _host.Automation.Character.ObjectId;

        if (command.Equals("xp", StringComparison.OrdinalIgnoreCase))
        {
            RequestRecruit(sender, message.SenderObjectId, roster, isLeader);
            return;
        }
        if (command.Equals("line", StringComparison.OrdinalIgnoreCase)
            || command.Equals("list", StringComparison.OrdinalIgnoreCase)
            || command.Equals("status", StringComparison.OrdinalIgnoreCase))
        {
            SendLineStatus(sender, fellowship, isLeader);
            return;
        }
        if (command.Equals("remove", StringComparison.OrdinalIgnoreCase))
        {
            RemoveWaiting(sender);
            Tell(sender, "[VT Fellow Manager] You have been removed from the list.  -v-");
            return;
        }
        if (command.Equals("leader", StringComparison.OrdinalIgnoreCase))
        {
            string openness = fellowship.IsOpen ? "open" : "closed";
            Tell(sender, isLeader
                ? $"[VT Fellow Manager] I am the fellowship leader. The fellowship is {openness}.  -v-"
                : $"[VT Fellow Manager] The leader is currently: {LeaderName(fellowship)}. The fellowship is {openness}.  -v-");
            return;
        }
        if (command.Equals("help", StringComparison.OrdinalIgnoreCase))
        {
            Tell(sender, "[VT Fellow Manager] Available commands: xp, line, remove, leader, startvote, vote, location, help  -v-");
            return;
        }
        if (command.Equals("help startvote", StringComparison.OrdinalIgnoreCase))
        {
            Tell(sender, "[VT Fellow Manager] Usage: startvote [votetype] [parameter]. Possible vote types: kick, ban, giveleader, setopen.  -v-");
            return;
        }
        if (command.Equals("help vote", StringComparison.OrdinalIgnoreCase))
        {
            Tell(sender, "[VT Fellow Manager] Usage: vote [vote id] [yes/no]  -v-");
            return;
        }
        if (command.StartsWith("startvote ", StringComparison.OrdinalIgnoreCase))
        {
            StartVote(sender, command, roster, isMember, isLeader);
            return;
        }
        if (command.StartsWith("vote ", StringComparison.OrdinalIgnoreCase))
        {
            CastVote(sender, command, isMember);
            return;
        }
        if (command.Equals("location", StringComparison.OrdinalIgnoreCase))
        {
            Tell(sender, isMember
                ? $"[VT Fellow Manager] I am currently located in landcell: {_host.Automation.Navigation.Snapshot.Position.CellId:X8}  -v-"
                : "[VT Fellow Manager] Sorry, I can only send my location to members of the fellowship.  -v-");
        }
    }

    private void RequestRecruit(
        string sender,
        uint senderObjectId,
        IReadOnlyList<PluginFellowMember> roster,
        bool isLeader)
    {
        if (roster.Any(member => member.Name.Equals(
                sender, StringComparison.OrdinalIgnoreCase)))
        {
            Tell(sender, "[VT Fellow Manager] You are already in the fellowship.  -v-");
            return;
        }
        if (_banned.Contains(sender))
        {
            Tell(sender, "[VT Fellow Manager] Sorry, but you have been banned from this fellowship.  -v-");
            return;
        }
        IFellowshipAutomation fellowship = _host.Automation.Fellowship;
        if (!isLeader && !fellowship.IsOpen)
        {
            Tell(sender, $"[VT Fellow Manager] I'm sorry, but the fellowship is closed and I am not the leader. The leader is currently: {LeaderName(fellowship)}  -v-");
            return;
        }
        WaitingPlayer? existing = _waiting.FirstOrDefault(value =>
            value.Name.Equals(sender, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            existing.ObjectId = senderObjectId != 0u ? senderObjectId : existing.ObjectId;
            existing.ExpiresAt = _now + RequestLifetimeSeconds;
            int position = _waiting.IndexOf(existing) + 1;
            Tell(sender, $"[VT Fellow Manager] You are already number {position} of {_waiting.Count} on the waiting list.  -v-");
            return;
        }

        _waiting.Add(new WaitingPlayer(
            sender,
            senderObjectId,
            _now + RequestLifetimeSeconds));
        if (isLeader && roster.Count >= MaximumOtherMembers + 1)
        {
            _desiredOpen = fellowship.IsOpen;
            fellowship.SetOpen(false);
            Tell(sender, $"[VT Fellow Manager] The fellow is full, and I am the leader. I am adding you to the waiting list at position {_waiting.Count}  -v-");
        }
        else
        {
            Tell(sender, "[VT Fellow Manager] I will recruit you in a moment. Please stand close to me.  -v-");
        }
    }

    private void RecruitNext(IFellowshipAutomation fellowship)
    {
        if (_waiting.Count == 0)
        {
            if (fellowship.IsOpen != _desiredOpen)
                fellowship.SetOpen(_desiredOpen);
            return;
        }
        if (fellowship.CaptureRoster().Count >= MaximumOtherMembers + 1)
        {
            if (fellowship.IsOpen)
                fellowship.SetOpen(false);
            return;
        }
        if (_now < _nextRecruitAt)
            return;
        WaitingPlayer player = _waiting[0];
        if (player.ObjectId == 0u || !IsNear(player.ObjectId))
        {
            player.Attempts++;
            _nextRecruitAt = _now + 1d;
            if (player.Attempts == 16)
                Tell(player.Name, "[VT Fellow Manager] You are too far away. I will wait 20 seconds and give you one more chance.  -v-");
            if (player.Attempts > 30)
            {
                Tell(player.Name, "[VT Fellow Manager] I'm sorry, but I couldn't recruit you. Please try again.  -v-");
                _waiting.RemoveAt(0);
            }
            return;
        }
        PluginFellowshipCommandResult result = fellowship.Recruit(player.ObjectId);
        _nextRecruitAt = _now + 1d;
        if (!result.Accepted)
            player.Attempts++;
    }

    private void StartVote(
        string sender,
        string command,
        IReadOnlyList<PluginFellowMember> roster,
        bool isMember,
        bool isLeader)
    {
        if (!isMember || _banned.Contains(sender))
            return;
        if (!isLeader)
        {
            Tell(sender, "[VT Fellow Manager] I am not the fellowship leader and cannot manage votes.  -v-");
            return;
        }
        if (_voteCooldowns.TryGetValue(sender, out double readyAt) && readyAt > _now)
        {
            Tell(sender, "[VT Fellow Manager] You have initiated a vote too recently.  -v-");
            return;
        }
        string[] parts = command.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3)
        {
            Tell(sender, "[VT Fellow Manager] Not enough parameters to startvote command. Tell me 'help startvote' for more information.  -v-");
            return;
        }
        string kindText = parts[1].ToLowerInvariant();
        string parameter = parts[2].Trim();
        FellowVoteKind kind;
        if (kindText is "kick" or "ban" or "giveleader")
        {
            if (!roster.Any(member => member.Name.Equals(
                    parameter, StringComparison.OrdinalIgnoreCase)))
            {
                Tell(sender, $"[VT Fellow Manager] Cannot vote to {kindText} {parameter}, that player is not in the fellow.  -v-");
                return;
            }
            kind = kindText switch
            {
                "kick" => FellowVoteKind.Kick,
                "ban" => FellowVoteKind.Ban,
                _ => FellowVoteKind.GiveLeader,
            };
        }
        else if (kindText == "setopen"
            && bool.TryParse(parameter, out _))
        {
            kind = FellowVoteKind.SetOpen;
            parameter = parameter.ToLowerInvariant();
        }
        else
        {
            Tell(sender, "[VT Fellow Manager] Unknown vote type. Tell me 'help startvote' for more information.  -v-");
            return;
        }
        if (_votes.Any(value => value.Kind == kind
            && value.Parameter.Equals(parameter, StringComparison.OrdinalIgnoreCase)))
        {
            Tell(sender, "[VT Fellow Manager] An identical vote is already in progress!  -v-");
            return;
        }
        var vote = new FellowVote(
            _nextVoteId++, kind, parameter, _now + VoteLifetimeSeconds);
        vote.Ballots[sender] = true;
        _votes.Add(vote);
        _voteCooldowns[sender] = _now + VoteCallerCooldownSeconds;
        Fellow($"[VT Fellow Manager] {sender} has called a new vote: {kindText} {parameter}! To vote, tell me 'vote {vote.Id} yes' or 'vote {vote.Id} no'. You have 2 minutes.  -v-");
        AnnounceVote(vote);
    }

    private void CastVote(string sender, string command, bool isMember)
    {
        if (!isMember || _banned.Contains(sender))
            return;
        string[] parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3
            || !int.TryParse(parts[1], out int id)
            || !(parts[2].Equals("yes", StringComparison.OrdinalIgnoreCase)
                || parts[2].Equals("no", StringComparison.OrdinalIgnoreCase)))
        {
            Tell(sender, "[VT Fellow Manager] Invalid vote command. Votes should look like: vote idnumber yes, or: vote idnumber no  -v-");
            return;
        }
        FellowVote? vote = _votes.FirstOrDefault(value => value.Id == id);
        if (vote is null)
        {
            Tell(sender, "[VT Fellow Manager] Invalid vote ID number. Votes should look like: vote idnumber yes, or: vote idnumber no  -v-");
            return;
        }
        vote.Ballots[sender] = parts[2].Equals("yes", StringComparison.OrdinalIgnoreCase);
        AnnounceVote(vote);
    }

    private void ExpireVotes(bool isLeader)
    {
        foreach (FellowVote vote in _votes.Where(value => value.ExpiresAt <= _now).ToArray())
        {
            _votes.Remove(vote);
            int yes = vote.Ballots.Values.Count(static value => value);
            int no = vote.Ballots.Count - yes;
            bool passed = yes > (yes + no) / 2;
            Fellow($"[VT Fellow Manager] Vote {vote.Description} {(passed ? "passed" : "failed")} ({yes}/{no}).  -v-");
            if (passed && isLeader)
                ExecuteVote(vote);
        }
    }

    private void ExecuteVote(FellowVote vote)
    {
        IFellowshipAutomation fellowship = _host.Automation.Fellowship;
        PluginFellowMember target = fellowship.CaptureRoster().FirstOrDefault(member =>
            member.Name.Equals(vote.Parameter, StringComparison.OrdinalIgnoreCase));
        switch (vote.Kind)
        {
            case FellowVoteKind.Kick when target.ObjectId != 0u:
                fellowship.Dismiss(target.ObjectId);
                break;
            case FellowVoteKind.Ban when target.ObjectId != 0u:
                _banned.Add(target.Name);
                fellowship.Dismiss(target.ObjectId);
                break;
            case FellowVoteKind.GiveLeader when target.ObjectId != 0u:
                fellowship.AssignLeader(target.ObjectId);
                break;
            case FellowVoteKind.SetOpen:
                _desiredOpen = bool.Parse(vote.Parameter);
                fellowship.SetOpen(_desiredOpen);
                break;
        }
    }

    private void SendLineStatus(
        string sender,
        IFellowshipAutomation fellowship,
        bool isLeader)
    {
        if (!isLeader)
        {
            Tell(sender, $"[VT Fellow Manager] The leader is currently: {LeaderName(fellowship)}. The fellowship is {(fellowship.IsOpen ? "open" : "closed")}.  -v-");
            return;
        }
        WaitingPlayer? waiting = _waiting.FirstOrDefault(value =>
            value.Name.Equals(sender, StringComparison.OrdinalIgnoreCase));
        if (waiting is null)
        {
            Tell(sender, _waiting.Count == 0
                ? $"[VT Fellow Manager] There is no waiting list. The fellowship has {fellowship.CaptureRoster().Count} members.  -v-"
                : $"[VT Fellow Manager] The waiting list contains {_waiting.Count} players. You are not on it.  -v-");
            return;
        }
        Tell(sender, $"[VT Fellow Manager] You are number {_waiting.IndexOf(waiting) + 1} of {_waiting.Count} on the waiting list.  -v-");
    }

    private void RemoveJoinedPlayers(IReadOnlyList<PluginFellowMember> roster)
    {
        _waiting.RemoveAll(waiting => roster.Any(member => member.Name.Equals(
            waiting.Name, StringComparison.OrdinalIgnoreCase)));
        foreach (FellowVote vote in _votes)
        {
            foreach (string voter in vote.Ballots.Keys
                .Where(name => !roster.Any(member => member.Name.Equals(
                    name, StringComparison.OrdinalIgnoreCase)))
                .ToArray())
            {
                vote.Ballots.Remove(voter);
            }
        }
    }

    private void ExpireWaitingPlayers()
    {
        foreach (WaitingPlayer player in _waiting
            .Where(value => value.ExpiresAt <= _now).ToArray())
        {
            _waiting.Remove(player);
            Tell(player.Name, "[VT Fellow Manager] Your spot in the fellowship has expired. You have been removed from the list.  -v-");
        }
    }

    private bool IsNear(uint objectId)
    {
        INavigationAutomation navigation = _host.Automation.Navigation;
        PluginNavigationSnapshot self = navigation.Snapshot;
        return self.IsAvailable
            && navigation.TryGetObject(objectId, out PluginNavigationObject target)
            && self.Position.HorizontalDistanceMeters(target.Position)
                <= RecruitRangeMeters;
    }

    private bool IsSpam(string sender)
    {
        if (!_tellRate.TryGetValue(sender, out Queue<double>? times))
        {
            times = new Queue<double>();
            _tellRate[sender] = times;
        }
        while (times.Count != 0 && times.Peek() <= _now - 180d)
            times.Dequeue();
        times.Enqueue(_now);
        return times.Count > 8;
    }

    private static bool IsIncomingTell(in PluginChatMessage message) =>
        message.Kind == 3 && message.SenderObjectId != 0u;

    private string LeaderName(IFellowshipAutomation fellowship) =>
        fellowship.CaptureRoster().FirstOrDefault(member =>
            member.ObjectId == fellowship.LeaderObjectId).Name is { Length: > 0 } name
                ? name
                : "????";

    private void RemoveWaiting(string name) => _waiting.RemoveAll(value =>
        value.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private void AnnounceVote(FellowVote vote)
    {
        int yes = vote.Ballots.Values.Count(static value => value);
        int no = vote.Ballots.Count - yes;
        Fellow($"[VT Fellow Manager] Vote total for {vote.Description}: {yes}/{no}  -v-");
    }

    private void Tell(string player, string text) =>
        _host.Automation.Chat.Submit($"/t {player}, {text}");

    private void Fellow(string text) =>
        _host.Automation.Chat.Submit("/f " + text);

    private void ResetSocialState()
    {
        _waiting.Clear();
        _banned.Clear();
        _voteCooldowns.Clear();
        _votes.Clear();
        _wasLeader = false;
    }

    private sealed class WaitingPlayer(
        string name,
        uint objectId,
        double expiresAt)
    {
        public string Name { get; } = name;
        public uint ObjectId { get; set; } = objectId;
        public double ExpiresAt { get; set; } = expiresAt;
        public int Attempts { get; set; }
    }

    private enum FellowVoteKind
    {
        Kick,
        Ban,
        GiveLeader,
        SetOpen,
    }

    private sealed class FellowVote(
        int id,
        FellowVoteKind kind,
        string parameter,
        double expiresAt)
    {
        public int Id { get; } = id;
        public FellowVoteKind Kind { get; } = kind;
        public string Parameter { get; } = parameter;
        public double ExpiresAt { get; } = expiresAt;
        public Dictionary<string, bool> Ballots { get; } =
            new(StringComparer.OrdinalIgnoreCase);
        public string Description => $"'{Kind} {Parameter}' (ID {Id})";
    }
}
