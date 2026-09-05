using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Hatifect.UI.Tooling.Protocol;
using Hatifect.UI.Tooling.Server;
using Xunit;

namespace Hatifect.UI.Tooling.Server.Tests;

public sealed class ToolingServerTests
{
    [Fact]
    public async Task AcceptedExitReturnsSuccessAndLeavesTrailingInputUnread()
    {
        using var input = await FramedInputAsync(
            "{\"jsonrpc\":\"2.0\",\"method\":\"exit\",\"params\":{}}",
            "{\"jsonrpc\":\"2.0\",\"method\":\"initialized\",\"params\":{}}");
        using var output = new MemoryStream();
        using var diagnostics = new StringWriter();

        int exitCode = await UiToolingServer.RunAsync(input, output, diagnostics);

        Assert.Equal(UiToolingServer.SuccessExitCode, exitCode);
        Assert.True(input.Position < input.Length);
        Assert.Equal(0, output.Length);
        Assert.Empty(diagnostics.ToString());
        Assert.True(input.CanRead);
        Assert.True(output.CanWrite);
    }

    [Fact]
    public async Task CleanEofReturnsSuccessWithoutClosingBorrowedStreams()
    {
        using var input = new MemoryStream();
        using var output = new MemoryStream();
        using var diagnostics = new StringWriter();

        int exitCode = await UiToolingServer.RunAsync(input, output, diagnostics);

        Assert.Equal(UiToolingServer.SuccessExitCode, exitCode);
        Assert.Empty(diagnostics.ToString());
        input.WriteByte(0);
        output.WriteByte(0);
        Assert.True(input.CanRead);
        Assert.True(output.CanWrite);
    }

    [Fact]
    public async Task CallerCancellationReturnsCanceledClassificationWithoutClosingBorrowedStreams()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var input = new MemoryStream();
        using var output = new MemoryStream();
        using var diagnostics = new StringWriter();

        int exitCode = await UiToolingServer.RunAsync(
            input,
            output,
            diagnostics,
            cancellation.Token);

        Assert.Equal(UiToolingServer.CanceledExitCode, exitCode);
        Assert.Empty(diagnostics.ToString());
        Assert.True(input.CanRead);
        Assert.True(output.CanWrite);
    }

    [Fact]
    public async Task ProcessLifetimeCancellationReturnsCanceledClassification()
    {
        using var lifetime = new UiToolingProcessLifetime();
        lifetime.RequestCancellation();
        using var input = new MemoryStream();
        using var output = new MemoryStream();
        using var diagnostics = new StringWriter();

        int exitCode = await UiToolingServer.RunAsync(
            input,
            output,
            diagnostics,
            lifetime.Token);

        Assert.Equal(UiToolingServer.CanceledExitCode, exitCode);
        Assert.Empty(diagnostics.ToString());
    }

    [Fact]
    public async Task UnexpectedCancellationIsClassifiedAsFatal()
    {
        using var input = new UnexpectedCancellationStream();
        using var output = new MemoryStream();
        using var diagnostics = new StringWriter();

        int exitCode = await UiToolingServer.RunAsync(input, output, diagnostics);

        Assert.Equal(UiToolingServer.FatalExitCode, exitCode);
        Assert.NotEmpty(diagnostics.ToString());
        Assert.Equal(0, output.Length);
        Assert.True(input.CanRead);
        Assert.True(output.CanWrite);
    }

    [Fact]
    public async Task SyntheticTransportFailurePreservesExceptionDetailsOnStderrOnly()
    {
        using var input = new SyntheticFailureStream();
        using var output = new MemoryStream();
        using var diagnostics = new StringWriter();

        int exitCode = await UiToolingServer.RunAsync(input, output, diagnostics);

        Assert.Equal(UiToolingServer.FatalExitCode, exitCode);
        Assert.StartsWith(UiToolingServer.FatalDiagnosticPrefix, diagnostics.ToString());
        Assert.Contains(typeof(SyntheticTransportException).FullName!, diagnostics.ToString());
        Assert.Contains("Synthetic transport failure.", diagnostics.ToString());
        Assert.Equal(0, output.Length);
        Assert.True(input.CanRead);
        Assert.True(output.CanWrite);
    }

    [Fact]
    public async Task MalformedTransportReturnsFatalToStderrWithoutCorruptingProtocolOutput()
    {
        using var input = new MemoryStream(
            Encoding.ASCII.GetBytes("Content-Length: 10\r\n\r\n{}"));
        using var output = new MemoryStream();
        using var diagnostics = new StringWriter();

        int exitCode = await UiToolingServer.RunAsync(input, output, diagnostics);

        Assert.Equal(UiToolingServer.FatalExitCode, exitCode);
        Assert.NotEmpty(diagnostics.ToString());
        Assert.Equal(0, output.Length);
        Assert.True(input.CanRead);
        Assert.True(output.CanWrite);
    }

    [Fact]
    public async Task DiagnosticFailureDoesNotChangeFatalClassification()
    {
        using var input = new MemoryStream(
            Encoding.ASCII.GetBytes("Content-Length: 10\r\n\r\n{}"));
        using var output = new MemoryStream();
        using var diagnostics = new ThrowingTextWriter();

        int exitCode = await UiToolingServer.RunAsync(input, output, diagnostics);

        Assert.Equal(UiToolingServer.FatalExitCode, exitCode);
        Assert.Equal(0, output.Length);
    }

    private static async Task<MemoryStream> FramedInputAsync(params string[] payloads)
    {
        var stream = new MemoryStream();
        using (var messages = new UiLspMessageStream(Stream.Null, stream))
        {
            foreach (string payload in payloads)
                await messages.WriteFrameAsync(Encoding.UTF8.GetBytes(payload));
        }

        stream.Position = 0;
        return stream;
    }

    private sealed class UnexpectedCancellationStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => 0;

        public override long Position
        {
            get => 0;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
            => throw new OperationCanceledException();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => ValueTask.FromException<int>(new OperationCanceledException());

        public override long Seek(long offset, SeekOrigin origin)
            => throw new NotSupportedException();

        public override void SetLength(long value)
            => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();
    }

    private sealed class ThrowingTextWriter : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;

        public override Task WriteLineAsync(string? value)
            => Task.FromException(new IOException("Diagnostic output is unavailable."));
    }

    private sealed class SyntheticFailureStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => 0;

        public override long Position
        {
            get => 0;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
            => throw new SyntheticTransportException("Synthetic transport failure.");

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
            => ValueTask.FromException<int>(
                new SyntheticTransportException("Synthetic transport failure."));

        public override long Seek(long offset, SeekOrigin origin)
            => throw new NotSupportedException();

        public override void SetLength(long value)
            => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();
    }

    private sealed class SyntheticTransportException : IOException
    {
        public SyntheticTransportException(string message)
            : base(message)
        {
        }
    }
}
