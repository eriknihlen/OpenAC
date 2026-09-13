using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using AcDream.Plugin.Abstractions;

namespace AcDream.DrakBot.Meta;

/// <summary>
/// Evaluates RynthAi expressions.
///
/// Syntax:
///   Infix operators: == > < >= <= + - * / % # &amp;&amp; || ~ &lt;&lt; >> &amp; ^ |
///   Function calls:  funcname[arg1, arg2, ...]   (args can be nested expressions)
///   Variables:       $name  is shorthand for getvar[name]
///   Literals:        plain numbers (decimal or 0x hex) or identifiers
///
/// Boolean convention: "0" and "" are false, everything else is true.
/// All values are strings internally; numeric coercion happens per-operator.
/// Lists are stored by reference; variables hold a list handle (e.g. "L:0").
/// </summary>
public sealed class ExpressionEngine
{
    private readonly MetaWorld _host;
    private MetaWorld? _worldObjectCache;
    private MetaWorld? _fellowshipTracker;
    private readonly Dictionary<string, string> _variables = new(StringComparer.OrdinalIgnoreCase);

    // Lists are value-typed: serialized as "[item1,item2,...]" strings — no backing store.

    // Dict store: handle → (key → value).  Handles look like "D:0", "D:1", …
    // Dicts are ephemeral: cleared at the start of each top-level Evaluate call.
    private readonly Dictionary<string, Dictionary<string, string>> _dicts = new(StringComparer.Ordinal);
    private int _nextDictId;
    private int _evalDepth;

    // Hard recursion caps. A .NET StackOverflowException is uncatchable and
    // instantly kills acclient.exe, so a self-referential meta expression
    // (exec[$x] loop, listmap template, nested funcs) or a pathological paren
    // nest MUST be bounded before it reaches the runtime. 64/256 are far above
    // any legitimate meta and well under an 8 MB stack.
    private const int MaxEvalDepth = 64;
    private const int MaxParenDepth = 256;
    private int _parenDepth;

    // Plugin options store: name → value (case-insensitive keys).
    // Populated by the plugin via RegisterOption / SetOption.
    private readonly Dictionary<string, string> _options = new(StringComparer.OrdinalIgnoreCase);

    // ItemGiver profile cache: absolute path → (profile, mtime)

    // Stopwatch store: handle → Stopwatch. Persistent (not cleared per eval) — handles are stored in variables.
    private readonly Dictionary<string, System.Diagnostics.Stopwatch> _stopwatches = new(StringComparer.Ordinal);
    private int _nextSwId;

    // Motion name → (AC motion uint, wanted state). Matches UB Motion enum values.
    private static readonly Dictionary<string, uint> MotionValues = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Forward"]     = 0x45000005,
        ["Backward"]    = 0x45000006,
        ["TurnRight"]   = 0x6500000D,
        ["TurnLeft"]    = 0x6500000E,
        ["StrafeRight"] = 0x6500000F,
        ["StrafeLeft"]  = 0x65000010,
        ["Walk"]        = 0x11112222,
    };
    private readonly Dictionary<string, bool> _wantedMotion = new(StringComparer.OrdinalIgnoreCase);

    // delayexec queue. Drained on the game/plugin-tick thread via
    // PumpDelayedExecs() — NOT a threadpool Timer (which executed expressions
    // against AC native memory off-thread and, being static, survived plugin
    // hot-reload to fire into a freed engine). Instance-scoped so a hot-reload
    // discards pending execs. Single-threaded access (Evaluate and the pump
    // both run on the plugin-tick thread) → no lock needed.
    private readonly List<(long DueMs, string Expr)> _delayedExecs = new();

    private uint _playerId;
    private MetaSettings? _settings;

    // Persistent / global variable storage (lazy-loaded from disk)
    private Dictionary<string, string>? _pvars;
    private Dictionary<string, string>? _gvars;
    // set/clear/touch mark dirty; FlushVars() writes at most every
    // VarFlushIntervalMs (from the plugin-tick pump) instead of a synchronous
    // full-file write on every call. A bot setting a pvar each tick was doing a
    // disk write per tick on the game thread.
    private bool _pvarsDirty, _gvarsDirty;
    private long _lastVarFlushMs;
    private string? _pvarPathCached;
    private const long VarFlushIntervalMs = 2000;
    // Persistent and global variables live in the plugin's own storage.
    private const string PvarsPrefix = "meta/pvars/";
    private const string GvarsKey = "meta/gvars.txt";
    private Dictionary<string, (Func<string> Get, Action<string> Set)>? _settingsMap;

    public IReadOnlyDictionary<string, string> Variables => _variables;
    public IReadOnlyDictionary<string, string> Options   => _options;
    public IReadOnlyDictionary<string, string> Pvars     => GetPvars();
    public IReadOnlyDictionary<string, string> Gvars     => GetGvars();

    /// <summary>Registers a named option with a default value if it has not been set.</summary>
    public void RegisterOption(string name, string defaultValue)
    {
        if (!_options.ContainsKey(name))
            _options[name] = defaultValue;
    }

    /// <summary>Gets an option value, or "0" if not registered.</summary>
    public string GetOption(string name)
        => _options.TryGetValue(name, out string? v) ? v : "0";

    /// <summary>Sets an option value. Returns false if the option was not previously registered.</summary>
    public bool SetOption(string name, string value)
    {
        _options[name] = value;
        return true;
    }

    public ExpressionEngine(MetaWorld host)
    {
        _host = host;
        _worldObjectCache = host;
        _fellowshipTracker = host;
        _questTracker = host;
    }

    private MetaWorld? _questTracker;

    public void SetPlayerId(uint id)
    {
        if (id != _playerId)
        {
            FlushVars(force: true);   // persist the old char's pending pvars first
            _pvars = null;
            _pvarPathCached = null;
        }
        _playerId = id;
    }
    public void SetObjectCache(MetaWorld? cache) => _worldObjectCache = cache;
    public void SetSettings(MetaSettings settings) { _settings = settings; _settingsMap = null; }


    // ── Backward-compat shim ──────────────────────────────────────────────────

    /// <summary>Executes an expression for its side effects. Always returns true.</summary>
    public bool TryExecuteAction(string expression)
    {
        Evaluate(expression);
        return true;
    }

    // ── Entry point ───────────────────────────────────────────────────────────

    /// <summary>
    /// Evaluates a full expression (infix operators + function calls) and returns the result.
    /// </summary>
    public string Evaluate(string expression)
    {
        string expr = expression?.Trim() ?? "";
        if (expr.Length == 0) return "";
        if (_evalDepth >= MaxEvalDepth) return "ERR:Depth:max recursion exceeded";
        if (_evalDepth == 0) { _dicts.Clear(); _nextDictId = 0; _parenDepth = 0; }
        _evalDepth++;
        try   { return new Parser(this, expr).Run(); }
        catch (Exception ex) { return $"ERR:{ex.GetType().Name}:{ex.Message}"; }
        finally { _evalDepth--; }
    }

    // ── Primary evaluator ─────────────────────────────────────────────────────

    /// <summary>
    /// Evaluates a primary: a function call <c>funcname[args]</c> or a plain literal.
    /// Called by the Parser when it has isolated an atom.
    /// </summary>
    internal string EvaluatePrimary(string expression)
    {
        string expr = expression.Trim();
        if (expr.Length == 0) return "";

        int lb = expr.IndexOf('[');
        if (lb < 0 || !expr.EndsWith("]", StringComparison.Ordinal))
            return expr; // plain literal

        string funcName = expr.Substring(0, lb).Trim().ToLowerInvariant();
        string argsStr  = expr.Substring(lb + 1, expr.Length - lb - 2);

        // ── Lazy (only the taken branch is evaluated) ─────────────────────────
        if (funcName == "iif")
        {
            var raw = SplitArgs(argsStr);
            if (raw.Count < 2) return "";
            return ToBool(Evaluate(raw[0]))
                ? Evaluate(raw[1])
                : (raw.Count >= 3 ? Evaluate(raw[2]) : "");
        }
        if (funcName == "if")
        {
            var raw = SplitArgs(argsStr);
            if (raw.Count < 2) return "";
            return ToBool(Evaluate(raw[0])) ? Evaluate(raw[1]) : "";
        }
        if (funcName == "ifthen")
        {
            // ifthen[value, trueexpr, falseexpr?]
            // Evaluates value, picks the matching branch string, re-evaluates it.
            var raw = SplitArgs(argsStr);
            if (raw.Count < 2) return "";
            bool cond = ToBool(Evaluate(raw[0]));
            string branchExpr = cond ? Evaluate(raw[1]) : (raw.Count >= 3 ? Evaluate(raw[2]) : "");
            return branchExpr.Length > 0 ? Evaluate(branchExpr) : "";
        }

        // ── Eager (args evaluated on demand via A()) ──────────────────────────
        var rawArgs = SplitArgs(argsStr);
        string A(int i) => i < rawArgs.Count ? Evaluate(rawArgs[i]) : "";

        // Raw expression template for higher-order list functions (not pre-evaluated).
        string Tmpl(int i) => i < rawArgs.Count ? StripBackticks(rawArgs[i]) : "";

        return funcName switch
        {
            // ── Session variables ──────────────────────────────────────────────
            "setvar"       => EvalSetVar(A(0), A(1)),
            "getvar"       => _variables.TryGetValue(A(0), out string? sv) ? sv : "0",
            "testvar"      => _variables.ContainsKey(A(0)) ? "1" : "0",
            "touchvar"     => EvalTouchVar(A(0)),
            "clearvar"     => _variables.Remove(A(0)) ? "1" : "0",
            "clearallvars" => EvalClearAllVars(),

            // ── Persistent variables (per-character, survive relog) ────────────
            "setpvar"       => EvalSetPvar(A(0), A(1)),
            "getpvar"       => GetPvars().TryGetValue(A(0), out string? pv) ? pv : "0",
            "testpvar"      => GetPvars().ContainsKey(A(0)) ? "1" : "0",
            "touchpvar"     => EvalTouchPvar(A(0)),
            "clearpvar"     => EvalClearPvar(A(0)),
            "clearallpvars" => EvalClearAllPvars(),

            // ── Global variables (shared across all characters) ────────────────
            "setgvar"       => EvalSetGvar(A(0), A(1)),
            "getgvar"       => GetGvars().TryGetValue(A(0), out string? gv) ? gv : "0",
            "testgvar"      => GetGvars().ContainsKey(A(0)) ? "1" : "0",
            "touchgvar"     => EvalTouchGvar(A(0)),
            "cleargvar"     => EvalClearGvar(A(0)),
            "clearallgvars" => EvalClearAllGvars(),

            "getfreeitemslots"       => EvalGetFreeItemSlots(rawArgs.Count > 0 ? A(0) : null),
            "getfreecontainerslots"  => EvalGetFreeContainerSlots(rawArgs.Count > 0 ? A(0) : null),
            "getcontaineritemcount"  => EvalGetContainerItemCount(rawArgs.Count > 0 ? A(0) : null),

            // ── Character properties ──────────────────────────────────────────
            "getcharintprop"    => EvalCharInt(A(0)),
            "getchardoubleprop" => EvalCharDouble(A(0)),
            "getcharquadprop"   => EvalCharQuad(A(0)),
            "getcharboolprop"     => EvalCharBool(A(0)),
            "getcharstringprop"   => EvalCharString(A(0)),
            "getisspellknown"         => EvalIsSpellKnown(A(0)),
            "getcancastspell_hunt"    => EvalCanCastSpell(A(0)),
            "getcancastspell_buff"    => EvalCanCastSpell(A(0)),
            "getspellexpiration"       => EvalSpellExpiration(A(0)),
            "getspellexpirationbyname" => EvalSpellExpirationByName(A(0)),
            "getcharvital_base"        => EvalCharVitalBase(A(0)),
            "getcharvital_current"     => EvalCharVitalCurrent(A(0)),
            "getcharvital_buffedmax"   => EvalCharVitalBuffedMax(A(0)),
            "getcharskill_traininglevel" => EvalCharSkillTrainingLevel(A(0)),
            "getcharskill_base"          => EvalCharSkillBase(A(0)),
            "getcharskill_buffed"        => EvalCharSkillBuffed(A(0)),
            "getcharattribute_base"      => EvalCharAttributeBase(A(0)),
            "getcharattribute_buffed"    => EvalCharAttributeBuffed(A(0)),
            "getcharburden"              => EvalCharBurdenPct(),
            "getcharburden_total"        => EvalCharInt("5"),
            "getplayerlandcell"          => EvalPlayerLandcell(),
            "getplayerlandblock"         => EvalPlayerLandblock(),
            "getplayercoordinates"       => EvalPlayerCoordinates(),
            "coordinategetns"            => EvalCoordinateGetNS(A(0)),
            "coordinategetwe"            => EvalCoordinateGetEW(A(0)),
            "coordinategetz"             => EvalCoordinateGetZ(A(0)),
            "coordinatetostring"         => EvalCoordinateToString(A(0)),
            "coordinateparse"            => EvalCoordinateParse(string.Join(",", Enumerable.Range(0, rawArgs.Count).Select(i => A(i)))),
            "coordinatedistancewithz"    => EvalCoordinateDistanceWithZ(A(0), A(1)),
            "coordinatedistanceflat"     => EvalCoordinateDistanceFlat(A(0), A(1)),

            // ── List functions ────────────────────────────────────────────────
            "listcreate"      => EvalListCreate(rawArgs),
            "listadd"         => EvalListAdd(A(0), A(1)),
            "listinsert"      => EvalListInsert(A(0), A(1), A(2)),
            "listremove"      => EvalListRemove(A(0), A(1)),
            "listremoveat"    => EvalListRemoveAt(A(0), A(1)),
            "listgetitem"     => EvalListGetItem(A(0), A(1)),
            "listcontains"    => EvalListContains(A(0), A(1)),
            "listindexof"     => EvalListIndexOf(A(0), A(1)),
            "listlastindexof" => EvalListLastIndexOf(A(0), A(1)),
            "listcopy"        => EvalListCopy(A(0)),
            "listreverse"     => EvalListReverse(A(0)),
            "listpop"         => EvalListPop(A(0), rawArgs.Count >= 2 ? A(1) : "-1"),
            "listcount"       => EvalListCount(A(0)),
            "listclear"       => EvalListClear(A(0)),
            "listfromrange"   => EvalListFromRange(A(0), A(1)),

            // Higher-order: expression arg is a raw template evaluated per iteration
            "listfilter"  => EvalListFilter(A(0), Tmpl(1)),
            "listmap"     => EvalListMap(A(0), Tmpl(1)),
            "listreduce"  => EvalListReduce(A(0), Tmpl(1)),
            "listsort"    => EvalListSort(A(0), Tmpl(1)),

            // ── World state ───────────────────────────────────────────────────
            "isportaling"       => EvalIsPortaling(),
            "getcellid"         => EvalGetCellId(),
            "vitae"             => EvalVitae(),
            "getaccounthash"    => EvalGetAccountHash(),
            "getworldname"      => EvalGetWorldName(),
            "getdatetimelocal"  => DateTime.Now.ToString(A(0)),
            "getdatetimeutc"    => DateTime.UtcNow.ToString(A(0)),
            "getunixtime"       => DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture),

            // ── World object queries ──────────────────────────────────────────
            "wobjectfindnearestbytemplatetype"        => EvalWobjectFindNearestByTemplateType(A(0)),
            "wobjectfindininventorybytemplatetype"    => EvalWobjectFindInInventoryByTemplateType(A(0)),
            "wobjectfindall"                          => EvalWobjectFindAll(),
            "wobjectfindallbyobjectclass"             => EvalWobjectFindAllByObjectClass(A(0)),
            "wobjectfindallbytemplatetype"            => EvalWobjectFindAllByTemplateType(A(0)),
            "wobjectfindallbynamerx"                  => EvalWobjectFindAllByNameRx(Tmpl(0)),
            "wobjectfindallinventory"                 => EvalWobjectFindAllInventory(),
            "wobjectfindalllandscape"                 => EvalWobjectFindAllLandscape(),
            "wobjectfindallinventorybytemplatetype"   => EvalWobjectFindAllInventoryByTemplateType(A(0)),
            "wobjectfindallinventorybyobjectclass"    => EvalWobjectFindAllInventoryByObjectClass(A(0)),
            "wobjectfindallinventorybynamerx"         => EvalWobjectFindAllInventoryByNameRx(Tmpl(0)),
            "wobjectfindininventorybyname"             => EvalWobjectFindInInventoryByName(Tmpl(0)),
            "wobjectfindininventorybynamerx"          => EvalWobjectFindInInventoryByNameRx(Tmpl(0)),
            "wobjectfindalllandscapebytemplatetype"   => EvalWobjectFindAllLandscapeByTemplateType(A(0)),
            "wobjectfindalllandscapebynamerx"         => EvalWobjectFindAllLandscapeByNameRx(Tmpl(0)),
            "wobjectfindallbycontainer"               => EvalWobjectFindAllByContainer(A(0)),
            "wobjectgetselection"          => EvalWobjectGetSelection(),
            "wobjectgetplayer"             => EvalWobjectGetPlayer(),
            "wobjectgetopencontainer"      => EvalWobjectGetOpenContainer(),
            "wobjectgetphysicscoordinates" => EvalWobjectGetPhysicsCoordinates(A(0)),
            "wobjectgetname"         => EvalWobjectGetName(A(0)),
            "wobjectgetid"           => EvalWobjectGetId(A(0)),
            "wobjectgettemplatetype" => EvalWobjectGetTemplateType(A(0)),
            "wobjectgetobjectclass"   => EvalWobjectGetObjClass(A(0)),
            "wobjectgetobjclass"     => EvalWobjectGetObjClass(A(0)), // legacy alias
            "wobjectfindnearestdoor" => EvalWobjectFindNearestDoor(),
            "wobjectfindnearestmonster" => EvalWobjectFindNearestMonster(),
            "wobjectfindbyid"                 => EvalWobjectFindById(A(0)),
            "wobjectfindnearestbyobjectclass" => EvalWobjectFindNearestByObjectClass(A(0)),
            "wobjectfindnearestbynameandobjectclass" => EvalWobjectFindNearestByNameAndObjectClass(A(0), Tmpl(1)),
            "wobjectgetisdooropen"   => EvalWobjectGetIsDoorOpen(A(0)),
            "wobjectgetphysicsstate" => EvalWobjectGetPhysicsState(A(0)),
            "wobjectgetintprop"      => EvalWobjectGetIntProp(A(0), A(1)),
            "wobjectgetdoubleprop"   => EvalWobjectGetDoubleProp(A(0), A(1)),
            "wobjectgetboolprop"     => EvalWobjectGetBoolProp(A(0), A(1)),
            "wobjectgetstringprop"   => EvalWobjectGetStringProp(A(0), A(1)),
            "wobjecthasdata"         => EvalWobjectHasData(A(0)),
            "wobjectrequestdata"     => EvalWobjectRequestData(A(0)),
            "wobjectlastidtime"      => EvalWobjectLastIdTime(A(0)),
            "wobjectisvalid"         => EvalWobjectIsValid(A(0)),
            "wobjectgethealth"         => EvalWobjectGetHealth(A(0)),
            "wobjectgetspellids"       => EvalWobjectGetSpellIds(A(0)),
            "wobjectgetactivespellids"     => EvalWobjectGetActiveSpellIds(A(0)),
            "wobjectgetactivespelldurations" => EvalWobjectGetActiveSpellDurations(A(0)),
            "getitemcountininventorybyname"   => EvalItemCountByName(Tmpl(0)),
            "getitemcountininventorybynamerx" => EvalItemCountByNameRx(Tmpl(0)),
            "getinventorycountbytemplatetype" => EvalGetInventoryCountByTemplateType(A(0)),
            "actiontryselect"        => EvalActionTrySelect(A(0)),
            "actiontryuseitem"       => EvalActionTryUseItem(A(0)),
            "actiontryapplyitem"     => EvalActionTryApplyItem(A(0), A(1)),
            "actiontrygiveitem"      => EvalActionTryGiveItem(A(0), A(1)),
            "actiontryequipanywand"  => EvalActionTryEquipAnyWand(),
            "actiontrycastbyid"            => EvalActionTryCastById(A(0)),
            "actiontrycastbyidontarget"    => EvalActionTryCastByIdOnTarget(A(0), A(1)),
            "getequippedweapontype"  => EvalGetEquippedWeaponType(),
            "getcombatstate"         => EvalGetCombatState(),
            "setcombatstate"         => EvalSetCombatState(A(0)),
            "getbusystate"           => EvalGetBusyState(),
            "setmotion"              => EvalSetMotion(A(0), A(1)),
            "getmotion"              => EvalGetMotion(A(0)),
            "clearmotion"            => EvalClearMotion(),

            // ── Quest flag queries ────────────────────────────────────────────
            "testquestflag"        => EvalTestQuestFlag(A(0)),
            "getqueststatus"       => EvalGetQuestStatus(A(0)),
            "getquestktprogress"   => EvalGetQuestKtProgress(A(0)),
            "getquestktrequired"   => EvalGetQuestKtRequired(A(0)),
            "isrefreshingquests"   => (_questTracker?.IsRefreshing == true) ? "1" : "0",
            "refreshquests"        => EvalRefreshQuests(),

            // ── Spell / component lookups ─────────────────────────────────────
            "spellname"      => _host.SpellName((uint)ToDouble(A(0))),
            "componentname"  => _host.ComponentName((uint)ToDouble(A(0))),
            "componentdata"  => EvalComponentData(A(0)),

            "getheadingto"           => EvalGetHeadingTo(A(0)),
            "getheading"             => EvalGetHeading(A(0)),

            // ── Math ──────────────────────────────────────────────────────────
            "getobjectinternaltype" => EvalGetObjectInternalType(A(0)),
            "isfalse"  => ToDouble(A(0)) == 0 ? "1" : "0",
            "istrue"   => ToDouble(A(0)) != 0 ? "1" : "0",
            "iif"      => EvalIif(A(0), A(1), A(2)),
            "randint"  => Random.Shared.Next((int)ToDouble(A(0)), (int)ToDouble(A(1))).ToString(CultureInfo.InvariantCulture),
            "cstr"     => A(0),
            "cstrf"    => EvalCstrf(A(0), Tmpl(1)),
            "cnumber"  => ToDouble(A(0)).ToString("G", CultureInfo.InvariantCulture),
            "strlen"   => A(0).Length.ToString(CultureInfo.InvariantCulture),
            "floor"    => Math.Floor(ToDouble(A(0))).ToString("G", CultureInfo.InvariantCulture),
            "ceiling"  => Math.Ceiling(ToDouble(A(0))).ToString("G", CultureInfo.InvariantCulture),
            "round"    => Math.Round(ToDouble(A(0)), MidpointRounding.AwayFromZero).ToString("G", CultureInfo.InvariantCulture),
            "abs"      => Math.Abs(ToDouble(A(0))).ToString("G", CultureInfo.InvariantCulture),
            "ord"      => EvalOrd(A(0)),
            "chr"      => EvalChr(A(0)),

            // ── Stopwatch ─────────────────────────────────────────────────────
            "stopwatchcreate"         => EvalStopwatchCreate(),
            "stopwatchstart"          => EvalStopwatchStart(A(0)),
            "stopwatchstop"           => EvalStopwatchStop(A(0)),
            "stopwatchelapsedseconds" => EvalStopwatchElapsedSeconds(A(0)),
            "hexstr" => "0x" + ((long)ToDouble(A(0))).ToString("X", CultureInfo.InvariantCulture),
            "acos"   => Math.Acos(ToDouble(A(0))).ToString("G", CultureInfo.InvariantCulture),
            "asin"   => Math.Asin(ToDouble(A(0))).ToString("G", CultureInfo.InvariantCulture),
            "atan"   => Math.Atan(ToDouble(A(0))).ToString("G", CultureInfo.InvariantCulture),
            "atan2"  => Math.Atan2(ToDouble(A(0)), ToDouble(A(1))).ToString("G", CultureInfo.InvariantCulture),
            "cos"    => Math.Cos(ToDouble(A(0))).ToString("G", CultureInfo.InvariantCulture),
            "cosh"   => Math.Cosh(ToDouble(A(0))).ToString("G", CultureInfo.InvariantCulture),
            "sin"    => Math.Sin(ToDouble(A(0))).ToString("G", CultureInfo.InvariantCulture),
            "sinh"   => Math.Sinh(ToDouble(A(0))).ToString("G", CultureInfo.InvariantCulture),
            "sqrt"   => Math.Sqrt(ToDouble(A(0))).ToString("G", CultureInfo.InvariantCulture),
            "tan"    => Math.Tan(ToDouble(A(0))).ToString("G", CultureInfo.InvariantCulture),
            "tanh"   => Math.Tanh(ToDouble(A(0))).ToString("G", CultureInfo.InvariantCulture),

            // ── Chat ──────────────────────────────────────────────────────────
            "chatbox"      => EvalChatbox(A(0)),
            "chatboxpaste" => "0",   // no backing API — stub
            "echo"         => EvalEcho(A(0), A(1)),

            // ── Salvage (UST) ─────────────────────────────────────────────────
            "ustopen"    => EvalUstOpen(),
            "ustadd"     => EvalUstAdd(A(0)),
            "ustsalvage" => EvalUstSalvage(),

            // ── Game time (Dereth) ────────────────────────────────────────────
            "getgameyear"          => GetGameYear().ToString(CultureInfo.InvariantCulture),
            "getgamemonth"         => GetGameMonth().ToString(CultureInfo.InvariantCulture),
            "getgamemonthname"     => GetGameMonthName(A(0)),
            "getgameday"           => GetGameDay().ToString(CultureInfo.InvariantCulture),
            "getgamehour"          => GetGameHour().ToString(CultureInfo.InvariantCulture),
            "getgamehourname"      => GetGameHourName(A(0)),
            "getminutesuntilday"   => GetMinutesUntilDay().ToString(CultureInfo.InvariantCulture),
            "getminutesuntilnight" => GetMinutesUntilNight().ToString(CultureInfo.InvariantCulture),
            "getgameticks"         => GetGameTicks().ToString("G", CultureInfo.InvariantCulture),
            "getisday"             => GetIsDay() ? "1" : "0",
            "getisnight"           => GetIsDay() ? "0" : "1",

            // ── Fellowship ────────────────────────────────────────────────────
            "getfellowshipname"        => _fellowshipTracker?.FellowshipName ?? "",
            "getfellowshipcount"       => (_fellowshipTracker?.MemberCount ?? 0).ToString(CultureInfo.InvariantCulture),
            "getfellowshipleaderid"    => (_fellowshipTracker?.LeaderId ?? 0).ToString(CultureInfo.InvariantCulture),
            "getfellowshiplocked"      => (_fellowshipTracker?.IsLocked == true ? "1" : "0"),
            "getfellowshipisleader"    => (_fellowshipTracker?.IsLeader == true ? "1" : "0"),
            "getfellowshipisopen"      => (_fellowshipTracker?.IsOpen == true ? "1" : "0"),
            "getfellowshipstatus"      => EvalGetFellowshipStatus(),
            "getfellowshipisfull"      => EvalFellowshipIsFull(),
            "getfellowshipcanrecruit"  => EvalFellowshipCanRecruit(),
            "getfellowid"              => EvalGetFellowId(A(0)),
            "getfellowname"            => EvalGetFellowName(A(0)),
            "getfellownames"           => EvalGetFellowNames(),
            "getfellowids"             => EvalGetFellowIds(),

            // ── Plugin options ────────────────────────────────────────────────
            "raoptget" or "uboptget" => GetOption(A(0)),
            "raoptset" or "uboptset" => EvalOptSet(A(0), A(1)),

            // ── RynthAi settings / meta state (VTank-compatible names) ────────
            "rasetmetastate" or "vtsetmetastate" or "setmetastate" => EvalVtSetMetaState(Tmpl(0)),
            "ragetmetastate" => _settings?.CurrentState ?? "",
            "rasetsetting" or "vtsetsetting"     => EvalVtSetSetting(Tmpl(0), A(1)),
            "ragetsetting" or "vtgetsetting" or "vtankgetsetting" => EvalVtGetSetting(Tmpl(0)),

            // ── Dynamic evaluation ────────────────────────────────────────────
            "exec"      => Evaluate(A(0)),
            "delayexec" => EvalDelayExec(A(0), A(1)),
            "tostring"  => A(0),

            // ── String functions ──────────────────────────────────────────────
            "getregexmatch" => EvalGetRegexMatch(A(0), A(1)),

            // ── Dictionary functions ───────────────────────────────────────────
            "dictcreate"    => EvalDictCreate(rawArgs),
            "dictgetitem"   => EvalDictGetItem(A(0), A(1)),
            "dictadditem"   => EvalDictAddItem(A(0), A(1), A(2)),
            "dicthaskey"    => EvalDictHasKey(A(0), A(1)),
            "dictremovekey" => EvalDictRemoveKey(A(0), A(1)),
            "dictkeys"      => EvalDictKeys(A(0)),
            "dictvalues"    => EvalDictValues(A(0)),
            "dictsize"      => EvalDictSize(A(0)),
            "dictclear"     => EvalDictClear(A(0)),
            "dictcopy"      => EvalDictCopy(A(0)),

            _ => EvalUnknownFunction(funcName)
        };
    }

    /// <summary>Unknown-function fallback (M3): returns "" per engine convention but logs the
    /// missing verb ONCE per distinct name, so a typo'd or unsupported meta function is visible
    /// instead of silently evaluating to empty. Rate-limited by the seen-set to avoid log spam.</summary>
    private readonly HashSet<string> _loggedUnknownFns = new();
    private string EvalUnknownFunction(string funcName)
    {
        if (!string.IsNullOrEmpty(funcName) && _loggedUnknownFns.Add(funcName))
            _host.Log($"[Meta] unknown expression function '{funcName}[...]' — evaluates to empty; " +
                      "check spelling / supported verbs. (logged once per name)");
        return "";
    }

    // ── Variable / char-prop implementations ──────────────────────────────────

    public void SetVariable(string key, string value)
    {
        if (!string.IsNullOrEmpty(key)) _variables[key] = value;
    }

    private string EvalSetVar(string key, string value)
    {
        if (!string.IsNullOrEmpty(key)) _variables[key] = value;
        return value;
    }

    private string EvalTouchVar(string key)
    {
        if (_variables.ContainsKey(key)) return "1";
        _variables[key] = "0";
        return "0";
    }

    private string EvalClearAllVars()
    {
        _variables.Clear();
        return "1";
    }

    // ── Persistent variable implementations ───────────────────────────────────

    private Dictionary<string, string> GetPvars()
    {
        if (_pvars != null) return _pvars;
        return _pvars = LoadVarFile(GetPvarPath());
    }

    private string GetPvarPath()
    {
        if (_pvarPathCached != null) return _pvarPathCached;
        string charName = "unknown";
        if (_playerId != 0 && _host.HasGetObjectName)
            _host.TryGetObjectName(_playerId, out charName);
        charName = SanitizeFileName(charName ?? "unknown");
        string p = PvarsPrefix + charName + ".txt";
        if (charName != "unknown") _pvarPathCached = p;   // only cache a resolved name
        return p;
    }

    private string EvalSetPvar(string key, string value)
    {
        if (string.IsNullOrEmpty(key)) return value;
        GetPvars()[key] = value;
        _pvarsDirty = true;
        return value;
    }

    private string EvalTouchPvar(string key)
    {
        var pvars = GetPvars();
        if (pvars.ContainsKey(key)) return "1";
        pvars[key] = "0";
        _pvarsDirty = true;
        return "0";
    }

    private string EvalClearPvar(string key)
    {
        var pvars = GetPvars();
        if (!pvars.Remove(key)) return "0";
        _pvarsDirty = true;
        return "1";
    }

    private string EvalClearAllPvars()
    {
        GetPvars().Clear();
        _pvarsDirty = true;
        return "1";
    }

    // ── Global variable implementations ───────────────────────────────────────

    private Dictionary<string, string> GetGvars()
        => _gvars ??= LoadVarFile(GvarsKey);

    private string EvalSetGvar(string key, string value)
    {
        if (string.IsNullOrEmpty(key)) return value;
        GetGvars()[key] = value;
        _gvarsDirty = true;
        return value;
    }

    private string EvalTouchGvar(string key)
    {
        var gvars = GetGvars();
        if (gvars.ContainsKey(key)) return "1";
        gvars[key] = "0";
        _gvarsDirty = true;
        return "0";
    }

    private string EvalClearGvar(string key)
    {
        var gvars = GetGvars();
        if (!gvars.Remove(key)) return "0";
        _gvarsDirty = true;
        return "1";
    }

    private string EvalClearAllGvars()
    {
        GetGvars().Clear();
        _gvarsDirty = true;
        return "1";
    }

    /// <summary>
    /// Writes dirty pvar/gvar files. Called from the plugin-tick pump; throttled
    /// to <see cref="VarFlushIntervalMs"/> unless <paramref name="force"/>
    /// (char switch / macro stop / shutdown) to bound data loss to ~2 s without
    /// a per-tick synchronous disk write on the game thread.
    /// </summary>
    public void FlushVars(bool force = false)
    {
        if (!_pvarsDirty && !_gvarsDirty) return;
        long now = Environment.TickCount64;
        if (!force && now - _lastVarFlushMs < VarFlushIntervalMs) return;
        if (_pvarsDirty && _pvars != null) { SaveVarFile(GetPvarPath(), _pvars); _pvarsDirty = false; }
        if (_gvarsDirty && _gvars != null) { SaveVarFile(GvarsKey, _gvars);     _gvarsDirty = false; }
        _lastVarFlushMs = now;
    }

    // ── Var file helpers ──────────────────────────────────────────────────────

    private Dictionary<string, string> LoadVarFile(string key)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? text = _host.ReadStorage(key);
        if (text is null) return dict;
        foreach (string line in text.Split('\n'))
        {
            int tab = line.IndexOf('\t');
            if (tab > 0) dict[line[..tab]] = line[(tab + 1)..].TrimEnd('\r');
        }
        return dict;
    }

    private void SaveVarFile(string key, Dictionary<string, string> dict)
    {
        try
        {
            _host.WriteStorage(key, string.Join('\n', dict.Select(kv => $"{kv.Key}\t{kv.Value}")));
        }
        catch { /* non-fatal */ }
    }

    private static string SanitizeFileName(string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (char c in name) sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        return sb.Length > 0 ? sb.ToString() : "unknown";
    }

    // ── Stopwatch implementations ─────────────────────────────────────────────

    private string EvalStopwatchCreate()
    {
        string handle = $"SW:{_nextSwId++}";
        _stopwatches[handle] = new System.Diagnostics.Stopwatch();
        return handle;
    }

    private string EvalStopwatchStart(string handle)
    {
        if (_stopwatches.TryGetValue(handle, out var sw)) sw.Start();
        return handle;
    }

    private string EvalStopwatchStop(string handle)
    {
        if (_stopwatches.TryGetValue(handle, out var sw)) sw.Stop();
        return handle;
    }

    private string EvalStopwatchElapsedSeconds(string handle)
    {
        if (!_stopwatches.TryGetValue(handle, out var sw)) return "0";
        return sw.Elapsed.TotalSeconds.ToString("G", CultureInfo.InvariantCulture);
    }

    private static string EvalOrd(string s)
        => s.Length > 0 ? ((int)s[0]).ToString(CultureInfo.InvariantCulture) : "0";

    private static string EvalChr(string arg)
    {
        int code = (int)ToDouble(arg);
        return code is >= 0 and <= 0xFFFF ? ((char)code).ToString() : "";
    }

    private static string EvalCstrf(string numArg, string fmt)
    {
        double d = ToDouble(numArg);
        try { return d.ToString(fmt, CultureInfo.InvariantCulture); }
        catch { return d.ToString("G", CultureInfo.InvariantCulture); }
    }

    // Both trueVal and falseVal are pre-evaluated by the caller (C# arg eval order) — matches UB behaviour.
    private static string EvalIif(string cond, string trueVal, string falseVal)
        => ToDouble(cond) != 0 ? trueVal : falseVal;

    // 0=none, 1=number, 3=string, 7=object — matches UB internal type constants.
    private static string EvalGetObjectInternalType(string val)
    {
        if (string.IsNullOrEmpty(val)) return "0";
        if (TryParseWobjectHandle(val, out _, out _)) return "7";
        if (val.Length > 3 && val[0] == 'S' && val[1] == 'W' && val[2] == ':') return "7";
        if (val.Length > 2 && val[0] == 'D' && val[1] == ':') return "7";
        if (double.TryParse(val, System.Globalization.NumberStyles.Any, CultureInfo.InvariantCulture, out _)) return "1";
        return "3";
    }

    private string EvalCharInt(string arg)
    {
        if (!uint.TryParse(arg.Trim(), out uint st) || _playerId == 0 || !_host.HasGetObjectIntProperty) return "0";
        return _host.TryGetObjectIntProperty(_playerId, st, out int v) ? v.ToString(CultureInfo.InvariantCulture) : "0";
    }

    private string EvalCharDouble(string arg)
    {
        if (!uint.TryParse(arg.Trim(), out uint st) || _playerId == 0 || !_host.HasGetObjectDoubleProperty) return "0";
        return _host.TryGetObjectDoubleProperty(_playerId, st, out double v) ? v.ToString("G", CultureInfo.InvariantCulture) : "0";
    }

    private string EvalCharQuad(string arg)
    {
        if (!uint.TryParse(arg.Trim(), out uint st) || _playerId == 0 || !_host.HasGetObjectQuadProperty) return "0";
        return _host.TryGetObjectQuadProperty(_playerId, st, out long v) ? v.ToString(CultureInfo.InvariantCulture) : "0";
    }

    private string EvalCharBool(string arg)
    {
        if (!uint.TryParse(arg.Trim(), out uint st) || _playerId == 0 || !_host.HasGetObjectBoolProperty) return "0";
        return _host.TryGetObjectBoolProperty(_playerId, st, out bool v) ? (v ? "1" : "0") : "0";
    }

    private string EvalCharString(string arg)
    {
        if (!uint.TryParse(arg.Trim(), out uint st) || _playerId == 0 || !_host.HasGetObjectStringProperty) return "";
        return _host.TryGetObjectStringProperty(_playerId, st, out string? v) ? v ?? "" : "";
    }

    private string EvalIsSpellKnown(string arg)
    {
        if (!uint.TryParse(arg.Trim(), out uint spellId) || _playerId == 0 || !_host.HasIsSpellKnown) return "0";
        return _host.IsSpellKnown(_playerId, spellId, out bool known) ? (known ? "1" : "0") : "0";
    }

    private string EvalCanCastSpell(string arg)
    {
        if (!uint.TryParse(arg.Trim(), out uint spellId) || _playerId == 0) return "0";
        if (_host.HasIsSpellKnown && !(_host.IsSpellKnown(_playerId, spellId, out bool known) && known)) return "0";
        if (_host.HasGetPlayerVitals &&
            _host.TryGetPlayerVitals(out _, out _, out _, out _, out uint mana, out _) && mana == 0) return "0";
        return "1";
    }

    private string EvalCharVitalBase(string arg)
    {
        // Map vitalId 1/2/3 → STypeAttribute2nd 1/3/5 (MAX_HEALTH/MAX_STAMINA/MAX_MANA)
        // Base = _initLevel + _levelFromCp from InqAttribute2ndStruct — confirmed unbuffed in PlayerVitalsHooks.
        if (!_host.HasGetPlayerBaseVitals) return "0";
        if (!_host.TryGetPlayerBaseVitals(out uint baseHp, out uint baseStam, out uint baseMana)) return "0";
        return arg.Trim() switch
        {
            "1" => baseHp.ToString(CultureInfo.InvariantCulture),
            "2" => baseStam.ToString(CultureInfo.InvariantCulture),
            "3" => baseMana.ToString(CultureInfo.InvariantCulture),
            _   => "0",
        };
    }

    private string EvalCharVitalCurrent(string arg)
    {
        if (!_host.HasGetPlayerVitals) return "0";
        if (!_host.TryGetPlayerVitals(out uint hp, out _, out uint stam, out _, out uint mana, out _)) return "0";
        return arg.Trim() switch
        {
            "1" => hp.ToString(CultureInfo.InvariantCulture),
            "2" => stam.ToString(CultureInfo.InvariantCulture),
            "3" => mana.ToString(CultureInfo.InvariantCulture),
            _   => "0",
        };
    }

    private string EvalCharVitalBuffedMax(string arg)
    {
        // Uses InqAttribute2nd live — always returns current buffed max, no stale snapshot issue.
        uint stype2nd = arg.Trim() switch { "1" => 1u, "2" => 3u, "3" => 5u, _ => 0u };
        if (stype2nd == 0 || _playerId == 0 || !_host.HasGetObjectAttribute2ndBaseLevel) return "0";
        return _host.TryGetObjectAttribute2ndBaseLevel(_playerId, stype2nd, out uint buffedMax)
            ? buffedMax.ToString(CultureInfo.InvariantCulture) : "0";
    }

    private string EvalCharSkillTrainingLevel(string arg)
    {
        if (!uint.TryParse(arg.Trim(), out uint skillId) || skillId == 0) return "0";
        if (_playerId == 0 || !_host.HasGetObjectSkill) return "0";
        return _host.TryGetObjectSkill(_playerId, skillId, out _, out int training)
            ? training.ToString(CultureInfo.InvariantCulture) : "0";
    }

    private string EvalCharSkillBase(string arg)
    {
        if (!uint.TryParse(arg.Trim(), out uint skillId) || skillId == 0) return "0";
        if (_playerId == 0 || !_host.HasGetObjectSkillBuffed) return "0";
        return _host.TryGetObjectSkillLevel(_playerId, skillId, 1, out int level)
            ? level.ToString(CultureInfo.InvariantCulture) : "0";
    }

    private string EvalCharSkillBuffed(string arg)
    {
        if (!uint.TryParse(arg.Trim(), out uint skillId) || skillId == 0) return "0";
        if (_playerId == 0 || !_host.HasGetObjectSkillBuffed) return "0";
        return _host.TryGetObjectSkillLevel(_playerId, skillId, 0, out int level)
            ? level.ToString(CultureInfo.InvariantCulture) : "0";
    }

    private string EvalCharAttributeBase(string arg)
    {
        if (!uint.TryParse(arg.Trim(), out uint attrId) || attrId == 0) return "0";
        if (_playerId == 0 || !_host.HasGetObjectAttribute) return "0";
        return _host.TryGetObjectAttribute(_playerId, attrId, 1, out uint value)
            ? value.ToString(CultureInfo.InvariantCulture) : "0";
    }

    private string EvalCharAttributeBuffed(string arg)
    {
        if (!uint.TryParse(arg.Trim(), out uint attrId) || attrId == 0) return "0";
        if (_playerId == 0 || !_host.HasGetObjectAttribute) return "0";
        return _host.TryGetObjectAttribute(_playerId, attrId, 0, out uint value)
            ? value.ToString(CultureInfo.InvariantCulture) : "0";
    }

    private string EvalCharBurdenPct()
    {
        // Burden capacity = buffed Strength * 150 (not stored as a property, derived from attribute)
        if (_playerId == 0 || !_host.HasGetObjectIntProperty || !_host.HasGetObjectAttribute) return "0";
        if (!_host.TryGetObjectIntProperty(_playerId, 5u, out int current)) return "0";
        if (!_host.TryGetObjectAttribute(_playerId, 1u, 0, out uint strength) || strength == 0) return "0";
        double pct = current * 100.0 / ((double)strength * 150.0);
        return pct.ToString("G", CultureInfo.InvariantCulture);
    }

    private string EvalPlayerLandcell()
    {
        if (!_host.HasGetPlayerPose) return "0";
        if (!_host.TryGetPlayerPose(out uint objCellId, out _, out _, out _, out _, out _, out _, out _)) return "0";
        return ((double)objCellId).ToString("G", CultureInfo.InvariantCulture);
    }

    private string EvalPlayerLandblock()
    {
        if (!_host.HasGetPlayerPose) return "0";
        if (!_host.TryGetPlayerPose(out uint objCellId, out _, out _, out _, out _, out _, out _, out _)) return "0";
        return ((double)(objCellId & 0xFFFF0000u)).ToString("G", CultureInfo.InvariantCulture);
    }

    // Coordinates object internal format: "NS|EW|Z" (pipe-delimited, full precision).
    // NS positive=N, negative=S. EW positive=E, negative=W. Z is in coordinate units (raw_z/240).
    // coordinatetostring[] formats for display ("41.53N, 34.46E, 0.47Z"); coordinateparse[] converts display → internal.

    private static readonly System.Text.RegularExpressions.Regex _coordParseRegex = new(
        @"(?<NSval>[0-9]{1,3}(?:\.[0-9]{1,3})?)(?<NSchr>[ns])(?:[,\s]+)?(?<EWval>[0-9]{1,3}(?:\.[0-9]{1,3})?)(?<EWchr>[ew])?(,?\s*(?<Zval>-?\d+\.?\d*)z)?",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase |
        System.Text.RegularExpressions.RegexOptions.Compiled);

    private static bool TryParseCoordObject(string coords, out double ns, out double ew, out double z)
    {
        ns = ew = z = 0;
        if (string.IsNullOrEmpty(coords)) return false;
        var parts = coords.Split('|');
        if (parts.Length < 2) return false;
        return double.TryParse(parts[0], System.Globalization.NumberStyles.Any, CultureInfo.InvariantCulture, out ns)
            && double.TryParse(parts[1], System.Globalization.NumberStyles.Any, CultureInfo.InvariantCulture, out ew)
            && (parts.Length < 3 || double.TryParse(parts[2], System.Globalization.NumberStyles.Any, CultureInfo.InvariantCulture, out z));
    }

    private static string MakeCoordObject(double ns, double ew, double z)
        => string.Create(CultureInfo.InvariantCulture, $"{ns}|{ew}|{z}");

    private string EvalPlayerCoordinates()
    {
        if (!_host.HasGetPlayerPose) return "";
        if (!_host.TryGetPlayerPose(out uint objCellId, out float x, out float y, out float z, out _, out _, out _, out _)) return "";
        if (!MetaWorld.TryConvertPoseToCoords(objCellId, x, y, out double ns, out double ew)) return "";
        return MakeCoordObject(ns, ew, z / 240.0);
    }

    private string EvalCoordinateGetNS(string coords)
    {
        if (!TryParseCoordObject(coords, out double ns, out _, out _)) return "0";
        return ns.ToString("G", CultureInfo.InvariantCulture);
    }

    private string EvalCoordinateGetEW(string coords)
    {
        if (!TryParseCoordObject(coords, out _, out double ew, out _)) return "0";
        return ew.ToString("G", CultureInfo.InvariantCulture);
    }

    private string EvalCoordinateGetZ(string coords)
    {
        if (!TryParseCoordObject(coords, out _, out _, out double z)) return "0";
        return z.ToString("G", CultureInfo.InvariantCulture);
    }

    private string EvalCoordinateToString(string coords)
    {
        if (!TryParseCoordObject(coords, out double ns, out double ew, out double z)) return "";
        string nsStr = ns >= 0
            ? string.Create(CultureInfo.InvariantCulture, $"{ns:F2}N")
            : string.Create(CultureInfo.InvariantCulture, $"{-ns:F2}S");
        string ewStr = ew >= 0
            ? string.Create(CultureInfo.InvariantCulture, $"{ew:F2}E")
            : string.Create(CultureInfo.InvariantCulture, $"{-ew:F2}W");
        return string.Create(CultureInfo.InvariantCulture, $"{nsStr}, {ewStr}, {z:F2}Z");
    }

    private string EvalCoordinateParse(string input)
    {
        var m = _coordParseRegex.Match(input.Trim());
        if (!m.Success) return "";
        if (!double.TryParse(m.Groups["NSval"].Value, System.Globalization.NumberStyles.Any, CultureInfo.InvariantCulture, out double ns)) return "";
        if (!double.TryParse(m.Groups["EWval"].Value, System.Globalization.NumberStyles.Any, CultureInfo.InvariantCulture, out double ew)) return "";
        ns *= m.Groups["NSchr"].Value.Equals("n", StringComparison.OrdinalIgnoreCase) ? 1 : -1;
        ew *= m.Groups["EWchr"].Value.Equals("e", StringComparison.OrdinalIgnoreCase) ? 1 : -1;
        double z = 0;
        if (m.Groups["Zval"].Success && !string.IsNullOrEmpty(m.Groups["Zval"].Value))
            double.TryParse(m.Groups["Zval"].Value, System.Globalization.NumberStyles.Any, CultureInfo.InvariantCulture, out z);
        return MakeCoordObject(ns, ew, z);
    }

    private string EvalCoordinateDistanceWithZ(string coords1, string coords2)
    {
        if (!TryParseCoordObject(coords1, out double ns1, out double ew1, out double z1)) return "0";
        if (!TryParseCoordObject(coords2, out double ns2, out double ew2, out double z2)) return "0";
        double dNS = (ns1 - ns2) * 240.0;
        double dEW = (ew1 - ew2) * 240.0;
        double dZ  = (z1  - z2)  * 240.0;
        return Math.Sqrt(dNS * dNS + dEW * dEW + dZ * dZ).ToString("G", CultureInfo.InvariantCulture);
    }

    private string EvalWobjectGetPhysicsCoordinates(string objArg)
    {
        if (!TryParseWobjectHandle(objArg, out uint uid, out _)) return "";
        if (!_host.HasGetObjectPosition) return "";
        if (!_host.TryGetObjectPosition(uid, out uint objCellId, out float x, out float y, out float z)) return "";
        if (!MetaWorld.TryConvertPoseToCoords(objCellId, x, y, out double ns, out double ew)) return "";
        return MakeCoordObject(ns, ew, z / 240.0);
    }

    private string EvalCoordinateDistanceFlat(string coords1, string coords2)
    {
        if (!TryParseCoordObject(coords1, out double ns1, out double ew1, out _)) return "0";
        if (!TryParseCoordObject(coords2, out double ns2, out double ew2, out _)) return "0";
        double dNS = (ns1 - ns2) * 240.0;
        double dEW = (ew1 - ew2) * 240.0;
        return Math.Sqrt(dNS * dNS + dEW * dEW).ToString("G", CultureInfo.InvariantCulture);
    }

    private string EvalSpellExpiration(string arg)
    {
        if (!uint.TryParse(arg.Trim(), out uint spellId)) return "0";
        if (!_host.HasReadPlayerEnchantments || !_host.HasGetServerTime) return "0";
        double serverNow = _host.GetServerTime();
        if (serverNow <= 0) return "0";
        const int Max = 512;
        uint[] ids = new uint[Max];
        double[] expiry = new double[Max];
        int count = _host.ReadPlayerEnchantments(ids, expiry, Max);
        if (count <= 0) return "0";
        for (int i = 0; i < count; i++)
        {
            if (ids[i] != spellId) continue;
            double remaining = expiry[i] - serverNow;
            if (remaining <= 0) return "0";
            if (remaining > 86400 * 365) return "2147483647";
            return ((int)Math.Ceiling(remaining)).ToString(CultureInfo.InvariantCulture);
        }
        return "0";
    }

    private string EvalSpellExpirationByName(string nameArg)
    {
        if (string.IsNullOrEmpty(nameArg)) return "-1";
        if (!_host.HasReadPlayerEnchantments || !_host.HasGetServerTime) return "-1";
        double serverNow = _host.GetServerTime();
        if (serverNow <= 0) return "-1";
        const int Max = 512;
        uint[] ids = new uint[Max];
        double[] expiry = new double[Max];
        int count = _host.ReadPlayerEnchantments(ids, expiry, Max);
        if (count < 0) return "-1";
        for (int i = 0; i < count; i++)
        {
            string spellName = _host.SpellName(ids[i]);
            if (spellName.IndexOf(nameArg, StringComparison.OrdinalIgnoreCase) < 0) continue;
            double remaining = expiry[i] - serverNow;
            if (remaining <= 0) return "0";
            if (remaining > 86400 * 365) return "2147483647";
            return ((int)Math.Ceiling(remaining)).ToString(CultureInfo.InvariantCulture);
        }
        return "0";
    }

    // ── List helpers ──────────────────────────────────────────────────────────

    private string EvalListCreate(List<string> rawArgs)
    {
        if (rawArgs.Count == 0) return "[]";
        var items = new List<string>(rawArgs.Count);
        foreach (var arg in rawArgs) items.Add(Evaluate(arg));
        return NewList(items);
    }

    private static string NewList(IEnumerable<string>? items = null)
        => items != null ? "[" + string.Join(",", items) + "]" : "[]";

    private static List<string>? GetList(string handle)
    {
        handle = handle.Trim();
        if (handle.Length < 2 || handle[0] != '[' || handle[handle.Length - 1] != ']') return null;
        string inner = handle.Substring(1, handle.Length - 2);
        return inner.Length == 0 ? new List<string>() : new List<string>(inner.Split(','));
    }

    private static string StripBackticks(string s)
    {
        s = s.Trim();
        return s.Length >= 2 && s[0] == '`' && s[s.Length - 1] == '`'
            ? s.Substring(1, s.Length - 2)
            : s;
    }

    // ── List function implementations ─────────────────────────────────────────

    private static string EvalListAdd(string handle, string item)
    {
        var list = GetList(handle);
        if (list == null) return "0";
        list.Add(item);
        return NewList(list);
    }

    private static string EvalListRemove(string handle, string item)
    {
        var list = GetList(handle);
        if (list == null) return "0";
        for (int i = 0; i < list.Count; i++)
        {
            if (AreEqual(list[i], item)) { list.RemoveAt(i); break; }
        }
        return NewList(list);
    }

    private static string EvalListRemoveAt(string handle, string indexArg)
    {
        var list = GetList(handle);
        if (list == null) return "0";
        int idx = (int)ToLong(indexArg);
        if (idx < 0 || idx > list.Count - 1) return "0";
        list.RemoveAt(idx);
        return NewList(list);
    }

    private static string EvalListInsert(string handle, string item, string indexArg)
    {
        var list = GetList(handle);
        if (list == null) return "0";
        int idx = (int)ToLong(indexArg);
        if (idx < 0 || idx > list.Count) return "0";
        list.Insert(idx, item);
        return NewList(list);
    }

    private static string EvalListGetItem(string handle, string indexArg)
    {
        var list = GetList(handle);
        if (list == null) return "";
        int idx = (int)ToLong(indexArg);
        return idx >= 0 && idx < list.Count ? list[idx] : "";
    }

    private static string EvalListContains(string handle, string item)
    {
        var list = GetList(handle);
        if (list == null) return "0";
        foreach (var s in list)
            if (AreEqual(s, item)) return "1";
        return "0";
    }

    private static string EvalListIndexOf(string handle, string item)
    {
        var list = GetList(handle);
        if (list == null) return "-1";
        for (int i = 0; i < list.Count; i++)
            if (AreEqual(list[i], item)) return Fmt((long)i);
        return "-1";
    }

    private static string EvalListLastIndexOf(string handle, string item)
    {
        var list = GetList(handle);
        if (list == null) return "-1";
        for (int i = list.Count - 1; i >= 0; i--)
            if (AreEqual(list[i], item)) return Fmt((long)i);
        return "-1";
    }

    private static string EvalListCopy(string handle)
        => GetList(handle) != null ? handle.Trim() : "[]";

    private static string EvalListReverse(string handle)
    {
        var list = GetList(handle);
        if (list == null) return "[]";
        var copy = new List<string>(list);
        copy.Reverse();
        return NewList(copy);
    }

    private static string EvalListPop(string handle, string indexArg)
    {
        var list = GetList(handle);
        if (list == null || list.Count == 0) return "";
        int idx = (int)ToLong(indexArg);
        if (idx < 0) idx = list.Count - 1; // -1 = last item
        if (idx < 0 || idx >= list.Count) return "";
        return list[idx];
    }

    private static string EvalListCount(string handle)
    {
        var list = GetList(handle);
        return list != null ? Fmt((long)list.Count) : "0";
    }

    private static string EvalListClear(string _) => "[]";

    private static string EvalListFromRange(string startArg, string endArg)
    {
        long start = ToLong(startArg);
        long end   = ToLong(endArg);
        var items  = new List<string>();
        if (start <= end)
            for (long i = start; i <= end; i++) items.Add(Fmt(i));
        else
            for (long i = start; i >= end; i--) items.Add(Fmt(i));
        return NewList(items);
    }

    // Higher-order helpers: set $0/$1/$2, evaluate template, restore.
    //
    // Deep-audit finding #26 (2026-06-18): despite this comment, none of the
    // four functions below actually restored "0"/"1"/"2" afterward — they
    // wrote straight into the persistent _variables dictionary and left
    // whatever the last loop iteration set. Nested list ops clobbered each
    // other's $0/$1 mid-evaluation, and a user's own numeric-named $0/$1/$2
    // (if they had any) were silently overwritten for the rest of the meta.
    // Snapshot before, restore-or-Remove in a finally, per the fix note.

    /// <summary>Snapshots the given _variables keys, returning a restore action for a finally block.</summary>
    private Action SnapshotVars(params string[] keys)
    {
        var saved = new (string Key, bool Had, string? Value)[keys.Length];
        for (int i = 0; i < keys.Length; i++)
            saved[i] = (keys[i], _variables.TryGetValue(keys[i], out var v), v);
        return () =>
        {
            foreach (var (key, had, value) in saved)
            {
                if (had) _variables[key] = value!;
                else _variables.Remove(key);
            }
        };
    }

    private string EvalListFilter(string handle, string exprTemplate)
    {
        var list = GetList(handle);
        if (list == null || exprTemplate.Length == 0) return NewList();
        var restore = SnapshotVars("0", "1");
        try
        {
            var result = new List<string>();
            for (int i = 0; i < list.Count; i++)
            {
                _variables["0"] = Fmt((long)i);
                _variables["1"] = list[i];
                if (ToBool(Evaluate(exprTemplate)))
                    result.Add(list[i]);
            }
            return NewList(result);
        }
        finally { restore(); }
    }

    private string EvalListMap(string handle, string exprTemplate)
    {
        var list = GetList(handle);
        if (list == null || exprTemplate.Length == 0) return NewList();
        var restore = SnapshotVars("0", "1");
        try
        {
            var result = new List<string>(list.Count);
            for (int i = 0; i < list.Count; i++)
            {
                _variables["0"] = Fmt((long)i);
                _variables["1"] = list[i];
                result.Add(Evaluate(exprTemplate));
            }
            return NewList(result);
        }
        finally { restore(); }
    }

    private string EvalListReduce(string handle, string exprTemplate)
    {
        var list = GetList(handle);
        if (list == null || list.Count == 0 || exprTemplate.Length == 0) return "0";
        var restore = SnapshotVars("0", "1", "2");
        try
        {
            string acc = "0";
            for (int i = 0; i < list.Count; i++)
            {
                _variables["0"] = Fmt((long)i);
                _variables["1"] = list[i];
                _variables["2"] = acc;
                acc = Evaluate(exprTemplate);
            }
            return acc;
        }
        finally { restore(); }
    }

    private string EvalListSort(string handle, string exprTemplate)
    {
        var list = GetList(handle);
        if (list == null) return NewList();
        var copy = new List<string>(list);
        var restore = SnapshotVars("1", "2");
        try
        {
            if (exprTemplate.Length > 0)
            {
                copy.Sort((a, b) =>
                {
                    _variables["1"] = a;
                    _variables["2"] = b;
                    return (int)ToLong(Evaluate(exprTemplate));
                });
            }
            else
            {
                copy.Sort(StringComparer.Ordinal);
            }
            return NewList(copy);
        }
        finally { restore(); }
    }

    // ── World state implementations ───────────────────────────────────────────

    private string EvalIsPortaling()
    {
        if (!_host.HasIsPortaling) return "0";
        return _host.IsPortaling() ? "1" : "0";
    }

    private string EvalVitae()
    {
        if (!_host.HasGetVitae || _playerId == 0) return "100";
        float v = _host.GetVitae(_playerId);
        // Convert multiplier to penalty: 1.0 → 0 (no penalty), 0.95 → 5 (5% penalty).
        int penalty = 100 - (int)Math.Round(v * 100.0f);
        if (penalty < 0) penalty = 0;
        if (penalty > 100) penalty = 100;
        return penalty.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private string EvalGetWorldName()
    {
        if (!_host.HasGetWorldName) return string.Empty;
        return _host.TryGetWorldName(out string name) ? name : string.Empty;
    }

    private string EvalGetAccountHash()
    {
        if (!_host.HasGetAccountName) return string.Empty;
        if (!_host.TryGetAccountName(out string name) || string.IsNullOrEmpty(name)) return string.Empty;
        byte[] bytes = System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(name));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private string EvalGetCellId()
    {
        if (!_host.HasGetPlayerPose) return "unavailable";
        if (!_host.TryGetPlayerPose(out uint cellId, out _, out _, out _, out _, out _, out _, out _)) return "pose_failed";
        return $"0x{cellId:X8}";
    }

    // ── MetaObject handle helpers ────────────────────────────────────────────
    // Handle format (UB-compatible): "[MetaObject] 0xHHHHHHHH: Name"
    // "0" means no object found.

    private static string MakeWobjectHandle(uint uid, string name)
        => $"[MetaObject] 0x{uid:X8}: {name}";

    /// <summary>
    /// Parses a MetaObject handle string into a uid and name.
    /// Accepts "[MetaObject] 0xHHHHHHHH: Name", bare hex "0xHHHHHHHH", or unsigned decimal.
    /// </summary>
    private static bool TryParseWobjectHandle(string handle, out uint uid, out string name)
    {
        uid = 0;
        name = string.Empty;
        handle = handle.Trim();

        if (handle == "0" || handle == string.Empty) return false;

        // Full handle: "[MetaObject] 0xHHHHHHHH: Name"
        const string prefix = "[MetaObject] 0x";
        if (handle.StartsWith(prefix, StringComparison.Ordinal))
        {
            int colonIdx = handle.IndexOf(':', prefix.Length);
            string hexPart = colonIdx > prefix.Length
                ? handle.Substring(prefix.Length, colonIdx - prefix.Length)
                : handle.Substring(prefix.Length);
            if (!uint.TryParse(hexPart, System.Globalization.NumberStyles.HexNumber, null, out uid))
                return false;
            name = colonIdx >= 0 && colonIdx + 2 < handle.Length
                ? handle.Substring(colonIdx + 2)
                : string.Empty;
            return uid != 0;
        }

        // Bare hex: "0xHHHHHHHH"
        if (handle.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (!uint.TryParse(handle.Substring(2), System.Globalization.NumberStyles.HexNumber, null, out uid))
                return false;
            return uid != 0;
        }

        // Unsigned or signed decimal
        if (uint.TryParse(handle, out uid)) return uid != 0;
        if (long.TryParse(handle, out long l)) { uid = unchecked((uint)l); return uid != 0; }
        return false;
    }

    private string EvalWobjectFindNearestByTemplateType(string arg)
    {
        if (_worldObjectCache == null || _playerId == 0 || !_host.HasGetObjectWcid) return "0";
        if (!uint.TryParse(arg.Trim(), out uint targetWcid) || targetWcid == 0) return "0";

        int bestId = 0;
        string bestName = string.Empty;
        double bestDist = double.MaxValue;
        foreach (var wo in _worldObjectCache.GetLandscapeObjects())
        {
            uint uid = unchecked((uint)wo.Id);
            if (!_host.TryGetObjectWcid(uid, out uint wcid) || wcid != targetWcid) continue;
            double dist = _worldObjectCache.Distance(unchecked((int)_playerId), wo.Id);
            if (dist < bestDist)
            {
                bestDist = dist;
                bestId = wo.Id;
                bestName = wo.Name;
            }
        }

        if (bestId == 0) return "0";
        uint bestUid = unchecked((uint)bestId);
        if (string.IsNullOrEmpty(bestName))
            _host.TryGetObjectName(bestUid, out bestName);
        return MakeWobjectHandle(bestUid, bestName ?? string.Empty);
    }

    private string EvalWobjectFindInInventoryByName(string name)
    {
        if (_worldObjectCache == null || string.IsNullOrEmpty(name)) return "0";

        foreach (var wo in _worldObjectCache.GetDirectInventory())
        {
            if (string.Equals(wo.Name, name, StringComparison.OrdinalIgnoreCase))
                return MakeWobjectHandle(unchecked((uint)wo.Id), wo.Name);
        }
        return "0";
    }

    private string EvalWobjectFindInInventoryByNameRx(string pattern)
    {
        if (_worldObjectCache == null || string.IsNullOrEmpty(pattern)) return "0";
        var re = RegexCache.Get(pattern, RegexOptions.IgnoreCase);
        if (re == null) return "0";

        foreach (var wo in _worldObjectCache.GetDirectInventory())
        {
            if (re.IsMatch(wo.Name))
                return MakeWobjectHandle(unchecked((uint)wo.Id), wo.Name);
        }
        return "0";
    }

    private string EvalWobjectFindAllInventoryByNameRx(string pattern)
    {
        if (_worldObjectCache == null || string.IsNullOrEmpty(pattern)) return "[]";
        var re = RegexCache.Get(pattern, RegexOptions.IgnoreCase);
        if (re == null) return "[]";

        var items = new List<string>();
        foreach (var wo in _worldObjectCache.GetDirectInventory())
        {
            if (re.IsMatch(wo.Name ?? string.Empty))
                items.Add(MakeWobjectHandle(unchecked((uint)wo.Id), wo.Name ?? string.Empty));
        }
        return NewList(items);
    }

    private string EvalWobjectFindInInventoryByTemplateType(string arg)
    {
        if (_worldObjectCache == null || !_host.HasGetObjectWcid) return "0";
        if (!uint.TryParse(arg.Trim(), out uint targetWcid) || targetWcid == 0) return "0";

        foreach (var wo in _worldObjectCache.GetDirectInventory())
        {
            uint uid = unchecked((uint)wo.Id);
            if (_host.TryGetObjectWcid(uid, out uint wcid) && wcid == targetWcid)
                return MakeWobjectHandle(uid, wo.Name);
        }
        return "0";
    }

    private string EvalWobjectFindAllInventoryByTemplateType(string arg)
    {
        if (_worldObjectCache == null || !_host.HasGetObjectWcid) return "[]";
        if (!uint.TryParse(arg.Trim(), out uint targetWcid) || targetWcid == 0) return "[]";

        var items = new List<string>();
        foreach (var wo in _worldObjectCache.GetDirectInventory())
        {
            uint uid = unchecked((uint)wo.Id);
            if (_host.TryGetObjectWcid(uid, out uint wcid) && wcid == targetWcid)
                items.Add(MakeWobjectHandle(uid, wo.Name));
        }
        return NewList(items);
    }

    private string EvalWobjectFindAllInventoryByObjectClass(string arg)
    {
        if (_worldObjectCache == null) return "[]";
        if (!int.TryParse(arg.Trim(), out int targetClass)) return "[]";

        var items = new List<string>();
        foreach (var wo in _worldObjectCache.GetDirectInventory())
        {
            if (ResolveEffectiveObjectClass(wo) == targetClass)
                items.Add(MakeWobjectHandle(unchecked((uint)wo.Id), wo.Name ?? string.Empty));
        }
        return NewList(items);
    }

    private string EvalWobjectFindAllByObjectClass(string arg)
    {
        if (_worldObjectCache == null) return "[]";
        if (!int.TryParse(arg.Trim(), out int targetClass)) return "[]";

        var items = new List<string>();
        var seen = new System.Collections.Generic.HashSet<int>();

        void Check(MetaObject wo)
        {
            if (!seen.Add(wo.Id)) return;
            if (ResolveEffectiveObjectClass(wo) != targetClass) return;
            items.Add(MakeWobjectHandle(unchecked((uint)wo.Id), wo.Name ?? string.Empty));
        }

        foreach (var wo in _worldObjectCache.GetLandscapeObjects()) Check(wo);
        foreach (var wo in _worldObjectCache.GetLandscape()) Check(wo);

        return NewList(items);
    }

    private string EvalWobjectFindAllByNameRx(string pattern)
    {
        if (_worldObjectCache == null || string.IsNullOrEmpty(pattern)) return "[]";
        var re = RegexCache.Get(pattern, RegexOptions.IgnoreCase);
        if (re == null) return "[]";

        var items = new List<string>();
        var seen = new System.Collections.Generic.HashSet<int>();

        void Check(MetaObject wo)
        {
            if (!seen.Add(wo.Id)) return;
            if (re.IsMatch(wo.Name ?? string.Empty))
                items.Add(MakeWobjectHandle(unchecked((uint)wo.Id), wo.Name ?? string.Empty));
        }

        foreach (var wo in _worldObjectCache.GetLandscapeObjects()) Check(wo);
        foreach (var wo in _worldObjectCache.GetLandscape()) Check(wo);
        foreach (var wo in _worldObjectCache.GetInventory()) Check(wo);

        return NewList(items);
    }

    private string EvalWobjectFindAllByTemplateType(string arg)
    {
        if (_worldObjectCache == null || !_host.HasGetObjectWcid) return "[]";
        if (!uint.TryParse(arg.Trim(), out uint targetWcid) || targetWcid == 0) return "[]";

        var items = new List<string>();
        var seen = new System.Collections.Generic.HashSet<int>();

        void Check(MetaObject wo)
        {
            if (!seen.Add(wo.Id)) return;
            uint uid = unchecked((uint)wo.Id);
            if (_host.TryGetObjectWcid(uid, out uint wcid) && wcid == targetWcid)
                items.Add(MakeWobjectHandle(uid, wo.Name));
        }

        foreach (var wo in _worldObjectCache.GetLandscapeObjects()) Check(wo);
        foreach (var wo in _worldObjectCache.GetLandscape()) Check(wo);
        foreach (var wo in _worldObjectCache.GetInventory()) Check(wo);

        return NewList(items);
    }

    private string EvalWobjectFindAllLandscape()
    {
        if (_worldObjectCache == null) return "[]";

        var items = new List<string>();
        var seen = new System.Collections.Generic.HashSet<int>();

        foreach (var wo in _worldObjectCache.GetLandscapeObjects())
            if (seen.Add(wo.Id))
                items.Add(MakeWobjectHandle(unchecked((uint)wo.Id), wo.Name ?? string.Empty));

        foreach (var wo in _worldObjectCache.GetLandscape())
            if (seen.Add(wo.Id))
                items.Add(MakeWobjectHandle(unchecked((uint)wo.Id), wo.Name ?? string.Empty));

        return NewList(items);
    }

    private string EvalWobjectFindAllInventory()
    {
        if (_worldObjectCache == null) return "[]";

        var items = new List<string>();
        foreach (var wo in _worldObjectCache.GetInventory())
            items.Add(MakeWobjectHandle(unchecked((uint)wo.Id), wo.Name ?? string.Empty));

        return NewList(items);
    }

    private string EvalWobjectFindAll()
    {
        if (_worldObjectCache == null) return "[]";

        var items = new List<string>();
        var seen = new System.Collections.Generic.HashSet<int>();

        foreach (var wo in _worldObjectCache.GetLandscapeObjects())
            if (seen.Add(wo.Id))
                items.Add(MakeWobjectHandle(unchecked((uint)wo.Id), wo.Name ?? string.Empty));

        foreach (var wo in _worldObjectCache.GetLandscape())
            if (seen.Add(wo.Id))
                items.Add(MakeWobjectHandle(unchecked((uint)wo.Id), wo.Name ?? string.Empty));

        foreach (var wo in _worldObjectCache.GetInventory())
            if (seen.Add(wo.Id))
                items.Add(MakeWobjectHandle(unchecked((uint)wo.Id), wo.Name ?? string.Empty));

        return NewList(items);
    }

    private string EvalWobjectFindAllLandscapeByTemplateType(string arg)
    {
        if (_worldObjectCache == null || !_host.HasGetObjectWcid) return "[]";
        if (!uint.TryParse(arg.Trim(), out uint targetWcid) || targetWcid == 0) return "[]";

        var items = new List<string>();
        foreach (var wo in _worldObjectCache.GetLandscapeObjects())
        {
            uint uid = unchecked((uint)wo.Id);
            if (_host.TryGetObjectWcid(uid, out uint wcid) && wcid == targetWcid)
                items.Add(MakeWobjectHandle(uid, wo.Name));
        }
        return NewList(items);
    }

    private string EvalWobjectFindAllLandscapeByNameRx(string pattern)
    {
        if (_worldObjectCache == null || string.IsNullOrEmpty(pattern)) return "[]";
        var re = RegexCache.Get(pattern, RegexOptions.IgnoreCase);
        if (re == null) return "[]";

        var items = new List<string>();
        foreach (var wo in _worldObjectCache.GetLandscapeObjects())
        {
            if (re.IsMatch(wo.Name ?? string.Empty))
                items.Add(MakeWobjectHandle(unchecked((uint)wo.Id), wo.Name ?? string.Empty));
        }
        return NewList(items);
    }

    private string EvalWobjectFindAllByContainer(string arg)
    {
        if (_worldObjectCache == null || !_host.HasGetContainerContents) return "[]";
        if (!TryParseWobjectHandle(arg, out uint containerId, out _) || containerId == 0) return "[]";

        uint[] buf = new uint[512];
        int count = _host.GetContainerContents(containerId, buf);

        var items = new List<string>();
        for (int i = 0; i < count; i++)
        {
            uint itemId = buf[i];
            string name = _worldObjectCache[unchecked((int)itemId)]?.Name ?? "";
            if (string.IsNullOrEmpty(name))
                _host.TryGetObjectName(itemId, out name);
            items.Add(MakeWobjectHandle(itemId, name ?? ""));
        }
        return NewList(items);
    }

    private string EvalWobjectGetName(string arg)
    {
        if (!TryParseWobjectHandle(arg, out uint uid, out string name)) return "";
        if (!string.IsNullOrEmpty(name)) return name;
        return _host.TryGetObjectName(uid, out string fetched) ? fetched : "";
    }

    private static string EvalWobjectGetId(string arg)
    {
        return TryParseWobjectHandle(arg, out uint uid, out _)
            ? uid.ToString(CultureInfo.InvariantCulture)
            : "0";
    }

    private string EvalWobjectGetTemplateType(string arg)
    {
        if (!TryParseWobjectHandle(arg, out uint uid, out _) || !_host.HasGetObjectWcid) return "0";
        return _host.TryGetObjectWcid(uid, out uint wcid) ? wcid.ToString(CultureInfo.InvariantCulture) : "0";
    }

    private string EvalWobjectGetObjClass(string arg)
    {
        if (_worldObjectCache == null || !TryParseWobjectHandle(arg, out uint uid, out _)) return "0";
        var wo = _worldObjectCache[unchecked((int)uid)];
        if (wo == null) return "0";
        return ResolveEffectiveObjectClass(wo).ToString(CultureInfo.InvariantCulture);
    }

    // PublicWeenieDesc._bitfield flags for ObjectClass resolution
    private const uint BF_PLAYER    = 0x8;
    private const uint BF_ATTACKABLE= 0x10;
    private const uint BF_VENDOR    = 0x200;
    private const uint BF_DOOR      = 0x1000;
    private const uint BF_CORPSE    = 0x2000;
    private const uint BF_LIFESTONE = 0x4000;
    private const uint BF_FOOD      = 0x8000;
    private const uint BF_HEALER    = 0x10000;
    private const uint BF_LOCKPICK  = 0x20000;
    private const uint BF_PORTAL    = 0x40000;

    /// <summary>
    /// Resolves the VTank-compatible ObjectClass for a world object at runtime.
    /// Uses PublicWeenieDesc._bitfield flags for accurate classification.
    /// </summary>
    private int ResolveEffectiveObjectClass(MetaObject wo)
    {
        int cls = (int)wo.ObjectClass;
        uint uid = unchecked((uint)wo.Id);

        if (_host.HasGetObjectBitfield && _host.TryGetObjectBitfield(uid, out uint bf))
        {
            if ((bf & BF_PLAYER) != 0)    return (int)PluginObjectClass.Player;
            if ((bf & BF_VENDOR) != 0)    return (int)PluginObjectClass.Vendor;
            if (cls == (int)PluginObjectClass.Monster)
                return (bf & BF_ATTACKABLE) != 0 ? (int)PluginObjectClass.Monster : (int)PluginObjectClass.Npc;
            if ((bf & BF_DOOR) != 0)      return (int)PluginObjectClass.Door;
            if ((bf & BF_CORPSE) != 0)    return (int)PluginObjectClass.Corpse;
            if ((bf & BF_PORTAL) != 0)    return (int)PluginObjectClass.Portal;
            if ((bf & BF_LIFESTONE) != 0) return (int)PluginObjectClass.Lifestone;
            if ((bf & BF_FOOD) != 0)      return (int)PluginObjectClass.Food;
            if ((bf & BF_HEALER) != 0)    return (int)PluginObjectClass.HealingKit;
            if ((bf & BF_LOCKPICK) != 0)  return (int)PluginObjectClass.Lockpick;
        }
        else if (cls == (int)PluginObjectClass.Monster && _host.HasObjectIsAttackable)
        {
            return _host.ObjectIsAttackable(uid) ? (int)PluginObjectClass.Monster : (int)PluginObjectClass.Npc;
        }

        return cls;
    }

    private string EvalWobjectFindNearestDoor()
    {
        if (_worldObjectCache == null || _playerId == 0) return "0";

        int bestId = 0;
        string bestName = string.Empty;
        double bestDist = double.MaxValue;
        bool hasBitfield = _host.HasGetObjectBitfield;

        foreach (var wo in _worldObjectCache.GetLandscapeObjects())
        {
            uint uid = unchecked((uint)wo.Id);

            // Doors have static GUIDs (< 0x80000000). Dynamic objects are creatures,
            // NPCs, pack items — none of them are doors.
            if (uid >= 0x80000000u) continue;

            // Primary: check PublicWeenieDesc._bitfield for BF_DOOR (0x1000).
            // Fallback: name-based detection for older engine builds without GetObjectBitfield.
            if (hasBitfield)
            {
                if (!_host.TryGetObjectBitfield(uid, out uint bf) || (bf & 0x1000u) == 0)
                    continue;
            }
            else
            {
                string nl = (wo.Name ?? string.Empty).ToLowerInvariant();
                if (!nl.Contains("door") && !nl.Contains("gate") && !nl.Contains("hatch") && !nl.Contains("portcullis"))
                    continue;
            }

            double dist = _worldObjectCache.Distance(unchecked((int)_playerId), wo.Id);
            if (dist < bestDist)
            {
                bestDist = dist;
                bestId = wo.Id;
                bestName = wo.Name ?? string.Empty;
            }
        }

        if (bestId == 0) return "0";
        return MakeWobjectHandle(unchecked((uint)bestId), bestName);
    }

    private string EvalWobjectFindNearestMonster()
    {
        if (_worldObjectCache == null || _playerId == 0) return "0";

        uint bestId = 0;
        string bestName = string.Empty;
        double bestDist = double.MaxValue;
        bool hasAttackCheck = _host.HasObjectIsAttackable;

        foreach (var wo in _worldObjectCache.GetLandscape())
        {
            uint uid = unchecked((uint)wo.Id);
            if (uid == _playerId) continue;

            // Only monsters — skip NPCs, vendors, pets
            if (hasAttackCheck && !_host.ObjectIsAttackable(uid)) continue;

            double dist = _worldObjectCache.Distance(unchecked((int)_playerId), wo.Id);
            if (dist < bestDist)
            {
                bestDist = dist;
                bestId = uid;
                bestName = wo.Name ?? string.Empty;
            }
        }

        if (bestId == 0) return "0";
        return MakeWobjectHandle(bestId, bestName);
    }

    private string EvalWobjectFindById(string arg)
    {
        if (_worldObjectCache == null) return "0";
        arg = arg.Trim();
        uint uid;
        if (arg.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (!uint.TryParse(arg.AsSpan(2), System.Globalization.NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uid))
                return "0";
        }
        else if (!uint.TryParse(arg, out uid))
        {
            return "0";
        }
        if (uid == 0) return "0";

        var wo = _worldObjectCache[unchecked((int)uid)];
        if (wo == null) return "0";

        string name = wo.Name ?? string.Empty;
        if (name.Length == 0)
            _host.TryGetObjectName(uid, out name);
        return MakeWobjectHandle(uid, name ?? string.Empty);
    }

    private string EvalWobjectFindNearestByObjectClass(string arg)
    {
        if (_worldObjectCache == null || _playerId == 0) return "0";
        if (!int.TryParse(arg.Trim(), out int targetClass)) return "0";

        uint bestId = 0;
        string bestName = string.Empty;
        double bestDist = double.MaxValue;
        int pid = unchecked((int)_playerId);
        var seen = new System.Collections.Generic.HashSet<int>();

        void Check(MetaObject wo)
        {
            if (!seen.Add(wo.Id)) return;
            uint uid = unchecked((uint)wo.Id);
            if (uid == _playerId) return;
            if (ResolveEffectiveObjectClass(wo) != targetClass) return;

            double dist = _worldObjectCache.Distance(pid, wo.Id);
            if (dist < bestDist)
            {
                bestDist = dist;
                bestId = uid;
                bestName = wo.Name ?? string.Empty;
            }
        }

        foreach (var wo in _worldObjectCache.GetLandscapeObjects())
            Check(wo);
        foreach (var wo in _worldObjectCache.GetLandscape())
            Check(wo);

        if (bestId == 0) return "0";
        return MakeWobjectHandle(bestId, bestName);
    }

    private string EvalWobjectFindNearestByNameAndObjectClass(string classArg, string pattern)
    {
        if (_worldObjectCache == null || _playerId == 0) return "0";
        if (!int.TryParse(classArg.Trim(), out int targetClass)) return "0";
        if (string.IsNullOrEmpty(pattern)) return "0";

        var re = RegexCache.Get(pattern, RegexOptions.IgnoreCase);
        if (re == null) return "0";

        uint bestId = 0;
        string bestName = string.Empty;
        double bestDist = double.MaxValue;
        int pid = unchecked((int)_playerId);
        var seen = new System.Collections.Generic.HashSet<int>();

        void Check(MetaObject wo)
        {
            if (!seen.Add(wo.Id)) return;
            uint uid = unchecked((uint)wo.Id);
            if (uid == _playerId) return;
            if (ResolveEffectiveObjectClass(wo) != targetClass) return;
            if (!re.IsMatch(wo.Name ?? string.Empty)) return;

            double dist = _worldObjectCache.Distance(pid, wo.Id);
            if (dist < bestDist)
            {
                bestDist = dist;
                bestId = uid;
                bestName = wo.Name ?? string.Empty;
            }
        }

        foreach (var wo in _worldObjectCache.GetLandscapeObjects())
            Check(wo);
        foreach (var wo in _worldObjectCache.GetLandscape())
            Check(wo);

        if (bestId == 0) return "0";
        return MakeWobjectHandle(bestId, bestName);
    }

    private string EvalWobjectGetIsDoorOpen(string objArg)
    {
        if (!TryParseWobjectHandle(objArg, out uint uid, out _) || uid == 0) return "0";
        // Read CPhysicsObj::m_state directly — no m_pQualities needed.
        // ETHEREAL_PS (0x4) is set when a door is open (passable); clear when closed.
        if (!_host.HasGetObjectState) return "0";
        if (!_host.TryGetObjectState(uid, out uint physState)) return "0";
        return (physState & 0x4u) != 0 ? "1" : "0";
    }

    /// <summary>Returns the raw CPhysicsObj::m_state bitfield as a decimal string for debugging.
    /// Use wobjectgetphysicsstate[wobjectfindnearestdoor[]] to observe state change on open/close.</summary>
    private string EvalWobjectGetPhysicsState(string objArg)
    {
        if (!TryParseWobjectHandle(objArg, out uint uid, out _) || uid == 0) return "noobj";
        if (!_host.HasGetObjectState) return "nofn";
        return _host.TryGetObjectState(uid, out uint s) ? s.ToString() : "nostate";
    }

    private string EvalWobjectGetSelection()
    {
        if (!_host.HasGetSelectedItemId) return "0";
        uint uid = _host.GetSelectedItemId();
        if (uid == 0) return "0";
        _host.TryGetObjectName(uid, out string name);
        return MakeWobjectHandle(uid, name ?? string.Empty);
    }

    private string EvalWobjectGetPlayer()
    {
        uint uid = _host.GetPlayerId();
        if (uid == 0) return "0";
        _host.TryGetObjectName(uid, out string name);
        return MakeWobjectHandle(uid, name ?? string.Empty);
    }

    private string EvalGetFreeContainerSlots(string? arg)
    {
        // Resolve the container UID: no arg → player backpack
        uint uid;
        if (string.IsNullOrEmpty(arg))
        {
            uid = _playerId;
            if (uid == 0) return "-1";
        }
        else
        {
            if (!TryParseWobjectHandle(arg, out uid, out _) || uid == 0)
                return "-1";
        }

        // STypeInt 7 = ContainersCapacity (max sub-containers)
        if (!_host.HasGetObjectIntProperty) return "-1";
        if (!_host.TryGetObjectIntProperty(uid, (uint)LongValueKey.ContainersCapacity, out int capacity) || capacity <= 0)
            return "-1";

        // Use the AC client's own GetNumContainedContainers() — authoritative count of
        // packs + foci occupying container slots (same source as Decal's ContainersContained).
        if (!_host.HasGetNumContainedContainers) return "-1";
        int containerCount = _host.GetNumContainedContainers(uid);
        if (containerCount < 0) return "-1";

        int free = capacity - containerCount;
        return free < 0 ? "0" : free.ToString(CultureInfo.InvariantCulture);
    }

    private string EvalGetContainerItemCount(string? arg)
    {
        uint uid;
        if (string.IsNullOrEmpty(arg))
        {
            uid = _playerId;
            if (uid == 0) return "-1";
        }
        else
        {
            if (!TryParseWobjectHandle(arg, out uid, out _) || uid == 0)
                return "-1";
        }

        if (!_host.HasGetNumContainedItems) return "-1";
        int count = _host.GetNumContainedItems(uid);
        return count < 0 ? "-1" : count.ToString(CultureInfo.InvariantCulture);
    }

    private string EvalWobjectGetOpenContainer()
    {
        if (!_host.HasGetGroundContainerId) return "0";
        uint uid = _host.GetGroundContainerId();
        if (uid == 0) return "0";
        _host.TryGetObjectName(uid, out string name);
        return MakeWobjectHandle(uid, name ?? string.Empty);
    }

    private string EvalGetFreeItemSlots(string? arg)
    {
        // Resolve the container UID: no arg → player backpack
        uint uid;
        if (string.IsNullOrEmpty(arg))
        {
            uid = _playerId;
            if (uid == 0) return "-1";
        }
        else
        {
            if (!TryParseWobjectHandle(arg, out uid, out _) || uid == 0)
                return "-1";
        }

        // STypeInt 6 = ItemsCapacity (max item slots)
        if (!_host.HasGetObjectIntProperty) return "-1";
        if (!_host.TryGetObjectIntProperty(uid, (uint)LongValueKey.ItemsCapacity, out int capacity) || capacity <= 0)
            return "-1";

        // Current item count from GetContainerContents
        if (!_host.HasGetContainerContents) return "-1";
        var buf = new uint[capacity + 1];
        int count = _host.GetContainerContents(uid, buf);
        if (count < 0) return "-1";

        int free = capacity - count;
        return free < 0 ? "0" : free.ToString(CultureInfo.InvariantCulture);
    }

    private string EvalWobjectGetIntProp(string objArg, string propArg)
    {
        if (!TryParseWobjectHandle(objArg, out uint uid, out _)) return "0";
        if (!uint.TryParse(propArg.Trim(), out uint prop)) return "0";

        // UB extended property 218103808 (0xD000000) = TEMPLATE_TYPE → reads WCID from PublicWeenieDesc
        if (prop == 218103808u)
        {
            if (!_host.HasGetObjectWcid) return "0";
            return _host.TryGetObjectWcid(uid, out uint wcid) ? wcid.ToString(CultureInfo.InvariantCulture) : "0";
        }

        if (!_host.HasGetObjectIntProperty) return "0";
        return _host.TryGetObjectIntProperty(uid, prop, out int value)
            ? value.ToString(CultureInfo.InvariantCulture)
            : "0";
    }

    private string EvalWobjectGetDoubleProp(string objArg, string propArg)
    {
        if (!TryParseWobjectHandle(objArg, out uint uid, out _)) return "0";
        if (!uint.TryParse(propArg.Trim(), out uint prop)) return "0";

        // UB extended double property constants use their own index, not AC STypeFloat values.
        // Map each known UB extended constant to the correct AC STypeFloat (or special path).
        uint stype = prop switch
        {
            167772169u => 280u, // 0xA000009 = WORKMANSHIP → STypeFloat 280 (ITEM_WORKMANSHIP), reads PWD+152
            _ => prop,
        };

        if (!_host.HasGetObjectDoubleProperty) return "0";
        return _host.TryGetObjectDoubleProperty(uid, stype, out double value)
            ? value.ToString("G", CultureInfo.InvariantCulture)
            : "0";
    }

    private string EvalWobjectGetBoolProp(string objArg, string propArg)
    {
        if (!TryParseWobjectHandle(objArg, out uint uid, out _)) return "0";
        if (!uint.TryParse(propArg.Trim(), out uint prop)) return "0";
        if (!_host.HasGetObjectBoolProperty) return "0";
        return _host.TryGetObjectBoolProperty(uid, prop, out bool value) ? (value ? "1" : "0") : "0";
    }

    private string EvalWobjectGetStringProp(string objArg, string propArg)
    {
        if (!TryParseWobjectHandle(objArg, out uint uid, out _)) return "";
        if (!uint.TryParse(propArg.Trim(), out uint prop)) return "";
        if (!_host.HasGetObjectStringProperty) return "";
        return _host.TryGetObjectStringProperty(uid, prop, out string? value) ? value ?? "" : "";
    }

    private string EvalWobjectHasData(string objArg)
    {
        if (!TryParseWobjectHandle(objArg, out uint uid, out _)) return "0";
        if (!_host.HasHasAppraisalData) return "0";
        return _host.HasAppraisalData(uid) ? "1" : "0";
    }

    private string EvalWobjectRequestData(string objArg)
    {
        if (!TryParseWobjectHandle(objArg, out uint uid, out _)) return "0";
        if (!_host.HasRequestId) return "0";
        _host.RequestId(uid);
        return "1";
    }

    private string EvalWobjectLastIdTime(string objArg)
    {
        if (!TryParseWobjectHandle(objArg, out uint uid, out _)) return "0";
        if (!_host.HasGetLastIdTime) return "0";
        return _host.GetLastIdTime(uid).ToString(CultureInfo.InvariantCulture);
    }

    private string EvalWobjectIsValid(string objArg)
    {
        if (!TryParseWobjectHandle(objArg, out uint uid, out _)) return "0";
        if (uid == 0) return "0";
        if (!_host.HasGetObjectName) return "0";
        return _host.TryGetObjectName(uid, out _) ? "1" : "0";
    }

    private string EvalWobjectGetHealth(string objArg)
    {
        if (_worldObjectCache == null) return "-1";
        if (!TryParseWobjectHandle(objArg, out uint uid, out _)) return "-1";
        if (uid == 0) return "-1";
        float ratio = _worldObjectCache.GetHealthRatio((int)uid);
        return ratio.ToString("G", CultureInfo.InvariantCulture);
    }

    private string EvalWobjectGetSpellIds(string objArg)
    {
        if (!TryParseWobjectHandle(objArg, out uint uid, out _) || uid == 0) return "[]";
        if (!_host.HasGetObjectSpellIds) return "[]";
        var buf = new uint[512];
        int count = _host.GetObjectSpellIds(uid, buf, buf.Length);
        if (count <= 0) return "[]";
        var strs = new List<string>(count);
        int fill = Math.Min(count, buf.Length);
        for (int i = 0; i < fill; i++)
            strs.Add(buf[i].ToString(CultureInfo.InvariantCulture));
        return NewList(strs);
    }

    private string EvalWobjectGetActiveSpellIds(string objArg)
    {
        if (!TryParseWobjectHandle(objArg, out uint uid, out _) || uid == 0) return "[]";

        // Primary path: enchantment registry (works for player and creatures).
        if (_host.HasReadObjectEnchantments)
        {
            var spellIds = new uint[128];
            var expiry = new double[128];
            int count = _host.ReadObjectEnchantments(uid, spellIds, expiry, spellIds.Length);
            if (count > 0)
            {
                var strs = new List<string>(count);
                for (int i = 0; i < count; i++)
                    strs.Add(spellIds[i].ToString(CultureInfo.InvariantCulture));
                return NewList(strs);
            }
        }

        // Fallback for items: use appraisal spell book entries with the high bit set.
        // The AC client uses bit 31 to distinguish active (player-cast) enchantments from
        // native item spells — same logic used by ItemExamineUI to render the "Enchantments:"
        // section. Strip the high bit to recover the actual spell ID.
        if (_host.HasGetObjectSpellIds)
        {
            var buf = new uint[512];
            int total = _host.GetObjectSpellIds(uid, buf, buf.Length);
            if (total > 0)
            {
                var strs = new List<string>();
                int fill = Math.Min(total, buf.Length);
                for (int i = 0; i < fill; i++)
                {
                    if ((buf[i] & 0x80000000u) != 0)
                        strs.Add((buf[i] & 0x7FFFFFFFu).ToString(CultureInfo.InvariantCulture));
                }
                if (strs.Count > 0)
                    return NewList(strs);
            }
        }

        return "[]";
    }

    private string EvalWobjectGetActiveSpellDurations(string objArg)
    {
        if (!TryParseWobjectHandle(objArg, out uint uid, out _) || uid == 0) return "[]";

        // Primary path: enchantment registry (works for player and creatures).
        if (_host.HasReadObjectEnchantments)
        {
            var spellIds = new uint[128];
            var expiry = new double[128];
            int count = _host.ReadObjectEnchantments(uid, spellIds, expiry, spellIds.Length);
            if (count > 0)
            {
                double serverTime = _host.GetServerTime();
                var strs = new List<string>(count);
                for (int i = 0; i < count; i++)
                {
                    double remaining = expiry[i] > 0 && serverTime > 0 ? expiry[i] - serverTime : -1;
                    strs.Add(remaining.ToString("G", CultureInfo.InvariantCulture));
                }
                return NewList(strs);
            }
        }

        // Fallback for items: the AC server never sends expiry times for item enchantments
        // to the client (no 0x2C2 packet is issued for item buffs — only for body buffs).
        // Return -1 per active spell so the list length matches wobjectgetactivespellids[]
        // and scripts can still count/detect spells by ID.
        if (_host.HasGetObjectSpellIds)
        {
            var buf = new uint[512];
            int total = _host.GetObjectSpellIds(uid, buf, buf.Length);
            if (total > 0)
            {
                var strs = new List<string>();
                int fill = Math.Min(total, buf.Length);
                for (int i = 0; i < fill; i++)
                {
                    if ((buf[i] & 0x80000000u) != 0)
                        strs.Add("-1");
                }
                if (strs.Count > 0)
                    return NewList(strs);
            }
        }

        return "[]";
    }

    private string EvalItemCountByName(string name)
    {
        if (_worldObjectCache == null || string.IsNullOrEmpty(name)) return "0";
        int total = 0;
        foreach (var wo in _worldObjectCache.GetDirectInventory())
            if (string.Equals(wo.Name, name, StringComparison.OrdinalIgnoreCase))
                total += Math.Max(1, wo.Values(LongValueKey.StackCount, 1));
        return total.ToString(CultureInfo.InvariantCulture);
    }

    private string EvalItemCountByNameRx(string pattern)
    {
        if (_worldObjectCache == null || string.IsNullOrEmpty(pattern)) return "0";
        var rx = RegexCache.Get(pattern, RegexOptions.IgnoreCase);
        if (rx == null) return "0";
        int total = 0;
        foreach (var wo in _worldObjectCache.GetDirectInventory())
            if (rx.IsMatch(wo.Name))
                total += Math.Max(1, wo.Values(LongValueKey.StackCount, 1));
        return total.ToString(CultureInfo.InvariantCulture);
    }

    private string EvalGetInventoryCountByTemplateType(string arg)
    {
        if (_worldObjectCache == null || !_host.HasGetObjectWcid) return "0";
        if (!uint.TryParse(arg, out uint targetWcid) || targetWcid == 0) return "0";
        int total = 0;
        foreach (var wo in _worldObjectCache.GetDirectInventory())
        {
            uint uid = (uint)wo.Id;
            if (_host.TryGetObjectWcid(uid, out uint wcid) && wcid == targetWcid)
                total += Math.Max(1, wo.Values(LongValueKey.StackCount, 1));
        }
        return total.ToString(CultureInfo.InvariantCulture);
    }

    private string EvalActionTrySelect(string arg)
    {
        if (!TryParseWobjectHandle(arg, out uint uid, out _) || uid == 0) return "0";
        if (!_host.HasSelectItem) return "0";
        _host.SelectItem(uid);
        return "0";
    }

    private string EvalActionTryUseItem(string arg)
    {
        if (!TryParseWobjectHandle(arg, out uint uid, out _) || uid == 0) return "0";
        if (!_host.HasUseObject) return "0";
        _host.UseObject(uid);
        return "0";
    }

    private string EvalActionTryApplyItem(string useArg, string onArg)
    {
        if (!TryParseWobjectHandle(useArg, out uint useId, out _) || useId == 0) return "0";
        if (!TryParseWobjectHandle(onArg, out uint onId, out _) || onId == 0) return "0";
        if (!_host.HasUseObjectOn) return "0";
        return _host.UseObjectOn(useId, onId) ? "1" : "0";
    }

    private string EvalActionTryCastByIdOnTarget(string spellArg, string targetArg)
    {
        if (!int.TryParse(spellArg.Trim(), out int spellId) || spellId == 0) return "2";
        if (!_host.HasCastSpell) return "2";
        if (!TryParseWobjectHandle(targetArg, out uint targetId, out _) || targetId == 0) return "2";


        _host.CastSpell(targetId, spellId);
        return "1";
    }

    private string EvalActionTryCastById(string arg)
    {
        if (!int.TryParse(arg.Trim(), out int spellId) || spellId == 0) return "2";
        if (!_host.HasCastSpell) return "2";


        uint targetId = (_host.HasGetSelectedItemId ? _host.GetSelectedItemId() : 0);
        if (targetId == 0) targetId = _host.GetPlayerId();
        if (targetId == 0) return "2";

        _host.CastSpell(targetId, spellId);
        return "1";
    }

    private string EvalActionTryEquipAnyWand()
    {
        if (_worldObjectCache == null || !_host.HasUseObject) return "0";

        MetaObject? unequipped = null;
        foreach (var wo in _worldObjectCache.GetDirectInventory())
        {
            if (wo.ObjectClass != PluginObjectClass.WandStaffOrb) continue;
            if (wo.Values(LongValueKey.CurrentWieldedLocation, 0) > 0)
                return "1"; // already wielded
            unequipped ??= wo;
        }

        if (unequipped != null)
            _host.UseObject(unchecked((uint)unequipped.Id));

        return "0";
    }

    private string EvalActionTryGiveItem(string giveArg, string destArg)
    {
        if (!TryParseWobjectHandle(giveArg, out uint giveId, out _) || giveId == 0) return "0";
        if (!TryParseWobjectHandle(destArg, out uint destId, out _) || destId == 0) return "0";
        if (!_host.HasMoveItemExternal) return "0";
        var wo = _worldObjectCache?[unchecked((int)giveId)];
        int amount = wo != null ? Math.Max(1, wo.Values(LongValueKey.StackCount, 1)) : 1;
        return _host.MoveItemExternal(giveId, destId, amount) ? "1" : "0";
    }

    private string EvalGetCombatState()
    {
        if (!_host.HasGetCurrentCombatMode) return "Peace";
        return _host.GetCurrentCombatMode() switch
        {
            2 => "Melee",
            4 => "Missile",
            8 => "Magic",
            _ => "Peace",
        };
    }

    private string EvalSetCombatState(string stateArg)
    {
        if (!_host.HasChangeCombatMode) return "0";
        int mode = stateArg.Trim().ToLowerInvariant() switch
        {
            "peace"   => 1,
            "melee"   => 2,
            "missile" => 4,
            "magic"   => 8,
            _ => 0,
        };
        if (mode == 0) return "0";
        return _host.ChangeCombatMode(mode) ? "1" : "0";
    }

    private string EvalGetBusyState()
    {
        if (!_host.HasGetBusyState) return "0";
        return _host.GetBusyState().ToString(CultureInfo.InvariantCulture);
    }

    private string EvalSetMotion(string motionArg, string stateArg)
    {
        if (!_host.HasSetMotion) return "0";
        if (!MotionValues.TryGetValue(motionArg, out uint motionVal)) return "0";
        bool on = ToDouble(stateArg) != 0;
        _wantedMotion[motionArg] = on;
        return _host.SetMotion(motionVal, on) ? "1" : "0";
    }

    private string EvalGetMotion(string motionArg)
    {
        if (!MotionValues.ContainsKey(motionArg)) return "0";
        bool wanted = _wantedMotion.TryGetValue(motionArg, out bool w) && w;
        // Returns 0 (inactive+unwanted), 1 (wanted+inactive), matching UB convention.
        // Active-but-unwanted (-1) and wanted+active (2) require client state polling not available.
        return wanted ? "1" : "0";
    }

    private string EvalClearMotion()
    {
        if (_host.HasSetMotion)
        {
            foreach (var kvp in MotionValues)
                _host.SetMotion(kvp.Value, false);
        }
        _wantedMotion.Clear();
        return "1";
    }

    // ── Quest flag expressions ───────────────────────────────────────────────

    private string EvalTestQuestFlag(string key)
        => (_questTracker != null && _questTracker.HasFlag(key)) ? "1" : "0";

    private string EvalGetQuestStatus(string key)
    {
        if (_questTracker == null)
            return "1"; // no data — assume ready (matches UB behaviour)
        if (!_questTracker.TryGetFlag(key, out QuestRecord? rec))
            return "1"; // not in list → ready
        return rec.IsReady() ? "1" : "0";
    }

    private string EvalGetQuestKtProgress(string key)
    {
        if (_questTracker == null || !_questTracker.TryGetFlag(key, out QuestRecord? rec))
            return "0";
        return rec.Solves.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private string EvalGetQuestKtRequired(string key)
    {
        if (_questTracker == null || !_questTracker.TryGetFlag(key, out QuestRecord? rec))
            return "0";
        return rec.MaxSolves.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private string EvalRefreshQuests()
    {
        _questTracker?.Refresh();
        return "1";
    }

    private string EvalComponentData(string idArg)
    {
        uint id = (uint)ToDouble(idArg);
        if (!_host.Surface.Spells.TryGetComponent(id, out PluginSpellComponentInfo component))
            return NewDict();

        var d = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Id"]           = component.ComponentId.ToString(CultureInfo.InvariantCulture),
            ["Name"]         = component.Name,
            ["Wcid"]         = component.WeenieClassId.ToString(CultureInfo.InvariantCulture),
            ["GestureId"]    = component.GestureId.ToString(CultureInfo.InvariantCulture),
            ["GestureSpeed"] = component.GestureSpeed.ToString("R", CultureInfo.InvariantCulture),
            ["BurnRate"]     = component.BurnRate.ToString("R", CultureInfo.InvariantCulture),
        };
        return NewDict(d);
    }

    private string EvalGetEquippedWeaponType()
    {
        if (_worldObjectCache == null) return "None";
        foreach (var item in _worldObjectCache.GetDirectInventory())
        {
            if (item.WieldedLocation <= 0) continue;
            switch (item.ObjectClass)
            {
                case PluginObjectClass.MeleeWeapon:   return "Melee";
                case PluginObjectClass.MissileWeapon: return "Missile";
                case PluginObjectClass.WandStaffOrb:  return "Wand";
            }
        }
        return "None";
    }

    private string EvalGetHeading(string objArg)
    {
        if (!TryParseWobjectHandle(objArg, out uint uid, out _)) return "0";
        if (!_host.HasGetObjectHeading) return "0";
        if (!_host.TryGetObjectHeading(uid, out float heading)) return "0";
        return heading.ToString("G", CultureInfo.InvariantCulture);
    }

    private string EvalGetHeadingTo(string objArg)
    {
        if (!TryParseWobjectHandle(objArg, out uint uid, out _)) return "0";
        if (!_host.HasGetPlayerPose) return "0";
        if (!_host.TryGetPlayerPose(out _, out float px, out float py, out _, out _, out _, out _, out _)) return "0";
        if (!_host.HasGetObjectPosition) return "0";
        if (!_host.TryGetObjectPosition(uid, out _, out float tx, out float ty, out _)) return "0";

        double deg = Math.Atan2(tx - px, ty - py) * (180.0 / Math.PI);
        if (deg < 0) deg += 360.0;
        return deg.ToString("G", CultureInfo.InvariantCulture);
    }

    // ── Chat implementations ──────────────────────────────────────────────────

    private string EvalChatbox(string message)
    {
        if (string.IsNullOrEmpty(message) || !_host.HasInvokeChatParser) return message;
        _host.InvokeChatParser(message);
        return message;
    }

    private string EvalEcho(string message, string colorArg)
    {
        if (!_host.HasWriteToChat) return "0";
        int chatType = (int)ToDouble(colorArg);
        return _host.WriteToChat(message, chatType) ? "1" : "0";
    }

    // ── Salvage (UST) implementations ────────────────────────────────────────

    private string EvalUstOpen()
    {
        if (_worldObjectCache == null || !_host.HasUseObject) return "0";
        foreach (var item in _worldObjectCache.GetDirectInventory())
        {
            if (string.Equals(item.Name, "Ust", StringComparison.OrdinalIgnoreCase))
            {
                _host.UseObject((uint)item.Id);
                return "1";
            }
        }
        return "0";
    }

    private string EvalUstAdd(string objArg)
    {
        if (!_host.HasSalvagePanel) return "0";
        uint uid;
        // Accept either a wobject handle ("[MetaObject] 0x...") or a plain numeric ID
        if (!TryParseWobjectHandle(objArg, out uid, out _))
        {
            if (!uint.TryParse(objArg.Trim(), out uid) || uid == 0) return "0";
        }
        _host.SalvagePanelAddItem(uid);
        return "1";
    }

    private string EvalUstSalvage()
    {
        if (!_host.HasSalvagePanel) return "0";
        _host.SalvagePanelExecute();
        return "1";
    }

    // ── Game time (Dereth) implementations ───────────────────────────────────

    // AC game clock: raw double at 0x008379A8 in game ticks.
    // CurrentTime = rawTicks - 210 + (476.25 * 8) + (476.25 * 16 * 30 * 12 * 10)
    // Matches UB DerethTime.cs exactly.
    private const double TicksInHour  = 476.25;
    private const int    HoursInDay   = 16;
    private const int    DaysInMonth  = 30;
    private const int    MonthsInYear = 12;
    private static readonly int    TicksInDay   = (int)(TicksInHour * HoursInDay);
    private static readonly int    TicksInMonth = (int)(TicksInHour * HoursInDay * DaysInMonth);
    private static readonly int    TicksInYear  = (int)(TicksInHour * HoursInDay * DaysInMonth * MonthsInYear);

    private static readonly string[] MonthNames =
    {
        "Morningthaw","Solclaim","Seedsow","Leafdawning","Verdantine","Thistledown",
        "HarvestGain","Leafcull","Frostfell","Snowreap","Coldeve","Wintersebb"
    };

    private static readonly string[] HourNames =
    {
        "Darktide","Darktide-and-Half","Foredawn","Foredawn-and-Half",
        "Dawnsong","Dawnsong-and-Half","Morntide","Morntide-and-Half",
        "Midsong","Midsong-and-Half","Warmtide","Warmtide-and-Half",
        "Evensong","Evensong-and-Half","Gloaming","Gloaming-and-Half"
    };

    private bool _loggedGameClockNotInWorld;

    /// <summary>
    /// Reads the raw double at 0x008379A8 — the AC game clock register. That address
    /// is only mapped once the client is in world, so any getgame* expression
    /// evaluated before world entry used to fault (2026-06-03 audit P3). An
    /// AccessViolationException is a corrupted-state exception and is NOT catchable
    /// in .NET, so this guards instead of catching: no player id means no world,
    /// which means the register must not be touched.
    /// </summary>
    private double ReadGameClock()
    {
        if (!_host.HasGetPlayerId || _host.GetPlayerId() == 0)
        {
            if (!_loggedGameClockNotInWorld)
            {
                _loggedGameClockNotInWorld = true;
                _host.Log("[Meta] getgame* evaluated before world entry — game clock unreadable, returning 0 (not faulting).");
            }
            return 0;
        }

        long raw = System.Runtime.InteropServices.Marshal.ReadInt64(new IntPtr(unchecked((int)0x008379A8)));
        return BitConverter.Int64BitsToDouble(raw);
    }

    private double GetGameTicks()
    {
        double rawTicks = ReadGameClock();
        return rawTicks - 210 + (TicksInHour * 8) + (TicksInHour * HoursInDay * DaysInMonth * MonthsInYear * 10);
    }

    private int GetGameYear()   => (int)(GetGameTicks() / TicksInYear);
    private int GetGameMonth()  => (int)(GetGameTicks() % TicksInYear  / TicksInMonth);
    private int GetGameDay()    => (int)(GetGameTicks() % TicksInMonth / TicksInDay);
    private int GetGameHour()   => (int)(GetGameTicks() % TicksInDay   / TicksInHour);
    private bool GetIsDay()     { int h = GetGameHour(); return h >= 4 && h < 12; }

    // Lookup by index (0-based), matches UB: getgamemonthname[0] = "Morningthaw", getgamemonthname[1] = "Solclaim"
    private static string GetGameMonthName(string arg)
    {
        if (!int.TryParse(arg.Trim(), out int n)) return "";
        return n >= 0 && n < MonthNames.Length ? MonthNames[n] : "";
    }

    private static string GetGameHourName(string arg)
    {
        if (!int.TryParse(arg.Trim(), out int n)) return "";
        return n >= 0 && n < HourNames.Length ? HourNames[n] : "";
    }

    private int GetMinutesUntilDay()
    {
        if (GetIsDay()) return 0;
        // Ticks remaining until hour 4 (dawn)
        double ticksIntoDay = GetGameTicks() % TicksInDay;
        double dawnTick     = 4 * TicksInHour;
        double ticksUntil   = ticksIntoDay < dawnTick
            ? dawnTick - ticksIntoDay
            : TicksInDay - ticksIntoDay + dawnTick;
        return (int)(ticksUntil / 60);
    }

    private int GetMinutesUntilNight()
    {
        if (!GetIsDay()) return 0;
        // Ticks remaining until hour 12 (dusk)
        double ticksIntoDay = GetGameTicks() % TicksInDay;
        double duskTick     = 12 * TicksInHour;
        double ticksUntil   = duskTick - ticksIntoDay;
        return (int)(ticksUntil / 60);
    }

    // ── Fellowship implementations ────────────────────────────────────────────

    private string EvalFellowshipIsFull()
    {
        if (_fellowshipTracker == null || !_fellowshipTracker.IsInFellowship) return "0";
        return _fellowshipTracker.MemberCount >= 9 ? "1" : "0";
    }

    private string EvalFellowshipCanRecruit()
    {
        if (_fellowshipTracker == null || !_fellowshipTracker.IsInFellowship) return "0";
        if (_fellowshipTracker.MemberCount >= 9) return "0";
        return (_fellowshipTracker.IsLeader || _fellowshipTracker.IsOpen) ? "1" : "0";
    }

    // VTank getfellowshipstatus[] — the migration keystone (~149 metas use it).
    // Contract reverse-engineered from real meta usage across the VTank library
    // (512 calls: only ==0 / ==1 / !=0; AutoFellow gates `/ub fellow recruit`
    // — a leader-only action — on ==1):
    //   0 = not in a fellowship
    //   1 = in a fellowship and you are the leader
    //   2 = in a fellowship and you are NOT the leader
    private string EvalGetFellowshipStatus()
    {
        if (_fellowshipTracker == null || !_fellowshipTracker.IsInFellowship) return "0";
        return _fellowshipTracker.IsLeader ? "1" : "2";
    }

    private string EvalGetFellowId(string arg)
    {
        if (_fellowshipTracker == null) return "0";
        if (!int.TryParse(arg.Trim(), out int idx)) return "0";
        int id = _fellowshipTracker.GetMemberId(idx);
        return id.ToString(CultureInfo.InvariantCulture);
    }

    private string EvalGetFellowName(string arg)
    {
        if (_fellowshipTracker == null) return "";
        if (!int.TryParse(arg.Trim(), out int idx)) return "";
        return _fellowshipTracker.GetMemberName(idx);
    }

    private string EvalGetFellowNames()
    {
        if (_fellowshipTracker == null) return "[]";
        var names = new List<string>(_fellowshipTracker.GetMemberNames());
        names.Sort(StringComparer.OrdinalIgnoreCase);
        return NewList(names);
    }

    private string EvalGetFellowIds()
    {
        if (_fellowshipTracker == null) return "[]";
        int count = _fellowshipTracker.MemberCount;
        var ids = new List<int>(count);
        for (int i = 0; i < count; i++)
        {
            int id = _fellowshipTracker.GetMemberId(i);
            if (id != 0) ids.Add(id);
        }
        ids.Sort();
        var strs = new List<string>(ids.Count);
        foreach (int id in ids) strs.Add(id.ToString(CultureInfo.InvariantCulture));
        return NewList(strs);
    }

    // ── Option function implementations ───────────────────────────────────────

    private string EvalOptSet(string name, string value)
    {
        if (string.IsNullOrEmpty(name)) return "0";
        _options[name] = value;
        return "1";
    }

    // ── RynthAi settings / meta state implementations ─────────────────────────

    private string EvalVtSetMetaState(string state)
    {
        if (_settings == null || string.IsNullOrEmpty(state)) return "0";
        _settings.CurrentState = state;
        _settings.ForceStateReset = true;
        return "1";
    }

    private string EvalVtGetSetting(string name)
    {
        if (_settings == null || string.IsNullOrEmpty(name)) return "";
        var map = BuildSettingsMap();
        if (map.TryGetValue(name, out var entry)) return entry.Get();
        return _options.TryGetValue(name, out string? v) ? v : "";
    }

    private string EvalVtSetSetting(string name, string value)
    {
        if (_settings == null || string.IsNullOrEmpty(name)) return "0";
        var map = BuildSettingsMap();
        if (map.TryGetValue(name, out var entry)) { entry.Set(value); return "1"; }
        _options[name] = value;
        return "1";
    }

    public Dictionary<string, (Func<string> Get, Action<string> Set)> BuildSettingsMapPublic()
        => BuildSettingsMap();

    private Dictionary<string, (Func<string> Get, Action<string> Set)> BuildSettingsMap()
        => _settingsMap ??= _settings?.BuildMap() ?? new(StringComparer.OrdinalIgnoreCase);

    // ── Dynamic evaluation implementations ───────────────────────────────────

    private string EvalDelayExec(string delayArg, string exprArg)
    {
        if (!double.TryParse(delayArg, NumberStyles.Float | NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out double delayMs))
            delayMs = 0;
        long ms = (long)Math.Max(0, delayMs);
        _delayedExecs.Add((Environment.TickCount64 + ms, exprArg));
        return "1";
    }

    /// <summary>
    /// Fires delayexec entries whose delay has elapsed. MUST be called on the
    /// game/plugin-tick thread (it Evaluates expressions that touch AC native
    /// state). Resolution is the tick rate (~30 Hz), fine for meta timing. A
    /// delayexec re-scheduled by a fired expression runs no earlier than the
    /// next pump (snapshot-then-fire, so it can't loop within one pump).
    /// </summary>
    public void PumpDelayedExecs()
    {
        if (_delayedExecs.Count == 0) return;
        long now = Environment.TickCount64;
        List<string>? due = null;
        for (int i = _delayedExecs.Count - 1; i >= 0; i--)
        {
            if (_delayedExecs[i].DueMs <= now)
            {
                (due ??= new()).Add(_delayedExecs[i].Expr);
                _delayedExecs.RemoveAt(i);
            }
        }
        if (due == null) return;
        // Collected back-to-front; fire in original scheduled order.
        for (int i = due.Count - 1; i >= 0; i--)
        {
            try { Evaluate(due[i]); } catch { }
        }
    }

    // ── String function implementations ───────────────────────────────────────

    private static string EvalGetRegexMatch(string text, string pattern)
    {
        if (pattern.Length == 0) return "";
        try
        {
            var m = Regex.Match(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return m.Success ? m.Value : "";
        }
        catch { return ""; }
    }

    // ── Dict helpers ──────────────────────────────────────────────────────────

    private string NewDict(Dictionary<string, string>? entries = null)
    {
        string handle = "D:" + _nextDictId++;
        _dicts[handle] = entries != null
            ? new Dictionary<string, string>(entries, StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal);
        return handle;
    }

    private Dictionary<string, string>? GetDict(string handle)
        => _dicts.TryGetValue(handle, out var d) ? d : null;

    public bool TryGetDict(string handle, out Dictionary<string, string>? dict)
        => _dicts.TryGetValue(handle, out dict);

    // ── Dict function implementations ─────────────────────────────────────────

    private string EvalDictCreate(List<string> rawArgs)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        // rawArgs are already the un-split arg strings from the outer SplitArgs call.
        // Evaluate them eagerly in pairs: key, value, key, value, …
        for (int i = 0; i + 1 < rawArgs.Count; i += 2)
        {
            string key = Evaluate(rawArgs[i]);
            string val = Evaluate(rawArgs[i + 1]);
            dict[key] = val;
        }
        return NewDict(dict);
    }

    private string EvalDictGetItem(string handle, string key)
    {
        var dict = GetDict(handle);
        if (dict == null) return "";
        return dict.TryGetValue(key, out string? v) ? v : "";
    }

    private string EvalDictAddItem(string handle, string key, string value)
    {
        var dict = GetDict(handle);
        if (dict == null) return "0";
        bool overwrote = dict.ContainsKey(key);
        dict[key] = value;
        return overwrote ? "1" : "0";
    }

    private string EvalDictHasKey(string handle, string key)
    {
        var dict = GetDict(handle);
        return dict != null && dict.ContainsKey(key) ? "1" : "0";
    }

    private string EvalDictRemoveKey(string handle, string key)
    {
        var dict = GetDict(handle);
        if (dict == null) return "0";
        return dict.Remove(key) ? "1" : "0";
    }

    private string EvalDictKeys(string handle)
    {
        var dict = GetDict(handle);
        return dict != null ? NewList(dict.Keys) : NewList();
    }

    private string EvalDictValues(string handle)
    {
        var dict = GetDict(handle);
        return dict != null ? NewList(dict.Values) : NewList();
    }

    private string EvalDictSize(string handle)
    {
        var dict = GetDict(handle);
        return dict != null ? Fmt((long)dict.Count) : "0";
    }

    private string EvalDictClear(string handle)
    {
        GetDict(handle)?.Clear();
        return handle;
    }

    private string EvalDictCopy(string handle)
    {
        var dict = GetDict(handle);
        return dict != null ? NewDict(dict) : NewDict();
    }

    // ── Coercions ─────────────────────────────────────────────────────────────

    /// <summary>"0" and "" are false; everything else is true.</summary>
    internal static bool ToBool(string s)
        => s.Length > 0 && s != "0"
           && !string.Equals(s, "false", StringComparison.OrdinalIgnoreCase)
           // An evaluation error / depth-guard sentinel is never "true" — keeps
           // if[]/iif[]/&&/|| on a broken sub-expression failing closed too.
           && !s.StartsWith("ERR:", StringComparison.Ordinal);

    private static double ToDouble(string s)
        => double.TryParse(s, NumberStyles.Float | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out double d) ? d : 0.0;

    private static long ToLong(string s)
    {
        if (long.TryParse(s, NumberStyles.Integer | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long l)) return l;
        return (long)ToDouble(s);
    }

    private static string Fmt(double d) => d.ToString("G", CultureInfo.InvariantCulture);
    private static string Fmt(long l)   => l.ToString(CultureInfo.InvariantCulture);

    /// <summary>Equality: numeric if both sides parse as numbers, otherwise case-INSENSITIVE
    /// string (M1). Case-insensitivity matches author intent ($x==True is true for 'true') and
    /// the conventions of the metas we import; numeric compares are unaffected.</summary>
    private static bool AreEqual(string a, string b)
    {
        if (double.TryParse(a, NumberStyles.Float | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out double da)
         && double.TryParse(b, NumberStyles.Float | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out double db))
            return da == db;
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    // ── Argument splitter ─────────────────────────────────────────────────────

    /// <summary>Splits a comma-separated argument list, respecting nested bracket depth and backtick strings.</summary>
    private static List<string> SplitArgs(string s)
    {
        var list = new List<string>();
        int depth = 0, start = 0;
        bool inBacktick = false;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '`') { inBacktick = !inBacktick; }
            else if (!inBacktick)
            {
                if (c == '[') depth++;
                else if (c == ']') depth--;
                else if (c == ',' && depth == 0)
                {
                    list.Add(s.Substring(start, i - start).Trim());
                    start = i + 1;
                }
            }
        }
        list.Add(s.Substring(start).Trim());
        return list;
    }

    // ── Infix expression parser ───────────────────────────────────────────────

    /// <summary>
    /// Recursive-descent infix parser.
    ///
    /// Operator precedence (lowest → highest):
    ///   ||  →  &amp;&amp;  →  |  →  ^  →  &amp;  →  ==  →  &lt; &lt;= > >=  →  &lt;&lt; >>  →  + -  →  * / %  →  #  →  unary ~  →  primary
    /// </summary>
    private sealed class Parser
    {
        private readonly ExpressionEngine _eng;
        private readonly string _s;
        private int _p;

        internal Parser(ExpressionEngine eng, string s) { _eng = eng; _s = s; }

        internal string Run() => ParseOr();

        // ── Whitespace ────────────────────────────────────────────────────────

        private void Ws()
        {
            while (_p < _s.Length && (_s[_p] == ' ' || _s[_p] == '\t' || _s[_p] == '\r' || _s[_p] == '\n'))
                _p++;
        }

        // ── Token consumer ────────────────────────────────────────────────────

        /// <summary>
        /// Tries to consume <paramref name="tok"/> at the current position.
        /// Disambiguates single-char tokens against longer operators (e.g., &amp; vs &amp;&amp;).
        /// Returns true and advances on success; leaves position unchanged on failure.
        /// </summary>
        private bool Try(string tok)
        {
            Ws();
            if (_p + tok.Length > _s.Length) return false;
            for (int i = 0; i < tok.Length; i++)
                if (_s[_p + i] != tok[i]) return false;

            // Single-char disambiguation: don't match if a longer operator follows.
            if (tok.Length == 1)
            {
                char nc = _p + 1 < _s.Length ? _s[_p + 1] : '\0';
                switch (tok[0])
                {
                    case '&': if (nc == '&') return false; break;
                    case '|': if (nc == '|') return false; break;
                    case '<': if (nc == '<' || nc == '=') return false; break;
                    case '>': if (nc == '>' || nc == '=') return false; break;
                }
            }

            _p += tok.Length;
            return true;
        }

        // ── Precedence levels ─────────────────────────────────────────────────

        // Priority 1 (lowest): ||
        private string ParseOr()
        {
            var l = ParseAnd();
            Ws();
            while (Try("||")) { var r = ParseAnd(); l = ToBool(l) || ToBool(r) ? "1" : "0"; Ws(); }
            return l;
        }

        // Priority 2: &&
        private string ParseAnd()
        {
            var l = ParseBitOr();
            Ws();
            while (Try("&&")) { var r = ParseBitOr(); l = ToBool(l) && ToBool(r) ? "1" : "0"; Ws(); }
            return l;
        }

        // Priority 3: | (bitwise OR)
        private string ParseBitOr()
        {
            var l = ParseBitXor();
            Ws();
            while (Try("|")) { var r = ParseBitXor(); l = Fmt(ToLong(l) | ToLong(r)); Ws(); }
            return l;
        }

        // Priority 4: ^ (bitwise XOR)
        private string ParseBitXor()
        {
            var l = ParseBitAnd();
            Ws();
            while (Try("^")) { var r = ParseBitAnd(); l = Fmt(ToLong(l) ^ ToLong(r)); Ws(); }
            return l;
        }

        // Priority 5: & (bitwise AND)
        private string ParseBitAnd()
        {
            var l = ParseEquality();
            Ws();
            while (Try("&")) { var r = ParseEquality(); l = Fmt(ToLong(l) & ToLong(r)); Ws(); }
            return l;
        }

        // Priority 6: ==  != (M1: != was previously unsupported and silently dropped, failing OPEN)
        private string ParseEquality()
        {
            var l = ParseComparison();
            Ws();
            while (true)
            {
                if (Try("=="))      { var r = ParseComparison(); l =  AreEqual(l, r) ? "1" : "0"; }
                else if (Try("!=")) { var r = ParseComparison(); l = !AreEqual(l, r) ? "1" : "0"; }
                else break;
                Ws();
            }
            return l;
        }

        // Priority 7: < <= > >=  (multi-char tried first to beat single-char)
        private string ParseComparison()
        {
            var l = ParseShift();
            Ws();
            while (true)
            {
                string op;
                if      (Try("<=")) op = "<=";
                else if (Try(">=")) op = ">=";
                else if (Try("<"))  op = "<";
                else if (Try(">"))  op = ">";
                else break;
                var r = ParseShift();
                l = op switch
                {
                    "<=" => ToDouble(l) <= ToDouble(r) ? "1" : "0",
                    ">=" => ToDouble(l) >= ToDouble(r) ? "1" : "0",
                    "<"  => ToDouble(l) <  ToDouble(r) ? "1" : "0",
                    _    => ToDouble(l) >  ToDouble(r) ? "1" : "0",
                };
                Ws();
            }
            return l;
        }

        // Priority 8: << >>
        private string ParseShift()
        {
            var l = ParseAddSub();
            Ws();
            while (true)
            {
                if      (Try("<<")) { var r = ParseAddSub(); l = Fmt(ToLong(l) << (int)ToLong(r)); }
                else if (Try(">>")) { var r = ParseAddSub(); l = Fmt(ToLong(l) >> (int)ToLong(r)); }
                else break;
                Ws();
            }
            return l;
        }

        // Priority 9: + -   (+ also works on strings)
        private string ParseAddSub()
        {
            var l = ParseMulDiv();
            Ws();
            while (true)
            {
                if (Try("+"))
                {
                    var r = ParseMulDiv();
                    // Numeric add if both sides parse as numbers; string concat otherwise.
                    if (double.TryParse(l, NumberStyles.Float | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out double dl)
                     && double.TryParse(r, NumberStyles.Float | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out double dr))
                        l = Fmt(dl + dr);
                    else
                        l = l + r;
                }
                else if (Try("-")) { var r = ParseMulDiv(); l = Fmt(ToDouble(l) - ToDouble(r)); }
                else break;
                Ws();
            }
            return l;
        }

        // Priority 10: * / %
        private string ParseMulDiv()
        {
            var l = ParseRegex();
            Ws();
            while (true)
            {
                if (Try("*")) { var r = ParseRegex(); l = Fmt(ToDouble(l) * ToDouble(r)); }
                else if (Try("/")) { var r = ParseRegex(); double b = ToDouble(r); l = b != 0 ? Fmt(ToDouble(l) / b) : "0"; }
                else if (Try("%")) { var r = ParseRegex(); double b = ToDouble(r); l = b != 0 ? Fmt(ToDouble(l) % b) : "0"; }
                else break;
                Ws();
            }
            return l;
        }

        // Priority 11: # (regex match — left#pattern, case-insensitive)
        private string ParseRegex()
        {
            var l = ParseUnary();
            Ws();
            while (Try("#"))
            {
                var pattern = ParseUnary();
                l = RegexCache.IsMatch(l, pattern, RegexOptions.IgnoreCase) ? "1" : "0";
                Ws();
            }
            return l;
        }

        // Priority 12: unary ~  (also unary - on non-digit expressions)
        private string ParseUnary()
        {
            Ws();
            if (_p < _s.Length && _s[_p] == '~') { _p++; return Fmt(~ToLong(ParseUnary())); }
            // Unary minus on a non-digit (digit case is handled inside ParseAtom as a negative literal)
            if (_p < _s.Length && _s[_p] == '-' && (_p + 1 >= _s.Length || !char.IsDigit(_s[_p + 1])))
            {
                _p++;
                return Fmt(-ToDouble(ParseUnary()));
            }
            return ParsePrimary();
        }

        // Priority 13 (highest): parenthesised group or atom
        private string ParsePrimary()
        {
            Ws();
            if (_p < _s.Length && _s[_p] == '(')
            {
                if (_eng._parenDepth >= MaxParenDepth) { _p = _s.Length; return "ERR:Depth:paren nesting"; }
                _eng._parenDepth++;
                _p++; // consume '('
                var inner = ParseOr();
                Ws();
                if (_p < _s.Length && _s[_p] == ')') _p++;
                _eng._parenDepth--;
                return inner;
            }
            return ParseAtom();
        }

        /// <summary>
        /// Reads one atom: a function call <c>name[...]</c>, a <c>$variable</c> reference,
        /// a number literal (decimal or 0x hex), or a plain identifier.
        /// </summary>
        private string ParseAtom()
        {
            Ws();
            if (_p >= _s.Length) return "";

            // Backtick string literal: `content` → content (verbatim, no evaluation)
            // \` inside the string is an escaped backtick (needed for nested expressions).
            if (_s[_p] == '`')
            {
                _p++; // consume opening backtick
                var sb = new System.Text.StringBuilder();
                while (_p < _s.Length && _s[_p] != '`')
                {
                    if (_s[_p] == '\\' && _p + 1 < _s.Length && _s[_p + 1] == '`')
                    {
                        sb.Append('`');
                        _p += 2;
                    }
                    else
                    {
                        sb.Append(_s[_p++]);
                    }
                }
                if (_p < _s.Length) _p++; // consume closing backtick
                return sb.ToString();
            }

            // $name — shorthand for getvar[name]
            if (_s[_p] == '$')
            {
                _p++;
                int vs = _p;
                while (_p < _s.Length && (char.IsLetterOrDigit(_s[_p]) || _s[_p] == '_'))
                    _p++;
                string varName = _s.Substring(vs, _p - vs);
                return _eng._variables.TryGetValue(varName, out string? vv) ? vv ?? "" : "";
            }

            // Hex literal: 0x / 0X (positive or negative)
            bool negHex = _s[_p] == '-' && _p + 2 < _s.Length && _s[_p + 1] == '0' && (_s[_p + 2] == 'x' || _s[_p + 2] == 'X');
            bool posHex = _s[_p] == '0' && _p + 1 < _s.Length && (_s[_p + 1] == 'x' || _s[_p + 1] == 'X');
            if (negHex || posHex)
            {
                if (negHex) _p++;   // skip leading '-'
                _p += 2;            // skip '0x'
                int hs = _p;
                while (_p < _s.Length && (char.IsDigit(_s[_p]) ||
                       (_s[_p] >= 'a' && _s[_p] <= 'f') || (_s[_p] >= 'A' && _s[_p] <= 'F')))
                    _p++;
                if (long.TryParse(_s.Substring(hs, _p - hs), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long hv))
                    return Fmt(negHex ? -hv : hv);
                return "0";
            }

            // Negative decimal literal: '-' immediately followed by a digit.
            // (Unary minus on expressions is handled by ParseUnary above.)
            if (_s[_p] == '-' && _p + 1 < _s.Length && char.IsDigit(_s[_p + 1]))
            {
                int ns = _p++;
                while (_p < _s.Length && (char.IsDigit(_s[_p]) || _s[_p] == '.')) _p++;
                return _s.Substring(ns, _p - ns);
            }

            int start = _p;

            // Identifier (letters, digits, underscores, dots for decimal numbers)
            while (_p < _s.Length && (char.IsLetterOrDigit(_s[_p]) || _s[_p] == '_' || _s[_p] == '.'))
                _p++;

            if (_p < _s.Length && _s[_p] == '[')
            {
                // Function call: consume '[' and scan to its matching ']'
                _p++;
                int depth = 1;
                while (_p < _s.Length && depth > 0)
                {
                    if      (_s[_p] == '[') depth++;
                    else if (_s[_p] == ']') depth--;
                    _p++;
                }
                // Pass the entire "funcname[args]" string to EvaluatePrimary
                return _eng.EvaluatePrimary(_s.Substring(start, _p - start));
            }

            if (_p > start)
                return _eng.EvaluatePrimary(_s.Substring(start, _p - start));

            // Skip unrecognised characters (guards against infinite loops)
            _p++;
            return "";
        }
    }
}
