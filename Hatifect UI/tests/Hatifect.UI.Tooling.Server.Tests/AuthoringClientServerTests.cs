using System.IO.Pipes;
using System.Text.Json;
using Hatifect.UI.Semantics;
using Hatifect.UI.Tooling.Client;
using Hatifect.UI.Tooling.Metadata;
using Hatifect.UI.Tooling.Server;
using Xunit;

namespace Hatifect.UI.Tooling.Server.Tests;

public sealed class AuthoringClientServerTests
{
    [Fact]
    public async Task PublicClientPreservesServerRejectionOfUnsupportedCapabilityAndCannotBeReused()
    {
        var bindings = new UiBindingContext(new UiSymbolId("External.Author", "storage")).DeclareRole("Item");
        using var metadata = JsonDocument.Parse(UiBindingContextJson.Export(bindings));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var serverInput = new AnonymousPipeServerStream(PipeDirection.In);
        using var clientOutput = new AnonymousPipeClientStream(PipeDirection.Out, serverInput.GetClientHandleAsString());
        using var serverOutput = new AnonymousPipeServerStream(PipeDirection.Out);
        using var clientInput = new AnonymousPipeClientStream(PipeDirection.In, serverOutput.GetClientHandleAsString());
        using var errors = new StringWriter();
        Task<int> server = UiToolingServer.RunAsync(serverInput, serverOutput, errors, deadline.Token).AsTask();
        using var client = new UiToolingClient(clientInput, clientOutput);
        try
        {
            UiToolingRequestException rejection = await Assert.ThrowsAsync<UiToolingRequestException>(() =>
                client.InitializeAsync(metadata.RootElement, new[] { "unknown" }, cancellationToken: deadline.Token));
            Assert.Equal(-32602, rejection.Code);
            Assert.Contains("Unsupported tooling capability 'unknown'", rejection.Message, StringComparison.Ordinal);
            Assert.False(client.Supports("compilation"));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                client.InitializeAsync(metadata.RootElement, new[] { "compilation" }, cancellationToken: deadline.Token));
        }
        finally
        {
            deadline.Cancel();
            Assert.Equal(UiToolingServer.CanceledExitCode, await server);
        }
        Assert.Empty(errors.ToString());
    }

    [Fact]
    public async Task PublicClientAgreesWithCompilerAcrossInvalidEditAndReopen()
    {
        var bindings = new UiBindingContext(new UiSymbolId("External.Author", "storage")).DeclareRole("Item");
        using var metadata = JsonDocument.Parse(UiBindingContextJson.Export(bindings));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var serverInput = new AnonymousPipeServerStream(PipeDirection.In);
        using var clientOutput = new AnonymousPipeClientStream(PipeDirection.Out, serverInput.GetClientHandleAsString());
        using var serverOutput = new AnonymousPipeServerStream(PipeDirection.Out);
        using var clientInput = new AnonymousPipeClientStream(PipeDirection.In, serverOutput.GetClientHandleAsString());
        using var errors = new StringWriter();
        Task<int> server = UiToolingServer.RunAsync(serverInput, serverOutput, errors, deadline.Token).AsTask();
        using var client = new UiToolingClient(clientInput, clientOutput);
        try
        {
            await client.InitializeAsync(metadata.RootElement, new[] { "compilation", "diagnostics", "plannerTrace" }, 7, deadline.Token);
            Assert.True(client.Supports("plannerTrace"));
            Assert.False(client.Supports("unknown"));
            const string uri = "file:///Storage.hatifect";
            string[] sources = { "visual Storage\nItem\n    opacity = 1", "visual Storage\nItem\n    opacity = 2", "visual Storage\nItem\n    opacity = 0.5" };
            string? priorId = null;
            for (int index = 0; index < sources.Length; index++)
            {
                if (index == 2) await client.CloseAsync(uri, deadline.Token);
                await client.SynchronizeAsync(uri, index + 1, sources[index], deadline.Token);
                UiToolingCompilation actual = await client.CompileAsync(uri, deadline.Token);
                UiCompilationResult expected = new UiCompiler().Compile(sources[index], bindings, uri);
                Assert.Equal(expected.IsValid, actual.IsValid);
                Assert.Equal(uri, actual.Uri);
                Assert.Equal(index + 1, actual.Version);
                Assert.Equal(7, actual.BindingRevision);
                Assert.NotEqual(priorId, actual.ResultId);
                Assert.Equal(expected.Diagnostics.Select(d => d.Id), actual.Diagnostics.EnumerateArray().Select(d => d.GetProperty("code").GetString()));
                priorId = actual.ResultId;
            }
            await client.ShutdownAsync(deadline.Token);
            Assert.False(client.Supports("compilation"));
            Assert.Equal(0, await server.WaitAsync(deadline.Token));
            Assert.Empty(errors.ToString());
        }
        finally
        {
            deadline.Cancel();
            await server;
        }
    }
}
