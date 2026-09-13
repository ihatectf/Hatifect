using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Hatifect.UI.Tooling.Protocol;
using Xunit;

namespace Hatifect.UI.Tooling.Tests;

public sealed class LspMessageStreamTests
{
    [Fact]
    public async Task FragmentedUtf8FramesRoundTripAndEndCleanly()
    {
        byte[] payload = Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"method\":\"hatifect/привет\"}");
        using var wire = new MemoryStream();
        using (var writer = new UiLspMessageStream(Stream.Null, wire))
            await writer.WriteFrameAsync(payload);
        wire.Position = 0;
        using var input = new FragmentedReadStream(wire, maximumChunk: 1);
        using var reader = new UiLspMessageStream(input, Stream.Null);

        byte[] actual = Assert.IsType<byte[]>(await reader.ReadFrameAsync());

        Assert.Equal(payload, actual);
        Assert.Null(await reader.ReadFrameAsync());
    }

    [Fact]
    public async Task ConcurrentWritesRemainWholeFrames()
    {
        byte[][] payloads =
        {
            Encoding.UTF8.GetBytes("{\"id\":1}"),
            Encoding.UTF8.GetBytes("{\"id\":2}"),
            Encoding.UTF8.GetBytes("{\"id\":3}")
        };
        using var wire = new MemoryStream();
        using (var writer = new UiLspMessageStream(Stream.Null, wire))
            await Task.WhenAll(payloads.Select(payload => writer.WriteFrameAsync(payload).AsTask()));
        wire.Position = 0;
        using var reader = new UiLspMessageStream(wire, Stream.Null);

        byte[][] frames =
        {
            Assert.IsType<byte[]>(await reader.ReadFrameAsync()),
            Assert.IsType<byte[]>(await reader.ReadFrameAsync()),
            Assert.IsType<byte[]>(await reader.ReadFrameAsync())
        };

        Assert.Equal(
            payloads.Select(payload => Encoding.UTF8.GetString(payload))
                .OrderBy(value => value, StringComparer.Ordinal),
            frames.Select(payload => Encoding.UTF8.GetString(payload))
                .OrderBy(value => value, StringComparer.Ordinal));
        Assert.Null(await reader.ReadFrameAsync());
    }

    [Theory]
    [InlineData("Content-Type: application/vscode-jsonrpc; charset=utf-8\r\n\r\n{}")]
    [InlineData("Content-Length: 0\r\n\r\n")]
    [InlineData("Content-Length: no\r\n\r\n{}")]
    [InlineData("Content-Length: 2\r\nContent-Length: 2\r\n\r\n{}")]
    public async Task InvalidLengthHeadersFailClosed(string frame)
    {
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(frame));
        using var stream = new UiLspMessageStream(input, Stream.Null);

        await Assert.ThrowsAsync<InvalidDataException>(() => stream.ReadFrameAsync().AsTask());
    }

    [Fact]
    public async Task OversizedAndTruncatedFramesFailBeforeReturningPayload()
    {
        using var oversizedInput = new MemoryStream(Encoding.ASCII.GetBytes("Content-Length: 5\r\n\r\n12345"));
        using var oversized = new UiLspMessageStream(
            oversizedInput,
            Stream.Null,
            maximumPayloadBytes: 4);
        await Assert.ThrowsAsync<InvalidDataException>(() => oversized.ReadFrameAsync().AsTask());

        using var truncatedInput = new MemoryStream(Encoding.ASCII.GetBytes("Content-Length: 5\r\n\r\n12"));
        using var truncated = new UiLspMessageStream(truncatedInput, Stream.Null);
        await Assert.ThrowsAsync<EndOfStreamException>(() => truncated.ReadFrameAsync().AsTask());
    }

    [Fact]
    public async Task HeaderLimitAndDisposedOwnerDoNotAffectBorrowedStreams()
    {
        byte[] frame = Encoding.ASCII.GetBytes("Long-Header: value\r\nContent-Length: 2\r\n\r\n{}");
        using var input = new MemoryStream(frame);
        using var output = new MemoryStream();
        var stream = new UiLspMessageStream(input, output, maximumHeaderBytes: 16);

        await Assert.ThrowsAsync<InvalidDataException>(() => stream.ReadFrameAsync().AsTask());
        stream.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => stream.WriteFrameAsync(new byte[] { 1 }).AsTask());

        input.Position = 0;
        Assert.NotEqual(-1, input.ReadByte());
        output.WriteByte(1);
    }

    private sealed class FragmentedReadStream : Stream
    {
        private readonly Stream _inner;
        private readonly int _maximumChunk;

        public FragmentedReadStream(Stream inner, int maximumChunk)
        {
            _inner = inner;
            _maximumChunk = maximumChunk;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
            => _inner.Read(buffer, offset, Math.Min(count, _maximumChunk));

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
            => _inner.ReadAsync(buffer[..Math.Min(buffer.Length, _maximumChunk)], cancellationToken);

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
