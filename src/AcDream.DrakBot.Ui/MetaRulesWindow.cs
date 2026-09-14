using System.Numerics;
using AcDream.DrakBot.Meta;
using AcDream.Plugin.Abstractions;
using ImGuiNET;

namespace AcDream.DrakBot.Ui;

/// <summary>
/// The macro rules window, RynthAi's meta editor over the DrakBot meta
/// engine: the loaded rules grouped by state with the row that just fired
/// flashing red, reorder and delete per row, a two-pane rule editor with
/// nested All/Any/Not conditions and multi-action bodies, a pop-out
/// expression editor, a source view that round-trips the <c>.af</c> text,
/// and load/save against the VTank profiles folder. An edit is auto-saved
/// back to the file the meta came from when that file is an <c>.af</c>.
/// </summary>
public sealed class MetaRulesWindow(BotController controller, IAutomationSurface surface)
{
    private static readonly Vector4 ColCyan = new(0f, 1f, 1f, 1f);
    private static readonly Vector4 ColYellow = new(1f, 1f, 0f, 1f);
    private static readonly Vector4 ColGreen = new(0f, 1f, 0.4f, 1f);
    private static readonly Vector4 ColRed = new(1f, 0.25f, 0.25f, 1f);

    private readonly string[] _conditionNames = MetaSchema.ConditionLabels;
    private readonly string[] _actionNames = MetaSchema.ActionLabels;
    private readonly HashSet<int> _expandedRules = [];

    private bool _open;
    private bool _showEditor;
    private MetaRule _editing = new();
    private int _editingIndex = -1;
    private string _newStateName = string.Empty;
    private bool _openCreateStatePopup;

    private bool _exprPopupOpen;
    private string _exprPopupTitle = string.Empty;
    private string _exprPopupBuffer = string.Empty;
    private Action<string>? _exprPopupApply;
    private bool _exprPopupFocus;

    private bool _showSource;
    private string _sourceText = string.Empty;
    private string _sourceMessage = string.Empty;
    private double _sourceMessageAt = double.NegativeInfinity;

    private IReadOnlyList<string> _files = [];
    private double _filesRefreshedAt = double.NegativeInfinity;
    private int _selectedFile = -1;
    private bool _openSavePopup;
    private string _saveName = "macro";
    private string _status = string.Empty;
    private double _statusAt = double.NegativeInfinity;

    public bool IsOpen
    {
        get => _open;
        set => _open = value;
    }

    public void Draw()
    {
        if (!_open)
            return;
        MetaEngine? meta = controller.Meta;
        ImGui.SetNextWindowSize(new Vector2(640f, 480f), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Macro Rules##drakbot", ref _open))
        {
            ImGui.End();
            return;
        }
        if (meta is null)
        {
            ImGui.TextDisabled("No meta engine on this host.");
            ImGui.End();
            return;
        }
        DrawLoadSaveBar(meta);
        ImGui.Spacing();
        DrawViewToggle(meta);
        ImGui.Spacing();
        if (_showSource)
            DrawSource(meta);
        else
            DrawRules(meta);
        ImGui.End();

        DrawEditor(meta);
        DrawExpressionPopup();
    }

    // ── load / save ──────────────────────────────────────────────────────

    private void DrawLoadSaveBar(MetaEngine meta)
    {
        double now = ImGui.GetTime();
        if (now - _filesRefreshedAt > 5d)
        {
            _files = controller.MetaFileNames();
            _filesRefreshedAt = now;
            if (_selectedFile < 0 && meta.MetaName.Length > 0)
            {
                for (int index = 0; index < _files.Count; index++)
                {
                    if (StripExtension(_files[index]).Equals(meta.MetaName, StringComparison.OrdinalIgnoreCase))
                        _selectedFile = index;
                }
            }
        }
        string label = _selectedFile >= 0 && _selectedFile < _files.Count
            ? _files[_selectedFile]
            : meta.MetaName.Length > 0 ? meta.MetaName : "-- None --";
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - 230f);
        if (ImGui.BeginCombo("##metafile", label))
        {
            if (ImGui.Selectable("-- None --", _selectedFile < 0))
                _selectedFile = -1;
            for (int index = 0; index < _files.Count; index++)
            {
                if (ImGui.Selectable(_files[index], _selectedFile == index))
                    _selectedFile = index;
            }
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        if (ImGui.Button("Load", new Vector2(55f, 0f)))
        {
            if (_selectedFile < 0)
            {
                meta.Clear();
                controller.Update(p => p with { Meta = p.Meta with { Name = string.Empty } });
                Status("macro cleared");
            }
            else
            {
                string name = StripExtension(_files[_selectedFile]);
                Status(meta.LoadByName(name)
                    ? $"loaded {meta.Rules.Count} rules / {meta.StateNames().Count} states / {meta.EmbeddedNavs.Count} routes from {name}"
                    : $"could not load {name}");
            }
        }
        ImGui.SameLine();
        if (ImGui.Button("Refresh", new Vector2(60f, 0f)))
            _filesRefreshedAt = double.NegativeInfinity;
        ImGui.SameLine();
        if (ImGui.Button("Save", new Vector2(55f, 0f)))
        {
            _saveName = meta.MetaName.Length > 0 ? meta.MetaName : "macro";
            _openSavePopup = true;
        }
        ImGui.SameLine();
        ImGui.TextDisabled($"{meta.Rules.Count} rules");
        if (_status.Length > 0 && now - _statusAt < 5d)
            ImGui.TextColored(ColGreen, _status);

        if (_openSavePopup)
        {
            ImGui.OpenPopup("SaveMacro");
            _openSavePopup = false;
        }
        if (ImGui.BeginPopup("SaveMacro", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.Text("File name:");
            ImGui.SetNextItemWidth(250f);
            ImGui.InputText("##savename", ref _saveName, 64);
            ImGui.SameLine();
            ImGui.TextDisabled(".af");
            ImGui.Spacing();
            if (ImGui.Button("Save", new Vector2(120f, 0f)))
            {
                controller.SaveMeta(_saveName, out string message);
                Status(message);
                _filesRefreshedAt = double.NegativeInfinity;
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel", new Vector2(120f, 0f)))
                ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }
    }

    private void DrawViewToggle(MetaEngine meta)
    {
        Vector4 plain = ImGui.GetStyle().Colors[(int)ImGuiCol.Button];
        var active = new Vector4(0.15f, 0.35f, 0.6f, 1f);
        ImGui.PushStyleColor(ImGuiCol.Button, _showSource ? plain : active);
        if (ImGui.Button("Visual", new Vector2(60f, 0f)))
            _showSource = false;
        ImGui.PopStyleColor();
        ImGui.SameLine();
        ImGui.PushStyleColor(ImGuiCol.Button, _showSource ? active : plain);
        if (ImGui.Button("Source", new Vector2(60f, 0f)) && !_showSource)
        {
            _sourceText = AfFileWriter.SaveToString(meta.EditableRules, meta.EmbeddedNavs);
            _showSource = true;
        }
        ImGui.PopStyleColor();
    }

    private void DrawSource(MetaEngine meta)
    {
        float height = ImGui.GetContentRegionAvail().Y - 30f;
        ImGui.InputTextMultiline("##afsource", ref _sourceText, 1024 * 1024, new Vector2(-1f, height), ImGuiInputTextFlags.AllowTabInput);
        if (_sourceMessage.Length > 0 && ImGui.GetTime() - _sourceMessageAt < 4d)
        {
            ImGui.TextColored(ColGreen, _sourceMessage);
            ImGui.SameLine();
        }
        if (ImGui.Button("Apply", new Vector2(80f, 0f)))
        {
            try
            {
                LoadedMeta loaded = AfFileParser.LoadFromText(_sourceText);
                string warn = loaded.Warnings.Count == 0 ? string.Empty : $" - {loaded.Warnings.Count} warning(s): {loaded.Warnings[0]}";
                if (loaded.Rules.Count == 0)
                {
                    _sourceMessage = $"No rules parsed - check the syntax.{warn}";
                }
                else
                {
                    meta.ReplaceRules(loaded.Rules, loaded.EmbeddedNavs);
                    AutoSave(meta);
                    _sourceMessage = $"Applied {loaded.Rules.Count} rules.{warn}";
                }
            }
            catch (Exception error) when (error is FormatException or InvalidOperationException or ArgumentException)
            {
                _sourceMessage = $"Error: {error.Message}";
            }
            _sourceMessageAt = ImGui.GetTime();
        }
        ImGui.SameLine();
        if (ImGui.Button("Revert", new Vector2(80f, 0f)))
        {
            _sourceText = AfFileWriter.SaveToString(meta.EditableRules, meta.EmbeddedNavs);
            _sourceMessage = "Reverted to the loaded rules.";
            _sourceMessageAt = ImGui.GetTime();
        }
    }

    // ── the rule list ────────────────────────────────────────────────────

    private void DrawRules(MetaEngine meta)
    {
        List<MetaRule> rules = meta.EditableRules;
        double now = controller.Engine.Clock.Now;
        var groups = rules.GroupBy(rule => rule.State, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (ImGui.BeginChild("##rules", new Vector2(0f, -35f)))
        {
            foreach (IGrouping<string, MetaRule> group in groups)
            {
                List<MetaRule> stateRules = group.ToList();
                bool anyFired = stateRules.Any(rule => now - rule.LastFiredAt < 1.5d);
                bool current = group.Key.Equals(meta.CurrentState, StringComparison.OrdinalIgnoreCase);
                string header = $"{group.Key}  ({stateRules.Count}){(current ? "  - current" : string.Empty)}{(anyFired ? "  - firing" : string.Empty)}##hdr_{group.Key}";
                if (anyFired)
                    ImGui.PushStyleColor(ImGuiCol.Text, ColRed);
                else if (current)
                    ImGui.PushStyleColor(ImGuiCol.Text, ColCyan);
                bool open = ImGui.CollapsingHeader(header, ImGuiTreeNodeFlags.DefaultOpen);
                if (anyFired || current)
                    ImGui.PopStyleColor();
                if (!open)
                    continue;
                if (!ImGui.BeginTable($"##tbl_{group.Key}", 5, ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.RowBg))
                    continue;
                ImGui.TableSetupColumn("Up", ImGuiTableColumnFlags.WidthFixed, 20f);
                ImGui.TableSetupColumn("Dn", ImGuiTableColumnFlags.WidthFixed, 20f);
                ImGui.TableSetupColumn("Del", ImGuiTableColumnFlags.WidthFixed, 25f);
                ImGui.TableSetupColumn("Condition", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("Action", ImGuiTableColumnFlags.WidthStretch);

                bool changed = false;
                for (int i = 0; i < stateRules.Count; i++)
                {
                    MetaRule rule = stateRules[i];
                    int globalIndex = rules.IndexOf(rule);
                    ImGui.TableNextRow();

                    double fade = now - rule.LastFiredAt;
                    bool flashing = fade < 1.5d;
                    if (flashing)
                    {
                        float t = fade < 0.5d ? 0f : (float)Math.Clamp((fade - 0.5d) / 1.0d, 0d, 1d);
                        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.25f + 0.75f * t, 0.25f + 0.75f * t, 1f));
                    }

                    ImGui.TableNextColumn();
                    if (i > 0 && ImGui.Button($"^##up{globalIndex}"))
                    {
                        Swap(rules, rule, stateRules[i - 1]);
                        changed = true;
                    }
                    ImGui.TableNextColumn();
                    if (i < stateRules.Count - 1 && ImGui.Button($"v##dn{globalIndex}"))
                    {
                        Swap(rules, rule, stateRules[i + 1]);
                        changed = true;
                    }
                    ImGui.TableNextColumn();
                    ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0f, 0f, 1f));
                    if (ImGui.Button($"X##del{globalIndex}"))
                    {
                        rules.Remove(rule);
                        changed = true;
                    }
                    ImGui.PopStyleColor();

                    ImGui.TableNextColumn();
                    string condition = rule.Condition + (rule.ConditionData.Length == 0 ? string.Empty : $": {rule.ConditionData}");
                    bool toggled = false;
                    void ToggleExpand()
                    {
                        if (toggled)
                            return;
                        toggled = true;
                        if (!_expandedRules.Add(globalIndex))
                            _expandedRules.Remove(globalIndex);
                    }
                    if (ImGui.Selectable($"{condition}##edit{globalIndex}", false, ImGuiSelectableFlags.SpanAllColumns))
                    {
                        _editing = Clone(rule);
                        _editingIndex = globalIndex;
                        _showEditor = true;
                    }
                    if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
                        ToggleExpand();

                    ImGui.TableNextColumn();
                    if (rule.Action == MetaActionType.All)
                    {
                        List<MetaRule> actions = rule.ActionChildren.Count > 0 ? rule.ActionChildren : rule.Children;
                        ImGui.TextColored(ColYellow, $"All: [{actions.Count} actions] (right-click to expand)");
                        if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
                            ToggleExpand();
                        if (_expandedRules.Contains(globalIndex))
                        {
                            for (int a = 0; a < actions.Count; a++)
                            {
                                MetaRule child = actions[a];
                                ImGui.TextDisabled($"   {a + 1}. {child.Action}{(child.ActionData.Length == 0 ? string.Empty : $": {child.ActionData}")}");
                                if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
                                    ToggleExpand();
                            }
                        }
                    }
                    else if (rule.Action == MetaActionType.EmbeddedNavRoute && rule.ActionData.Contains(';'))
                    {
                        string[] parts = rule.ActionData.Split(';');
                        ImGui.Text($"EmbeddedNavRoute: {parts[0]} ({parts[1]} pts)");
                        if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
                            ToggleExpand();
                    }
                    else
                    {
                        ImGui.Text(rule.Action + (rule.ActionData.Length == 0 ? string.Empty : $": {rule.ActionData}"));
                        if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
                            ToggleExpand();
                    }
                    if (flashing)
                        ImGui.PopStyleColor();
                    if (changed)
                        break;
                }
                ImGui.EndTable();
                if (changed)
                {
                    meta.RulesChanged();
                    AutoSave(meta);
                    break;
                }
            }
        }
        ImGui.EndChild();

        ImGui.Separator();
        bool enabled = meta.Enabled;
        if (ImGui.Checkbox("Enable Meta", ref enabled))
            meta.Enabled = enabled;
        ImGui.SameLine();
        bool debug = meta.Debug;
        if (ImGui.Checkbox("Debug##metadbg", ref debug))
            meta.Debug = debug;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Echo every rule that fires to chat.");
        ImGui.SameLine();
        ImGui.Text("Current State:");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(150f);
        if (ImGui.BeginCombo("##currentstate", meta.CurrentState))
        {
            if (ImGui.Selectable("< Create New State >"))
                _openCreateStatePopup = true;
            ImGui.Separator();
            foreach (string state in StatesWithDefault(meta))
            {
                if (ImGui.Selectable(state, state.Equals(meta.CurrentState, StringComparison.OrdinalIgnoreCase)))
                    meta.SetState(state);
            }
            ImGui.EndCombo();
        }
        if (_openCreateStatePopup)
        {
            ImGui.OpenPopup("CreateState");
            _openCreateStatePopup = false;
        }
        if (ImGui.BeginPopup("CreateState", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.Text("New state name:");
            ImGui.InputText("##newstate", ref _newStateName, 64);
            ImGui.Spacing();
            if (ImGui.Button("Create", new Vector2(120f, 0f)))
            {
                if (!string.IsNullOrWhiteSpace(_newStateName))
                {
                    meta.SetState(_newStateName.Trim());
                    _newStateName = string.Empty;
                }
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel", new Vector2(120f, 0f)))
                ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }
        ImGui.SameLine(ImGui.GetWindowWidth() - 100f);
        if (ImGui.Button("Create", new Vector2(80f, 25f)))
        {
            _editing = new MetaRule { State = meta.CurrentState };
            _editingIndex = -1;
            _showEditor = true;
        }
    }

    // ── the editor ───────────────────────────────────────────────────────

    private void DrawEditor(MetaEngine meta)
    {
        if (!_showEditor)
            return;
        ImGui.SetNextWindowSize(new Vector2(650f, 450f), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Edit Meta Rule##drakbot", ref _showEditor, ImGuiWindowFlags.NoCollapse))
        {
            ImGui.End();
            return;
        }
        float half = ImGui.GetContentRegionAvail().X / 2f - 4f;
        List<string> states = StatesWithDefault(meta);

        ImGui.BeginChild("ConditionPane", new Vector2(half, -35f), ImGuiChildFlags.Borders);
        ImGui.TextColored(ColCyan, "Condition Type:");
        int conditionIndex = (int)_editing.Condition;
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.Combo("##cond", ref conditionIndex, _conditionNames, _conditionNames.Length))
        {
            _editing.Condition = (MetaConditionType)conditionIndex;
            ClearConditionDataIfNeeded(_editing);
        }
        ImGui.TextColored(ColCyan, "State Name:");
        ImGui.SetNextItemWidth(-30f);
        string state = _editing.State;
        if (ImGui.InputTextWithHint("##statename", "State (e.g. Default)", ref state, 64))
            _editing.State = state;
        ImGui.SameLine();
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.BeginCombo("##statepick", string.Empty, ImGuiComboFlags.NoPreview))
        {
            foreach (string name in states)
            {
                if (ImGui.Selectable(name))
                    _editing.State = name;
            }
            ImGui.EndCombo();
        }
        ImGui.Separator();
        DrawConditionNode(_editing, "root_cond");
        ImGui.EndChild();

        ImGui.SameLine();
        ImGui.BeginChild("ActionPane", new Vector2(0f, -35f), ImGuiChildFlags.Borders);
        ImGui.TextColored(ColYellow, "Action Type:");
        DrawActionNode(meta, _editing, "root_action");
        ImGui.EndChild();

        ImGui.SetCursorPosY(ImGui.GetWindowHeight() - 30f);
        if (ImGui.Button(_editingIndex == -1 ? "Add Rule" : "Save Rule", new Vector2(100f, 25f)))
        {
            List<MetaRule> rules = meta.EditableRules;
            if (_editingIndex < 0 || _editingIndex >= rules.Count)
                rules.Add(_editing);
            else
                rules[_editingIndex] = _editing;
            meta.RulesChanged();
            AutoSave(meta);
            _showEditor = false;
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel", new Vector2(100f, 25f)))
            _showEditor = false;
        ImGui.End();
    }

    private void DrawConditionNode(MetaRule rule, string id)
    {
        ImGui.PushID(id);
        if (rule.Condition is MetaConditionType.Any or MetaConditionType.All or MetaConditionType.Not)
        {
            if (rule.Condition != MetaConditionType.Not || rule.Children.Count == 0)
            {
                if (ImGui.Button("+##add"))
                    rule.Children.Add(new MetaRule { Condition = MetaConditionType.Always });
                ImGui.SameLine();
            }
            ImGui.TextColored(ColYellow, $"{rule.Condition} Sub-Conditions");
            ImGui.Indent(15f);
            for (int i = 0; i < rule.Children.Count; i++)
            {
                MetaRule child = rule.Children[i];
                ImGui.PushID($"child_{i}");
                int childIndex = (int)child.Condition;
                ImGui.SetNextItemWidth(150f);
                if (ImGui.Combo("##cond", ref childIndex, _conditionNames, _conditionNames.Length))
                {
                    child.Condition = (MetaConditionType)childIndex;
                    ClearConditionDataIfNeeded(child);
                }
                ImGui.SameLine();
                float x = ImGui.GetCursorPosX();
                float y = ImGui.GetCursorPosY();
                ImGui.SameLine(ImGui.GetWindowWidth() - 35f);
                ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.8f, 0.1f, 0.1f, 1f));
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(1f, 0.2f, 0.2f, 1f));
                bool delete = ImGui.Button(PhosphorIcons.X);
                ImGui.PopStyleColor(2);
                ImGui.SetCursorPos(new Vector2(x, y));
                DrawConditionNode(child, $"node_{i}");
                if (delete)
                {
                    rule.Children.RemoveAt(i);
                    i--;
                }
                ImGui.Dummy(new Vector2(0f, 2f));
                ImGui.PopID();
            }
            ImGui.Unindent(15f);
            ImGui.PopID();
            return;
        }

        switch (rule.Condition)
        {
            case MetaConditionType.InventoryItemCount_LE:
            case MetaConditionType.InventoryItemCount_GE:
            {
                string[] parts = rule.ConditionData.Split(',');
                string item = parts[0];
                string count = parts.Length > 1 ? parts[1] : "0";
                ImGui.Text("Item Name:");
                ImGui.SetNextItemWidth(-28f);
                if (ImGui.InputText("##invname", ref item, 64))
                    rule.ConditionData = $"{item},{count}";
                CopyButton("invname", item);
                ImGui.Text("Item Count:");
                ImGui.SetNextItemWidth(-1f);
                if (ImGui.InputText("##invcount", ref count, 16, ImGuiInputTextFlags.CharsDecimal))
                    rule.ConditionData = $"{item},{count}";
                break;
            }
            case MetaConditionType.TimeLeftOnSpell_GE:
            case MetaConditionType.TimeLeftOnSpell_LE:
            {
                string[] parts = rule.ConditionData.Split(',');
                string spellId = parts[0];
                string seconds = parts.Length > 1 ? parts[1] : "0";
                ImGui.Text("Spell ID:");
                ImGui.SetNextItemWidth(120f);
                if (ImGui.InputText("##spellid", ref spellId, 16, ImGuiInputTextFlags.CharsDecimal))
                    rule.ConditionData = $"{spellId},{seconds}";
                if (uint.TryParse(spellId, out uint spellNumber) && surface.Spells.TryGet(spellNumber, out PluginSpellInfo spell))
                {
                    ImGui.SameLine();
                    ImGui.TextColored(ColCyan, $"({spell.Name})");
                }
                ImGui.Text("Seconds Remaining:");
                ImGui.SetNextItemWidth(-1f);
                if (ImGui.InputText("##spelltime", ref seconds, 16, ImGuiInputTextFlags.CharsDecimal))
                    rule.ConditionData = $"{spellId},{seconds}";
                break;
            }
            default:
            {
                string? hint = ConditionHint(rule.Condition);
                if (hint is null)
                {
                    ImGui.TextDisabled("(No extra data)");
                    break;
                }
                ImGui.SetNextItemWidth(Math.Max(50f, ImGui.GetWindowWidth() - ImGui.GetCursorPosX() - 65f));
                string data = rule.ConditionData;
                if (ImGui.InputTextWithHint("##data", hint, ref data, 256))
                    rule.ConditionData = data;
                if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
                {
                    MetaRule target = rule;
                    OpenExpressionPopup("Condition Data", target.ConditionData, value => target.ConditionData = value);
                }
                if (ImGui.IsItemHovered() && data.Length > 0)
                    ImGui.SetTooltip("Right-click for a larger editor");
                CopyButton("conddata", data);
                break;
            }
        }
        ImGui.PopID();
    }

    private void DrawActionNode(MetaEngine meta, MetaRule rule, string id)
    {
        ImGui.PushID(id);
        int actionIndex = (int)rule.Action;
        ImGui.SetNextItemWidth(180f);
        if (ImGui.Combo("##action", ref actionIndex, _actionNames, _actionNames.Length))
        {
            rule.Action = (MetaActionType)actionIndex;
            if (rule.Action == MetaActionType.All)
                rule.ActionData = string.Empty;
            else
                rule.ActionChildren.Clear();
        }
        ImGui.SameLine();

        if (rule.Action == MetaActionType.All)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.2f, 0.6f, 0.2f, 1f));
            if (ImGui.Button("+ Add Action"))
                rule.ActionChildren.Add(new MetaRule { Action = MetaActionType.ChatCommand });
            ImGui.PopStyleColor();
            ImGui.Indent(20f);
            for (int i = 0; i < rule.ActionChildren.Count; i++)
            {
                ImGui.PushID($"act_{i}");
                if (ImGui.Button(PhosphorIcons.X))
                {
                    rule.ActionChildren.RemoveAt(i);
                    i--;
                }
                else
                {
                    ImGui.SameLine();
                    DrawActionNode(meta, rule.ActionChildren[i], $"child_{i}");
                }
                ImGui.PopID();
                if (i < rule.ActionChildren.Count - 1)
                    ImGui.Separator();
            }
            ImGui.Unindent(20f);
            ImGui.PopID();
            return;
        }

        switch (rule.Action)
        {
            case MetaActionType.ChatCommand:
            {
                string command = rule.ActionData;
                ImGui.SetNextItemWidth(-28f);
                if (ImGui.InputTextWithHint("##cmd", "e.g. /tell {1} hello!", ref command, 256))
                    rule.ActionData = command;
                CopyButton("cmd", command);
                break;
            }
            case MetaActionType.SetMetaState:
            case MetaActionType.CallMetaState:
            {
                List<string> states = StatesWithDefault(meta);
                if (rule.ActionData.Length > 0 && !states.Contains(rule.ActionData, StringComparer.OrdinalIgnoreCase))
                    states.Add(rule.ActionData);
                ImGui.SetNextItemWidth(-1f);
                if (ImGui.BeginCombo("##state", rule.ActionData.Length == 0 ? "Select State..." : rule.ActionData))
                {
                    foreach (string state in states)
                    {
                        if (ImGui.Selectable(state, rule.ActionData.Equals(state, StringComparison.OrdinalIgnoreCase)))
                            rule.ActionData = state;
                    }
                    ImGui.EndCombo();
                }
                break;
            }
            case MetaActionType.EmbeddedNavRoute:
            {
                string current = rule.ActionData.Split(';')[0];
                if (ImGui.BeginCombo("##nav", current.Length == 0 ? "Select Route..." : current))
                {
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (string name in meta.EmbeddedNavs.Keys)
                    {
                        if (seen.Add(name) && ImGui.Selectable($"{name} (embedded)", current.Equals(name, StringComparison.OrdinalIgnoreCase)))
                            rule.ActionData = name;
                    }
                    foreach (string file in controller.NavFileNames())
                    {
                        string name = StripExtension(file);
                        if (seen.Add(name) && ImGui.Selectable(file, current.Equals(name, StringComparison.OrdinalIgnoreCase)))
                            rule.ActionData = name;
                    }
                    foreach (string name in controller.Store.RouteNames())
                    {
                        if (seen.Add(name) && ImGui.Selectable($"{name} (saved)", current.Equals(name, StringComparison.OrdinalIgnoreCase)))
                            rule.ActionData = name;
                    }
                    ImGui.EndCombo();
                }
                break;
            }
            case MetaActionType.SetWatchdog:
            {
                string[] parts = (rule.ActionData.Length == 0 ? "Default;10;60" : rule.ActionData).Split(';');
                string state = parts[0];
                string meters = parts.Length > 1 ? parts[1] : "10";
                string seconds = parts.Length > 2 ? parts[2] : "60";
                List<string> states = StatesWithDefault(meta);
                if (state.Length > 0 && !states.Contains(state, StringComparer.OrdinalIgnoreCase))
                    states.Add(state);
                ImGui.Text("To State:");
                ImGui.SetNextItemWidth(-1f);
                if (ImGui.BeginCombo("##wdstate", state))
                {
                    foreach (string name in states)
                    {
                        if (ImGui.Selectable(name, state.Equals(name, StringComparison.OrdinalIgnoreCase)))
                            state = name;
                    }
                    ImGui.EndCombo();
                }
                ImGui.Text("Meters in:");
                ImGui.SetNextItemWidth(-1f);
                ImGui.InputText("##wdmeters", ref meters, 8, ImGuiInputTextFlags.CharsDecimal);
                ImGui.Text("Seconds:");
                ImGui.SetNextItemWidth(-1f);
                ImGui.InputText("##wdsecs", ref seconds, 8, ImGuiInputTextFlags.CharsDecimal);
                rule.ActionData = $"{state};{meters};{seconds}";
                break;
            }
            case MetaActionType.SetRAOption:
            {
                string[] parts = (rule.ActionData.Length == 0 ? "EnableBuffing;False" : rule.ActionData).Split(';');
                string option = parts[0];
                string value = parts.Length > 1 ? parts[1] : "False";
                ImGui.Text("Option:");
                ImGui.SetNextItemWidth(120f);
                if (ImGui.InputText("##raopt", ref option, 64))
                    rule.ActionData = $"{option};{value}";
                CopyButton("raopt", option);
                ImGui.SameLine();
                ImGui.Text("Val:");
                ImGui.SetNextItemWidth(-28f);
                if (ImGui.InputText("##raval", ref value, 64))
                    rule.ActionData = $"{option};{value}";
                CopyButton("raval", value);
                break;
            }
            case MetaActionType.ExpressionAction:
            case MetaActionType.ChatExpression:
            {
                string expression = rule.ActionData;
                ImGui.SetNextItemWidth(-28f);
                if (ImGui.InputTextWithHint("##expr", "Expression (e.g. setvar[count, getvar[count]+1])", ref expression, 256))
                    rule.ActionData = expression;
                if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
                {
                    MetaRule target = rule;
                    OpenExpressionPopup("Action Expression", target.ActionData, value => target.ActionData = value);
                }
                if (ImGui.IsItemHovered() && expression.Length > 0)
                    ImGui.SetTooltip("Right-click for a larger editor");
                CopyButton("expr", expression);
                break;
            }
            case MetaActionType.GetRAOption:
            case MetaActionType.CreateView:
            case MetaActionType.DestroyView:
            {
                string data = rule.ActionData;
                ImGui.SetNextItemWidth(-28f);
                if (ImGui.InputText("##viewdata", ref data, 128))
                    rule.ActionData = data;
                CopyButton("viewdata", data);
                break;
            }
            case MetaActionType.ReturnFromCall:
            case MetaActionType.ClearWatchdog:
            case MetaActionType.DestroyAllViews:
                ImGui.TextDisabled("(No extra data required)");
                break;
            default:
                ImGui.TextDisabled("(Select an action type)");
                break;
        }
        ImGui.PopID();
    }

    // ── the pop-out expression editor ────────────────────────────────────

    private void OpenExpressionPopup(string title, string current, Action<string> apply)
    {
        _exprPopupTitle = title;
        _exprPopupBuffer = current;
        _exprPopupApply = apply;
        _exprPopupOpen = true;
        _exprPopupFocus = true;
    }

    private void DrawExpressionPopup()
    {
        if (!_exprPopupOpen)
            return;
        ImGui.SetNextWindowSize(new Vector2(720f, 380f), ImGuiCond.FirstUseEver);
        if (_exprPopupFocus)
            ImGui.SetNextWindowFocus();
        if (ImGui.Begin($"Expression Editor - {_exprPopupTitle}##drakbotexpr", ref _exprPopupOpen))
        {
            ImGui.TextDisabled("Edit the expression here; Apply writes it back to the rule.");
            ImGui.Separator();
            if (_exprPopupFocus)
            {
                ImGui.SetKeyboardFocusHere();
                _exprPopupFocus = false;
            }
            ImGui.InputTextMultiline("##exprtext", ref _exprPopupBuffer, 4096, new Vector2(-1f, ImGui.GetContentRegionAvail().Y - 36f));
            ImGui.Separator();
            if (ImGui.Button("Apply", new Vector2(120f, 0f)))
            {
                _exprPopupApply?.Invoke(_exprPopupBuffer);
                _exprPopupOpen = false;
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel", new Vector2(120f, 0f)))
                _exprPopupOpen = false;
        }
        ImGui.End();
    }

    // ── helpers ──────────────────────────────────────────────────────────

    /// <summary>An edited meta that came from an <c>.af</c> is written back to it; a <c>.met</c> is left untouched.</summary>
    private void AutoSave(MetaEngine meta)
    {
        if (meta.MetaName.Length == 0)
            return;
        bool fromAf = _files.Any(file => file.Equals(meta.MetaName + ".af", StringComparison.OrdinalIgnoreCase));
        if (!fromAf)
            return;
        controller.SaveMeta(meta.MetaName, out string message);
        Status(message);
    }

    private void Status(string text)
    {
        _status = text;
        _statusAt = ImGui.GetTime();
    }

    private static List<string> StatesWithDefault(MetaEngine meta)
    {
        var states = new List<string>(meta.StateNames());
        if (!states.Contains("Default", StringComparer.OrdinalIgnoreCase))
            states.Insert(0, "Default");
        return states;
    }

    private static void Swap(List<MetaRule> rules, MetaRule a, MetaRule b)
    {
        int ia = rules.IndexOf(a);
        int ib = rules.IndexOf(b);
        if (ia >= 0 && ib >= 0)
            (rules[ia], rules[ib]) = (b, a);
    }

    private static string? ConditionHint(MetaConditionType condition) => condition switch
    {
        MetaConditionType.Never or MetaConditionType.Always or MetaConditionType.CharacterDeath
            or MetaConditionType.AnyVendorOpen or MetaConditionType.VendorClosed or MetaConditionType.NeedToBuff
            or MetaConditionType.PortalspaceEntered or MetaConditionType.PortalspaceExited
            or MetaConditionType.NavrouteEmpty => null,
        MetaConditionType.ChatMessage or MetaConditionType.ChatMessageCapture => "Regex pattern...",
        MetaConditionType.PackSlots_LE => "Min slots (e.g. 5)",
        MetaConditionType.BurdenPercentage_GE => "Percentage (e.g. 250)",
        MetaConditionType.SecondsInState_GE or MetaConditionType.SecondsInStateP_GE => "Seconds (e.g. 10)",
        MetaConditionType.NoMonstersWithinDistance => "Distance in yards (e.g. 20)",
        MetaConditionType.MonsterNameCountWithinDistance => "name regex,distance,min count (e.g. Drudge,20,3)",
        MetaConditionType.MonsterPriorityCountWithinDistance => "min count,distance (e.g. 1,20)",
        MetaConditionType.DistAnyRoutePT_GE => "Distance in yards (e.g. 10)",
        MetaConditionType.Landblock_EQ or MetaConditionType.Landcell_EQ => "Hex value (e.g. A9B40000)",
        MetaConditionType.VitaePHE => "Vitae penalty % (e.g. 5)",
        MetaConditionType.Expression => "Expression (e.g. getvar[count] > 3)",
        MetaConditionType.MainHealthLE or MetaConditionType.MainManaLE or MetaConditionType.MainStamLE => "Value (e.g. 100)",
        MetaConditionType.MainHealthPHE or MetaConditionType.MainManaPHE => "Percentage (e.g. 50)",
        _ => "Data...",
    };

    private static void ClearConditionDataIfNeeded(MetaRule rule)
    {
        if (ConditionHint(rule.Condition) is null || rule.Condition is MetaConditionType.All or MetaConditionType.Any or MetaConditionType.Not)
            rule.ConditionData = string.Empty;
    }

    private static void CopyButton(string id, string text)
    {
        ImGui.SameLine();
        if (ImGui.SmallButton($"C##{id}"))
            ImGui.SetClipboardText(text);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Copy");
    }

    private static MetaRule Clone(MetaRule rule) => new()
    {
        State = rule.State,
        Condition = rule.Condition,
        ConditionData = rule.ConditionData,
        Action = rule.Action,
        ActionData = rule.ActionData,
        Enabled = rule.Enabled,
        Children = rule.Children.Select(Clone).ToList(),
        ActionChildren = rule.ActionChildren.Select(Clone).ToList(),
    };

    private static string StripExtension(string file)
    {
        int dot = file.LastIndexOf('.');
        return dot > 0 ? file[..dot] : file;
    }
}
