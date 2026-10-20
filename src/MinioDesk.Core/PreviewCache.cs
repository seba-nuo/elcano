using System.Data;

namespace MinioDesk.Core;

// UI-thread cache. Store previews, never credentials, full files or native databases.
public sealed class PreviewCache<T>(Func<T, long> sizeOf, long maxBytes = 16 * 1024 * 1024, int maxEntries = 12)
{
    private readonly Dictionary<string, LinkedListNode<(string Key, T Value, long Bytes)>> entries = new(StringComparer.Ordinal);
    private readonly LinkedList<(string Key, T Value, long Bytes)> recent = new();
    public long Bytes { get; private set; }
    public int Count => entries.Count;
    public bool TryGet(string key, out T value)
    {
        if (!entries.TryGetValue(key, out var node)) { value = default!; return false; }
        recent.Remove(node); recent.AddFirst(node); value = node.Value.Value; return true;
    }
    public void Put(string key, T value)
    {
        if (entries.Remove(key, out var old)) { recent.Remove(old); Bytes -= old.Value.Bytes; }
        var bytes = Math.Max(1, sizeOf(value));
        if (bytes > maxBytes || maxEntries < 1) return;
        while (recent.Last is { } last && (Bytes + bytes > maxBytes || entries.Count >= maxEntries))
        { entries.Remove(last.Value.Key); recent.RemoveLast(); Bytes -= last.Value.Bytes; }
        entries[key] = recent.AddFirst((key, value, bytes)); Bytes += bytes;
    }
    public void Clear() { entries.Clear(); recent.Clear(); Bytes = 0; }
}

public static class PreviewMemory
{
    public static long Estimate(ParquetPreview preview) => 1024 + (preview.Text?.Length ?? 0) * 2L + Estimate(preview.Rows) + Estimate(preview.Schema);
    private static long Estimate(DataTable table) => 1024 + table.Columns.Cast<DataColumn>().Sum(column => 256L + column.Caption.Length * 2L) +
        table.Rows.Cast<DataRow>().Sum(row => 128L + row.ItemArray.Sum(value => 64L + (value is string text ? text.Length * 2L : 8)));
}
