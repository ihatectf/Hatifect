using System.IO.Pipes;

namespace Hatifect.UI.Tooling.Server.Tests;

/// <summary>A cross-platform, asynchronously cancellable duplex transport for in-process protocol tests.</summary>
internal sealed class ToolingTestDuplexPipe : IDisposable
{
    private ToolingTestDuplexPipe(NamedPipeServerStream server, NamedPipeClientStream client)
    {
        Server = server;
        Client = client;
    }

    public NamedPipeServerStream Server { get; }
    public NamedPipeClientStream Client { get; }

    public static async Task<ToolingTestDuplexPipe> ConnectAsync(CancellationToken cancellationToken)
    {
        // .NET maps named pipes to Unix-domain sockets off Windows, where the
        // complete temporary socket path is limited to 104 characters.
        string name = "hf-" + Guid.NewGuid().ToString("N")[..12];
        var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            Task accepting = server.WaitForConnectionAsync(cancellationToken);
            await client.ConnectAsync(cancellationToken);
            await accepting;
            return new ToolingTestDuplexPipe(server, client);
        }
        catch
        {
            client.Dispose();
            server.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        Client.Dispose();
        Server.Dispose();
    }
}
