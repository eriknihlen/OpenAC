using AcDream.Core.Net.Messages;

namespace AcDream.Runtime.Chat;

public sealed class RetailAdministrationCommandDispatcher
{
    public sealed record FeedbackBindings(
        Action<string> ShowSystemMessage,
        Action<string> ShowClientLocalMessage,
        Action<uint, bool> SetSingleCharacterOption,
        Action<string> RequestAllegianceInfo);

    public sealed record ActionBindings(
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

    private readonly FeedbackBindings _feedback;
    private readonly ActionBindings _actions;

    public RetailAdministrationCommandDispatcher(
        FeedbackBindings feedback,
        ActionBindings actions)
    {
        _feedback = feedback ?? throw new ArgumentNullException(nameof(feedback));
        _actions = actions ?? throw new ArgumentNullException(nameof(actions));
    }

    public bool TryExecute(ClientCommandId command, string arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        switch (command)
        {
            case ClientCommandId.AllegianceInfo:
                ExecuteAllegianceInfo(arguments);
                return true;
            case ClientCommandId.AllegianceBoot:
                ExecuteAllegianceBoot(arguments);
                return true;
            case ClientCommandId.AllegianceBan:
                ExecuteAllegianceBan(arguments);
                return true;
            case ClientCommandId.AllegianceChat:
                ExecuteAllegianceChat(arguments);
                return true;
            case ClientCommandId.AllegianceBroadcast:
                ExecuteAllegianceBroadcast(arguments);
                return true;
            case ClientCommandId.AllegianceOfficer:
                ExecuteAllegianceOfficer(arguments);
                return true;
            case ClientCommandId.AllegianceOfficerTitle:
                ExecuteAllegianceOfficerTitle(arguments);
                return true;
            case ClientCommandId.AllegianceName:
                ExecuteAllegianceName(arguments);
                return true;
            case ClientCommandId.AllegianceLock:
                ExecuteAllegianceLock(arguments);
                return true;
            case ClientCommandId.AllegianceHouse:
                ExecuteAllegianceHouse(arguments);
                return true;
            case ClientCommandId.AllegianceMotd:
                ExecuteAllegianceMotd(arguments);
                return true;
            case ClientCommandId.AllegianceUnrecognizedSubcommand:
                ShowAllegianceHelpRefusal();
                return true;
            case ClientCommandId.HouseOpenStatus:
                _actions.SetOpenHouseStatus(
                    arguments.Equals("open", StringComparison.OrdinalIgnoreCase));
                return true;
            case ClientCommandId.HouseStorage:
                ExecuteHouseStorage(arguments);
                return true;
            case ClientCommandId.HouseBoot:
                ExecuteHouseBoot(arguments);
                return true;
            case ClientCommandId.HouseBootAll:
                _actions.BootEveryone();
                return true;
            case ClientCommandId.HouseGuests:
                ExecuteHouseGuests(arguments);
                return true;
            case ClientCommandId.HouseHooks:
                ExecuteHouseHooks(arguments);
                return true;
            case ClientCommandId.HouseUnrecognizedSubcommand:
                ShowHouseHelpRefusal();
                return true;
            default:
                return false;
        }
    }

    private void ExecuteAllegianceInfo(string arguments)
    {
        string name = arguments.Trim();
        if (name.Length == 0)
            ShowClientLocal("Please specify an actual name.");
        else
            _feedback.RequestAllegianceInfo(name);
    }

    private void ExecuteAllegianceBoot(string arguments)
    {
        string name = arguments.Trim();
        if (name.Length == 0)
        {
            ShowClientLocal("Please specify an actual name.");
            return;
        }

        int accountFlag = name.IndexOf("-account", StringComparison.OrdinalIgnoreCase);
        bool accountBoot = accountFlag >= 0;
        if (accountBoot)
            name = name.Remove(accountFlag, "-account".Length).Trim();

        _feedback.ShowSystemMessage(
            $"Attempting to boot {name}{(accountBoot ? " (Account)" : string.Empty)}...");
        _actions.BreakAllegianceBoot(name, accountBoot);
    }

    private void ExecuteAllegianceBan(string arguments)
    {
        string operation = FirstArgument(arguments);
        if (operation.Equals("list", StringComparison.OrdinalIgnoreCase))
        {
            _actions.ListAllegianceBans();
            return;
        }

        string name = RemainderAfterFirstArgument(arguments).Trim();
        if (name.Length == 0)
        {
            ShowClientLocal("Please specify an actual name.");
            return;
        }

        if (operation.Equals("add", StringComparison.OrdinalIgnoreCase))
            _actions.AddAllegianceBan(name);
        else if (operation.Equals("remove", StringComparison.OrdinalIgnoreCase))
            _actions.RemoveAllegianceBan(name);
        else
            ShowAllegianceHelpRefusal();
    }

    private void ExecuteAllegianceChat(string arguments)
    {
        string operation = FirstArgument(arguments);
        if (operation.Equals("on", StringComparison.OrdinalIgnoreCase)
            || operation.Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            _feedback.SetSingleCharacterOption(
                (uint)CharacterOptionId.ListenToAllegianceChat,
                operation.Equals("on", StringComparison.OrdinalIgnoreCase));
            return;
        }

        string remainder = RemainderAfterFirstArgument(arguments).Trim();
        if (operation.Equals("kick", StringComparison.OrdinalIgnoreCase))
        {
            int comma = remainder.IndexOf(',');
            string name = comma < 0 ? remainder : remainder[..comma].Trim();
            string reason = comma < 0
                ? "No reason given."
                : remainder[(comma + 1)..].Trim();
            _actions.AllegianceChatBoot(name, reason);
            return;
        }

        bool gag = operation.Equals("gag", StringComparison.OrdinalIgnoreCase);
        bool ungag = operation.Equals("ungag", StringComparison.OrdinalIgnoreCase);
        if (!gag && !ungag)
        {
            ShowAllegianceHelpRefusal();
            return;
        }

        if (remainder.Length == 0)
        {
            ShowClientLocal("Please specify an actual name.");
            return;
        }

        _actions.AllegianceChatGag(remainder, gag);
    }

    private void ExecuteAllegianceBroadcast(string arguments)
    {
        string message = arguments.Trim();
        if (message.Length == 0)
            ShowAllegianceHelpRefusal();
        else
            _actions.AllegianceBroadcast(message);
    }

    private void ExecuteAllegianceOfficer(string arguments)
    {
        string operation = FirstArgument(arguments);
        if (operation.Length == 0
            || operation.Equals("list", StringComparison.OrdinalIgnoreCase))
        {
            _actions.ListAllegianceOfficers();
            return;
        }

        if (operation.Equals("clear", StringComparison.OrdinalIgnoreCase))
        {
            _actions.ClearAllegianceOfficers();
            return;
        }

        string remainder = RemainderAfterFirstArgument(arguments);
        if (operation.Equals("remove", StringComparison.OrdinalIgnoreCase))
        {
            string name = remainder.Trim();
            if (name.Length == 0)
                ShowClientLocal("Please specify the name of an allegiance member.");
            else
                _actions.RemoveAllegianceOfficer(name);
            return;
        }

        if (!operation.Equals("add", StringComparison.OrdinalIgnoreCase)
            && !operation.Equals("set", StringComparison.OrdinalIgnoreCase))
        {
            ShowAllegianceHelpRefusal();
            return;
        }

        string levelText = FirstArgument(remainder);
        int level = RetailStrtolBaseZero(levelText);
        if (level is < 1 or > 3)
        {
            ShowClientLocal(
                "Please specify a valid officer level as a number between 1 and 3. "
                + "Check the game help files for more information on officer levels.");
            return;
        }

        string officerName = RemainderAfterFirstArgument(remainder).Trim();
        if (officerName.Length == 0)
        {
            ShowClientLocal("Please specify the name of an allegiance member.");
            return;
        }

        _actions.SetAllegianceOfficer(officerName, (uint)level);
    }

    private void ExecuteAllegianceOfficerTitle(string arguments)
    {
        string operation = FirstArgument(arguments);
        if (operation.Length == 0
            || operation.Equals("list", StringComparison.OrdinalIgnoreCase))
        {
            _actions.ListAllegianceOfficerTitles();
            return;
        }

        if (operation.Equals("clear", StringComparison.OrdinalIgnoreCase))
        {
            _actions.ClearAllegianceOfficerTitles();
            return;
        }

        if (!operation.Equals("set", StringComparison.OrdinalIgnoreCase))
        {
            ShowAllegianceHelpRefusal();
            return;
        }

        string remainder = RemainderAfterFirstArgument(arguments);
        string levelText = FirstArgument(remainder);
        int level = RetailStrtolBaseZero(levelText);
        if (level is < 1 or > 3)
        {
            ShowClientLocal(
                "Please specify a valid officer level as a number between 1 and 3.");
            return;
        }

        string title = RemainderAfterFirstArgument(remainder).Trim();
        _actions.SetAllegianceOfficerTitle((uint)level, title);
    }

    private void ExecuteAllegianceName(string arguments)
    {
        string operation = FirstArgument(arguments);
        if (operation.Length == 0)
            _actions.QueryAllegianceName();
        else if (operation.Equals("set", StringComparison.OrdinalIgnoreCase))
            _actions.SetAllegianceName(RemainderAfterFirstArgument(arguments).Trim());
        else if (operation.Equals("clear", StringComparison.OrdinalIgnoreCase))
            _actions.ClearAllegianceName();
        else
            ShowAllegianceHelpRefusal();
    }

    private void ExecuteAllegianceLock(string arguments)
    {
        string operation = FirstArgument(arguments);
        uint? action = operation.ToLowerInvariant() switch
        {
            "" or "check" => 4u,
            "off" => 1u,
            "on" => 2u,
            "toggle" => 3u,
            _ => null,
        };
        if (action is not null)
        {
            _actions.AllegianceLockAction(action.Value);
            return;
        }

        if (!operation.Equals("bypass", StringComparison.OrdinalIgnoreCase))
        {
            ShowAllegianceHelpRefusal();
            return;
        }

        string approved = RemainderAfterFirstArgument(arguments).Trim();
        if (approved.Length == 0)
            _actions.AllegianceLockAction(5u);
        else if (approved.Equals("clear", StringComparison.OrdinalIgnoreCase))
            _actions.AllegianceLockAction(6u);
        else
            _actions.SetAllegianceApprovedVassal(approved);
    }

    private void ExecuteAllegianceHouse(string arguments)
    {
        string category = FirstArgument(arguments);
        if (category.Length == 0)
        {
            _actions.AllegianceHouseAction(1u);
            return;
        }

        string state = FirstArgument(RemainderAfterFirstArgument(arguments));
        uint action = (category.ToLowerInvariant(), state.ToLowerInvariant()) switch
        {
            ("guest", "open") => 2u,
            ("guest", "close") => 3u,
            ("storage", "open") => 4u,
            ("storage", "close") => 5u,
            _ => 0u,
        };
        if (action == 0u)
            ShowAllegianceHelpRefusal();
        else
            _actions.AllegianceHouseAction(action);
    }

    private void ExecuteAllegianceMotd(string arguments)
    {
        string operation = FirstArgument(arguments);
        if (operation.Length == 0)
            _actions.QueryMotd();
        else if (operation.Equals("set", StringComparison.OrdinalIgnoreCase))
            _actions.SetMotd(RemainderAfterFirstArgument(arguments).Trim());
        else if (operation.Equals("clear", StringComparison.OrdinalIgnoreCase))
            _actions.ClearMotd();
        else
            ShowAllegianceHelpRefusal();
    }

    private void ExecuteHouseGuests(string arguments)
    {
        string operation = FirstArgument(arguments);
        string name = RemainderAfterFirstArgument(arguments).Trim();
        if (operation.Equals("add", StringComparison.OrdinalIgnoreCase)
            || operation.Equals("remove", StringComparison.OrdinalIgnoreCase))
        {
            if (name.Length == 0)
            {
                ShowClientLocal("Please specify the guest's name.");
                return;
            }

            if (operation.Equals("add", StringComparison.OrdinalIgnoreCase))
                _actions.AddPermanentGuest(name);
            else
                _actions.RemovePermanentGuest(name);
            return;
        }

        if (operation.Equals("remove_all", StringComparison.OrdinalIgnoreCase))
            _actions.RemoveAllPermanentGuests();
        else if (operation.Equals("list", StringComparison.OrdinalIgnoreCase)
                 || operation.Equals("show", StringComparison.OrdinalIgnoreCase))
            _actions.RequestFullGuestList();
        else if (operation.Equals("add_allegiance", StringComparison.OrdinalIgnoreCase))
            _actions.ModifyAllegianceGuestPermission(true);
        else if (operation.Equals("remove_allegiance", StringComparison.OrdinalIgnoreCase))
            _actions.ModifyAllegianceGuestPermission(false);
        else
            ShowHouseHelpRefusal();
    }

    private void ExecuteHouseStorage(string arguments)
    {
        string operation = FirstArgument(arguments);
        string name = RemainderAfterFirstArgument(arguments).Trim();
        if (operation.Equals("add", StringComparison.OrdinalIgnoreCase)
            || operation.Equals("remove", StringComparison.OrdinalIgnoreCase))
        {
            if (name.Length == 0)
            {
                ShowClientLocal("Please specify an actual name.");
                return;
            }

            bool enabled = operation.Equals("add", StringComparison.OrdinalIgnoreCase);
            if (name.Equals("-all", StringComparison.OrdinalIgnoreCase))
            {
                if (enabled)
                    _actions.AddAllStoragePermission();
                else
                    _actions.RemoveAllStoragePermission();
            }
            else
            {
                _actions.ChangeStoragePermission(name, enabled);
            }
            return;
        }

        if (operation.Equals("remove_all", StringComparison.OrdinalIgnoreCase))
            _actions.RemoveAllStoragePermission();
        else if (operation.Equals("list", StringComparison.OrdinalIgnoreCase)
                 || operation.Equals("show", StringComparison.OrdinalIgnoreCase))
            _actions.RequestFullGuestList();
        else if (operation.Equals("add_allegiance", StringComparison.OrdinalIgnoreCase))
            _actions.ModifyAllegianceStoragePermission(true);
        else if (operation.Equals("remove_allegiance", StringComparison.OrdinalIgnoreCase))
            _actions.ModifyAllegianceStoragePermission(false);
        else
            ShowHouseHelpRefusal();
    }

    private void ExecuteHouseBoot(string arguments)
    {
        string name = arguments.Trim();
        if (name.Length == 0)
        {
            ShowHouseHelpRefusal();
            return;
        }

        if (name.Equals("-all", StringComparison.OrdinalIgnoreCase))
            _actions.BootEveryone();
        else
            _actions.BootSpecificHouseGuest(name);
    }

    private void ExecuteHouseHooks(string arguments)
    {
        string state = FirstArgument(arguments);
        if (state.Equals("on", StringComparison.OrdinalIgnoreCase))
            _actions.SetHooksVisibility(true);
        else if (state.Equals("off", StringComparison.OrdinalIgnoreCase))
            _actions.SetHooksVisibility(false);
        else
            ShowHouseHelpRefusal();
    }

    private void ShowAllegianceHelpRefusal() => ShowClientLocal(
        "Please see @help Allegiance for more information on how to use this command.");

    private void ShowHouseHelpRefusal() => ShowClientLocal(
        "Please see @help House for more information on how to use this command.");

    private void ShowClientLocal(string text) =>
        _feedback.ShowClientLocalMessage(text);

    private static int RetailStrtolBaseZero(string value)
    {
        if (string.IsNullOrEmpty(value))
            return 0;

        int index = 0;
        int sign = 1;
        if (value[index] is '+' or '-')
        {
            if (value[index] == '-')
                sign = -1;
            if (++index == value.Length)
                return 0;
        }

        int numberBase = 10;
        if (value[index] == '0')
        {
            numberBase = 8;
            if (index + 2 < value.Length
                && value[index + 1] is 'x' or 'X'
                && HexDigit(value[index + 2]) >= 0)
            {
                numberBase = 16;
                index += 2;
            }
        }

        long result = 0;
        bool sawDigit = false;
        while (index < value.Length)
        {
            int digit = HexDigit(value[index]);
            if (digit < 0 || digit >= numberBase)
                break;
            sawDigit = true;
            result = Math.Min(
                (long)int.MaxValue + (sign < 0 ? 1L : 0L),
                result * numberBase + digit);
            index++;
        }

        if (!sawDigit)
            return 0;
        long signed = sign < 0 ? -result : result;
        return (int)Math.Clamp(signed, int.MinValue, int.MaxValue);

        static int HexDigit(char c) => c switch
        {
            >= '0' and <= '9' => c - '0',
            >= 'a' and <= 'f' => c - 'a' + 10,
            >= 'A' and <= 'F' => c - 'A' + 10,
            _ => -1,
        };
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
}
