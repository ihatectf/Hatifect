using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Hatifect.UI.Tooling.Protocol;

namespace Hatifect.UI.Tooling.Server;

internal static class Program
{
    private static async Task<int> Main()
    {
        using var lifetime = new UiToolingProcessLifetime();
        return await UiToolingServer.RunAsync(
            Console.OpenStandardInput(),
            Console.OpenStandardOutput(),
            Console.Error,
            lifetime.Token).ConfigureAwait(false);
    }
}

/// <summary>
/// Bridges the process console cancellation event into the server's caller-owned cancellation
/// token. The editor still owns whether and when the process is cancelled.
/// </summary>
internal sealed class UiToolingProcessLifetime : IDisposable
{
    private readonly CancellationTokenSource cancellation = new();
    private readonly object gate = new();
    private bool disposed;

    public UiToolingProcessLifetime()
        => Console.CancelKeyPress += OnCancelKeyPress;

    public CancellationToken Token => cancellation.Token;

    internal void RequestCancellation()
    {
        lock (gate)
        {
            if (!disposed)
                cancellation.Cancel();
        }
    }

    public void Dispose()
    {
        Console.CancelKeyPress -= OnCancelKeyPress;
        lock (gate)
        {
            if (disposed)
                return;

            cancellation.Dispose();
            disposed = true;
        }
    }

    private void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs args)
    {
        args.Cancel = true;
        RequestCancellation();
    }
}

/// <summary>
/// Internal composition root for one editor-owned tooling protocol session. All streams are borrowed;
/// this type owns neither their lifetime nor any process or service lifecycle.
/// </summary>
internal static class UiToolingServer
{
    internal const int SuccessExitCode = 0;
    internal const int FatalExitCode = 1;
    internal const int CanceledExitCode = 2;
    internal const string FatalDiagnosticPrefix = "Hatifect UI tooling server fatal error: ";

    public static async ValueTask<int> RunAsync(
        Stream input,
        Stream output,
        TextWriter diagnostics,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(diagnostics);

        try
        {
            _ = await UiToolingProtocolTransportLoop.RunAsync(
                input,
                output,
                new UiToolingProtocolSession(),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return SuccessExitCode;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return CanceledExitCode;
        }
        catch (Exception exception)
        {
            await WriteFatalDiagnosticAsync(diagnostics, exception).ConfigureAwait(false);
            return FatalExitCode;
        }
    }

    private static async ValueTask WriteFatalDiagnosticAsync(
        TextWriter diagnostics,
        Exception exception)
    {
        try
        {
            await diagnostics.WriteLineAsync(FatalDiagnosticPrefix + exception.ToString())
                .ConfigureAwait(false);
            await diagnostics.FlushAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Reporting must never change the deterministic fatal classification or write to stdout.
        }
    }
}
