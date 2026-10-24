using System.Collections.ObjectModel;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Data;
using Amazon.S3;
using Microsoft.Win32;
using MinioDesk.Core;

namespace MinioDesk.App;

public sealed partial class MainViewModel
{
    private async Task ReloadAsync(CancellationToken token)
    {
        ExplorerPosition? position = null;
        if (SelectedProfile is { } profile && SelectedBucket is { } bucket) explorerPositions.TryGet(PositionKey(profile, bucket, prefix), out position);
        ClearPreview(); ClearPage(); NotifyLocation();
        Filter = position?.Filter ?? "";
        if (position is not null) { pageIndex = position.Page; pageTokens.Clear(); pageTokens.AddRange(position.Tokens); }
        var page = await LoadPageAsync(SelectedProfile!, SelectedBucket!, prefix, pageTokens[pageIndex], token); SetPage(page);
        StartFolderCount(page);
        if (position is not null) { SelectedEntry = Entries.FirstOrDefault(entry => entry.Key == position.SelectedKey); ExplorerScrollOffset = position.Offset; }
    }

    public void RememberExplorerScroll(double offset) { if (!IsBusy) explorerScrollOffset = Math.Max(0, offset); }
    private static string PositionKey(ConnectionProfile profile, string bucket, string folder) => JsonSerializer.Serialize(ExplorerLocation.For(profile, bucket, folder));
    private async Task NavigateFolderAsync(string target, CancellationToken token)
    {
        explorerPositions.Put(PositionKey(SelectedProfile!, SelectedBucket!, prefix), new ExplorerPosition(pageIndex, pageTokens.ToArray(), SelectedEntry?.Key, explorerScrollOffset, Filter));
        prefix = target; await ReloadAsync(token);
    }

    private async Task<ObjectPage> LoadPageAsync(ConnectionProfile profile, string bucket, string folder, string? continuation, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var location = ExplorerLocation.For(profile, bucket, folder);
        if (explorerCache.TryGetPage(location, continuation, out var cached, out var updated)) { SetCacheLabel(true, updated); return cached; }
        var generation = explorerCache.Generation;
        IsExplorerLoading = true;
        try
        {
            var page = await s3.ListAsync(profile, bucket, folder, continuation, token);
            token.ThrowIfCancellationRequested();
            explorerCache.StorePage(location, continuation, page, generation);
            SetCacheLabel(false, DateTimeOffset.Now);
            return page;
        }
        finally { IsExplorerLoading = false; }
    }

    private void QueuePrefetch(ObjectPage page, string parent)
    {
        foreach (var entry in page.Entries)
        {
            // Bound both traversal and retained names, even in buckets with millions of folders.
            if (prefetchFolders.Count >= 64) break;
            if (entry.IsFolder && entry.Key.Length > parent.Length && entry.Key.StartsWith(parent, StringComparison.Ordinal) &&
                entry.Key.EndsWith('/') && prefetchFolders.Add(entry.Key)) prefetchQueue.Enqueue(entry.Key);
        }
    }

    private void ResumePrefetch()
    {
        if (backgroundStopped || !PrefetchEnabled || IsBusy || !connected || SelectedProfile is not { } profile || SelectedBucket is not { } bucket ||
            prefetchQueue.Count == 0 || prefetchOperation is not null) return;
        var cancellation = new CancellationTokenSource(); prefetchOperation = cancellation;
        _ = PrefetchAsync(profile, bucket, explorerCache.Generation, cancellation);
    }

    private async Task PrefetchAsync(ConnectionProfile profile, string bucket, long generation, CancellationTokenSource cancellation)
    {
        try
        {
            while (prefetchQueue.Count > 0 && generation == explorerCache.Generation)
            {
                var folder = prefetchQueue.Peek();
                cancellation.Token.ThrowIfCancellationRequested();
                var location = ExplorerLocation.For(profile, bucket, folder);
                ObjectPage? page = null;
                try
                {
                    if (!explorerCache.TryGetPage(location, null, out page))
                    {
                        await Task.Delay(TimeSpan.FromSeconds(2), cancellation.Token);
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
                        timeout.CancelAfter(TimeSpan.FromSeconds(10));
                        page = await s3.ListAsync(profile, bucket, folder, null, timeout.Token);
                        cancellation.Token.ThrowIfCancellationRequested();
                        explorerCache.StorePage(location, null, page, generation);
                    }
                    cancellation.Token.ThrowIfCancellationRequested();
                }
                catch (Exception exception)
                { cancellation.Token.ThrowIfCancellationRequested(); diagnostics.Record(exception, "Prefetch"); page = null; }
                cancellation.Token.ThrowIfCancellationRequested();
                if (generation != explorerCache.Generation) return;
                prefetchQueue.Dequeue();
                if (page is not null) QueuePrefetch(page, folder);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (ReferenceEquals(prefetchOperation, cancellation)) prefetchOperation = null;
            cancellation.Dispose(); ResumePrefetch();
        }
    }

    private void NotifyLocation() { Notify(nameof(Location)); Notify(nameof(FolderPath)); Notify(nameof(Breadcrumbs)); Notify(nameof(Ancestors)); }
    private void NotifyConnection() { Notify(nameof(ConnectionState)); Notify(nameof(IsConnected)); Notify(nameof(ExplorerEmptyMessage)); }
    private void SetCacheLabel(bool cached, DateTimeOffset updated)
    { ExplorerCacheLabel = $"{(cached ? "Caché · " : "")}{updated.ToLocalTime():HH:mm:ss}"; ExplorerCacheDetails = $"{(cached ? "Listado recuperado de memoria" : "Listado consultado al servidor")}. Actualizado: {updated.ToLocalTime():g}. Pulsa Actualizar para recoger cambios externos."; }
    private void NotifyTotals() { Notify(nameof(PageLabel)); Notify(nameof(FolderCountLabel)); }
    private void CancelFolderCount()
    {
        folderCountOperation?.Cancel(); folderCountOperation = null; folderTotals = null; isCountingFolder = false; NotifyTotals();
    }
    private void StartFolderCount(ObjectPage firstPage)
    {
        CancelFolderCount();
        var location = ExplorerLocation.For(SelectedProfile!, SelectedBucket!, prefix);
        if (explorerCache.TryGetTotals(location, out var cached))
        { folderTotals = cached; NotifyTotals(); return; }
        if (firstPage.NextToken is null && pageIndex == 0)
        {
            folderTotals = new FolderTotals(firstPage.Entries.LongCount(entry => !entry.IsFolder), firstPage.Entries.LongCount(entry => entry.IsFolder), 1);
            explorerCache.StoreTotals(location, folderTotals, explorerCache.Generation);
            NotifyTotals(); return;
        }
        var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        folderCountOperation = cancellation; isCountingFolder = true; NotifyTotals();
        _ = CountFolderAsync(SelectedProfile!, SelectedBucket!, prefix, explorerCache.Generation, cancellation);
    }
    private async Task CountFolderAsync(ConnectionProfile profile, string bucket, string folder, long generation, CancellationTokenSource cancellation)
    {
        try
        {
            var totals = await s3.CountFolderAsync(profile, bucket, folder, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            explorerCache.StoreTotals(ExplorerLocation.For(profile, bucket, folder), totals, generation);
            if (ReferenceEquals(folderCountOperation, cancellation)) folderTotals = totals;
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { diagnostics.Record(exception, "FolderCount"); }
        finally
        {
            if (ReferenceEquals(folderCountOperation, cancellation)) { folderCountOperation = null; isCountingFolder = false; NotifyTotals(); }
            cancellation.Dispose();
        }
    }
    private void ClearPage() { CancelFolderCount(); pageIndex = 0; nextToken = null; pageTokens.Clear(); pageTokens.Add(null); pageEntries = []; Entries.Clear(); SelectedEntry = null; Notify(nameof(PageLabel)); }
    private void SetPage(ObjectPage page) { pageEntries = page.Entries; nextToken = page.NextToken; SelectedEntry = null; ExplorerScrollOffset = 0; ApplyFilter(); QueuePrefetch(page, prefix); NotifyLocation(); Notify(nameof(PageLabel)); Status = page.Entries.Count == 0 ? "Esta carpeta está vacía." : $"{page.Entries.Count} elementos en esta página."; }
    private void ApplyFilter() => Entries = new ObservableCollection<ObjectEntry>(pageEntries.Where(item => item.Name.Contains(Filter, StringComparison.OrdinalIgnoreCase)));

    private void InvalidateCache()
    {
        previewCache.Clear(); currentCacheKey = null;
        prefetchQueue.Clear(); prefetchFolders.Clear(); explorerCache.Clear(); explorerPositions.Clear(); ExplorerCacheLabel = ""; ExplorerCacheDetails = ""; CancelFolderCount(); prefetchOperation?.Cancel();
    }

    public void OpenSelected()
    {
        if (!CanBrowse() || SelectedEntry is not { } entry) return;
        if (entry.IsFolder) _ = RunAsync("Abriendo carpeta…", token => NavigateFolderAsync(entry.Key, token));
        else if (CanPreview(entry)) _ = PreviewSelectedAsync();
        else Status = "El visor admite archivos Parquet, CSV y TXT. Puedes descargar otros formatos con clic secundario.";
    }

    public void NavigateToPrefix(string target)
    {
        if (!CanBrowse() || target == prefix || !prefix.StartsWith(target, StringComparison.Ordinal) || (target.Length > 0 && !target.EndsWith('/'))) return;
        _ = RunAsync("Abriendo carpeta…", token => NavigateFolderAsync(target, token));
    }
}
