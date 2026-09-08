using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AcDream.UI.Abstractions.Input;

namespace AcDream.App.Input;

public static class RetailKeymapFile
{
    private static readonly Regex BindingLine = new(
        "^(?<action>[A-Za-z0-9_]+)\\s*\\[\\s*\"\"\\s*\\[\\s*"
        + "(?<device>[0-9]+)\\s+(?<control>[A-Za-z0-9_]+)"
        + "(?:\\s+(?<sub>[A-Za-z]+))?\\s*\\]"
        + "(?:\\s+(?<modifier>0x[0-9A-Fa-f]+|[0-9]+))?"
        + "(?:\\s+(?<activation>[A-Za-z]+))?\\s*\\]$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly (uint Id, string Name)[] GroupOrder =
    {
        (0x00000004u, "MovementCommands"),
        (0x10000007u, "ItemSelectionCommands"),
        (0x10000009u, "UICommands"),
        (0x1000000Cu, "QuickslotCommands"),
        (0x1000000Du, "ToggleChatEntry"),
        (0x1000000Au, "ChatCommands"),
        (0x10000002u, "Combat"),
        (0x10000003u, "MeleeCombat"),
        (0x10000004u, "MissileCombat"),
        (0x10000005u, "MagicCombat"),
        (0x10000006u, "Emotes"),
        (0x00000005u, "CameraControls"),
        (0x00000006u, "CameraAlternateControls"),
        (0x10000008u, "CharacterOptionCommands"),
    };

    private static readonly IReadOnlyDictionary<string, uint> GroupIds =
        GroupOrder.ToDictionary(static group => group.Name, static group => group.Id,
            StringComparer.OrdinalIgnoreCase);

    private static readonly Lazy<IReadOnlyDictionary<InputAction, string>> ActionNamesHolder =
        new(BuildActionNames);
    private static readonly Lazy<IReadOnlyDictionary<string, InputAction>> ActionsByFileNameHolder =
        new(BuildActionsByFileName);
    private static IReadOnlyDictionary<InputAction, string> ActionNames => ActionNamesHolder.Value;
    private static IReadOnlyDictionary<string, InputAction> ActionsByFileName =>
        ActionsByFileNameHolder.Value;

    public static KeyBindings Parse(string text, KeyBindings baseBindings)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(baseBindings);

        var result = new KeyBindings();
        foreach (Binding binding in baseBindings.All)
        {
            if (!RetailActionIdentityTable.ReverseMap.ContainsKey(binding.Action))
                result.Add(binding);
        }

        bool foundBindings = false;
        bool inBindings = false;
        uint? currentGroup = null;
        int lineNumber = 0;
        foreach (string rawLine in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            lineNumber++;
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            if (line.Equals("Bindings", StringComparison.OrdinalIgnoreCase))
            {
                foundBindings = true;
                inBindings = true;
                currentGroup = null;
                continue;
            }
            if (!inBindings)
                continue;

            if (Regex.IsMatch(line, "^[A-Za-z][A-Za-z0-9_]*$",
                    RegexOptions.CultureInvariant))
            {
                currentGroup = GroupIds.TryGetValue(line, out uint groupId)
                    ? groupId
                    : null;
                continue;
            }
            if (currentGroup is not uint inputMapId || line is "[" or "]")
                continue;

            Match match = BindingLine.Match(line);
            if (!match.Success)
                throw new FormatException(
                    $"Malformed retail key binding at line {lineNumber}: {line}");

            string actionName = match.Groups["action"].Value;
            if (!ActionsByFileName.TryGetValue(FileIdentity(inputMapId, actionName), out InputAction action))
            {
                continue;
            }

            string control = match.Groups["control"].Value;
            if (!RetailScanCodeMap.TryFromFileControl(control, out uint scan, out uint tokenDevice)
                || !uint.TryParse(match.Groups["device"].Value,
                    NumberStyles.None, CultureInfo.InvariantCulture, out uint device)
                || device != tokenDevice
                || RetailScanCodeMap.ToSilkKey(scan, device) is not { } key)
            {
                throw new FormatException(
                    $"Unsupported retail control '{control}' at line {lineNumber}.");
            }

            uint fileModifier = 0u;
            if (match.Groups["modifier"].Success)
            {
                string value = match.Groups["modifier"].Value;
                NumberStyles style = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                    ? NumberStyles.AllowHexSpecifier
                    : NumberStyles.None;
                string digits = style == NumberStyles.AllowHexSpecifier ? value[2..] : value;
                if (!uint.TryParse(digits, style, CultureInfo.InvariantCulture, out fileModifier))
                    throw new FormatException($"Invalid modifier at line {lineNumber}.");
            }

            var chord = new KeyChord(key, (ModifierMask)(fileModifier & 0x0Fu), (byte)device);
            result.Add(new Binding(
                chord,
                action,
                RetailActionIdentityTable.ActivationFor(inputMapId,
                    RetailActionIdentityTable.ReverseMap[action].ActionId),
                RetailActionIdentityTable.ScopeForInputMap(inputMapId)));
        }

        if (!foundBindings)
            throw new FormatException("The file does not contain a retail Bindings section.");
        return result;
    }

    public static string Write(KeyBindings bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        var output = new StringBuilder(24_000);
        output.AppendLine("#Asheron's Call: Throne of Destiny Keymap File")
            .AppendLine("#")
            .AppendLine("#Generated by acdream's retail Configure Keyboard screen.")
            .AppendLine("#This file is compatible with the Sept-2013 retail PFile keymap grammar.")
            .AppendLine("#")
            .AppendLine("\"User Defined Keymap\" [ 00000000-0000-0000-0000-000000000000 ]")
            .AppendLine()
            .AppendLine("Devices")
            .AppendLine("[")
            .AppendLine("  Keyboard [ GUID_SysKeyboard ]")
            .AppendLine("  Mouse [ GUID_SysMouse ]")
            .AppendLine("  Virtual [ GUID_Virtual ]")
            .AppendLine("]")
            .AppendLine()
            .AppendLine("MetaKeys")
            .AppendLine("[")
            .AppendLine("  1 [ 0 DIK_LSHIFT ]")
            .AppendLine("  2 [ 0 DIK_LCONTROL ]")
            .AppendLine("  2 [ 0 DIK_RCONTROL ]")
            .AppendLine("  3 [ 0 DIK_LMENU ]")
            .AppendLine("  3 [ 0 DIK_RALT ]")
            .AppendLine("  4 [ 0 DIK_LWIN ]")
            .AppendLine("  4 [ 0 DIK_RWIN ]")
            .AppendLine("  5 [ 1 DIMOFS_BUTTON3 ]")
            .AppendLine("  6 [ 1 DIMOFS_BUTTON4 ]")
            .AppendLine("]")
            .AppendLine()
            .AppendLine("Bindings")
            .AppendLine("[");

        foreach ((uint inputMapId, string groupName) in GroupOrder)
        {
            output.Append("  ").AppendLine(groupName).AppendLine("  [");
            foreach (((uint InputMapId, uint ActionId) identity, InputAction action) in
                RetailActionIdentityTable.Map
                    .Where(pair => pair.Key.InputMapId == inputMapId)
                    .OrderBy(static pair => pair.Key.ActionId))
            {
                foreach (Binding binding in bindings.ForAction(action))
                {
                    if (!RetailScanCodeMap.TryToFileControl(binding.Chord, out string control))
                    {
                        throw new InvalidOperationException(
                            $"{binding.Chord} cannot be represented by the retail DirectInput keymap.");
                    }

                    output.Append("    ").Append(ActionNames[action])
                        .Append(" [ \"\" [ ").Append(binding.Chord.Device)
                        .Append(' ').Append(control).Append(" ]");
                    uint modifier = (uint)binding.Chord.Modifiers & 0x0Fu;
                    if (modifier != 0u)
                        output.Append(" 0x").Append(modifier.ToString("X8", CultureInfo.InvariantCulture));
                    output.AppendLine(" ]");
                }
            }
            if (inputMapId == 0x10000009u)
                output.AppendLine("    EscapeKey [ \"\" [ 0 DIK_ESCAPE ] ]");
            output.AppendLine("  ]").AppendLine();
        }

        output.Append(FixedRetailMaps);
        output.AppendLine("]");
        return output.ToString();
    }

    private static IReadOnlyDictionary<InputAction, string> BuildActionNames()
    {
        var names = new Dictionary<InputAction, string>();
        foreach (InputAction action in RetailActionIdentityTable.ReverseMap.Keys)
            names[action] = FileActionName(action);
        return names;
    }

    private static IReadOnlyDictionary<string, InputAction> BuildActionsByFileName()
    {
        var actions = new Dictionary<string, InputAction>(StringComparer.OrdinalIgnoreCase);
        foreach (((uint InputMapId, uint ActionId) identity, InputAction action) in
            RetailActionIdentityTable.Map)
        {
            actions.Add(FileIdentity(identity.InputMapId, ActionNames[action]), action);
        }
        return actions;
    }

    private static string FileIdentity(uint inputMapId, string actionName) =>
        $"{inputMapId:X8}:{actionName}";

    private static string GroupName(uint inputMapId) =>
        GroupOrder.First(group => group.Id == inputMapId).Name;

    private static string FileActionName(InputAction action)
    {
        if (CharacterOptionNames.TryGetValue(action, out string? characterOption))
            return characterOption;
        if (action == InputAction.SelectionPlaceInInventory) return "SelectionPickUp";
        if (action == InputAction.UseSelected) return "USE";
        string name = action.ToString();
        if (name.StartsWith("CameraAlternate", StringComparison.Ordinal))
            return "Camera" + name["CameraAlternate".Length..];
        if (!name.StartsWith("Emote", StringComparison.Ordinal))
            return name;
        string emote = name["Emote".Length..];
        return emote switch
        {
            "AfkState" => "AFKState",
            "AToyotState" => "ATOYOT",
            "MimeDrinking" => "MimeDrink",
            "MimeEating" => "MimeEat",
            "TalkToTheHandState" => "TalktotheHandState",
            "YawnAndStretch" => "YawnStretch",
            "Ymca" => "YMCA",
            _ => emote,
        };
    }

    private static readonly IReadOnlyDictionary<InputAction, string> CharacterOptionNames =
        new Dictionary<InputAction, string>
        {
            [InputAction.ToggleCharacterOptionAutoRepeatAttack] = "AutoRepeatAttacks",
            [InputAction.ToggleCharacterOptionIgnoreAllegianceRequests] = "IgnoreAllegianceRequests",
            [InputAction.ToggleCharacterOptionIgnoreFellowshipRequests] = "IgnoreFellowshipRequests",
            [InputAction.ToggleCharacterOptionIgnoreTradeRequests] = "IgnoreTradeRequests",
            [InputAction.ToggleCharacterOptionPersistentAtDay] = "PersistentAtDay",
            [InputAction.ToggleCharacterOptionAllowGive] = "LetPlayersGiveYouItems",
            [InputAction.ToggleCharacterOptionViewCombatTarget] = "AutoTrackCombatTargets",
            [InputAction.ToggleCharacterOptionShowTooltips] = "DisplayTooltips",
            [InputAction.ToggleCharacterOptionUseDeception] = "AttemptToDeceivePlayers",
            [InputAction.ToggleCharacterOptionToggleRun] = "RunAsDefaultMovement",
            [InputAction.ToggleCharacterOptionStayInChatMode] = "StayInChatModeAfterSend",
            [InputAction.ToggleCharacterOptionAdvancedCombatUi] = "AdvancedCombatInterface",
            [InputAction.ToggleCharacterOptionAutoTarget] = "AutoTarget",
            [InputAction.ToggleCharacterOptionVividTargetingIndicator] = "VividTargetIndicator",
            [InputAction.ToggleCharacterOptionFellowshipShareXp] = "ShareFellowshipXP",
            [InputAction.ToggleCharacterOptionAcceptLootPermits] = "AcceptCorpseLooting",
            [InputAction.ToggleCharacterOptionFellowshipShareLoot] = "ShareFellowshipLoot",
            [InputAction.ToggleCharacterOptionFellowshipAutoAcceptRequests] = "AutomaticallyAcceptFellowshipRequests",
            [InputAction.ToggleCharacterOptionCoordinatesOnRadar] = "ShowRadarCoordinates",
            [InputAction.ToggleCharacterOptionSpellDuration] = "ShowSpellDurations",
            [InputAction.ToggleCharacterOptionDisableHouseRestrictionEffects] = "DisableHouseEffect",
            [InputAction.ToggleCharacterOptionDragItemOnPlayerOpensSecureTrade] = "DragItemOnPlayerOpensSecureTrade",
            [InputAction.ToggleCharacterOptionDisplayAllegianceLogonNotifications] = "DisplayAllegianceLogonNotifications",
            [InputAction.ToggleCharacterOptionUseChargeAttack] = "UseChargeAttack",
            [InputAction.ToggleCharacterOptionUseCraftSuccessDialog] = "ToggleCraftingChanceOfSuccessDialog",
            [InputAction.ToggleCharacterOptionListenToAllegianceChat] = "AllegianceChat",
            [InputAction.ToggleCharacterOptionDisplayDateOfBirth] = "DisplayDateOfBirth",
            [InputAction.ToggleCharacterOptionDisplayAge] = "DisplayAge",
            [InputAction.ToggleCharacterOptionDisplayChessRank] = "DisplayChessRank",
            [InputAction.ToggleCharacterOptionDisplayFishingSkill] = "Fishing",
            [InputAction.ToggleCharacterOptionDisplayNumberDeaths] = "DisplayNumberDeaths",
            [InputAction.ToggleCharacterOptionDisplayTimeStamps] = "DisplayTimeStamps",
            [InputAction.ToggleCharacterOptionSalvageMultiple] = "SalvageMultiple",
            [InputAction.ToggleCharacterOptionListenToGeneralChat] = "GeneralChat",
            [InputAction.ToggleCharacterOptionListenToTradeChat] = "TradeChat",
            [InputAction.ToggleCharacterOptionListenToLfgChat] = "LFGChat",
            [InputAction.ToggleCharacterOptionListenToRoleplayChat] = "RoleplayChat",
            [InputAction.ToggleCharacterOptionDisplayNumberCharacterTitles] = "DisplayNumberCharacterTitles",
            [InputAction.ToggleCharacterOptionMainPackPreferred] = "MainPackPreferred",
            [InputAction.ToggleCharacterOptionLeadMissileTargets] = "LeadMissileTargets",
            [InputAction.ToggleCharacterOptionUseFastMissiles] = "UseFastMissiles",
            [InputAction.ToggleCharacterOptionFilterLanguage] = "FilterLanguage",
            [InputAction.ToggleCharacterOptionConfirmVolatileRareUse] = "ConfirmVolatileRareUse",
            [InputAction.ToggleCharacterOptionListenToSocietyChat] = "SocietyChat",
            [InputAction.ToggleCharacterOptionShowHelm] = "ShowHelm",
            [InputAction.ToggleCharacterOptionDisableDistanceFog] = "DisableDistanceFog",
            [InputAction.ToggleCharacterOptionShowCloak] = "ShowCloak",
            [InputAction.ToggleCharacterOptionSideBySideVitals] = "SideBySideVitals",
        };

    private const string FixedRetailMaps = """
      TargetedUsage
      [
        SelectLeft [ "" [ 1 DIMOFS_BUTTON0 ] ]
        SelectRight [ "" [ 1 DIMOFS_BUTTON1 ] ]
      ]

      SystemKeys
      [
        AltEnter [ "" [ 0 DIK_RETURN ] 0x00000004 ]
        AltTab [ "" [ 0 DIK_TAB ] 0x00000004 ]
        AltF4 [ "" [ 0 DIK_F4 ] 0x00000004 ]
        CtrlShiftEsc [ "" [ 0 DIK_ESCAPE ] 0x00000003 ]
      ]

      MouseCommands
      [
        PointerX [ "" [ 1 DIMOFS_X ] 0x00000000 Analog ]
        PointerY [ "" [ 1 DIMOFS_Y ] 0x00000000 Analog ]
        SelectLeft [ "" [ 1 DIMOFS_BUTTON0 ] ]
        SelectRight [ "" [ 1 DIMOFS_BUTTON1 ] ]
        SelectMid [ "" [ 1 DIMOFS_BUTTON2 ] ]
        SelectDblLeft [ "" [ 1 DIMOFS_BUTTON0 ] 0x00000000 MouseDblClick ]
        SelectDblRight [ "" [ 1 DIMOFS_BUTTON1 ] 0x00000000 MouseDblClick ]
        SelectDblMid [ "" [ 1 DIMOFS_BUTTON2 ] 0x00000000 MouseDblClick ]
      ]

      ScrollableControls
      [
        ScrollUp [ "" [ 1 DIMOFS_Z AxisPositive ] ]
        ScrollDown [ "" [ 1 DIMOFS_Z AxisNegative ] ]
        ScrollUp [ "" [ 0 DIK_UPARROW ] 0x00000002 ]
        ScrollDown [ "" [ 0 DIK_DOWNARROW ] 0x00000002 ]
      ]

      EditControls
      [
        CursorCharLeft [ "" [ 0 DIK_LEFT ] ]
        CursorCharRight [ "" [ 0 DIK_RIGHTARROW ] ]
        CursorPreviousLine [ "" [ 0 DIK_UPARROW ] ]
        CursorNextLine [ "" [ 0 DIK_DOWNARROW ] ]
        CursorPreviousPage [ "" [ 0 DIK_PGUP ] ]
        CursorNextPage [ "" [ 0 DIK_PGDN ] ]
        CursorWordLeft [ "" [ 0 DIK_LEFT ] 0x00000002 ]
        CursorWordRight [ "" [ 0 DIK_RIGHTARROW ] 0x00000002 ]
        CursorStartOfLine [ "" [ 0 DIK_HOME ] ]
        CursorStartOfDocument [ "" [ 0 DIK_HOME ] 0x00000002 ]
        CursorEndOfLine [ "" [ 0 DIK_END ] ]
        CursorEndOfDocument [ "" [ 0 DIK_END ] 0x00000002 ]
        EscapeKey [ "" [ 0 DIK_ESCAPE ] ]
        AcceptInput [ "" [ 0 DIK_RETURN ] ]
        DeleteKey [ "" [ 0 DIK_DELETE ] ]
        BackspaceKey [ "" [ 0 DIK_BACK ] ]
      ]

      CopyAndPasteControls
      [
        CopyText [ "" [ 0 DIK_C ] 0x00000002 ]
        CopyText [ "" [ 0 DIK_INSERT ] 0x00000002 ]
        CutText [ "" [ 0 DIK_X ] 0x00000002 ]
        CutText [ "" [ 0 DIK_DELETE ] 0x00000001 ]
        PasteText [ "" [ 0 DIK_V ] 0x00000002 ]
        PasteText [ "" [ 0 DIK_INSERT ] 0x00000001 ]
      ]

      DialogBoxes
      [
        EscapeKey [ "" [ 0 DIK_ESCAPE ] ]
        AcceptInput [ "" [ 0 DIK_RETURN ] ]
      ]

    """;
}

public enum RetailKeymapSaveStatus
{
    Saved,
    Exists,
    ReadOnly,
    InvalidName,
    Failed,
}

public readonly record struct RetailKeymapSaveResult(
    RetailKeymapSaveStatus Status,
    string FileName,
    string? Error = null);

public sealed class RetailKeymapProfileStore
{
    public const string DefaultFileName = "acdream.keymap";

    private readonly string _jsonPath;
    private readonly string _directory;
    private readonly string _selectorPath;

    public RetailKeymapProfileStore(string jsonPath, string? keymapDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jsonPath);
        _jsonPath = Path.GetFullPath(jsonPath);
        string configDirectory = Path.GetDirectoryName(_jsonPath)
            ?? Directory.GetCurrentDirectory();
        _selectorPath = Path.Combine(configDirectory, "active-keymap.txt");
        _directory = keymapDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "Asheron's Call");
    }

    public string DirectoryPath => _directory;

    public string CurrentFileName
    {
        get
        {
            try
            {
                if (File.Exists(_selectorPath))
                {
                    string selected = NormalizeFileName(File.ReadAllText(_selectorPath));
                    if (selected.Length != 0) return selected;
                }
            }
            catch (Exception failure)
            {
                Console.WriteLine($"keymap: active-profile preference could not be read: {failure.Message}");
            }
            return DefaultFileName;
        }
    }

    public IReadOnlyList<string> ListFiles()
    {
        try
        {
            if (!Directory.Exists(_directory)) return Array.Empty<string>();
            return Directory.EnumerateFiles(_directory, "*.keymap", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .Where(static name => !string.IsNullOrEmpty(name))
                .Cast<string>()
                .OrderBy(static name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception failure)
        {
            Console.WriteLine($"keymap: profile list failed: {failure.Message}");
            return Array.Empty<string>();
        }
    }

    public bool TryLoad(
        string fileName,
        KeyBindings baseBindings,
        out KeyBindings bindings,
        out string? error)
    {
        bindings = baseBindings;
        error = null;
        string normalized = NormalizeFileName(fileName);
        if (normalized.Length == 0)
        {
            error = "The keymap filename is invalid.";
            return false;
        }

        try
        {
            string text = File.ReadAllText(Path.Combine(_directory, normalized));
            bindings = RetailKeymapFile.Parse(text, baseBindings);
            WriteSelector(normalized);
            return true;
        }
        catch (Exception failure)
        {
            error = failure.Message;
            return false;
        }
    }

    public RetailKeymapSaveResult Save(
        string fileName,
        KeyBindings bindings,
        bool overwrite)
    {
        string normalized = NormalizeFileName(fileName);
        if (normalized.Length == 0)
            return new(RetailKeymapSaveStatus.InvalidName, string.Empty);

        string path = Path.Combine(_directory, normalized);
        try
        {
            if (File.Exists(path))
            {
                if (!overwrite)
                    return new(RetailKeymapSaveStatus.Exists, normalized);
                if ((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0)
                    return new(RetailKeymapSaveStatus.ReadOnly, normalized);
            }

            Directory.CreateDirectory(_directory);
            AtomicWrite(path, RetailKeymapFile.Write(bindings));
            WriteSelector(normalized);
            return new(RetailKeymapSaveStatus.Saved, normalized);
        }
        catch (UnauthorizedAccessException failure)
        {
            return new(RetailKeymapSaveStatus.ReadOnly, normalized, failure.Message);
        }
        catch (Exception failure)
        {
            return new(RetailKeymapSaveStatus.Failed, normalized, failure.Message);
        }
    }

    public RetailKeymapSaveResult SaveActive(KeyBindings bindings) =>
        Save(CurrentFileName, bindings, overwrite: true);

    public static KeyBindings LoadActiveOrJson(
        string jsonPath,
        out string profileName)
    {
        KeyBindings fallback = KeyBindings.LoadOrDefault(jsonPath);
        var store = new RetailKeymapProfileStore(jsonPath);
        profileName = store.CurrentFileName;
        string profilePath = Path.Combine(store.DirectoryPath, profileName);
        if (!File.Exists(profilePath)) return fallback;
        if (store.TryLoad(profileName, fallback, out KeyBindings loaded, out string? error))
            return loaded;
        Console.WriteLine($"keymap: '{profileName}' could not be loaded; using JSON/defaults: {error}");
        return fallback;
    }

    public static string NormalizeFileName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        string trimmed = value.Trim();
        if (!string.Equals(trimmed, Path.GetFileName(trimmed), StringComparison.Ordinal))
            return string.Empty;
        if (trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return string.Empty;
        return trimmed.EndsWith(".keymap", StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : trimmed + ".keymap";
    }

    private void WriteSelector(string fileName)
    {
        string? directory = Path.GetDirectoryName(_selectorPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        AtomicWrite(_selectorPath, fileName + Environment.NewLine);
    }

    private static void AtomicWrite(string path, string content)
    {
        string temp = path + ".tmp-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        try
        {
            File.WriteAllText(temp, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
}
