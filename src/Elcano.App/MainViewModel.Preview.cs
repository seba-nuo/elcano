using System.Collections.ObjectModel;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Data;
using Amazon.S3;
using Microsoft.Win32;
using Elcano.Core;

namespace Elcano.App;

public sealed partial class MainViewModel
{
    private void SaveCurrentView()
    {
        if (currentCacheKey is not null && currentPreview is not null)
            previewCache.Put(currentCacheKey, new CachedView(currentPreview, QueryText, LastExecutedQuery, LastAttemptedQuery, QueryError, PreviewSummary));
    }

    private void ClearPreview()
    {
        SaveCurrentView(); currentCacheKey = null; currentPreview = null;
        previewTarget = null; IsPreviewOpen = false;
        IsTextPreview = false; PreviewText = "";
        PreviewRows = null; PreviewSchema = null; PreviewSummary = ""; QueryError = "";
        PreviewPath = "";
        QueryText = ParquetQuery.Default; LastExecutedQuery = ParquetQuery.Default; LastAttemptedQuery = ParquetQuery.Default; RefreshCommands();
    }





    private async Task PreviewSelectedAsync()
    {
        if (SelectedEntry is not { } entry || SelectedProfile is not { } profile || SelectedBucket is not { } bucket) return;
        var target = new PreviewTarget(profile, bucket, entry.Key);
        var cacheKey = JsonSerializer.Serialize(new[] { profile.Id.ToString(), profile.Endpoint, bucket, entry.Key, entry.Size.ToString(), entry.Modified?.ToUniversalTime().Ticks.ToString() ?? "" });
        await OpenPreviewAsync(target, cacheKey, entry.Name);
    }

    private void ChooseLocalPreview()
    {
        var dialog = new OpenFileDialog { Filter = "Archivos compatibles|*.parquet;*.csv;*.txt|Parquet|*.parquet|CSV|*.csv|Texto|*.txt", Title = "Abrir archivo local" };
        if (dialog.ShowDialog() != true) return;
        _ = OpenLocalAsync(dialog.FileName);
    }

    public async Task OpenLocalAsync(string path)
    {
        if (IsBusy) return;
        await RunAsync("Abriendo archivo local…", async token =>
        {
            var info = new FileInfo(path);
            var cacheKey = JsonSerializer.Serialize(new[] { "local", info.FullName, info.Length.ToString(), info.LastWriteTimeUtc.Ticks.ToString() });
            await LoadPreviewAsync(new PreviewTarget(null, null, info.FullName), cacheKey, info.Name, token);
        });
    }

    private Task OpenPreviewAsync(PreviewTarget target, string cacheKey, string name)
        => RunAsync($"Abriendo {name}…", token => LoadPreviewAsync(target, cacheKey, name, token));

    private async Task LoadPreviewAsync(PreviewTarget target, string cacheKey, string name, CancellationToken token)
    {
        ClearPreview(); Error = "";
        if (previewCache.TryGet(cacheKey, out var cached))
        {
            previewTarget = target; currentCacheKey = cacheKey;
            QueryText = cached.Sql; LastExecutedQuery = cached.ExecutedSql; LastAttemptedQuery = cached.AttemptedSql;
            ShowPreview(cached.Result, name); QueryError = cached.Error; PreviewSummary = cached.Summary;
            Status = "Vista recuperada de la caché."; return;
        }
        LoadingFileName = name; IsFileLoading = true;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            ParquetPreview result;
            try
            {
                result = target.Profile is null
                    ? await files.PreviewLocalAsync(target.Key, timeout.Token)
                    : await files.PreviewRemoteAsync(target.Profile, target.Bucket!, target.Key, timeout.Token);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            { throw new ArgumentException("La apertura superó 60 segundos. Revisa el archivo y la conexión."); }
            previewTarget = target; currentCacheKey = cacheKey; ShowPreview(result, name); SaveCurrentView();
        }
        finally { IsFileLoading = false; }
    }

    private void ShowPreview(ParquetPreview result, string name)
    {
        currentPreview = result; IsTextPreview = result.Text is not null; PreviewText = result.Text ?? "";
        selectedResultColumns = result.Rows.Columns.Cast<DataColumn>().Select(column => column.Caption).ToArray();
        PreviewRows = result.Rows.DefaultView; PreviewSchema = result.Schema.DefaultView;
        PreviewTitle = name; IsPreviewOpen = true;
        PreviewPath = previewTarget is { } target ? target.Profile is null ? target.Key : $"s3://{target.Bucket}/{target.Key}" : result.Source;
        PreviewSummary = IsTextPreview
            ? $"{PreviewText.Length:N0} caracteres{(result.IsTruncated ? " · vista parcial: se alcanzó el límite de texto" : " · texto completo") }"
            : DescribePreview(result);
        Notify(nameof(PreviewLimitsLabel));
        Status = IsTextPreview ? "Vista previa de texto." : "Máximo 200 filas, 64 columnas y 512 caracteres por celda.";
    }

    public IReadOnlyList<SqlSuggestion> QuerySuggestions(string prefix) => SqlCompletion.Suggest(
        PreviewSchema?.Table?.Rows.Cast<DataRow>().Select(row => ((string)row[0], (string)row[1])) ?? [], prefix);

    private async Task ExecuteQueryAsync()
    {
        if (previewTarget is not { } target || IsBusy || !IsTabularPreview) return;
        var sql = QueryText;
        if (sql != LastExecutedQuery) selectedResultColumns = null;
        LastAttemptedQuery = sql;
        await RunAsync("Ejecutando consulta…", async token =>
        {
            QueryError = "";
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            var clock = Stopwatch.StartNew();
            ParquetPreview result;
            try
            {
                result = target.Profile is null
                    ? await files.QueryLocalAsync(target.Key, sql, timeout.Token, selectedResultColumns)
                    : await files.QueryRemoteAsync(target.Profile, target.Bucket!, target.Key, sql, timeout.Token, selectedResultColumns);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            { throw new ParquetQueryException("La consulta superó 60 segundos. Reduce los datos consultados o añade un filtro."); }
            LastExecutedQuery = sql;
            ShowPreview(result, PreviewTitle);
            PreviewSummary = $"{DescribePreview(result)} · {clock.ElapsedMilliseconds:N0} ms";
            Status = "Consulta terminada. Se muestran hasta 200 filas del resultado.";
            SaveCurrentView();
        });
    }


    public void SortPreview(string column, bool descending)
    {
        if (PreviewRows?.Table?.Columns[column] is not { } info || PreviewItems is null) return;
        PreviewItems.CustomSort = new PreviewRowComparer(column, info.ExtendedProperties["DuckDB.Type"] as string ?? "VARCHAR", descending);
    }
    private static string DescribePreview(ParquetPreview preview)
        => $"{preview.Rows.Rows.Count} filas{(preview.RowsTruncated ? " (resultado parcial)" : "")} · {preview.Rows.Columns.Count} de {preview.ResultSchema?.Rows.Count ?? preview.Schema.Rows.Count} columnas" +
            (preview.CellsTruncated ? " · celdas recortadas a 512 caracteres" : "");
    private void ChooseColumns()
    {
        if (currentPreview?.ResultSchema is not { } schema) return;
        var dialog = new ColumnsDialog(schema, selectedResultColumns ?? []) { Owner = Application.Current.MainWindow };
        if (dialog.ShowDialog() != true) return;
        selectedResultColumns = dialog.SelectedColumns; _ = ExecuteQueryAsync();
    }
}
