using System;
using System.Buffers;
using System.IO;

namespace Hatifect.Flow.Infrastructure.Persistence;

// Utf8JsonWriter(Stream) buffers its whole document before touching the stream.
// Supplying the buffer directly enforces the cap before any requested growth.
internal sealed class CheckpointBuffer : IBufferWriter<byte>
{
    private readonly int _limit;
    private byte[] _bytes;
    private int _written;

    internal CheckpointBuffer(int limit)
    {
        if (limit <= 0) throw new ArgumentOutOfRangeException(nameof(limit));
        _limit = limit;
        _bytes = new byte[Math.Min(limit, 16384)];
    }

    internal ReadOnlySpan<byte> WrittenSpan => _bytes.AsSpan(0, _written);

    public void Advance(int count)
    {
        if (count < 0 || count > _bytes.Length - _written)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }
        _written += count;
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        EnsureSpace(sizeHint);
        return _bytes.AsMemory(_written);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        EnsureSpace(sizeHint);
        return _bytes.AsSpan(_written);
    }

    private void EnsureSpace(int sizeHint)
    {
        if (sizeHint < 0) throw new ArgumentOutOfRangeException(nameof(sizeHint));
        int needed = Math.Max(sizeHint, 1);
        if (needed > _limit - _written)
        {
            throw new InvalidDataException("Checkpoint JSON exceeds the supported image size.");
        }
        if (needed > _bytes.Length - _written)
        {
            int capacity = (int)Math.Min(_limit, Math.Max((long)_bytes.Length * 2, (long)_written + needed));
            Array.Resize(ref _bytes, capacity);
        }
    }
}
