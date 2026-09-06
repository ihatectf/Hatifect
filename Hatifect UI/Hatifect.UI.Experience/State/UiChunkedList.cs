using System.Collections;

namespace Hatifect.UI.Experience;

/// <summary>Framework-owned immutable leaves. A new root retains leaves, never an older root.</summary>
internal sealed class UiChunkedList<T> : IReadOnlyList<T>
{
    private const int BlockSize = 128;
    private readonly T[][] _blocks;

    private UiChunkedList(T[][] blocks, int count) { _blocks = blocks; Count = count; }

    internal static UiChunkedList<T> Copy(IReadOnlyList<T> values)
    {
        if (values is UiChunkedList<T> captured) return captured;
        var blocks = new T[values.Count / BlockSize + (values.Count % BlockSize == 0 ? 0 : 1)][];
        for (int block = 0; block < blocks.Length; block++)
        {
            int start = block * BlockSize;
            var leaf = new T[Math.Min(BlockSize, values.Count - start)];
            for (int offset = 0; offset < leaf.Length; offset++) leaf[offset] = values[start + offset];
            blocks[block] = leaf;
        }
        return new(blocks, values.Count);
    }

    // Updates are sorted by index and contain each index once. Copy each touched leaf only once.
    internal UiChunkedList<T> With(IReadOnlyList<KeyValuePair<int, T>> updates)
    {
        if (updates.Count == 0) return this;
        var blocks = (T[][])_blocks.Clone();
        int previousIndex = -1, previousBlock = -1;
        for (int entry = 0; entry < updates.Count; entry++)
        {
            var update = updates[entry];
            int index = update.Key;
            if ((uint)index >= (uint)Count || index <= previousIndex)
                throw new ArgumentException("Updates must contain distinct ascending indices within the captured list.", nameof(updates));
            int block = index / BlockSize;
            if (block != previousBlock) blocks[block] = (T[])blocks[block].Clone();
            blocks[block][index % BlockSize] = update.Value;
            previousIndex = index;
            previousBlock = block;
        }
        return new(blocks, Count);
    }

    public int Count { get; }
    public T this[int index] => (uint)index < (uint)Count
        ? _blocks[index / BlockSize][index % BlockSize] : throw new ArgumentOutOfRangeException(nameof(index));
    public IEnumerator<T> GetEnumerator()
    {
        for (int index = 0; index < Count; index++) yield return this[index];
    }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
