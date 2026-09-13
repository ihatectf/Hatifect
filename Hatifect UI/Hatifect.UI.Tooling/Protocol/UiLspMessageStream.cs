using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Hatifect.UI.Tooling.Protocol;

/// <summary>
/// Internal bounded LSP Content-Length framing over borrowed streams. JSON-RPC method dispatch and
/// Experience binding-context discovery remain separate product concerns.
/// </summary>
internal sealed class UiLspMessageStream : IDisposable
{
    public const int DefaultMaximumHeaderBytes = 8 * 1024;
    public const int DefaultMaximumPayloadBytes = 4 * 1024 * 1024;

    private static readonly byte[] HeaderTerminator = { 13, 10, 13, 10 };
    private readonly Stream _input;
    private readonly Stream _output;
    private readonly byte[] _headerBuffer;
    private readonly SemaphoreSlim _readGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private int _disposed;

    public UiLspMessageStream(
        Stream input,
        Stream output,
        int maximumHeaderBytes = DefaultMaximumHeaderBytes,
        int maximumPayloadBytes = DefaultMaximumPayloadBytes)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _output = output ?? throw new ArgumentNullException(nameof(output));
        if (!input.CanRead) throw new ArgumentException("The LSP input stream must be readable.", nameof(input));
        if (!output.CanWrite) throw new ArgumentException("The LSP output stream must be writable.", nameof(output));
        if (maximumHeaderBytes < HeaderTerminator.Length)
            throw new ArgumentOutOfRangeException(nameof(maximumHeaderBytes));
        if (maximumPayloadBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumPayloadBytes));
        MaximumHeaderBytes = maximumHeaderBytes;
        MaximumPayloadBytes = maximumPayloadBytes;
        _headerBuffer = new byte[maximumHeaderBytes];
    }

    public int MaximumHeaderBytes { get; }
    public int MaximumPayloadBytes { get; }

    public async ValueTask<byte[]?> ReadFrameAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _readGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            int headerLength = await ReadHeaderAsync(_headerBuffer, cancellationToken).ConfigureAwait(false);
            if (headerLength == 0) return null;
            int contentLength = ParseContentLength(_headerBuffer, headerLength);
            byte[] payload = new byte[contentLength];
            int offset = 0;
            while (offset < payload.Length)
            {
                int read = await _input.ReadAsync(
                    payload.AsMemory(offset, payload.Length - offset),
                    cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    throw new EndOfStreamException("The LSP payload ended before Content-Length bytes were read.");
                offset += read;
            }
            return payload;
        }
        finally
        {
            _readGate.Release();
        }
    }

    public async ValueTask WriteFrameAsync(
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidatePayloadLength(payload.Length);
        byte[] header = Encoding.ASCII.GetBytes(
            $"Content-Length: {payload.Length.ToString(CultureInfo.InvariantCulture)}\r\n\r\n");
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await _output.WriteAsync(header.AsMemory(), cancellationToken).ConfigureAwait(false);
            await _output.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            await _output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public void Dispose()
        => Interlocked.Exchange(ref _disposed, 1);

    private async ValueTask<int> ReadHeaderAsync(byte[] header, CancellationToken cancellationToken)
    {
        int length = 0;
        int matched = 0;
        while (true)
        {
            int read = await _input.ReadAsync(header.AsMemory(length, 1), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                if (length == 0) return 0;
                throw new EndOfStreamException("The LSP header ended before its CRLF terminator.");
            }

            byte value = header[length++];
            matched = value == HeaderTerminator[matched]
                ? matched + 1
                : value == HeaderTerminator[0] ? 1 : 0;
            if (matched == HeaderTerminator.Length) return length;
            if (length == header.Length)
                throw new InvalidDataException(
                    $"The LSP header exceeds the {MaximumHeaderBytes} byte limit.");
        }
    }

    private int ParseContentLength(byte[] header, int headerLength)
    {
        int contentLength = -1;
        int bodyLength = headerLength - HeaderTerminator.Length;
        for (int index = 0; index < bodyLength; index++)
        {
            if (header[index] > 127)
                throw new InvalidDataException("LSP headers must contain ASCII characters only.");
        }

        string text = Encoding.ASCII.GetString(header, 0, bodyLength);
        foreach (string line in text.Split(new[] { "\r\n" }, StringSplitOptions.None))
        {
            int separator = line.IndexOf(':');
            if (separator <= 0)
                throw new InvalidDataException("Each LSP header line must contain a field name and value.");
            string name = line[..separator].Trim();
            string value = line[(separator + 1)..].Trim();
            if (!string.Equals(name, "Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
            if (contentLength >= 0)
                throw new InvalidDataException("The LSP frame contains duplicate Content-Length headers.");
            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out contentLength))
                throw new InvalidDataException("The LSP Content-Length header is not a non-negative integer.");
        }

        if (contentLength < 0)
            throw new InvalidDataException("The LSP frame has no Content-Length header.");
        ValidatePayloadLength(contentLength);
        return contentLength;
    }

    private void ValidatePayloadLength(int length)
    {
        if (length <= 0)
            throw new InvalidDataException("LSP payloads must contain at least one byte.");
        if (length > MaximumPayloadBytes)
            throw new InvalidDataException(
                $"The LSP payload exceeds the {MaximumPayloadBytes} byte limit.");
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(UiLspMessageStream));
    }
}
