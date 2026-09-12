using System.Globalization;
using AcDream.Core.Chat;
using AcDream.Core.Physics;
using AcDream.Core.Ui;
using AcDream.Core.Social;
using AcDream.Runtime.Chat;
using AcDream.UI.Abstractions;
using AcDream.UI.Abstractions.Panels.Chat;

namespace AcDream.App.UI;

public sealed class ClientCommandController
{
    public sealed record AdministrationBindings(
        Action<string, bool> BreakAllegianceBoot,
        Action<string, string> AllegianceChatBoot,
        Action<string, bool> AllegianceChatGag,
        Action<string> AllegianceBroadcast,
        Action ListAllegianceBans,
        Action<string> AddAllegianceBan,
        Action<string> RemoveAllegianceBan,
        Action ListAllegianceOfficers,
        Action ClearAllegianceOfficers,
        Action<string, uint> SetAllegianceOfficer,
        Action<string> RemoveAllegianceOfficer,
        Action ListAllegianceOfficerTitles,
        Action ClearAllegianceOfficerTitles,
        Action<uint, string> SetAllegianceOfficerTitle,
        Action QueryAllegianceName,
        Action<string> SetAllegianceName,
        Action ClearAllegianceName,
        Action<uint> AllegianceLockAction,
        Action<string> SetAllegianceApprovedVassal,
        Action<uint> AllegianceHouseAction,
        Action QueryMotd,
        Action<string> SetMotd,
        Action ClearMotd,
        Action<bool> SetOpenHouseStatus,
        Action<string> AddPermanentGuest,
        Action<string> RemovePermanentGuest,
        Action RemoveAllPermanentGuests,
        Action<string, bool> ChangeStoragePermission,
        Action AddAllStoragePermission,
        Action RemoveAllStoragePermission,
        Action RequestFullGuestList,
        Action<string> BootSpecificHouseGuest,
        Action BootEveryone,
        Action<bool> SetHooksVisibility,
        Action<bool> ModifyAllegianceGuestPermission,
        Action<bool> ModifyAllegianceStoragePermission);

    public sealed record Bindings(
        Action TeleportToLifestone,
        Action TeleportToMarketplace,
        Action TeleportToPkArena,
        Action TeleportToPkLiteArena,
        Action TeleportToHouse,
        Action TeleportToMansion,
        Action QueryAge,
        Action QueryBirth,
        Action ToggleFrameRate,
        Action ToggleUiLock,
        Action<string> ShowSystemMessage,
        Action<string> ShowClientLocalMessage,
        Action<uint> ShowWeenieError,
        Func<uint?> PlayerPublicWeenieBitfield,
        Func<string> ClientVersion,
        Func<Position?> CurrentPosition,
        Func<Position?> LastOutsideCorpsePosition,
        Action<string, Action<bool>> ShowConfirmation,
        Action Suicide,
        Action<bool> ClearChat,
        Func<string, ChatLogResult> SetChatLogFile,
        Action<string> SaveUi,
        Action<string> LoadUi,
        Action SaveAutoUi,
        Action LoadAutoUi,
        Func<bool> IsAway,
        Action<bool> SetAway,
        Action<string> SetAwayMessage,
        Func<bool> AcceptLootPermits,
        Action<bool> SetAcceptLootPermits,
        Action DisplayConsent,
        Action ClearConsent,
        Action<string> RemoveConsent,
        Action<string> SendEmote,
        FriendsState Friends,
        Action<string> AddFriend,
        Action<uint> RemoveFriend,
        Action ClearFriends,
        Action RequestLegacyFriends,
        SquelchState Squelch,
        Action<bool, uint, string, uint> ModifyCharacterSquelch,
        Action<bool, string> ModifyAccountSquelch,
        Action<bool, uint> ModifyGlobalSquelch,
        Func<string?> LastTeller,
        Action ClearDesiredComponents,
        Func<bool> HasOpenVendor,
        Action<uint?, uint> FillComponentBuyList,
        Action EnterPkLite,
        Func<bool> IsUsingTurbineChat,
        Action<string> SetChatTitle,
        Action<uint, bool> SetSingleCharacterOption,
        Action<string> AddPlayerPermission,
        Action<string> RemovePlayerPermission,
        Action<uint> RequestAvailableHouses,
        Action RequestChannelIndex,
        Action<uint> RequestChannelList,
        Action<uint> JoinGmChannel,
        Action<uint> LeaveGmChannel,
        Action RecallAllegianceHometown,
        Action<string> RequestAllegianceInfo,
        Action AbandonHouse,
        AdministrationBindings Administration,
        Func<bool> IsPersistentDaylight,
        Action<bool> SetPersistentDaylight,
        Action<int> SetLandscapeRadius,
        Action<float> SetFieldOfView);

    private readonly Bindings _bindings;
    private readonly RetailAdministrationCommandDispatcher _administration;

    public ClientCommandController(Bindings bindings)
    {
        _bindings = bindings ?? throw new ArgumentNullException(nameof(bindings));
        AdministrationBindings actions = bindings.Administration;
        _administration = new RetailAdministrationCommandDispatcher(
            new RetailAdministrationCommandDispatcher.FeedbackBindings(
                bindings.ShowSystemMessage,
                bindings.ShowClientLocalMessage,
                bindings.SetSingleCharacterOption,
                bindings.RequestAllegianceInfo),
            new RetailAdministrationCommandDispatcher.ActionBindings(
                actions.BreakAllegianceBoot,
                actions.AllegianceChatBoot,
                actions.AllegianceChatGag,
                actions.AllegianceBroadcast,
                actions.ListAllegianceBans,
                actions.AddAllegianceBan,
                actions.RemoveAllegianceBan,
                actions.ListAllegianceOfficers,
                actions.ClearAllegianceOfficers,
                actions.SetAllegianceOfficer,
                actions.RemoveAllegianceOfficer,
                actions.ListAllegianceOfficerTitles,
                actions.ClearAllegianceOfficerTitles,
                actions.SetAllegianceOfficerTitle,
                actions.QueryAllegianceName,
                actions.SetAllegianceName,
                actions.ClearAllegianceName,
                actions.AllegianceLockAction,
                actions.SetAllegianceApprovedVassal,
                actions.AllegianceHouseAction,
                actions.QueryMotd,
                actions.SetMotd,
                actions.ClearMotd,
                actions.SetOpenHouseStatus,
                actions.AddPermanentGuest,
                actions.RemovePermanentGuest,
                actions.RemoveAllPermanentGuests,
                actions.ChangeStoragePermission,
                actions.AddAllStoragePermission,
                actions.RemoveAllStoragePermission,
                actions.RequestFullGuestList,
                actions.BootSpecificHouseGuest,
                actions.BootEveryone,
                actions.SetHooksVisibility,
                actions.ModifyAllegianceGuestPermission,
                actions.ModifyAllegianceStoragePermission));
    }

    public void Execute(ExecuteClientCommandCmd command)
    {
        ArgumentNullException.ThrowIfNull(command);

        switch (command.Command)
        {
            case ClientCommandId.LifestoneRecall:
                _bindings.TeleportToLifestone();
                break;
            case ClientCommandId.MarketplaceRecall:
                _bindings.TeleportToMarketplace();
                break;
            case ClientCommandId.PkArenaRecall:
                if (HasPlayerFlag(EntityCollisionFlags.IsPK) == false)
                    _bindings.ShowWeenieError(0x055Fu);
                else
                    _bindings.TeleportToPkArena();
                break;
            case ClientCommandId.PkLiteArenaRecall:
                if (HasPlayerFlag(EntityCollisionFlags.IsPKLite) == false)
                    _bindings.ShowWeenieError(0x0560u);
                else
                    _bindings.TeleportToPkLiteArena();
                break;
            case ClientCommandId.EnterPkLite:
                if (HasPlayerFlag(EntityCollisionFlags.IsPK) == true
                    || HasPlayerFlag(EntityCollisionFlags.IsPKLite) == true)
                    _bindings.ShowWeenieError(0x0507u);
                else
                    _bindings.EnterPkLite();
                break;
            case ClientCommandId.HouseRecall:
                _bindings.TeleportToHouse();
                break;
            case ClientCommandId.MansionRecall:
                _bindings.TeleportToMansion();
                break;
            case ClientCommandId.QueryAge:
                _bindings.QueryAge();
                break;
            case ClientCommandId.QueryBirth:
                _bindings.QueryBirth();
                break;
            case ClientCommandId.ToggleFrameRate:
                _bindings.ToggleFrameRate();
                break;
            case ClientCommandId.TogglePersistentDaylight:
            {
                bool enabled = !_bindings.IsPersistentDaylight();
                _bindings.SetPersistentDaylight(enabled);
                _bindings.ShowSystemMessage(enabled
                    ? "Let there be light!"
                    : "Normality has been restored.");
                break;
            }
            case ClientCommandId.RenderOption:
                ExecuteRenderOption(command.Arguments);
                break;
            case ClientCommandId.ToggleUiLock:
                _bindings.ToggleUiLock();
                break;
            case ClientCommandId.ShowVersion:
                _bindings.ShowSystemMessage(_bindings.IsUsingTurbineChat()
                    ? $"Client version {_bindings.ClientVersion()}\nUsing Turbine Chat."
                    : $"Client version {_bindings.ClientVersion()}");
                break;
            case ClientCommandId.ShowLocation:
                Position? position = _bindings.CurrentPosition();
                _bindings.ShowSystemMessage(position is { ObjCellId: not 0u }
                    ? $"Your location is: {RetailPositionFormatter.Format(position.Value)}"
                    : "Not in valid cell!");
                break;
            case ClientCommandId.ShowLastCorpseLocation:
                Position? corpse = _bindings.LastOutsideCorpsePosition();
                string? coordinates = corpse is null
                    ? null
                    : RetailPositionFormatter.FormatOutdoorCell(corpse.Value.ObjCellId);
                _bindings.ShowSystemMessage(coordinates is null
                    ? "We're sorry, but we have no record of your last outside corpse location."
                    : $"The last time you died outside, your corpse was located at ({coordinates}).");
                break;
            case ClientCommandId.Die:
                _bindings.ShowConfirmation(
                    "Do you really want to kill your character? You may drop items and accrue a vitae penalty.",
                    accepted =>
                    {
                        if (accepted)
                            _bindings.Suicide();
                    });
                break;
            case ClientCommandId.ClearChat:
                _bindings.ClearChat(FirstArgument(command.Arguments)
                    .Equals("all", StringComparison.OrdinalIgnoreCase));
                break;
            case ClientCommandId.ChatLogFile:
                ExecuteChatLogFile(command.Arguments);
                break;
            case ClientCommandId.SaveUi:
                ExecuteUiProfile(command.Arguments, save: true);
                break;
            case ClientCommandId.LoadUi:
                ExecuteUiProfile(command.Arguments, save: false);
                break;
            case ClientCommandId.SaveAutoUi:
                if (RequireNoArguments(command.Arguments, "/saveautoui"))
                    _bindings.SaveAutoUi();
                break;
            case ClientCommandId.LoadAutoUi:
                if (RequireNoArguments(command.Arguments, "/loadautoui"))
                    _bindings.LoadAutoUi();
                break;
            case ClientCommandId.Away:
                ExecuteAway(command.Arguments);
                break;
            case ClientCommandId.Consent:
                ExecuteConsent(command.Arguments);
                break;
            case ClientCommandId.Emote:
                if (!string.IsNullOrWhiteSpace(command.Arguments))
                    _bindings.SendEmote(command.Arguments.Trim());
                break;
            case ClientCommandId.ListEmotes:
                _bindings.ShowSystemMessage(StandardEmotes);
                break;
            case ClientCommandId.Friends:
                ExecuteFriends(command.Arguments);
                break;
            case ClientCommandId.FriendsAdd:
                AddFriend(command.Arguments);
                break;
            case ClientCommandId.FriendsRemove:
                RemoveFriend(command.Arguments);
                break;
            case ClientCommandId.Squelch:
                ExecuteSquelch(command.Arguments, add: true);
                break;
            case ClientCommandId.Unsquelch:
                ExecuteSquelch(command.Arguments, add: false);
                break;
            case ClientCommandId.Filter:
                ExecuteGlobalFilter(command.Arguments, add: true);
                break;
            case ClientCommandId.Unfilter:
                ExecuteGlobalFilter(command.Arguments, add: false);
                break;
            case ClientCommandId.ListMessageTypes:
                _bindings.ShowSystemMessage(
                    "Squelch channels are as follows:\n  "
                    + string.Join(", ", MessageTypes.Values));
                break;
            case ClientCommandId.FillComponents:
                ExecuteFillComponents(command.Arguments);
                break;

            case ClientCommandId.Endurance:
                _bindings.ShowSystemMessage(RetailEnduranceText);
                break;
            case ClientCommandId.Speaker:
                _bindings.ShowSystemMessage(
                    "This command is no longer in use, please see @allegiance officer.");
                break;
            case ClientCommandId.SetChatTitle:
                _bindings.SetChatTitle(command.Arguments.Trim());
                break;
            case ClientCommandId.ChatToggle:
                _bindings.ModifyGlobalSquelch(
                    command.Arguments.Equals("off", StringComparison.OrdinalIgnoreCase),
                    2u);
                break;
            case ClientCommandId.NoTellToggle:
                _bindings.ModifyGlobalSquelch(
                    command.Arguments.Equals("on", StringComparison.OrdinalIgnoreCase),
                    3u);
                break;
            case ClientCommandId.JoinChannel:
                if (RetailClientCommandCatalog.TryResolveJoinLeaveOption(command.Arguments, out uint joinOption))
                    _bindings.SetSingleCharacterOption(joinOption, true);
                break;
            case ClientCommandId.LeaveChannel:
                if (RetailClientCommandCatalog.TryResolveJoinLeaveOption(command.Arguments, out uint leaveOption))
                    _bindings.SetSingleCharacterOption(leaveOption, false);
                break;
            case ClientCommandId.Permit:
                ExecutePermit(command.Arguments);
                break;
            case ClientCommandId.HouseAvailableList:
                if (RetailClientCommandCatalog.TryResolveHouseType(command.Arguments, out uint houseType))
                    _bindings.RequestAvailableHouses(houseType);
                break;
            case ClientCommandId.IndexChannels:
                _bindings.RequestChannelIndex();
                break;
            case ClientCommandId.ListChannel:
                if (RetailChannelTagTable.TryResolve(command.Arguments.Trim(), out uint listChannelId))
                    _bindings.RequestChannelList(listChannelId);
                else
                    _bindings.ShowWeenieError(0x0422u);
                break;
            case ClientCommandId.OnChannel:
                if (RetailChannelTagTable.TryResolve(command.Arguments.Trim(), out uint onChannelId))
                    _bindings.JoinGmChannel(onChannelId);
                else
                    _bindings.ShowWeenieError(0x0422u);
                break;
            case ClientCommandId.OffChannel:
                if (RetailChannelTagTable.TryResolve(command.Arguments.Trim(), out uint offChannelId))
                    _bindings.LeaveGmChannel(offChannelId);
                else
                    _bindings.ShowWeenieError(0x0422u);
                break;
            // GameActionRecallAllegianceHometown — @alh/@ah/"@allegiance hometown".
            case ClientCommandId.AllegianceHometown:
                _bindings.RecallAllegianceHometown();
                break;
            case ClientCommandId.AllegianceInfo:
            case ClientCommandId.AllegianceBoot:
            case ClientCommandId.AllegianceBan:
            case ClientCommandId.AllegianceChat:
            case ClientCommandId.AllegianceBroadcast:
            case ClientCommandId.AllegianceOfficer:
            case ClientCommandId.AllegianceOfficerTitle:
            case ClientCommandId.AllegianceName:
            case ClientCommandId.AllegianceLock:
            case ClientCommandId.AllegianceHouse:
            case ClientCommandId.AllegianceMotd:
            case ClientCommandId.AllegianceUnrecognizedSubcommand:
            case ClientCommandId.HouseOpenStatus:
            case ClientCommandId.HouseStorage:
            case ClientCommandId.HouseBoot:
            case ClientCommandId.HouseBootAll:
            case ClientCommandId.HouseGuests:
            case ClientCommandId.HouseHooks:
            case ClientCommandId.HouseUnrecognizedSubcommand:
                _ = _administration.TryExecute(command.Command, command.Arguments);
                break;
            case ClientCommandId.HouseAbandon:
                _bindings.ShowConfirmation(
                    "Do you really want to abandon your house? Any items in the house (on hooks or in storage) will stay with the house, and you will lose access to them.",
                    firstAccepted =>
                    {
                        if (!firstAccepted)
                            return;

                        _bindings.ShowConfirmation(
                            "Are you absolutely certain you wish to abandon your house? Click yes only if you are sure!",
                            secondAccepted =>
                            {
                                if (secondAccepted)
                                    _bindings.AbandonHouse();
                            });
                    });
                break;

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(command), command.Command, "Unknown retail client command.");
        }
    }


    private void ExecuteUiProfile(string arguments, bool save)
    {
        string[] parts = SplitArguments(arguments);
        string command = save ? "saveui" : "loadui";
        if (parts.Length > 1)
        {
            _bindings.ShowSystemMessage($"Please use @help {command} for proper usage.");
            return;
        }

        string name = parts.Length == 0 ? string.Empty : parts[0];
        if (name.Length > 16)
        {
            _bindings.ShowSystemMessage("The file name must be 16 characters or less.");
            return;
        }

        if (save) _bindings.SaveUi(name);
        else _bindings.LoadUi(name);
    }

    private void ExecuteRenderOption(string arguments)
    {
        string[] parts = SplitArguments(arguments);
        if (parts.Length == 0
            || parts[0].Equals("usage", StringComparison.OrdinalIgnoreCase))
        {
            _bindings.ShowSystemMessage(RetailCommandHelpTable.Render.TrimEnd('\n'));
            return;
        }

        if (parts[0].Equals("radius", StringComparison.OrdinalIgnoreCase))
        {
            if (parts.Length < 2)
            {
                _bindings.ShowSystemMessage("Must specify a radius");
                return;
            }

            int radius = RetailAtoi(parts[1]);
            if (radius is < 5 or > 25)
            {
                _bindings.ShowSystemMessage("Radius must be between 5 and 25");
                return;
            }

            _bindings.SetLandscapeRadius(radius);
            _bindings.ShowSystemMessage("Landscape radius set");
            return;
        }

        if (!parts[0].Equals("fov", StringComparison.OrdinalIgnoreCase))
            return;

        if (parts.Length < 2)
        {
            _bindings.ShowSystemMessage("Must specify a field of view");
            return;
        }

        int fieldOfView = RetailAtoi(parts[1]);
        if (fieldOfView is < 10 or > 160)
        {
            _bindings.ShowSystemMessage(
                "Field of view must be between 10 and 160");
            return;
        }

        _bindings.SetFieldOfView(fieldOfView);
        _bindings.ShowSystemMessage("Field of view set");
    }

    private static int RetailAtoi(string value)
    {
        if (string.IsNullOrEmpty(value))
            return 0;

        int index = 0;
        int sign = 1;
        if (value[0] is '+' or '-')
        {
            if (value[0] == '-')
                sign = -1;
            index++;
        }

        long result = 0;
        bool sawDigit = false;
        while (index < value.Length && value[index] is >= '0' and <= '9')
        {
            sawDigit = true;
            result = Math.Min(
                (long)int.MaxValue + (sign < 0 ? 1L : 0L),
                result * 10L + (value[index] - '0'));
            index++;
        }

        if (!sawDigit)
            return 0;
        long signed = sign < 0 ? -result : result;
        return (int)Math.Clamp(signed, int.MinValue, int.MaxValue);
    }

    private bool RequireNoArguments(string arguments, string usage)
    {
        if (string.IsNullOrWhiteSpace(arguments)) return true;
        _bindings.ShowSystemMessage($"Usage: {usage}");
        return false;
    }

    private void ExecuteChatLogFile(string arguments)
    {
        string name = arguments.Trim();
        ChatLogResult result = _bindings.SetChatLogFile(name);

        if (result.Closed)
            _bindings.ShowSystemMessage($"Chat log {result.ClosedName} closed.");

        if (name.Length == 0)
        {
            _bindings.ShowSystemMessage(result.Closed
                ? "Chat output now directed only to the screen."
                : "Please specify a file to append chat messages to.");
            return;
        }

        _bindings.ShowSystemMessage(result.Opened
            ? $"Copying chat to {result.Name}.  Run command again with no arguments to turn off logging."
            : $"Failed to redirect to file {result.Name}!");
    }

    private void ExecuteAway(string arguments)
    {
        string first = FirstArgument(arguments);
        if (first.Length == 0 || first.Equals("on", StringComparison.OrdinalIgnoreCase))
        {
            if (!_bindings.IsAway()) _bindings.SetAway(true);
            return;
        }

        if (first.Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            if (_bindings.IsAway()) _bindings.SetAway(false);
            return;
        }

        if (first.Equals("msg", StringComparison.OrdinalIgnoreCase))
        {
            string message = RemainderAfterFirstArgument(arguments).Trim(' ');
            if (message.Length > 191) message = message[..191];
            if (message.Length > 0 && !message.Contains('\n')) message += "\n";
            _bindings.SetAwayMessage(message);
            _bindings.ShowSystemMessage(message.Length == 0
                ? "New AFK message set: I am currently away from the keyboard."
                : $"New AFK message set: {message}");
            return;
        }

        _bindings.ShowSystemMessage(AwayHelp);
    }

    private void ExecuteConsent(string arguments)
    {
        string first = FirstArgument(arguments);
        if (first.Equals("on", StringComparison.OrdinalIgnoreCase))
        {
            if (!_bindings.AcceptLootPermits()) _bindings.SetAcceptLootPermits(true);
            _bindings.ShowSystemMessage(
                "You can now accept corpse looting permissions from other players.");
        }
        else if (first.Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            if (_bindings.AcceptLootPermits()) _bindings.SetAcceptLootPermits(false);
            _bindings.ShowSystemMessage(
                "You are no longer accepting corpse looting permissions from other players.");
        }
        else if (first.Equals("who", StringComparison.OrdinalIgnoreCase))
        {
            _bindings.DisplayConsent();
        }
        else if (first.Equals("clear", StringComparison.OrdinalIgnoreCase))
        {
            _bindings.ClearConsent();
        }
        else if (first.Equals("remove", StringComparison.OrdinalIgnoreCase))
        {
            string name = RemainderAfterFirstArgument(arguments).Trim();
            if (name.Length == 0)
                _bindings.ShowSystemMessage(
                    "Please specify a person to remove from your consent list.");
            else
                _bindings.RemoveConsent(name);
        }
        else
        {
            _bindings.ShowSystemMessage("Please specify a valid consent command.");
        }
    }

    private void ExecuteFriends(string arguments)
    {
        string operation = FirstArgument(arguments);
        if (operation.Length == 0)
        {
            DisplayFriends(onlineOnly: false);
            return;
        }

        if (operation.Equals("online", StringComparison.OrdinalIgnoreCase))
        {
            DisplayFriends(onlineOnly: true);
            return;
        }

        string remainder = RemainderAfterFirstArgument(arguments);
        if (operation.Equals("add", StringComparison.OrdinalIgnoreCase))
            AddFriend(remainder);
        else if (operation.Equals("remove", StringComparison.OrdinalIgnoreCase))
            RemoveFriend(remainder);
        else if (operation.Equals("old", StringComparison.OrdinalIgnoreCase)
                 && string.IsNullOrWhiteSpace(remainder))
            _bindings.RequestLegacyFriends();
        else
            _bindings.ShowSystemMessage("Invalid friends command specified.");
    }

    private void AddFriend(string arguments)
    {
        string name = arguments.Trim();
        if (name.Length == 0)
        {
            _bindings.ShowSystemMessage(
                "You must specify the name of the friend you wish to add.");
            return;
        }

        if (_bindings.Friends.Snapshot().Count >= 50)
        {
            _bindings.ShowWeenieError(0x0561u);
            return;
        }

        _bindings.AddFriend(name);
    }

    private void RemoveFriend(string arguments)
    {
        string name = arguments.Trim();
        if (name.Length == 0)
        {
            _bindings.ShowSystemMessage(
                "You must specify the name of the friend you wish to remove.");
            return;
        }

        if (name.Equals("-all", StringComparison.OrdinalIgnoreCase))
        {
            _bindings.ClearFriends();
            _bindings.Friends.Clear();
            _bindings.ShowSystemMessage("Your friends list has been cleared.\n");
            return;
        }

        FriendEntry? friend = _bindings.Friends.Snapshot().FirstOrDefault(
            entry => entry.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (friend is null)
            _bindings.ShowWeenieError(0x0563u);
        else
            _bindings.RemoveFriend(friend.Id);
    }

    private void DisplayFriends(bool onlineOnly)
    {
        IReadOnlyList<FriendEntry> entries = _bindings.Friends.Snapshot();
        if (entries.Count == 0)
        {
            _bindings.ShowSystemMessage("Your friends list is empty!\n");
            return;
        }

        var lines = entries
            .Where(entry => !onlineOnly || entry.Online)
            .Select(entry => $"  {entry.Name}{(entry.Online ? " (Online)" : string.Empty)}")
            .ToArray();
        _bindings.ShowSystemMessage(lines.Length == 0
            ? "Your friends:\n  You have no friends that are online.\n"
            : "Your friends:\n" + string.Join("\n", lines) + "\n");
    }

    private void ExecuteSquelch(string arguments, bool add)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            DisplayCharacterSquelches();
            return;
        }

        if (!TryParseSquelch(arguments, out SquelchArguments parsed, out string error))
        {
            _bindings.ShowSystemMessage(error);
            return;
        }

        if (parsed.AccountWide)
            _bindings.ModifyAccountSquelch(add, parsed.Name);
        else
            _bindings.ModifyCharacterSquelch(add, 0u, parsed.Name, parsed.MessageType);
    }

    private void ExecuteGlobalFilter(string arguments, bool add)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            DisplayGlobalFilters();
            return;
        }

        string[] parts = SplitArguments(arguments);
        if (parts.Length != 1 || !parts[0].StartsWith('-'))
        {
            _bindings.ShowSystemMessage("Incorrect usage, use @help for proper usage.");
            return;
        }

        if (!TryGetMessageType(parts[0][1..], out uint type) || type == 1u)
        {
            _bindings.ShowSystemMessage("You must specify a valid message type.");
            return;
        }

        _bindings.ModifyGlobalSquelch(add, type);
    }

    private bool TryParseSquelch(
        string arguments,
        out SquelchArguments parsed,
        out string error)
    {
        parsed = default;
        error = string.Empty;
        string[] parts = SplitArguments(arguments);
        bool account = false;
        uint messageType = 1u;
        string? replyName = null;
        int index = 0;
        for (; index < parts.Length && parts[index].StartsWith('-'); index++)
        {
            string option = parts[index][1..];
            if (option.Equals("account", StringComparison.OrdinalIgnoreCase))
                account = true;
            else if (option.Equals("reply", StringComparison.OrdinalIgnoreCase))
            {
                replyName = _bindings.LastTeller();
                if (string.IsNullOrWhiteSpace(replyName))
                {
                    error = "A player must @tell you before you can use the -reply option.";
                    return false;
                }
            }
            else if (!TryGetMessageType(option, out messageType))
            {
                error = $"\"{option}\" is not a valid squelch category.";
                return false;
            }
        }

        string name = replyName ?? string.Join(' ', parts.Skip(index));
        if (name.Length == 0)
        {
            error = "You have not specified a squelch target.";
            return false;
        }

        parsed = new SquelchArguments(account, messageType, name);
        return true;
    }

    private void DisplayCharacterSquelches()
    {
        SquelchDatabase database = _bindings.Squelch.Snapshot();
        var lines = database.Characters.Values
            .Where(info => info.MessageTypes.Count > 0)
            .Select(FormatSquelchInfo)
            .ToArray();
        _bindings.ShowSystemMessage(
            "(account) denotes a character whose account has also been squelched.\n"
            + "Format: Name : List of squelched message types.\n--------\n"
            + (lines.Length == 0 ? "none\n" : string.Join("\n", lines) + "\n"));
    }

    private void DisplayGlobalFilters()
    {
        SquelchInfo global = _bindings.Squelch.Snapshot().Global;
        string list = global.MessageTypes.Count == 0
            ? "none"
            : FormatMessageTypes(global.MessageTypes);
        _bindings.ShowSystemMessage(
            "The following types of messages are currently being filtered globally:\n"
            + list + "\n(For a list of filter options, type @help filter)\n");
    }

    private static string FormatSquelchInfo(SquelchInfo info) =>
        $"Name: {info.Name}{(info.AccountWide ? " (account) " : " ")}"
        + FormatMessageTypes(info.MessageTypes);

    private static string FormatMessageTypes(IReadOnlySet<uint> types)
    {
        if (types.Contains(1u)) return "All message types";
        return string.Join(", ", MessageTypes
            .Where(pair => pair.Key != 1u && types.Contains(pair.Key))
            .Select(pair => pair.Value));
    }

    private void ExecutePermit(string arguments)
    {
        string[] parts = SplitArguments(arguments);
        string name = string.Join(' ', parts, 1, parts.Length - 1);
        if (parts[0].Equals("add", StringComparison.OrdinalIgnoreCase))
            _bindings.AddPlayerPermission(name);
        else
            _bindings.RemovePlayerPermission(name);
    }

    private void ExecuteFillComponents(string arguments)
    {
        string[] parts = SplitArguments(arguments);
        if (parts.Length > 2)
        {
            _bindings.ShowSystemMessage("Please use @help fillcomps for proper usage.");
            return;
        }

        if (parts.Length > 0 && parts[0].Equals("clear", StringComparison.OrdinalIgnoreCase))
        {
            _bindings.ClearDesiredComponents();
            _bindings.ShowSystemMessage("Component list cleared.");
            return;
        }

        uint? category = null;
        uint maximumPrice = 0u;
        foreach (string part in parts)
        {
            if (uint.TryParse(
                    part, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint price))
            {
                if (price == 0)
                {
                    _bindings.ShowSystemMessage("Please specify a value greater than 0.");
                    return;
                }
                maximumPrice = price;
            }
            else if (TryGetComponentCategory(part, out uint parsedCategory))
            {
                category = parsedCategory;
            }
            else
            {
                _bindings.ShowSystemMessage("Invalid component type specified.");
                return;
            }
        }

        if (!_bindings.HasOpenVendor())
        {
            _bindings.ShowSystemMessage("You need an open vendor.");
            return;
        }

        _bindings.FillComponentBuyList(category, maximumPrice);
    }

    private static bool TryGetComponentCategory(string value, out uint category)
    {
        category = value.ToLowerInvariant() switch
        {
            "scarab" or "scarabs" => 0u,
            "herb" or "herbs" => 1u,
            "powderedgem" or "powderedgems" or "powder" or "powders" => 2u,
            "alchemicalsubstance" or "alchemicalsubstances" or "potion" or "potions" => 3u,
            "talisman" or "talismans" => 4u,
            "taper" or "tapers" => 5u,
            "pea" or "peas" => 6u,
            _ => uint.MaxValue,
        };
        return category != uint.MaxValue;
    }

    private static bool TryGetMessageType(string value, out uint type)
    {
        foreach ((uint key, string name) in MessageTypes)
        {
            if (name.Equals(value, StringComparison.OrdinalIgnoreCase))
            {
                type = key;
                return true;
            }
        }
        type = 0u;
        return false;
    }

    private static string FirstArgument(string arguments)
    {
        string trimmed = arguments.Trim();
        int separator = trimmed.IndexOfAny([' ', '\t', '\r', '\n']);
        return separator < 0 ? trimmed : trimmed[..separator];
    }

    private static string RemainderAfterFirstArgument(string arguments)
    {
        string trimmed = arguments.Trim();
        int separator = trimmed.IndexOfAny([' ', '\t', '\r', '\n']);
        return separator < 0 ? string.Empty : trimmed[(separator + 1)..].TrimStart();
    }

    private static string[] SplitArguments(string arguments) =>
        arguments.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    private readonly record struct SquelchArguments(
        bool AccountWide, uint MessageType, string Name);

    private static readonly IReadOnlyDictionary<uint, string> MessageTypes =
        new Dictionary<uint, string>
        {
            [1] = "All",
            [2] = "Speech",
            [3] = "Tell",
            [6] = "Combat",
            [7] = "Magic",
            [12] = "Emote",
            [16] = "Appraisal",
            [17] = "Spellcasting",
            [18] = "Allegiance",
            [19] = "Fellowship",
            [21] = "Combat_Enemy",
            [22] = "Combat_Self",
            [23] = "Recall",
            [24] = "Craft",
            [25] = "Salvaging",
        };

    private const string StandardEmotes =
        "Standard Emotes:\n"
        + "Note: These commands should be bound on either side by asterisks. (Example: *wave*)\n"
        + "ShakeFist; Beckon; BeSeeingYou; BlowKiss; BowDeep; ClapHands; Cry; Laugh; Nod; Point; Shrug; Wave; Akimbo; HeartyLaugh; Salute; TapFoot; WaveHigh; WaveLow; Yawn; Stretch; Cringe; Kneel; Plead; Shiver; Shoo; Slouch; Spit; Surrender; Woah; Winded; YMCA; Eat; Drink; Teapot; Pray; Mock; Cheer; Helper; Warm Hands; Scratch Head; Shake Head\n\n";

    private const string RetailEnduranceText =
        "The endurance attribute has a number of abilities tied to it.\n"
        + "First, some combination of strength and endurance (with endurance being more important) now allows one to regenerate hit points at a faster rate the higher one's endurance is.  This bonus is in addition to any regeneration spells one may have placed upon themselves.  This endurance regeneration bonus caps at around 110%.\n"
        + "Second, the higher a player's Endurance, the less stamina one uses while attacking.  This benefit is tied to Endurance only, and it caps out at around 50% less stamina used per attack.  The minimum stamina used per attack remains one.\n"
        + "Third, the higher a player's Endurance, the more likely they are not to use a point of stamina to successfully evade a missile or melee attack.  A player is required to have Melee Defense for melee attacks or Missile Defense for missile attacks trained or specialized in order for this specific ability to work.  This benefit is tied to Endurance only, and it caps out at around a 75% chance to avoid losing a point of stamina per successful evasion.\n"
        + "Fourth, some combination of strength and endurance (the two are roughly of equivalent importance) now allows one to partially resist drain and harm attacks, up to a maximum of roughly 50%.\n"
        + "Fifth, some combination of strength and endurance (the two are roughly of equivalent importance) now allows one to have a level of \"natural resistances\" to the 7 damage types, the same as a certain level of life protections.  This caps out at a 50% resistance (the equivalent to level 5 life prots) to these damage types.  This resistance is not additive to life protections: higher level life protections will overwrite these natural resistances, although life vulns will take these natural resistances into account, if the player does not have a higher level life protection cast upon him.\n"
        + "The natural resistances, drain resistances, and regeneration rate info are now visible on the Character Information Panel, in what was once the Burden panel.  This panel now displays the above three Endurance benefits, the burden info, as well as information about your age, birth date, and number of deaths.\n"
        + "The 5 categories for the endurance benefits are, in order from lowest benefit to highest: Poor, Mediocre, Hardy, Resilient, and Indomitable, with each range of benefits divided up equally amongst the 5 (e.g. Poor describes having anywhere from 1-10% resistance against drain health attacks, etc.).\n";

    private const string AwayHelp =
        "@afk - Turns on AFK (away-from-keyboard) mode. When set to AFK, other players that send you directed chatyou will receive a customizable message that your are not currently at the keyboard.\n"
        + "@afk on - Turns on AFK mode. When set to AFK, other players that send you directed chatyou will receive a customizable message that your are not currently at the keyboard.\n"
        + "@afk off - Turn off AFK mode.\n"
        + "@afk msg <message> - Set the message that will be sent to players that send you directed chat while you are in AFK mode. Issuing \"@afk msg\" with no message will set your AFK message back to the default. Your custom AFK message is limited to 192 characters.\n";

    private bool? HasPlayerFlag(EntityCollisionFlags flag)
    {
        uint? bitfield = _bindings.PlayerPublicWeenieBitfield();
        return bitfield is null
            ? null
            : (EntityCollisionFlagsExt.FromPwdBitfield(bitfield.Value) & flag) != 0;
    }
}
