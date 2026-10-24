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

public sealed partial class MainViewModel : Observable
{
    private readonly ProfileStore store;
    private readonly IS3Service s3;
    private readonly DiagnosticLog diagnostics;
    private readonly ClientPreferencesStore preferences;
    private readonly FolderTransferService folders = new();
    private readonly FilePreviewService files = new();
    private readonly ExplorerCache explorerCache = new();
    private readonly PreviewCache<CachedView> previewCache = new(view => PreviewMemory.Estimate(view.Result) + view.Sql.Length * 2L);
    private readonly Queue<string> prefetchQueue = new();
    private readonly HashSet<string> prefetchFolders = new(StringComparer.Ordinal);
    private CancellationTokenSource? prefetchOperation;
    private bool backgroundStopped;
    private bool prefetchEnabled = true;
    private string explorerCacheLabel = "";
    private string explorerCacheDetails = "";
    private double explorerScrollOffset;
    private string diagnosticDetails = "";
    private ListCollectionView? previewItems;
    private IReadOnlyList<string>? selectedResultColumns;
    private readonly PreviewCache<ExplorerPosition> explorerPositions = new(value => 512 + value.Filter.Length * 2L + value.Tokens.Sum(token => 64L + (token?.Length ?? 0) * 2L), maxBytes: 1024 * 1024, maxEntries: 64);
    private readonly List<ActionCommand> commands = [];
    private readonly List<string?> pageTokens = [null];
    private IReadOnlyList<ObjectEntry> pageEntries = [];
    private CancellationTokenSource? operation;
    private CancellationTokenSource? folderCountOperation;
    private FolderTotals? folderTotals;
    private bool isCountingFolder;
    private bool storeReady;
    private bool connected;
    private int pageIndex;
    private string? nextToken;
    private string prefix = "";
    private string filter = "";
    private bool isBusy;
    private bool previewOpen;
    private bool isFileLoading;
    private bool isExplorerLoading;
    private string loadingFileName = "";
    private string previewText = "";
    private bool isTextPreview;
    private ParquetPreview? currentPreview;
    private string? currentCacheKey;
    private double progress;
    private string status = "Añade una conexión para explorar tus archivos.";
    private string error = "";
    private string previewTitle = "Vista previa de archivo";
    private string previewSummary = "";
    private string previewPath = "";
    private string queryText = ParquetQuery.Default;
    private string queryError = "";
    private bool autoRunQuery;
    private PreviewTarget? previewTarget;
    private DataView? previewRows;
    private DataView? previewSchema;
    private ConnectionProfile? selectedProfile;
    private string? selectedBucket;
    private ObjectEntry? selectedEntry;
    private ObservableCollection<ObjectEntry> entries = [];

    public ObservableCollection<ConnectionProfile> Profiles { get; } = [];
    public ObservableCollection<string> Buckets { get; } = [];
    public ObservableCollection<ObjectEntry> Entries { get => entries; private set => Set(ref entries, value); }
    public bool IsBusy { get => isBusy; private set { if (Set(ref isBusy, value)) { Notify(nameof(IsIdle)); RefreshCommands(); } } }
    public bool IsIdle => !IsBusy;
    public bool IsPreviewOpen { get => previewOpen; private set { if (Set(ref previewOpen, value)) { Notify(nameof(IsTabularPreview)); RefreshCommands(); } } }
    public bool IsFileLoading { get => isFileLoading; private set => Set(ref isFileLoading, value); }
    public bool IsExplorerLoading { get => isExplorerLoading; private set => Set(ref isExplorerLoading, value); }
    public string LoadingFileName { get => loadingFileName; private set => Set(ref loadingFileName, value); }
    public string PreviewText { get => previewText; private set => Set(ref previewText, value); }
    public bool IsTextPreview { get => isTextPreview; private set { if (Set(ref isTextPreview, value)) Notify(nameof(IsTabularPreview)); } }
    public bool IsTabularPreview => IsPreviewOpen && !IsTextPreview;
    public bool IsDarkTheme => ThemeManager.IsDark;
    public string ThemeAction => IsDarkTheme ? "Cambiar a modo claro" : "Cambiar a modo oscuro";
    public string QueryText { get => queryText; set { if (Set(ref queryText, value)) RefreshCommands(); } }
    public string QueryError { get => queryError; private set { if (Set(ref queryError, value)) Notify(nameof(HasQueryError)); } }
    public bool HasQueryError => QueryError.Length > 0;
    public bool AutoRunQuery { get => autoRunQuery; set => Set(ref autoRunQuery, value); }
    public string LastExecutedQuery { get; private set; } = ParquetQuery.Default;
    public string LastAttemptedQuery { get; private set; } = ParquetQuery.Default;
    public double Progress { get => progress; private set => Set(ref progress, value); }
    public string Status { get => status; private set => Set(ref status, value); }
    public string Error { get => error; private set { if (Set(ref error, value)) Notify(nameof(HasError)); } }
    public bool HasError => Error.Length > 0;
    public string PreviewTitle { get => previewTitle; private set => Set(ref previewTitle, value); }
    public string PreviewSummary { get => previewSummary; private set => Set(ref previewSummary, value); }
    public string PreviewPath { get => previewPath; private set => Set(ref previewPath, value); }
    public DataView? PreviewRows { get => previewRows; private set { if (Set(ref previewRows, value)) { previewItems = value is null ? null : new ListCollectionView(new PreviewRowList(value)); Notify(nameof(PreviewItems)); } } }
    public ListCollectionView? PreviewItems => previewItems;
    public string PreviewLimitsLabel => IsTextPreview ? "" : "Máximo: 200 filas · 64 columnas · 512 caracteres/celda. Ordenación: solo esta vista.";
    public string DiagnosticDetails { get => diagnosticDetails; private set { if (Set(ref diagnosticDetails, value)) RefreshCommands(); } }
    public string ExplorerCacheLabel { get => explorerCacheLabel; private set => Set(ref explorerCacheLabel, value); }
    public string ExplorerCacheDetails { get => explorerCacheDetails; private set => Set(ref explorerCacheDetails, value); }
    public double ExplorerScrollOffset { get => explorerScrollOffset; private set { explorerScrollOffset = value; Notify(); } }
    public bool HasFilter => Filter.Length > 0;
    public string ExplorerEmptyMessage => HasFilter ? "Sin coincidencias con el filtro." : connected ? "Esta carpeta está vacía." : "Conecta un servidor para explorar archivos.";
    public bool PrefetchEnabled
    {
        get => prefetchEnabled;
        set
        {
            if (!Set(ref prefetchEnabled, value)) return;
            if (!value) prefetchOperation?.Cancel(); else ResumePrefetch();
            try { preferences.Save(new ClientPreferences(value)); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { RecordFailure(exception, nameof(PrefetchEnabled)); }
        }
    }
    public DataView? PreviewSchema { get => previewSchema; private set => Set(ref previewSchema, value); }
    public string Location => selectedBucket is null ? "Selecciona una conexión" : $"{selectedBucket} / {prefix}";
    public string FolderPath => selectedBucket is null ? "" : $"s3://{selectedBucket}/{prefix}";
    public IReadOnlyList<FolderCrumb> Ancestors => FolderBreadcrumbs.Build(selectedBucket, prefix);
    public IReadOnlyList<FolderCrumb> Breadcrumbs => FolderBreadcrumbs.Compact(Ancestors);
    public string ConnectionState => connected ? "Conectado" : "Sin conexión";
    public bool IsConnected => connected;
    public string EndpointLabel => SelectedProfile?.Endpoint ?? "Tus servidores, en un solo lugar";
    public string PageLabel => folderTotals is { } totals ? $"Página {pageIndex + 1} de {Math.Max(pageIndex + 1, totals.Pages):N0}" : $"Página {pageIndex + 1} · {pageEntries.Count} elementos";
    public string FolderCountLabel => folderTotals is { } totals ? $"{totals.Files:N0} {(totals.Files == 1 ? "archivo" : "archivos")} · {totals.Folders:N0} {(totals.Folders == 1 ? "carpeta" : "carpetas")}" : isCountingFolder ? "Contando archivos…" : !connected ? "" : "Total no disponible";
    public string Filter { get => filter; set { if (Set(ref filter, value)) { ExplorerScrollOffset = 0; ApplyFilter(); Notify(nameof(HasFilter)); Notify(nameof(ExplorerEmptyMessage)); RefreshCommands(); } } }
    public ConnectionProfile? SelectedProfile
    {
        get => selectedProfile;
        set
        {
            if (!Set(ref selectedProfile, value)) return;
            InvalidateCache();
            connected = false; Buckets.Clear(); selectedBucket = null; prefix = "";
            Notify(nameof(SelectedBucket)); NotifyLocation(); NotifyConnection(); Notify(nameof(EndpointLabel));
            ClearPage(); ClearPreview(); Error = "";
            Status = value is null ? "Añade una conexión para comenzar." : "Pulsa Conectar para cargar los buckets autorizados.";
            RefreshCommands();
        }
    }
    public string? SelectedBucket
    {
        get => selectedBucket;
        set
        {
            if (!Set(ref selectedBucket, value)) return;
            InvalidateCache();
            NotifyLocation();
            if (connected && !IsBusy && value is not null) _ = RunAsync("Cargando archivos…", async token => { prefix = ""; await ReloadAsync(token); });
        }
    }
    public ObjectEntry? SelectedEntry { get => selectedEntry; set { if (Set(ref selectedEntry, value)) { Notify(nameof(DownloadAction)); Notify(nameof(DeleteAction)); RefreshCommands(); } } }
    public string DownloadAction => SelectedEntry?.IsFolder == true ? "Descargar carpeta (ZIP)" : "Descargar archivo";
    public string DeleteAction => SelectedEntry?.IsFolder == true ? "Eliminar carpeta y su contenido" : "Eliminar archivo";

    public ActionCommand AddConnectionCommand { get; }
    public ActionCommand EditConnectionCommand { get; }
    public ActionCommand RemoveConnectionCommand { get; }
    public ActionCommand ConnectCommand { get; }
    public ActionCommand RefreshCommand { get; }
    public ActionCommand UpCommand { get; }
    public ActionCommand NextPageCommand { get; }
    public ActionCommand PreviousPageCommand { get; }
    public ActionCommand UploadCommand { get; }
    public ActionCommand DownloadCommand { get; }
    public ActionCommand PreviewCommand { get; }
    public ActionCommand LocalPreviewCommand { get; }
    public ActionCommand CancelCommand { get; }
    public ActionCommand ClosePreviewCommand { get; }
    public ActionCommand DeleteCommand { get; }
    public ActionCommand ToggleThemeCommand { get; }
    public ActionCommand ExecuteQueryCommand { get; }
    public ActionCommand ResetQueryCommand { get; }
    public ActionCommand ClearFilterCommand { get; }
    public ActionCommand ChooseColumnsCommand { get; }
    public ActionCommand CopyDiagnosticCommand { get; }

    public MainViewModel(IS3Service? storage = null, ProfileStore? profileStore = null, ClientPreferencesStore? preferencesStore = null, DiagnosticLog? diagnosticLog = null)
    {
        s3 = storage ?? new S3Service(); store = profileStore ?? new ProfileStore();
        diagnostics = diagnosticLog ?? new DiagnosticLog(); preferences = preferencesStore ?? new ClientPreferencesStore();
        try { prefetchEnabled = preferences.Load().PrefetchEnabled; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException) { diagnostics.Record(exception, nameof(ClientPreferencesStore)); }
        ToggleThemeCommand = Command(ToggleTheme, () => true);
        ClearFilterCommand = Command(() => Filter = "", () => IsIdle && HasFilter);
        ChooseColumnsCommand = Command(ChooseColumns, () => IsIdle && IsTabularPreview && currentPreview?.ResultSchema is not null && QueryText == LastExecutedQuery);
        CopyDiagnosticCommand = Command(() => { try { Clipboard.SetText(DiagnosticDetails); } catch (System.Runtime.InteropServices.ExternalException) { Status = "No se pudo acceder al portapapeles. Inténtalo de nuevo."; } }, () => DiagnosticDetails.Length > 0);
        ExecuteQueryCommand = Command(() => _ = ExecuteQueryAsync(), () => IsIdle && IsTabularPreview && previewTarget is not null && !string.IsNullOrWhiteSpace(QueryText));
        ResetQueryCommand = Command(() => { selectedResultColumns = null; QueryText = ParquetQuery.Default; _ = ExecuteQueryAsync(); }, () => IsIdle && IsTabularPreview && previewTarget is not null);
        AddConnectionCommand = Command(() => EditProfile(null), () => IsIdle && storeReady);
        EditConnectionCommand = Command(() => EditProfile(SelectedProfile), () => IsIdle && storeReady && SelectedProfile is not null);
        RemoveConnectionCommand = Command(RemoveProfile, () => IsIdle && storeReady && SelectedProfile is not null);
        ConnectCommand = Command(() => _ = RunAsync("Conectando…", ConnectAsync), () => IsIdle && SelectedProfile is not null);
        RefreshCommand = Command(() => _ = RunAsync("Actualizando…", async token => { InvalidateCache(); await ReloadAsync(token); }), CanBrowse);
        UpCommand = Command(() => _ = RunAsync("Abriendo carpeta…", async token =>
        {
            var trimmed = prefix[..^1]; var slash = trimmed.LastIndexOf('/');
            await NavigateFolderAsync(slash < 0 ? "" : trimmed[..(slash + 1)], token);
        }), () => CanBrowse() && prefix.Length > 0);
        NextPageCommand = Command(() => _ = RunAsync("Cargando página…", async token =>
        {
            var target = nextToken; var page = await LoadPageAsync(SelectedProfile!, SelectedBucket!, prefix, target, token);
            if (pageTokens.Count > pageIndex + 1) pageTokens.RemoveRange(pageIndex + 1, pageTokens.Count - pageIndex - 1);
            pageTokens.Add(target); pageIndex++; SetPage(page);
        }), () => CanBrowse() && nextToken is not null);
        PreviousPageCommand = Command(() => _ = RunAsync("Cargando página…", async token =>
        {
            var page = await LoadPageAsync(SelectedProfile!, SelectedBucket!, prefix, pageTokens[pageIndex - 1], token);
            pageIndex--; SetPage(page);
        }), () => CanBrowse() && pageIndex > 0);
        UploadCommand = Command(ChooseUpload, CanBrowse);
        DownloadCommand = Command(ChooseDownload, () => CanBrowse() && SelectedEntry is not null);
        DeleteCommand = Command(DeleteSelected, () => CanBrowse() && SelectedEntry is not null);
        PreviewCommand = Command(() => _ = PreviewSelectedAsync(), () => CanBrowse() && CanPreview(SelectedEntry));
        LocalPreviewCommand = Command(ChooseLocalPreview, () => IsIdle);
        CancelCommand = Command(() => operation?.Cancel(), () => IsBusy);
        ClosePreviewCommand = Command(ClearPreview, () => IsIdle);
    }

    private ActionCommand Command(Action action, Func<bool> canExecute) { var command = new ActionCommand(action, canExecute); commands.Add(command); return command; }
    private bool CanBrowse() => IsIdle && connected && SelectedBucket is not null;
    private static bool CanPreview(ObjectEntry? entry) => entry is { IsFolder: false } && FilePreviewService.IsSupported(entry.Name);
    private void RefreshCommands() { foreach (var command in commands) command.Refresh(); }

    private void ToggleTheme()
    {
        try { ThemeManager.Apply(!IsDarkTheme, persist: true); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { Status = "El tema cambió, pero no se pudo guardar la preferencia en este equipo."; }
        Notify(nameof(IsDarkTheme)); Notify(nameof(ThemeAction));
    }

    private async Task RunAsync(string message, Func<CancellationToken, Task> action)
    {
        if (IsBusy) return;
        using var cancellation = new CancellationTokenSource(); operation = cancellation;
        IsBusy = true; Error = ""; DiagnosticDetails = ""; Status = message; Progress = 0;
        prefetchOperation?.Cancel();
        try { await action(cancellation.Token); }
        catch (ParquetQueryException exception)
        {
            RecordFailure(exception, action.Method.Name);
            if (IsPreviewOpen) QueryError = exception.Message; else Error = exception.Message;
            Status = "La consulta no se completó. Se conserva el último resultado disponible.";
        }
        catch (FolderTransferException exception) { RecordFailure(exception, action.Method.Name); Error = exception.Message; Status = "La operación de carpeta no se completó."; }
        catch (OperationCanceledException) { Status = "Operación cancelada. Los cambios ya completados se conservan."; }
        catch (AmazonS3Exception exception)
        {
            RecordFailure(exception, action.Method.Name);
            Error = exception.StatusCode == System.Net.HttpStatusCode.Forbidden
                ? "Acceso denegado. Revisa las credenciales y los permisos; si solo tienes acceso a un bucket, indícalo en la conexión."
                : $"No se pudo completar la operación S3 ({exception.ErrorCode ?? exception.StatusCode.ToString()}). Revisa endpoint, red y permisos.";
            Status = "La operación no se completó.";
        }
        catch (Exception exception)
        {
            RecordFailure(exception, action.Method.Name);
            // Provider exceptions can include SQL containing connection details; never display raw SQL/errors.
            Error = exception is ArgumentException ? exception.Message : "La operación no se completó. Revisa la conexión, el formato del archivo y la disponibilidad de httpfs para tablas remotas.";
            Status = "La operación no se completó.";
        }
        finally { operation = null; IsExplorerLoading = false; IsBusy = false; ResumePrefetch(); }
    }

    public void Cancel() => operation?.Cancel();
    public void StopBackgroundWork() { backgroundStopped = true; prefetchOperation?.Cancel(); CancelFolderCount(); }
    private void RecordFailure(Exception exception, string category) => DiagnosticDetails = diagnostics.Record(exception, category).Display;
    public void LoadDesignPreview(bool populated = true)
    {
        backgroundStopped = true;
        if (!populated) { storeReady = true; RefreshCommands(); return; }
        Profiles.Add(new ConnectionProfile { Name = "Almacenamiento", Endpoint = "https://storage.ejemplo.com" });
        Profiles.Add(new ConnectionProfile { Name = "Desarrollo", Endpoint = "https://dev.ejemplo.com" });
        SelectedProfile = Profiles[0]; connected = true; selectedBucket = "analytics"; Buckets.Add("analytics"); Buckets.Add("documentos");
        prefix = "ventas/2026/";
        SetPage(new ObjectPage([
            new("ventas/2026/octubre/", "octubre", true, 0, null),
            new("ventas/2026/ventas.parquet", "ventas.parquet", false, 18549023, DateTime.UtcNow),
            new("ventas/2026/clientes.parquet", "clientes.parquet", false, 2401938, DateTime.UtcNow),
            new("ventas/2026/notas.txt", "notas.txt", false, 1284, DateTime.UtcNow)
        ], null));
        SelectedEntry = Entries.First(item => item.Name == "ventas.parquet");
        var designRows = new DataTable();
        designRows.Columns.Add("c0").Caption = "pedido_id";
        designRows.Columns.Add("c1").Caption = "país";
        designRows.Columns.Add("c2").Caption = "importe";
        designRows.Rows.Add("1001", "México", "1290.50");
        designRows.Rows.Add("1002", "Colombia", "845.00");
        designRows.Rows.Add("1003", "México", "2150.75");
        var designSchema = new DataTable();
        designSchema.Columns.Add("Columna"); designSchema.Columns.Add("Tipo"); designSchema.Columns.Add("Nulos");
        designSchema.Rows.Add("pedido_id", "BIGINT", "YES");
        designSchema.Rows.Add("país", "VARCHAR", "YES");
        designSchema.Rows.Add("importe", "DECIMAL(12,2)", "YES");
        previewTarget = new PreviewTarget(SelectedProfile, "analytics", "ventas/2026/ventas.parquet");
        ShowPreview(new ParquetPreview(designRows, designSchema, "demo"), "ventas.parquet");
        folderTotals = new FolderTotals(3, 1, 1); NotifyTotals();
        Notify(nameof(SelectedBucket)); NotifyConnection(); storeReady = true; RefreshCommands();
    }

    private sealed record PreviewTarget(ConnectionProfile? Profile, string? Bucket, string Key);
    private sealed record ExplorerPosition(int Page, string?[] Tokens, string? SelectedKey, double Offset, string Filter);
    private sealed record CachedView(ParquetPreview Result, string Sql, string ExecutedSql, string AttemptedSql, string Error, string Summary);
}
