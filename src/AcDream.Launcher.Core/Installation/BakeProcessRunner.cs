using System.Diagnostics;
using System.Globalization;
using System.Text;
using AcDream.Platform;

namespace AcDream.Launcher.Core.Installation;

public sealed record BakeProcessRequest(
    string ExecutablePath,
    string DatDirectory,
    string OutputPath,
    int Threads,
    string? PublicationNonce = null,
    IReadOnlyList<uint>? DatIds = null,
    IReadOnlyList<byte>? Landblocks = null)
{
    public IReadOnlyList<string> Arguments
    {
        get
        {
            var arguments = new List<string>
            {
                "--dat-dir",
                DatDirectory,
                "--out",
                OutputPath,
                "--threads",
                Threads.ToString(CultureInfo.InvariantCulture),
                "--progress-json",
            };
            if (DatIds is { Count: > 0 })
            {
                arguments.Add("--ids");
                arguments.Add(string.Join(
                    ',',
                    DatIds.Select(static id => $"0x{id:X8}")));
            }

            if (Landblocks is { Count: > 0 })
            {
                arguments.Add("--landblocks");
                arguments.Add(string.Join(
                    ',',
                    Landblocks.Select(static id => $"0x{id:X2}")));
            }

            return arguments;
        }
    }
}

public sealed record BakeProcessResult(int ExitCode, string StandardError);

public interface IBakeProcessRunner
{
    Task<BakeProcessResult> RunAsync(
        BakeProcessRequest request,
        Action<string> onStandardOutput,
        CancellationToken cancellationToken = default);
}

public sealed class SystemBakeProcessRunner : IBakeProcessRunner
{
    private const int BufferSize = 4096;
    private const int MaximumCapturedErrorCharacters = 32 * 1024;

    public async Task<BakeProcessResult> RunAsync(
        BakeProcessRequest request,
        Action<string> onStandardOutput,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(onStandardOutput);
        if (request.Threads <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "Bake thread count must be positive.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        ProcessStartInfo startInfo = CreateStartInfo(request);

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("The bake process could not be started.");
        }

        process.StandardInput.Close();

        var standardError = new StringBuilder();
        Task stdoutPump = PumpAsync(
            process.StandardOutput,
            onStandardOutput,
            CancellationToken.None);
        Task stderrPump = PumpAsync(
            process.StandardError,
            chunk => AppendBounded(standardError, chunk),
            CancellationToken.None);

        using CancellationTokenRegistration cancellation = cancellationToken.Register(
            static state =>
            {
                var child = (Process)state!;
                try
                {
                    if (!child.HasExited)
                    {
                        child.Kill(entireProcessTree: true);
                    }
                }
                catch
                {
                }
            },
            process);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(stdoutPump, stderrPump).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return new BakeProcessResult(process.ExitCode, standardError.ToString());
        }
        catch (OperationCanceledException)
        {
            try
            {
                using var cleanupTimeout = new CancellationTokenSource(
                    TimeSpan.FromSeconds(5));
                await process.WaitForExitAsync(cleanupTimeout.Token)
                    .ConfigureAwait(false);
                await Task.WhenAll(stdoutPump, stderrPump)
                    .WaitAsync(cleanupTimeout.Token)
                    .ConfigureAwait(false);
            }
            catch
            {
            }

            throw;
        }
    }

    internal static ProcessStartInfo CreateStartInfo(BakeProcessRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var startInfo = new ProcessStartInfo
        {
            FileName = request.ExecutablePath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (string argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        startInfo.Environment.Remove(
            BakePublicationGuardPaths.NonceEnvironmentVariable);
        if (request.PublicationNonce is not null)
        {
            if (!BakePublicationGuardPaths.IsValidNonce(
                    request.PublicationNonce))
            {
                throw new ArgumentException(
                    "The bake publication nonce is invalid.",
                    nameof(request));
            }

            startInfo.Environment[
                BakePublicationGuardPaths.NonceEnvironmentVariable] =
                request.PublicationNonce;
        }

        return startInfo;
    }

    private static async Task PumpAsync(
        TextReader reader,
        Action<string> sink,
        CancellationToken cancellationToken)
    {
        char[] buffer = new char[BufferSize];
        while (true)
        {
            int read = await reader.ReadAsync(buffer, cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                return;
            }

            sink(new string(buffer, 0, read));
        }
    }

    private static void AppendBounded(StringBuilder destination, string chunk)
    {
        int remaining = MaximumCapturedErrorCharacters - destination.Length;
        if (remaining <= 0)
        {
            return;
        }

        destination.Append(chunk.AsSpan(0, Math.Min(remaining, chunk.Length)));
    }
}
