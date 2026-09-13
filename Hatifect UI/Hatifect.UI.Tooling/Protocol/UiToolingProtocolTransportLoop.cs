using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Hatifect.UI.Tooling.Protocol;

internal enum UiToolingProtocolTermination
{
    EndOfStream,
    ExitRequested
}

internal readonly record struct UiToolingProtocolLoopResult(
    UiToolingProtocolTermination Termination,
    int ProcessedMessages);

/// <summary>
/// Internal/provisional sequential transport loop for one tooling protocol session. The embedding
/// caller retains process lifetime and scheduling ownership; input and output streams are borrowed.
/// Clean EOF and an accepted exit notification are normal, distinguishable termination paths.
/// Framing, dispatch, notification-handler, output, and cancellation failures propagate fail closed.
/// </summary>
internal static class UiToolingProtocolTransportLoop
{
    public static async ValueTask<UiToolingProtocolLoopResult> RunAsync(
        Stream input,
        Stream output,
        UiToolingProtocolSession session,
        int maximumHeaderBytes = UiLspMessageStream.DefaultMaximumHeaderBytes,
        int maximumPayloadBytes = UiLspMessageStream.DefaultMaximumPayloadBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(session);
        using var messages = new UiLspMessageStream(
            input,
            output,
            maximumHeaderBytes,
            maximumPayloadBytes);
        if (session.ExitRequested)
            return new UiToolingProtocolLoopResult(UiToolingProtocolTermination.ExitRequested, 0);

        session.ConfigurePayloadLimit(maximumPayloadBytes);
        var dispatcher = new UiLspJsonRpcDispatcher(messages, session.HandleAsync);
        int processed = 0;
        while (!session.ExitRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await dispatcher.DispatchNextAsync(cancellationToken).ConfigureAwait(false))
                return new UiToolingProtocolLoopResult(
                    UiToolingProtocolTermination.EndOfStream,
                    processed);
            processed = checked(processed + 1);
        }

        return new UiToolingProtocolLoopResult(
            UiToolingProtocolTermination.ExitRequested,
            processed);
    }
}
