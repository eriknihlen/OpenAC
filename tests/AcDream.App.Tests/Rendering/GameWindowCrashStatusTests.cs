using System.Reflection;
using System.Reflection.Emit;
using AcDream.App.Diagnostics;
using AcDream.App.Rendering;
using AcDream.App.Tests.Architecture;
using AcDream.Runtime.Session;
using Silk.NET.Windowing;

namespace AcDream.App.Tests.Rendering;

public sealed class GameWindowCrashStatusTests
{
    [Fact]
    public void Run_LatchesThenReportsBeforeRethrowingFromTheFrameLoopCatch()
    {
        MethodInfo run = RequiredMethod(nameof(GameWindow.Run));
        IReadOnlyList<CompiledCall> calls = CompiledCallGraph.Read(run);
        CompiledCall frameLoop = Assert.Single(
            calls,
            call => call.Target.Name == nameof(IWindow.Run)
                && call.Target.DeclaringType?.Namespace == "Silk.NET.Windowing");
        CompiledCall retain = Assert.Single(
            calls,
            call => call.Target.DeclaringType
                    == typeof(ResourceConstructionCleanupLedger)
                && call.Target.Name
                    == nameof(ResourceConstructionCleanupLedger.RetainFrom));
        CompiledFieldReference latch = Assert.Single(
            CompiledCallGraph.ReadFieldReferences(run),
            field => field.Field.Name == "_runFailure"
                && field.OpCode == OpCodes.Stfld);
        CompiledInstruction rethrow = Assert.Single(
            CompiledCallGraph.ReadInstructions(run),
            instruction => instruction.OpCode == OpCodes.Rethrow);
        CompiledCall report = Assert.Single(calls, call =>
            call.Target.DeclaringType == typeof(LocalCrashReportWriter)
                && call.Target.Name == nameof(LocalCrashReportWriter.TryWrite));

        Assert.True(frameLoop.Offset < retain.Offset);
        Assert.True(retain.Offset < latch.Offset);
        Assert.True(latch.Offset < report.Offset);
        Assert.True(report.Offset < rethrow.Offset);

        ExceptionHandlingClause outerCatch = Assert.Single(run.GetMethodBody()!.ExceptionHandlingClauses,
            clause => clause.Flags == ExceptionHandlingClauseOptions.Clause
                && clause.CatchType == typeof(Exception)
                && clause.HandlerOffset <= retain.Offset
                && retain.Offset < clause.HandlerOffset + clause.HandlerLength);
        Assert.InRange(rethrow.Offset, outerCatch.HandlerOffset, outerCatch.HandlerOffset + outerCatch.HandlerLength - 1);
        ExceptionHandlingClause reportGuard = Assert.Single(run.GetMethodBody()!.ExceptionHandlingClauses,
            clause => clause.Flags == ExceptionHandlingClauseOptions.Clause
                && clause.TryOffset <= report.Offset
                && report.Offset < clause.TryOffset + clause.TryLength);
        Assert.True(latch.Offset < reportGuard.TryOffset);
        foreach (CompiledCall call in calls.Where(call => call.Offset > latch.Offset && call.Offset <= report.Offset))
            Assert.InRange(call.Offset, reportGuard.TryOffset, reportGuard.TryOffset + reportGuard.TryLength - 1);
        Assert.Single(CompiledCallGraph.ReadDeclared(typeof(GameWindow)), call =>
            call.Target.DeclaringType == typeof(LocalCrashReportWriter));
    }

    [Fact]
    public void CrashContext_ReadsControllerOnceAndUsesExistingCellPositionAndCachedGpuMetadata()
    {
        IReadOnlyList<CompiledCall> calls = CompiledCallGraph.Read(RequiredMethod("CaptureLocalCrashReportContext"));
        foreach (string getter in new[] { "get__playerController", "get_CellPosition", "get_State", "get_Capabilities", "get_Width", "get_Height", "get_SampleCount" })
            Assert.Single(calls, call => call.Target.Name == getter);
        Assert.DoesNotContain(calls, call => call.Target.Name == "get_Position" || call.Target.Name == "get_Device");
        Assert.DoesNotContain(CompiledCallGraph.Read(RequiredMethod("ReportExited")), call =>
            call.Target.DeclaringType == typeof(LocalCrashReportWriter));
    }

    [Fact]
    public void ReportExited_ChecksRunFailureBeforeEitherGracefulOrShutdownIncompletePaths()
    {
        MethodInfo reportExited = RequiredMethod("ReportExited");
        IReadOnlyList<CompiledCall> calls = CompiledCallGraph.Read(reportExited);
        CompiledFieldReference failureRead = Assert.Single(
            CompiledCallGraph.ReadFieldReferences(reportExited),
            field => field.Field.Name == "_runFailure"
                && field.OpCode == OpCodes.Ldfld);
        CompiledCall[] exited = calls
            .Where(call => call.Target.DeclaringType == typeof(SessionStatusWriter)
                && call.Target.Name == nameof(SessionStatusWriter.Exited))
            .ToArray();
        Assert.Equal(3, exited.Length);
        CompiledCall status = Assert.Single(
            calls,
            call => call.Target.DeclaringType == typeof(GameWindowLifetimeReport)
                && call.Target.Name == "get_Status");

        Assert.True(failureRead.Offset < exited[0].Offset);
        Assert.True(exited[0].Offset < status.Offset);
        Assert.True(status.Offset < exited[1].Offset);
        Assert.True(exited[1].Offset < exited[2].Offset);
        Assert.Equal(
            ["app", "crashed", "graceful", "shutdown-incomplete"],
            CompiledCallGraph.ReadStringLiterals(reportExited));

        MethodInfo completeShutdown = RequiredMethod("CompleteShutdown");
        Assert.Equal(
            2,
            CompiledCallGraph.Read(completeShutdown).Count(call =>
                call.Target.DeclaringType == typeof(GameWindow)
                && call.Target.Name == "ReportExited"));
        Assert.Equal(
            exited.Length,
            CompiledCallGraph.ReadDeclared(typeof(GameWindow)).Count(call =>
                call.Target.DeclaringType == typeof(SessionStatusWriter)
                && call.Target.Name == nameof(SessionStatusWriter.Exited)));
    }

    [Fact]
    public void RunFailureFieldExistsAndDefaultsToNull()
    {
        FieldInfo field = typeof(GameWindow).GetField(
            "_runFailure",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(GameWindow).FullName, "_runFailure");
        Assert.Equal(typeof(Exception), field.FieldType);

        ConstructorInfo[] constructors = typeof(GameWindow).GetConstructors(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotEmpty(constructors);
        Assert.DoesNotContain(
            constructors.SelectMany(CompiledCallGraph.ReadFieldReferences),
            reference => reference.Field == field
                && reference.OpCode == OpCodes.Stfld);
    }

    private static MethodInfo RequiredMethod(string name) =>
        typeof(GameWindow).GetMethod(
            name,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(typeof(GameWindow).FullName, name);
}
