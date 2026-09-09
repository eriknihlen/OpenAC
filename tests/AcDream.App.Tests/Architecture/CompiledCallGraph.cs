using System.Reflection;
using System.Reflection.Emit;

namespace AcDream.App.Tests.Architecture;

internal readonly record struct CompiledCall(int Offset, MethodBase Target);
internal readonly record struct CompiledInstruction(int Offset, OpCode OpCode);
internal readonly record struct CompiledFieldReference(
    int Offset,
    OpCode OpCode,
    FieldInfo Field);
internal readonly record struct CompiledBranch(
    int Offset,
    OpCode OpCode,
    int TargetOffset);

internal static class CompiledCallGraph
{
    private static readonly IReadOnlyDictionary<short, OpCode> OpCodesByValue =
        typeof(OpCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(OpCode))
            .Select(field => (OpCode)field.GetValue(null)!)
            .ToDictionary(opCode => opCode.Value);

    public static IReadOnlyList<CompiledCall> Read(MethodBase method)
        => ReadMethodReferences(method, includeDelegateTargets: false);

    public static IReadOnlyList<CompiledCall> ReadMethodReferences(MethodBase method) =>
        ReadMethodReferences(method, includeDelegateTargets: true);

    public static IReadOnlyList<CompiledCall> ReadDeclared(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        const BindingFlags flags = BindingFlags.Instance
            | BindingFlags.Static
            | BindingFlags.Public
            | BindingFlags.NonPublic
            | BindingFlags.DeclaredOnly;
        return type.GetMethods(flags)
            .Cast<MethodBase>()
            .Concat(type.GetConstructors(flags))
            .Where(method => method.GetMethodBody() is not null)
            .SelectMany(Read)
            .ToArray();
    }

    public static IReadOnlyList<CompiledCall> ReadOwned(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        const BindingFlags flags = BindingFlags.Instance
            | BindingFlags.Static
            | BindingFlags.Public
            | BindingFlags.NonPublic
            | BindingFlags.DeclaredOnly;
        MethodBase[] roots = EnumerateOwnedTypes(type)
            .SelectMany(owner => owner.GetMethods(flags)
                .Cast<MethodBase>()
                .Concat(owner.GetConstructors(flags)))
            .Where(method => method.GetMethodBody() is not null)
            .ToArray();
        var pending = new Queue<MethodBase>(roots);
        var seen = new HashSet<MethodBase>(roots);
        var calls = new List<CompiledCall>();
        while (pending.TryDequeue(out MethodBase? method))
        {
            foreach (CompiledCall call in ReadMethodReferences(method))
            {
                calls.Add(call);
                if (call.Target.Module == type.Module
                    && call.Target.Name.Contains('<', StringComparison.Ordinal)
                    && call.Target.GetMethodBody() is not null
                    && seen.Add(call.Target))
                {
                    pending.Enqueue(call.Target);
                }
            }
        }

        return calls;
    }

    private static IEnumerable<Type> EnumerateOwnedTypes(Type root)
    {
        yield return root;
        foreach (Type nested in root.GetNestedTypes(
            BindingFlags.Public | BindingFlags.NonPublic))
        {
            foreach (Type owned in EnumerateOwnedTypes(nested))
                yield return owned;
        }
    }

    public static IReadOnlyList<string> ReadStringLiterals(MethodBase method)
    {
        ArgumentNullException.ThrowIfNull(method);
        byte[] il = method.GetMethodBody()?.GetILAsByteArray()
            ?? throw new InvalidOperationException(
                $"{method.DeclaringType?.FullName}.{method.Name} has no compiled body.");
        var literals = new List<string>();

        for (int cursor = 0; cursor < il.Length;)
        {
            OpCode opCode = ReadOpCode(il, ref cursor);
            if (opCode == OpCodes.Ldstr)
            {
                int token = BitConverter.ToInt32(il, cursor);
                literals.Add(method.Module.ResolveString(token));
            }

            cursor += OperandSize(opCode.OperandType, il, cursor);
        }

        return literals;
    }

    public static IReadOnlyList<CompiledInstruction> ReadInstructions(
        MethodBase method)
    {
        ArgumentNullException.ThrowIfNull(method);
        byte[] il = method.GetMethodBody()?.GetILAsByteArray()
            ?? throw new InvalidOperationException(
                $"{method.DeclaringType?.FullName}.{method.Name} has no compiled body.");
        var instructions = new List<CompiledInstruction>();

        for (int cursor = 0; cursor < il.Length;)
        {
            int instructionOffset = cursor;
            OpCode opCode = ReadOpCode(il, ref cursor);
            instructions.Add(new CompiledInstruction(instructionOffset, opCode));
            cursor += OperandSize(opCode.OperandType, il, cursor);
        }

        return instructions;
    }

    public static IReadOnlyList<CompiledFieldReference> ReadFieldReferences(
        MethodBase method)
    {
        ArgumentNullException.ThrowIfNull(method);
        byte[] il = method.GetMethodBody()?.GetILAsByteArray()
            ?? throw new InvalidOperationException(
                $"{method.DeclaringType?.FullName}.{method.Name} has no compiled body.");
        Type[]? declaringArguments = method.DeclaringType?.IsGenericType == true
            ? method.DeclaringType.GetGenericArguments()
            : null;
        Type[]? methodArguments = method.IsGenericMethod
            ? method.GetGenericArguments()
            : null;
        var references = new List<CompiledFieldReference>();

        for (int cursor = 0; cursor < il.Length;)
        {
            int instructionOffset = cursor;
            OpCode opCode = ReadOpCode(il, ref cursor);
            if (opCode.OperandType == OperandType.InlineField)
            {
                int token = BitConverter.ToInt32(il, cursor);
                FieldInfo? field = method.Module.ResolveField(
                    token,
                    declaringArguments,
                    methodArguments);
                if (field is not null)
                {
                    references.Add(new CompiledFieldReference(
                        instructionOffset,
                        opCode,
                        field));
                }
            }

            cursor += OperandSize(opCode.OperandType, il, cursor);
        }

        return references;
    }

    public static IReadOnlyList<CompiledBranch> ReadBranches(MethodBase method)
    {
        ArgumentNullException.ThrowIfNull(method);
        byte[] il = method.GetMethodBody()?.GetILAsByteArray()
            ?? throw new InvalidOperationException(
                $"{method.DeclaringType?.FullName}.{method.Name} has no compiled body.");
        var branches = new List<CompiledBranch>();

        for (int cursor = 0; cursor < il.Length;)
        {
            int instructionOffset = cursor;
            OpCode opCode = ReadOpCode(il, ref cursor);
            if (opCode.OperandType == OperandType.ShortInlineBrTarget)
            {
                int target = cursor + sizeof(sbyte) + unchecked((sbyte)il[cursor]);
                branches.Add(new CompiledBranch(instructionOffset, opCode, target));
            }
            else if (opCode.OperandType == OperandType.InlineBrTarget)
            {
                int target = cursor + sizeof(int) + BitConverter.ToInt32(il, cursor);
                branches.Add(new CompiledBranch(instructionOffset, opCode, target));
            }

            cursor += OperandSize(opCode.OperandType, il, cursor);
        }

        return branches;
    }

    public static IReadOnlyList<Type> ReadTypeReferences(MethodBase method)
    {
        ArgumentNullException.ThrowIfNull(method);
        byte[] il = method.GetMethodBody()?.GetILAsByteArray()
            ?? throw new InvalidOperationException(
                $"{method.DeclaringType?.FullName}.{method.Name} has no compiled body.");
        Type[]? declaringArguments = method.DeclaringType?.IsGenericType == true
            ? method.DeclaringType.GetGenericArguments()
            : null;
        Type[]? methodArguments = method.IsGenericMethod
            ? method.GetGenericArguments()
            : null;
        var types = new List<Type>();

        for (int cursor = 0; cursor < il.Length;)
        {
            OpCode opCode = ReadOpCode(il, ref cursor);
            if (opCode.OperandType == OperandType.InlineType)
            {
                int token = BitConverter.ToInt32(il, cursor);
                Type? type = method.Module.ResolveType(
                    token,
                    declaringArguments,
                    methodArguments);
                if (type is not null)
                    types.Add(type);
            }

            cursor += OperandSize(opCode.OperandType, il, cursor);
        }

        return types;
    }

    public static int IndexOf(
        IReadOnlyList<CompiledCall> calls,
        Type declaringType,
        string methodName,
        int startIndex = 0) =>
        Enumerable.Range(startIndex, calls.Count - startIndex)
            .FirstOrDefault(
                index => calls[index].Target.DeclaringType == declaringType
                    && calls[index].Target.Name == methodName,
                -1);

    private static IReadOnlyList<CompiledCall> ReadMethodReferences(
        MethodBase method,
        bool includeDelegateTargets)
    {
        ArgumentNullException.ThrowIfNull(method);
        byte[] il = method.GetMethodBody()?.GetILAsByteArray()
            ?? throw new InvalidOperationException(
                $"{method.DeclaringType?.FullName}.{method.Name} has no compiled body.");
        Type[]? declaringArguments = method.DeclaringType?.IsGenericType == true
            ? method.DeclaringType.GetGenericArguments()
            : null;
        Type[]? methodArguments = method.IsGenericMethod
            ? method.GetGenericArguments()
            : null;
        var calls = new List<CompiledCall>();

        for (int cursor = 0; cursor < il.Length;)
        {
            int instructionOffset = cursor;
            OpCode opCode = ReadOpCode(il, ref cursor);
            if (opCode.OperandType == OperandType.InlineMethod)
            {
                int token = BitConverter.ToInt32(il, cursor);
                MethodBase? target = method.Module.ResolveMethod(
                    token,
                    declaringArguments,
                    methodArguments);
                bool invocation = opCode == OpCodes.Call
                    || opCode == OpCodes.Callvirt
                    || opCode == OpCodes.Newobj;
                bool delegateTarget = includeDelegateTargets
                    && (opCode == OpCodes.Ldftn || opCode == OpCodes.Ldvirtftn);
                if (target is not null && (invocation || delegateTarget))
                    calls.Add(new CompiledCall(instructionOffset, target));
            }

            cursor += OperandSize(opCode.OperandType, il, cursor);
        }

        return calls;
    }

    private static OpCode ReadOpCode(byte[] il, ref int cursor)
    {
        byte first = il[cursor++];
        short value = first == 0xFE
            ? unchecked((short)(0xFE00 | il[cursor++]))
            : first;
        return OpCodesByValue.TryGetValue(value, out OpCode opCode)
            ? opCode
            : throw new InvalidOperationException(
                $"Unknown IL opcode 0x{unchecked((ushort)value):X4}.");
    }

    private static int OperandSize(OperandType operandType, byte[] il, int cursor) =>
        operandType switch
        {
            OperandType.InlineNone => 0,
            OperandType.ShortInlineBrTarget or
            OperandType.ShortInlineI or
            OperandType.ShortInlineVar => 1,
            OperandType.InlineVar => 2,
            OperandType.InlineBrTarget or
            OperandType.InlineField or
            OperandType.InlineI or
            OperandType.InlineMethod or
            OperandType.InlineSig or
            OperandType.InlineString or
            OperandType.InlineTok or
            OperandType.InlineType or
            OperandType.ShortInlineR => 4,
            OperandType.InlineI8 or OperandType.InlineR => 8,
            OperandType.InlineSwitch =>
                sizeof(int) + (BitConverter.ToInt32(il, cursor) * sizeof(int)),
            _ => throw new ArgumentOutOfRangeException(
                nameof(operandType),
                operandType,
                "Unsupported IL operand type."),
        };
}
