using Elcano.Core;

namespace Elcano.UiTests;

internal sealed class FakeStorage : IS3Service
{
    public Dictionary<string, byte[]> Objects { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> Calls { get; } = new(StringComparer.Ordinal);
    public Task<IReadOnlyList<string>> ListBucketsAsync(ConnectionProfile profile, CancellationToken token) => Task.FromResult<IReadOnlyList<string>>(["demo", "otro"]);
    public Task<ObjectPage> ListAsync(ConnectionProfile profile, string bucket, string prefix, string? continuation, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); Calls[prefix] = Calls.GetValueOrDefault(prefix) + 1;
        if (prefix == "denegado/") throw new UnauthorizedAccessException("test-secret SQL SELECT private");
        var all = Level(prefix); var offset = int.Parse(continuation ?? "0");
        var entries = all.Skip(offset).Take(200).OrderByDescending(item => item.IsFolder).ThenBy(item => item.Name, StringComparer.Ordinal).ToArray();
        return Task.FromResult(new ObjectPage(entries, offset + entries.Length < all.Count ? (offset + entries.Length).ToString() : null));
    }
    public Task<FolderTotals> CountFolderAsync(ConnectionProfile profile, string bucket, string prefix, CancellationToken token)
    { var all = Level(prefix); return Task.FromResult(new FolderTotals(all.Count(item => !item.IsFolder), all.Count(item => item.IsFolder), Math.Max(1, (all.Count + 199) / 200))); }
    private List<ObjectEntry> Level(string prefix)
    {
        var entries = new Dictionary<string, ObjectEntry>(StringComparer.Ordinal);
        foreach (var key in Objects.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)))
        {
            var relative = key[prefix.Length..]; if (relative.Length == 0) continue;
            var slash = relative.IndexOf('/'); var folder = slash >= 0; var name = folder ? relative[..slash] : relative;
            var path = prefix + name + (folder ? "/" : ""); entries[path] = new(path, name, folder, 1, DateTime.UnixEpoch);
        }
        return entries.Values.OrderBy(item => item.Key, StringComparer.Ordinal).ToList();
    }
    public Task UploadAsync(ConnectionProfile profile, string bucket, string key, string path, IProgress<TransferProgress>? progress, CancellationToken token) => throw new NotSupportedException();
    public Task DownloadAsync(ConnectionProfile profile, string bucket, string key, string path, IProgress<TransferProgress>? progress, CancellationToken token) => throw new NotSupportedException();
    public Task DeleteAsync(ConnectionProfile profile, string bucket, string key, CancellationToken token) => throw new NotSupportedException();
}
