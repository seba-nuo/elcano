using System.Text.Json;

namespace Elcano.Core;

public sealed record ExplorerLocation(Guid ProfileId, string Endpoint, string Region, string Bucket, string Prefix)
{
    public static ExplorerLocation For(ConnectionProfile profile, string bucket, string prefix)
        => new(profile.Id, profile.Endpoint, profile.Region, bucket, prefix);
}

// Accessed on the UI thread, including continuations of background listings.
public sealed class ExplorerCache(long maxBytes = 8 * 1024 * 1024, int maxEntries = 128)
{
    private sealed record Listing(ObjectPage? Page, FolderTotals? Totals, long KeyBytes, DateTimeOffset Updated);
    private readonly PreviewCache<Listing> cache = new(Estimate, maxBytes, maxEntries);
    public long Generation { get; private set; }
    public long Bytes => cache.Bytes;
    public int Count => cache.Count;

    public bool TryGetPage(ExplorerLocation location, string? continuation, out ObjectPage page)
        => TryGetPage(location, continuation, out page, out _);
    public bool TryGetPage(ExplorerLocation location, string? continuation, out ObjectPage page, out DateTimeOffset updated)
    {
        if (cache.TryGet(Key(location, continuation), out var listing) && listing.Page is { } found)
        { page = found; updated = listing.Updated; return true; }
        page = null!; updated = default; return false;
    }

    public bool StorePage(ExplorerLocation location, string? continuation, ObjectPage page, long generation)
    {
        if (generation != Generation) return false;
        var key = Key(location, continuation);
        var totals = cache.TryGet(key, out var existing) ? existing.Totals : null;
        if (continuation is null && page.NextToken is null)
            totals = new FolderTotals(page.Entries.LongCount(item => !item.IsFolder), page.Entries.LongCount(item => item.IsFolder), 1);
        cache.Put(key, new Listing(page, totals, key.Length * 2L, DateTimeOffset.UtcNow)); return true;
    }

    public bool TryGetTotals(ExplorerLocation location, out FolderTotals totals)
    {
        if (cache.TryGet(Key(location, null), out var listing) && listing.Totals is { } found)
        { totals = found; return true; }
        totals = null!; return false;
    }

    public void StoreTotals(ExplorerLocation location, FolderTotals totals, long generation)
    {
        if (generation != Generation) return;
        var key = Key(location, null);
        var present = cache.TryGet(key, out var existing);
        cache.Put(key, new Listing(present ? existing.Page : null, totals, key.Length * 2L, present ? existing.Updated : DateTimeOffset.UtcNow));
    }

    public void Clear() { Generation++; cache.Clear(); }

    private static string Key(ExplorerLocation location, string? continuation)
        => JsonSerializer.Serialize(new[] { location.ProfileId.ToString(), location.Endpoint, location.Region, location.Bucket, location.Prefix, continuation });

    private static long Estimate(Listing listing) => 2048 + listing.KeyBytes + (listing.Page?.NextToken?.Length ?? 0) * 2L +
        (listing.Page?.Entries.Sum(item => 256L + (item.Key.Length + item.Name.Length) * 2L) ?? 0);
}
