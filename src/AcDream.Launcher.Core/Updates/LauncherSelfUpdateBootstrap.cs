using System.Diagnostics;

namespace AcDream.Launcher.Core.Updates;

public sealed record SelfUpdateStartupResult(
    bool ShouldExit,
    int ExitCode,
    string[] RemainingArguments);

public static class LauncherSelfUpdateBootstrap
{
    public const string HelperArgument = "--acdream-self-update-helper-v1";
    public const string ConfirmArgument = "--acdream-self-update-confirm-v1";
    internal const int UpdateLeaseBusyExitCode = 73;
    private const string InternalArgumentPrefix = "--acdream-self-update-";
    private static readonly TimeSpan ConfirmationTimeout = TimeSpan.FromSeconds(30);

    public static async Task<SelfUpdateStartupResult> HandleAsync(
        string[] args,
        LauncherSelfUpdateManager manager,
        string launcherBaseDirectory,
        string currentExecutablePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(manager);
        string baseDirectory = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(launcherBaseDirectory));
        string executable = Path.GetFullPath(currentExecutablePath);

        if (args.Length > 0
            && string.Equals(args[0], HelperArgument, StringComparison.Ordinal))
        {
            if (args.Length < 4
                || !int.TryParse(
                    args[1],
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out int parentPid)
                || parentPid <= 0)
            {
                return new SelfUpdateStartupResult(true, 64, []);
            }

            int exitCode = await RunHelperAsync(
                    manager,
                    baseDirectory,
                    executable,
                    parentPid,
                    args[2],
                    args[3],
                    args[4..],
                    cancellationToken)
                .ConfigureAwait(false);
            return new SelfUpdateStartupResult(true, exitCode, []);
        }

        if (args.Length > 0
            && string.Equals(args[0], ConfirmArgument, StringComparison.Ordinal))
        {
            if (args.Length < 2)
            {
                return new SelfUpdateStartupResult(true, 64, []);
            }

            if (manager.Barrier.TryAcquireSession(
                    out UpdateSessionBarrier.SessionLease? unexpectedSharedLease))
            {
                unexpectedSharedLease?.Dispose();
                throw new LauncherUpdateException(
                    "Self-update confirmation is trusted only while its helper owns "
                    + "the exclusive update lease.");
            }

            await manager.ConfirmAsync(
                    args[1],
                    baseDirectory,
                    executable,
                    cancellationToken)
                .ConfigureAwait(false);
            return new SelfUpdateStartupResult(false, 0, args[2..]);
        }

        if (args.Length > 0
            && args[0].StartsWith(InternalArgumentPrefix, StringComparison.Ordinal))
        {
            // Internal modes are an exact vocabulary. In particular, an old
            // deferred-restart marker must never become an authorization to
            // skip a pending recovery state.
            return new SelfUpdateStartupResult(true, 64, []);
        }

        _ = await manager.LoadPendingAsync(cancellationToken).ConfigureAwait(false);

        if (!manager.Barrier.TryAcquireExclusive(
                out UpdateSessionBarrier.ExclusiveLease? startupLease))
        {
            if (!manager.Barrier.TryAcquireSession(
                    out UpdateSessionBarrier.SessionLease? sharedLease))
            {
                throw new LauncherUpdateException(
                    "Launcher startup is blocked by an active update or recovery transaction.");
            }

            using (sharedLease
                ?? throw new InvalidOperationException("Shared startup lease is missing."))
            {
                SelfUpdatePlan? blockedPlan = await manager.LoadPendingAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (blockedPlan is null)
                {
                    return new SelfUpdateStartupResult(false, 0, args);
                }

                ValidateCanonicalStartup(blockedPlan, baseDirectory, executable);
                if (blockedPlan.State != SelfUpdatePlanState.Staged)
                {
                    throw new LauncherUpdateException(
                        $"Self-update state '{blockedPlan.State}' requires exclusive recovery.");
                }

                return new SelfUpdateStartupResult(false, 0, args);
            }
        }

        using (UpdateSessionBarrier.ExclusiveLease lease = startupLease
            ?? throw new InvalidOperationException("Exclusive startup lease is missing."))
        {
            SelfUpdatePlan? plan = await manager.LoadPendingAsync(cancellationToken)
                .ConfigureAwait(false);
            _ = manager.CleanupOwnedResidueUnderLease(
                plan,
                baseDirectory,
                lease);
            if (plan is null)
            {
                return new SelfUpdateStartupResult(false, 0, args);
            }

            ValidateCanonicalStartup(plan, baseDirectory, executable);

            if (plan.State == SelfUpdatePlanState.AwaitingConfirmation)
            {
                if (!manager.IsConfirmed(plan.TransactionId))
                {
                    await manager.ConfirmAsync(
                            plan.TransactionId,
                            baseDirectory,
                            executable,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                await manager.CompleteConfirmedAsync(
                        plan.TransactionId,
                        baseDirectory,
                        cancellationToken)
                    .ConfigureAwait(false);
                _ = manager.CleanupOwnedResidueUnderLease(
                    pending: null,
                    baseDirectory,
                    lease);
                return new SelfUpdateStartupResult(false, 0, args);
            }

            if (plan.State is SelfUpdatePlanState.Applying
                or SelfUpdatePlanState.RolledBack)
            {
                if (plan.State == SelfUpdatePlanState.Applying)
                {
                    plan = await manager.RecoverApplyingAsync(
                            baseDirectory,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                if (plan.State != SelfUpdatePlanState.RolledBack)
                {
                    throw new LauncherUpdateException(
                        "The interrupted self-update did not produce a rollback receipt.");
                }

                await manager.CompleteRolledBackAsync(
                        plan.TransactionId,
                        baseDirectory,
                        lease,
                        cancellationToken)
                    .ConfigureAwait(false);
                _ = manager.CleanupOwnedResidueUnderLease(
                    pending: null,
                    baseDirectory,
                    lease);
                return new SelfUpdateStartupResult(false, 0, args);
            }

            if (plan.State != SelfUpdatePlanState.Staged)
            {
                throw new LauncherUpdateException(
                    $"Self-update state '{plan.State}' cannot start a helper.");
            }

            StartStagedHelper(manager, plan, baseDirectory, args);
            return new SelfUpdateStartupResult(true, 0, []);
        }
    }

    public static async Task<bool> TryApplyStagedUpdateNowAsync(
        LauncherSelfUpdateManager manager,
        string launcherBaseDirectory,
        string currentExecutablePath,
        IReadOnlyList<string> publicArguments,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(publicArguments);
        string baseDirectory = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(launcherBaseDirectory));
        string executable = Path.GetFullPath(currentExecutablePath);

        if (!manager.Barrier.TryAcquireExclusive(
                out UpdateSessionBarrier.ExclusiveLease? applyLease))
        {
            return false;
        }

        using (UpdateSessionBarrier.ExclusiveLease lease = applyLease
            ?? throw new InvalidOperationException("Exclusive apply lease is missing."))
        {
            SelfUpdatePlan? plan = await manager.LoadPendingAsync(cancellationToken)
                .ConfigureAwait(false);
            if (plan is null || plan.State != SelfUpdatePlanState.Staged)
            {
                return false;
            }

            ValidateCanonicalStartup(plan, baseDirectory, executable);
            StartStagedHelper(manager, plan, baseDirectory, publicArguments);
            return true;
        }
    }

    private static void StartStagedHelper(
        LauncherSelfUpdateManager manager,
        SelfUpdatePlan plan,
        string baseDirectory,
        IReadOnlyList<string> publicArguments)
    {
        string helperPath = manager.GetStagedLauncherPath(plan);
        var startInfo = new ProcessStartInfo(helperPath)
        {
            UseShellExecute = false,
            WorkingDirectory = manager.GetPayloadDirectory(plan.TransactionId),
        };
        startInfo.ArgumentList.Add(HelperArgument);
        startInfo.ArgumentList.Add(
            Environment.ProcessId.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(baseDirectory);
        startInfo.ArgumentList.Add(plan.TransactionId);
        foreach (string argument in publicArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        _ = Process.Start(startInfo)
            ?? throw new LauncherUpdateException(
                "The launcher self-update helper could not be started.");
    }

    private static async Task<int> RunHelperAsync(
        LauncherSelfUpdateManager manager,
        string helperBaseDirectory,
        string currentExecutablePath,
        int parentPid,
        string targetDirectory,
        string transactionId,
        IReadOnlyList<string> publicArguments,
        CancellationToken cancellationToken)
    {
        SelfUpdatePlan plan = await manager.LoadPendingAsync(cancellationToken)
            .ConfigureAwait(false)
            ?? throw new LauncherUpdateException("The helper found no pending self-update.");
        if (plan.State != SelfUpdatePlanState.Staged
            || !string.Equals(plan.TransactionId, transactionId, StringComparison.Ordinal))
        {
            throw new LauncherUpdateException(
                "The helper mode does not match a staged self-update transaction.");
        }

        if (!PathsEqual(plan.TargetDirectory, targetDirectory))
        {
            throw new LauncherUpdateException(
                "The helper target does not match the pending self-update.");
        }

        string expectedHelperDirectory = manager.GetPayloadDirectory(plan.TransactionId);
        string expectedHelperPath = manager.GetStagedLauncherPath(plan);
        if (!PathsEqual(helperBaseDirectory, expectedHelperDirectory)
            || !PathsEqual(currentExecutablePath, expectedHelperPath))
        {
            throw new LauncherUpdateException(
                "Self-update helper mode is trusted only from the staged launcher payload.");
        }

        string launcherPath = ClientVersionStore.ResolveContained(
            targetDirectory,
            GetLauncherFileName(plan.Rid));
        var startInfo = new ProcessStartInfo(launcherPath)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetFullPath(targetDirectory),
        };
        startInfo.ArgumentList.Add(ConfirmArgument);
        startInfo.ArgumentList.Add(transactionId);
        foreach (string argument in publicArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        await WaitForParentExitAsync(parentPid, cancellationToken).ConfigureAwait(false);
        if (!manager.Barrier.TryAcquireExclusive(
                out UpdateSessionBarrier.ExclusiveLease? updateLease))
        {
            return UpdateLeaseBusyExitCode;
        }

        ProcessStartInfo? restoredStart = null;
        using (UpdateSessionBarrier.ExclusiveLease lease = updateLease
            ?? throw new InvalidOperationException("Exclusive update lease is missing."))
        {
            plan = await manager.LoadPendingAsync(cancellationToken)
                .ConfigureAwait(false)
                ?? throw new LauncherUpdateException(
                    "The helper found no pending self-update after acquiring the lease.");
            if (plan.State != SelfUpdatePlanState.Staged
                || !string.Equals(
                    plan.TransactionId,
                    transactionId,
                    StringComparison.Ordinal)
                || !PathsEqual(plan.TargetDirectory, targetDirectory))
            {
                throw new LauncherUpdateException(
                    "The pending self-update changed before the helper acquired its lease.");
            }

            _ = manager.CleanupOwnedResidueUnderLease(
                plan,
                targetDirectory,
                lease);
            Process? replacement = null;
            try
            {
                plan = await manager.ApplyPendingAsync(targetDirectory, cancellationToken)
                    .ConfigureAwait(false);
                replacement = Process.Start(startInfo)
                    ?? throw new LauncherUpdateException(
                        "The updated launcher could not be started.");
                DateTimeOffset deadline = DateTimeOffset.UtcNow + ConfirmationTimeout;
                while (!manager.IsConfirmed(transactionId))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (replacement.HasExited || DateTimeOffset.UtcNow >= deadline)
                    {
                        throw new LauncherUpdateException(
                            replacement.HasExited
                                ? $"The updated launcher exited with code {replacement.ExitCode} "
                                    + "before confirming startup."
                                : "The updated launcher did not confirm startup in time.");
                    }

                    await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                }

                await manager.CompleteConfirmedAsync(
                        transactionId,
                        targetDirectory,
                        cancellationToken)
                    .ConfigureAwait(false);
                return 0;
            }
            catch
            {
                if (replacement is { HasExited: false })
                {
                    replacement.Kill(entireProcessTree: true);
                    await replacement.WaitForExitAsync(CancellationToken.None)
                        .ConfigureAwait(false);
                }

                try
                {
                    SelfUpdatePlan? pending = await manager.LoadPendingAsync(
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    SelfUpdatePlan? rollbackReceipt = pending?.State switch
                    {
                        SelfUpdatePlanState.Applying =>
                            await manager.RecoverApplyingAsync(
                                    targetDirectory,
                                    CancellationToken.None)
                                .ConfigureAwait(false),
                        SelfUpdatePlanState.AwaitingConfirmation =>
                            await manager.RollbackAwaitingConfirmationAsync(
                                    targetDirectory,
                                    CancellationToken.None)
                                .ConfigureAwait(false),
                        SelfUpdatePlanState.RolledBack => pending,
                        _ => null,
                    };
                    if (rollbackReceipt?.State != SelfUpdatePlanState.RolledBack)
                    {
                        return 75;
                    }

                    await manager.VerifyRestoredPriorAsync(
                            targetDirectory,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch
                {
                    // An ambiguous state must not start either executable.
                    return 75;
                }

                restoredStart = new ProcessStartInfo(launcherPath)
                {
                    UseShellExecute = false,
                    WorkingDirectory = Path.GetFullPath(targetDirectory),
                };
                foreach (string argument in publicArguments)
                {
                    restoredStart.ArgumentList.Add(argument);
                }
            }
            finally
            {
                replacement?.Dispose();
            }
        }

        if (restoredStart is null || Process.Start(restoredStart) is null)
        {
            return 75;
        }

        return 74;
    }

    private static string GetLauncherFileName(string rid) =>
        PayloadExecutableNames.Launcher + PayloadExecutableNames.SuffixForRid(rid);

    private static void ValidateCanonicalStartup(
        SelfUpdatePlan plan,
        string baseDirectory,
        string executable)
    {
        if (!PathsEqual(plan.TargetDirectory, baseDirectory))
        {
            throw new LauncherUpdateException(
                "The pending self-update targets a different launcher directory.");
        }

        string expectedExecutable = ClientVersionStore.ResolveContained(
            baseDirectory,
            GetLauncherFileName(plan.Rid));
        if (!PathsEqual(executable, expectedExecutable))
        {
            throw new LauncherUpdateException(
                "Self-update can run only from the published acdream-launcher executable.");
        }
    }

    private static async Task WaitForParentExitAsync(
        int parentPid,
        CancellationToken cancellationToken)
    {
        try
        {
            using Process parent = Process.GetProcessById(parentPid);
            if (parent.Id == Environment.ProcessId)
            {
                throw new LauncherUpdateException(
                    "The self-update helper cannot wait on itself.");
            }

            await parent.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
            // The parent exited before the helper opened it.
        }
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
}
