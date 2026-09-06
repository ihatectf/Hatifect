using System.Buffers.Binary;

namespace Hatifect.UI.Runtime.Hosting;

/// <summary>UI-thread resource ownership with bounded admission and generation-safe leases.</summary>
internal sealed class UiSemanticTextureCatalog<T> : IDisposable where T : class, IDisposable
{
    private readonly Func<Stream, T> _decode;
    private readonly Func<T> _missing;
    private readonly Dictionary<UiSymbolId, Entry> _entries = new();
    private T? _fallback;
    private long _encoded;
    private long _decoded;
    private bool _disposed;

    internal UiSemanticTextureCatalog(Func<Stream, T> decode, Func<T> missing)
    { _decode = decode; _missing = missing; }

    internal IDisposable Register(UiSymbolId id, byte[] png)
    {
        EnsureActive();
        ArgumentNullException.ThrowIfNull(png);
        if (!id.IsValid) throw new ArgumentException("A texture ID is required.", nameof(id));
        if (_entries.ContainsKey(id)) throw new InvalidOperationException($"Texture '{id}' is already registered.");
        if (png.Length < 33 || png.Length > 4 * 1024 * 1024 ||
            !png.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ||
            BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(8, 4)) != 13 ||
            !png.AsSpan(12, 4).SequenceEqual(new byte[] { 73, 72, 68, 82 }))
            throw new ArgumentException("A bounded PNG image is required.", nameof(png));
        uint width = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16, 4));
        uint height = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(20, 4));
        if (width == 0 || height == 0 || width > 4096 || height > 4096)
            throw new ArgumentException("PNG dimensions must be between 1 and 4096.", nameof(png));
        long decoded = (long)width * height * 4;
        if (_entries.Count >= 256 || _encoded + png.Length > 16 * 1024 * 1024 || _decoded + decoded > 32 * 1024 * 1024)
            throw new InvalidOperationException("The session texture budget is exhausted.");
        using var stream = new MemoryStream((byte[])png.Clone(), writable: false);
        T texture = _decode(stream) ?? throw new InvalidOperationException("The texture decoder returned null.");
        var entry = new Entry(texture, png.Length, decoded);
        _entries.Add(id, entry);
        _encoded += entry.Encoded;
        _decoded += entry.Decoded;
        return new Lease(() => Release(id, entry));
    }

    internal T Resolve(UiSymbolId id)
    {
        EnsureActive();
        return _entries.TryGetValue(id, out Entry? entry) ? entry.Texture : _fallback ??= _missing();
    }
    public void Dispose()
    {
        if (_disposed) return;
        var failures = new List<Exception>();
        foreach (var pair in _entries.ToArray())
        {
            try { Release(pair.Key, pair.Value); } catch (Exception error) { failures.Add(error); }
        }
        try { _fallback?.Dispose(); _fallback = null; } catch (Exception error) { failures.Add(error); }
        _disposed = failures.Count == 0;
        if (failures.Count != 0) throw new AggregateException("Texture cleanup failed.", failures);
    }
    private void Release(UiSymbolId id, Entry entry)
    {
        if (!_entries.TryGetValue(id, out Entry? current) || !ReferenceEquals(current, entry)) return;
        entry.Texture.Dispose();
        _entries.Remove(id);
        _encoded -= entry.Encoded;
        _decoded -= entry.Decoded;
    }
    private void EnsureActive()
    { if (_disposed) throw new ObjectDisposedException(nameof(UiSemanticTextureCatalog<T>)); }
    private sealed record Entry(T Texture, int Encoded, long Decoded);
    private sealed class Lease : IDisposable
    {
        private Action? _release;
        internal Lease(Action release) => _release = release;
        public void Dispose() { _release?.Invoke(); _release = null; }
    }
}
