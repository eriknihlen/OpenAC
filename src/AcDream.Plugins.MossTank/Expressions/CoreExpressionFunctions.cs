using System.Globalization;
using System.Text.RegularExpressions;

namespace AcDream.Plugins.MossTank.Expressions;

internal static class CoreExpressionFunctions
{
    private static readonly Regex CoordinatePattern = new(
        @"^\s*(?<ns>[-+]?\d+(?:\.\d+)?)\s*(?<nsdir>[NS])\s*,\s*"
        + @"(?<ew>[-+]?\d+(?:\.\d+)?)\s*(?<ewdir>[EW])"
        + @"(?:\s*,\s*(?<z>[-+]?\d+(?:\.\d+)?))?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    public static ExpressionFunctionRegistry CreateDefault(Random? random = null)
    {
        var registry = new ExpressionFunctionRegistry();
        Register(registry, random);
        return registry;
    }

    public static void Register(
        ExpressionFunctionRegistry registry,
        Random? random = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        RegisterVariables(registry, ExpressionVariableScope.Session, string.Empty);
        RegisterVariables(registry, ExpressionVariableScope.Persistent, "p");
        RegisterVariables(registry, ExpressionVariableScope.Global, "g");
        RegisterConversionsAndMath(registry, random ?? Random.Shared);
        RegisterLists(registry);
        RegisterDictionaries(registry);
        RegisterCoordinates(registry);
        RegisterTime(registry);
    }

    private static void RegisterVariables(
        ExpressionFunctionRegistry registry,
        ExpressionVariableScope scope,
        string infix)
    {
        string get = "get" + infix + "var";
        string set = "set" + infix + "var";
        string test = "test" + infix + "var";
        string touch = "touch" + infix + "var";
        string clear = "clear" + infix + "var";
        string clearAll = "clearall" + infix + "vars";

        registry.Register(get, 1, 1, (context, args) =>
            context.State.Get(scope, args[0].AsString(get)), $"{get}[name]");
        registry.Register(set, 2, 2, (context, args) =>
            context.State.Set(scope, args[0].AsString(set), args[1]),
            $"{set}[name,value]");
        registry.Register(test, 1, 1, (context, args) =>
            ExpressionValue.Boolean(context.State.Contains(
                scope,
                args[0].AsString(test))), $"{test}[name]");
        registry.Register(touch, 1, 1, (context, args) =>
        {
            string name = args[0].AsString(touch);
            bool existed = context.State.Contains(scope, name);
            if (!existed)
                context.State.Set(scope, name, ExpressionValue.Zero);
            return ExpressionValue.Boolean(existed);
        }, $"{touch}[name]");
        registry.Register(clear, 1, 1, (context, args) =>
            ExpressionValue.Boolean(context.State.Clear(
                scope,
                args[0].AsString(clear))), $"{clear}[name]");
        registry.Register(clearAll, 0, 0, (context, _) =>
        {
            context.State.Clear(scope);
            return ExpressionValue.One;
        }, $"{clearAll}[]");
    }

    private static void RegisterConversionsAndMath(
        ExpressionFunctionRegistry registry,
        Random random)
    {
        RegisterUnaryMath(registry, "abs", Math.Abs);
        RegisterUnaryMath(registry, "acos", Math.Acos);
        RegisterUnaryMath(registry, "asin", Math.Asin);
        RegisterUnaryMath(registry, "atan", Math.Atan);
        RegisterUnaryMath(registry, "ceiling", Math.Ceiling);
        RegisterUnaryMath(registry, "cos", Math.Cos);
        RegisterUnaryMath(registry, "cosh", Math.Cosh);
        RegisterUnaryMath(registry, "floor", Math.Floor);
        RegisterUnaryMath(registry, "round", Math.Round);
        RegisterUnaryMath(registry, "sin", Math.Sin);
        RegisterUnaryMath(registry, "sinh", Math.Sinh);
        RegisterUnaryMath(registry, "sqrt", Math.Sqrt);
        RegisterUnaryMath(registry, "tan", Math.Tan);
        RegisterUnaryMath(registry, "tanh", Math.Tanh);
        registry.Register("atan2", 2, 2, (_, args) => ExpressionValue.Number(
            Math.Atan2(args[0].AsNumber("atan2"), args[1].AsNumber("atan2"))),
            "atan2[y,x]");
        registry.Register("chr", 1, 1, (_, args) => ExpressionValue.String(
            char.ConvertFromUtf32(checked((int)args[0].AsNumber("chr")))),
            "chr[codepoint]");
        registry.Register("ord", 1, 1, (_, args) =>
        {
            string value = args[0].AsString("ord");
            if (value.Length == 0)
                throw new ExpressionEvaluationException("ord expects a non-empty string");
            return ExpressionValue.Number(char.ConvertToUtf32(value, 0));
        }, "ord[text]");
        registry.Register("cnumber", 1, 1, (_, args) =>
            double.TryParse(
                args[0].AsString("cnumber"),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double result)
                    ? ExpressionValue.Number(result)
                    : ExpressionValue.Zero,
            "cnumber[text]");
        registry.Register("cstr", 1, 1, (_, args) => ExpressionValue.String(
            args[0].AsNumber("cstr").ToString("G15", CultureInfo.InvariantCulture)),
            "cstr[number]");
        registry.Register("cstrf", 2, 2, (_, args) =>
        {
            double number = args[0].AsNumber("cstrf");
            string format = args[1].AsString("cstrf");
            return ExpressionValue.String(
                format.Contains('X', StringComparison.OrdinalIgnoreCase)
                    ? checked((uint)number).ToString(format, CultureInfo.InvariantCulture)
                    : number.ToString(format, CultureInfo.InvariantCulture));
        }, "cstrf[number,format]");
        registry.Register("hexstr", 1, 1, (_, args) => ExpressionValue.String(
            $"0x{checked((int)args[0].AsNumber("hexstr")):X}"),
            "hexstr[number]");
        registry.Register("strlen", 1, 1, (_, args) => ExpressionValue.Number(
            args[0].AsString("strlen").Length), "strlen[text]");
        registry.Register("tostring", 1, 1, (_, args) =>
            ExpressionValue.String(args[0].ToDisplayString()), "tostring[value]");
        registry.Register("istrue", 1, 1, (_, args) =>
            ExpressionValue.Boolean(args[0].IsTruthy), "istrue[value]");
        registry.Register("isfalse", 1, 1, (_, args) =>
            ExpressionValue.Boolean(!args[0].IsTruthy), "isfalse[value]");
        registry.Register("iif", 3, 3, (_, args) =>
            args[0].IsTruthy ? args[1] : args[2], "iif[test,trueValue,falseValue]");
        registry.Register("ifthen", 2, 3, (context, args) =>
        {
            string? source = args[0].IsTruthy
                ? args[1].AsString("ifthen")
                : args.Count == 3
                    ? args[2].AsString("ifthen")
                    : null;
            return source is null
                ? ExpressionValue.Zero
                : ExpressionProgram.Compile(source).Evaluate(context);
        }, "ifthen[test,trueExpression,falseExpression?]");
        registry.Register("randint", 2, 2, (_, args) =>
        {
            int minimum = checked((int)args[0].AsNumber("randint"));
            int maximum = checked((int)args[1].AsNumber("randint"));
            return ExpressionValue.Number(random.Next(minimum, maximum));
        }, "randint[min,maxExclusive]");
        registry.Register("getregexmatch", 2, 2, (_, args) =>
        {
            var regex = new Regex(
                args[1].AsString("getregexmatch"),
                RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(100));
            Match match = regex.Match(args[0].AsString("getregexmatch"));
            return match.Success
                ? ExpressionValue.String(match.Value)
                : ExpressionValue.Zero;
        }, "getregexmatch[text,pattern]");
    }

    private static void RegisterUnaryMath(
        ExpressionFunctionRegistry registry,
        string name,
        Func<double, double> operation) =>
        registry.Register(name, 1, 1, (_, args) => ExpressionValue.Number(
            operation(args[0].AsNumber(name))), $"{name}[number]");

    private static void RegisterLists(ExpressionFunctionRegistry registry)
    {
        registry.Register("listcreate", 0, int.MaxValue, (_, args) =>
            ExpressionValue.List(new ExpressionList(args)), "listcreate[items...]");
        registry.Register("listadd", 2, 2, (_, args) =>
        {
            ExpressionList list = args[0].AsList("listadd");
            GuardNoCycle(list, args[1], "listadd");
            list.Items.Add(args[1]);
            return args[0];
        }, "listadd[list,item]");
        registry.Register("listinsert", 3, 3, (_, args) =>
        {
            ExpressionList list = args[0].AsList("listinsert");
            GuardNoCycle(list, args[1], "listinsert");
            int index = ToTruncatedInt(args[2], "listinsert");
            if ((uint)index > (uint)list.Items.Count)
                throw BadIndex("insert", index, list.Items.Count, allowEnd: true);
            list.Items.Insert(index, args[1]);
            return args[0];
        }, "listinsert[list,item,index]");
        registry.Register("listremove", 2, 2, (_, args) =>
        {
            args[0].AsList("listremove").Items.Remove(args[1]);
            return args[0];
        }, "listremove[list,item]");
        registry.Register("listremoveat", 2, 2, (_, args) =>
        {
            ExpressionList list = args[0].AsList("listremoveat");
            int index = RequireListIndex(list, args[1], "listremoveat");
            list.Items.RemoveAt(index);
            return args[0];
        }, "listremoveat[list,index]");
        registry.Register("listgetitem", 2, 2, (_, args) =>
        {
            ExpressionList list = args[0].AsList("listgetitem");
            return list.Items[RequireListIndex(list, args[1], "listgetitem")];
        }, "listgetitem[list,index]");
        registry.Register("listcontains", 2, 2, (_, args) =>
            ExpressionValue.Boolean(args[0].AsList("listcontains").Items.Contains(args[1])),
            "listcontains[list,item]");
        registry.Register("listindexof", 2, 2, (_, args) => ExpressionValue.Number(
            args[0].AsList("listindexof").Items.IndexOf(args[1])),
            "listindexof[list,item]");
        registry.Register("listlastindexof", 2, 2, (_, args) =>
            ExpressionValue.Number(args[0].AsList("listlastindexof")
                .Items.LastIndexOf(args[1])), "listlastindexof[list,item]");
        registry.Register("listcopy", 1, 1, (_, args) => ExpressionValue.List(
            new ExpressionList(args[0].AsList("listcopy").Items)), "listcopy[list]");
        registry.Register("listreverse", 1, 1, (_, args) =>
        {
            var values = args[0].AsList("listreverse").Items.ToArray();
            Array.Reverse(values);
            return ExpressionValue.List(new ExpressionList(values));
        }, "listreverse[list]");
        registry.Register("listpop", 1, 2, (_, args) =>
        {
            ExpressionList list = args[0].AsList("listpop");
            int index = args.Count == 1 || args[1].AsNumber("listpop") == -1d
                ? list.Items.Count - 1
                : RequireListIndex(list, args[1], "listpop");
            if (index < 0)
                throw BadIndex("pop", index, list.Items.Count, allowEnd: false);
            ExpressionValue result = list.Items[index];
            list.Items.RemoveAt(index);
            return result;
        }, "listpop[list,index?]");
        registry.Register("listcount", 1, 1, (_, args) => ExpressionValue.Number(
            args[0].AsList("listcount").Items.Count), "listcount[list]");
        registry.Register("listclear", 1, 1, (_, args) =>
        {
            args[0].AsList("listclear").Items.Clear();
            return args[0];
        }, "listclear[list]");
        registry.Register("listfilter", 2, 2, (context, args) =>
        {
            ExpressionList source = args[0].AsList("listfilter");
            ExpressionProgram program = ExpressionProgram.Compile(
                args[1].AsString("listfilter"));
            var result = new ExpressionList();
            WithIterationVariables(context.State, () =>
            {
                for (int index = 0; index < source.Items.Count; index++)
                {
                    SetIteration(context.State, index, source.Items[index]);
                    if (program.Evaluate(context).IsTruthy)
                        result.Items.Add(source.Items[index]);
                }
            });
            return ExpressionValue.List(result);
        }, "listfilter[list,expression]");
        registry.Register("listmap", 2, 2, (context, args) =>
        {
            ExpressionList source = args[0].AsList("listmap");
            ExpressionProgram program = ExpressionProgram.Compile(
                args[1].AsString("listmap"));
            var result = new ExpressionList();
            WithIterationVariables(context.State, () =>
            {
                for (int index = 0; index < source.Items.Count; index++)
                {
                    SetIteration(context.State, index, source.Items[index]);
                    result.Items.Add(program.Evaluate(context));
                }
            });
            return ExpressionValue.List(result);
        }, "listmap[list,expression]");
        registry.Register("listreduce", 2, 2, (context, args) =>
        {
            ExpressionList source = args[0].AsList("listreduce");
            ExpressionProgram program = ExpressionProgram.Compile(
                args[1].AsString("listreduce"));
            ExpressionValue result = ExpressionValue.Zero;
            WithIterationVariables(context.State, () =>
            {
                for (int index = 0; index < source.Items.Count; index++)
                {
                    SetIteration(context.State, index, source.Items[index], result);
                    result = program.Evaluate(context);
                }
            });
            return result;
        }, "listreduce[list,expression]");
        registry.Register("listsort", 1, 2, (context, args) =>
        {
            var result = new ExpressionList(args[0].AsList("listsort").Items);
            if (args.Count == 1 || args[1].AsString("listsort").Length == 0)
            {
                result.Items.Sort(DefaultValueComparer.Instance);
                return ExpressionValue.List(result);
            }

            ExpressionProgram program = ExpressionProgram.Compile(
                args[1].AsString("listsort"));
            WithIterationVariables(context.State, () =>
            {
                for (int index = 1; index < result.Items.Count; index++)
                {
                    ExpressionValue value = result.Items[index];
                    int cursor = index - 1;
                    while (cursor >= 0)
                    {
                        context.State.Set(ExpressionVariableScope.Session, "1", result.Items[cursor]);
                        context.State.Set(ExpressionVariableScope.Session, "2", value);
                        if (program.Evaluate(context).AsNumber("listsort comparator") <= 0d)
                            break;
                        result.Items[cursor + 1] = result.Items[cursor];
                        cursor--;
                    }
                    result.Items[cursor + 1] = value;
                }
            });
            return ExpressionValue.List(result);
        }, "listsort[list,expression?]");
        registry.Register("listfromrange", 2, 2, (_, args) =>
        {
            int start = ToTruncatedInt(args[0], "listfromrange");
            int end = ToTruncatedInt(args[1], "listfromrange");
            int count = checked(Math.Abs(end - start) + 1);
            if (count > 100_000)
            {
                throw new ExpressionEvaluationException(
                    "listfromrange is limited to 100000 entries");
            }
            var result = new ExpressionList();
            int step = start <= end ? 1 : -1;
            for (int value = start;; value += step)
            {
                result.Items.Add(ExpressionValue.Number(value));
                if (value == end)
                    break;
            }
            return ExpressionValue.List(result);
        }, "listfromrange[start,end]");
    }

    private static void RegisterDictionaries(ExpressionFunctionRegistry registry)
    {
        registry.Register("dictcreate", 0, int.MaxValue, (_, args) =>
        {
            if ((args.Count & 1) != 0)
                throw new ExpressionEvaluationException(
                    "dictcreate expects key/value pairs");
            var dictionary = new ExpressionDictionary();
            for (int index = 0; index < args.Count; index += 2)
            {
                string key = args[index].AsString("dictcreate key");
                if (!dictionary.Items.TryAdd(key, args[index + 1]))
                {
                    throw new ExpressionEvaluationException(
                        $"dictcreate received duplicate key '{key}'");
                }
            }
            return ExpressionValue.Dictionary(dictionary);
        }, "dictcreate[key,value,...]");
        registry.Register("dictgetitem", 2, 2, (_, args) =>
        {
            ExpressionDictionary dictionary = args[0].AsDictionary("dictgetitem");
            string key = args[1].AsString("dictgetitem key");
            if (!dictionary.Items.TryGetValue(key, out ExpressionValue value))
                throw new ExpressionEvaluationException($"Dictionary key '{key}' was not found");
            return value;
        }, "dictgetitem[dictionary,key]");
        registry.Register("dictadditem", 3, 3, (_, args) =>
        {
            ExpressionDictionary dictionary = args[0].AsDictionary("dictadditem");
            string key = args[1].AsString("dictadditem key");
            GuardNoCycle(dictionary, args[2], "dictadditem");
            bool replaced = dictionary.Items.ContainsKey(key);
            dictionary.Items[key] = args[2];
            return ExpressionValue.Boolean(replaced);
        }, "dictadditem[dictionary,key,value]");
        registry.Register("dicthaskey", 2, 2, (_, args) => ExpressionValue.Boolean(
            args[0].AsDictionary("dicthaskey").Items.ContainsKey(
                args[1].AsString("dicthaskey key"))), "dicthaskey[dictionary,key]");
        registry.Register("dictremovekey", 2, 2, (_, args) => ExpressionValue.Boolean(
            args[0].AsDictionary("dictremovekey").Items.Remove(
                args[1].AsString("dictremovekey key"))), "dictremovekey[dictionary,key]");
        registry.Register("dictkeys", 1, 1, (_, args) => ExpressionValue.List(
            new ExpressionList(args[0].AsDictionary("dictkeys").Items.Keys.Select(
                ExpressionValue.String))), "dictkeys[dictionary]");
        registry.Register("dictvalues", 1, 1, (_, args) => ExpressionValue.List(
            new ExpressionList(args[0].AsDictionary("dictvalues").Items.Values)),
            "dictvalues[dictionary]");
        registry.Register("dictsize", 1, 1, (_, args) => ExpressionValue.Number(
            args[0].AsDictionary("dictsize").Items.Count), "dictsize[dictionary]");
        registry.Register("dictclear", 1, 1, (_, args) =>
        {
            args[0].AsDictionary("dictclear").Items.Clear();
            return args[0];
        }, "dictclear[dictionary]");
        registry.Register("dictcopy", 1, 1, (_, args) =>
        {
            var result = new ExpressionDictionary();
            foreach ((string key, ExpressionValue value) in
                args[0].AsDictionary("dictcopy").Items)
            {
                result.Items[key] = value;
            }
            return ExpressionValue.Dictionary(result);
        }, "dictcopy[dictionary]");
    }

    private static void RegisterCoordinates(ExpressionFunctionRegistry registry)
    {
        registry.Register("coordinateparse", 1, 1, (_, args) =>
        {
            string source = args[0].AsString("coordinateparse");
            Match match = CoordinatePattern.Match(source);
            if (!match.Success)
            {
                throw new ExpressionEvaluationException(
                    $"Unable to parse coordinate '{source}'");
            }
            double northSouth = double.Parse(
                match.Groups["ns"].Value,
                CultureInfo.InvariantCulture);
            double eastWest = double.Parse(
                match.Groups["ew"].Value,
                CultureInfo.InvariantCulture);
            if (match.Groups["nsdir"].Value.Equals("S", StringComparison.OrdinalIgnoreCase))
                northSouth = -Math.Abs(northSouth);
            else
                northSouth = Math.Abs(northSouth);
            if (match.Groups["ewdir"].Value.Equals("W", StringComparison.OrdinalIgnoreCase))
                eastWest = -Math.Abs(eastWest);
            else
                eastWest = Math.Abs(eastWest);
            double elevation = match.Groups["z"].Success
                ? double.Parse(match.Groups["z"].Value, CultureInfo.InvariantCulture)
                : 0d;
            return ExpressionValue.Coordinates(new ExpressionCoordinates(
                eastWest,
                northSouth,
                elevation));
        }, "coordinateparse[text]");
        registry.Register("coordinategetns", 1, 1, (_, args) =>
            ExpressionValue.Number(args[0].AsCoordinates("coordinategetns").NorthSouth),
            "coordinategetns[coordinates]");
        registry.Register("coordinategetwe", 1, 1, (_, args) =>
            ExpressionValue.Number(args[0].AsCoordinates("coordinategetwe").EastWest),
            "coordinategetwe[coordinates]");
        registry.Register("coordinategetz", 1, 1, (_, args) =>
            ExpressionValue.Number(args[0].AsCoordinates("coordinategetz").Elevation),
            "coordinategetz[coordinates]");
        registry.Register("coordinatetostring", 1, 1, (_, args) =>
            ExpressionValue.String(args[0].AsCoordinates("coordinatetostring").ToString()),
            "coordinatetostring[coordinates]");
        registry.Register("coordinatedistanceflat", 2, 2, (_, args) =>
            ExpressionValue.Number(CoordinateDistance(args[0], args[1], includeElevation: false)),
            "coordinatedistanceflat[first,second]");
        registry.Register("coordinatedistancewithz", 2, 2, (_, args) =>
            ExpressionValue.Number(CoordinateDistance(args[0], args[1], includeElevation: true)),
            "coordinatedistancewithz[first,second]");
    }

    private static void RegisterTime(ExpressionFunctionRegistry registry)
    {
        registry.Register("getdatetimelocal", 0, 1, (_, args) => ExpressionValue.String(
            DateTime.Now.ToString(
                args.Count == 0 ? "hh:mm:ss tt" : args[0].AsString("getdatetimelocal"),
                CultureInfo.InvariantCulture)), "getdatetimelocal[format?]");
        registry.Register("getdatetimeutc", 0, 1, (_, args) => ExpressionValue.String(
            DateTime.UtcNow.ToString(
                args.Count == 0 ? "hh:mm:ss tt" : args[0].AsString("getdatetimeutc"),
                CultureInfo.InvariantCulture)), "getdatetimeutc[format?]");
        registry.Register("getunixtime", 0, 0, (_, _) => ExpressionValue.Number(
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d), "getunixtime[]");
        registry.Register("stopwatchcreate", 0, 0, (_, _) =>
            ExpressionValue.Stopwatch(new ExpressionStopwatch()), "stopwatchcreate[]");
        registry.Register("stopwatchstart", 1, 1, (_, args) =>
        {
            args[0].AsStopwatch("stopwatchstart").Start();
            return args[0];
        }, "stopwatchstart[stopwatch]");
        registry.Register("stopwatchstop", 1, 1, (_, args) =>
        {
            args[0].AsStopwatch("stopwatchstop").Stop();
            return args[0];
        }, "stopwatchstop[stopwatch]");
        registry.Register("stopwatchelapsedseconds", 1, 1, (_, args) =>
            ExpressionValue.Number(args[0].AsStopwatch(
                "stopwatchelapsedseconds").ElapsedSeconds),
            "stopwatchelapsedseconds[stopwatch]");
    }

    private static double CoordinateDistance(
        in ExpressionValue first,
        in ExpressionValue second,
        bool includeElevation)
    {
        ExpressionCoordinates left = first.AsCoordinates("coordinate distance");
        ExpressionCoordinates right = second.AsCoordinates("coordinate distance");
        double eastWest = (left.EastWest - right.EastWest) * 240d;
        double northSouth = (left.NorthSouth - right.NorthSouth) * 240d;
        double elevation = includeElevation
            ? (left.Elevation - right.Elevation) * 240d
            : 0d;
        return Math.Sqrt(
            eastWest * eastWest
            + northSouth * northSouth
            + elevation * elevation);
    }

    private static int RequireListIndex(
        ExpressionList list,
        in ExpressionValue value,
        string operation)
    {
        int index = ToTruncatedInt(value, operation);
        if ((uint)index >= (uint)list.Items.Count)
            throw BadIndex(operation, index, list.Items.Count, allowEnd: false);
        return index;
    }

    private static int ToTruncatedInt(in ExpressionValue value, string operation) =>
        checked((int)value.AsNumber(operation));

    private static ExpressionEvaluationException BadIndex(
        string operation,
        int index,
        int count,
        bool allowEnd) => new(
        $"Unable to {operation} index {index}; valid range is 0.."
        + (allowEnd ? count : count - 1));

    private static void SetIteration(
        ExpressionState state,
        int index,
        in ExpressionValue item,
        ExpressionValue? accumulator = null)
    {
        state.Set(ExpressionVariableScope.Session, "0", ExpressionValue.Number(index));
        state.Set(ExpressionVariableScope.Session, "1", item);
        if (accumulator is { } value)
            state.Set(ExpressionVariableScope.Session, "2", value);
    }

    private static void WithIterationVariables(ExpressionState state, Action action)
    {
        var saved = new (string Name, bool Exists, ExpressionValue Value)[3];
        for (int index = 0; index < saved.Length; index++)
        {
            string name = index.ToString(CultureInfo.InvariantCulture);
            saved[index] = (
                name,
                state.Contains(ExpressionVariableScope.Session, name),
                state.Get(ExpressionVariableScope.Session, name));
        }
        try
        {
            action();
        }
        finally
        {
            foreach ((string name, bool exists, ExpressionValue value) in saved)
            {
                if (exists)
                    state.Set(ExpressionVariableScope.Session, name, value);
                else
                    state.Clear(ExpressionVariableScope.Session, name);
            }
        }
    }

    private static void GuardNoCycle(object destination, in ExpressionValue value, string operation)
    {
        if (ContainsReference(value, destination, new HashSet<object>(
                ReferenceEqualityComparer.Instance)))
        {
            throw new ExpressionEvaluationException(
                $"{operation} cannot create a cyclic collection");
        }
    }

    private static bool ContainsReference(
        in ExpressionValue value,
        object destination,
        HashSet<object> visited)
    {
        if (value.Kind == ExpressionValueKind.List)
        {
            ExpressionList list = value.AsList();
            if (ReferenceEquals(list, destination))
                return true;
            return visited.Add(list)
                && list.Items.Any(item => ContainsReference(item, destination, visited));
        }
        if (value.Kind == ExpressionValueKind.Dictionary)
        {
            ExpressionDictionary dictionary = value.AsDictionary();
            if (ReferenceEquals(dictionary, destination))
                return true;
            return visited.Add(dictionary)
                && dictionary.Items.Values.Any(item =>
                    ContainsReference(item, destination, visited));
        }
        return false;
    }

    private sealed class DefaultValueComparer : IComparer<ExpressionValue>
    {
        public static DefaultValueComparer Instance { get; } = new();

        public int Compare(ExpressionValue left, ExpressionValue right)
        {
            if (left.Kind is ExpressionValueKind.Number or ExpressionValueKind.Boolean
                && right.Kind is ExpressionValueKind.Number or ExpressionValueKind.Boolean)
            {
                return left.AsNumber().CompareTo(right.AsNumber());
            }
            if (left.Kind == ExpressionValueKind.String
                && right.Kind == ExpressionValueKind.String)
            {
                return StringComparer.OrdinalIgnoreCase.Compare(
                    left.AsString(),
                    right.AsString());
            }
            int kind = left.Kind.CompareTo(right.Kind);
            return kind != 0
                ? kind
                : StringComparer.Ordinal.Compare(
                    left.ToDisplayString(),
                    right.ToDisplayString());
        }
    }
}
