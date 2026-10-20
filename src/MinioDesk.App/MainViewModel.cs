using System.Collections.ObjectModel;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using Amazon.S3;
using Microsoft.Win32;
using MinioDesk.Core;

namespace MinioDesk.App;

public sealed class MainViewModel : Observable
{
    private readonly ProfileStore store = new();
    private readonly S3Service s3 = new();
    private readonly FilePreviewService files = new();
    private readonly PreviewCache<CachedView> previewCache = new(view => PreviewMemory.Estimate(view.Result) + view.Sql.Length * 2L);
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
    public DataView? PreviewRows { get => previewRows; private set => Set(ref previewRows, value); }
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
    public string Filter { get => filter; set { if (Set(ref filter, value)) ApplyFilter(); } }
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
    public ObjectEntry? SelectedEntry { get => selectedEntry; set { if (Set(ref selectedEntry, value)) RefreshCommands(); } }

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

    public MainViewModel()
    {
        ToggleThemeCommand = Command(ToggleTheme, () => true);
        ExecuteQueryCommand = Command(() => _ = ExecuteQueryAsync(), () => IsIdle && IsTabularPreview && previewTarget is not null && !string.IsNullOrWhiteSpace(QueryText));
        ResetQueryCommand = Command(() => { QueryText = ParquetQuery.Default; _ = ExecuteQueryAsync(); }, () => IsIdle && IsTabularPreview && previewTarget is not null);
        AddConnectionCommand = Command(() => EditProfile(null), () => IsIdle && storeReady);
        EditConnectionCommand = Command(() => EditProfile(SelectedProfile), () => IsIdle && storeReady && SelectedProfile is not null);
        RemoveConnectionCommand = Command(RemoveProfile, () => IsIdle && storeReady && SelectedProfile is not null);
        ConnectCommand = Command(() => _ = RunAsync("Conectando…", ConnectAsync), () => IsIdle && SelectedProfile is not null);
        RefreshCommand = Command(() => _ = RunAsync("Actualizando…", async token => { InvalidateCache(); await ReloadAsync(token); }), CanBrowse);
        UpCommand = Command(() => _ = RunAsync("Abriendo carpeta…", async token =>
        {
            var trimmed = prefix.TrimEnd('/'); var slash = trimmed.LastIndexOf('/');
            prefix = slash < 0 ? "" : trimmed[..(slash + 1)]; await ReloadAsync(token);
        }), () => CanBrowse() && prefix.Length > 0);
        NextPageCommand = Command(() => _ = RunAsync("Cargando página…", async token =>
        {
            var target = nextToken; var page = await s3.ListAsync(SelectedProfile!, SelectedBucket!, prefix, target, token);
            if (pageTokens.Count > pageIndex + 1) pageTokens.RemoveRange(pageIndex + 1, pageTokens.Count - pageIndex - 1);
            pageTokens.Add(target); pageIndex++; SetPage(page);
        }), () => CanBrowse() && nextToken is not null);
        PreviousPageCommand = Command(() => _ = RunAsync("Cargando página…", async token =>
        {
            var page = await s3.ListAsync(SelectedProfile!, SelectedBucket!, prefix, pageTokens[pageIndex - 1], token);
            pageIndex--; SetPage(page);
        }), () => CanBrowse() && pageIndex > 0);
        UploadCommand = Command(ChooseUpload, CanBrowse);
        DownloadCommand = Command(ChooseDownload, () => CanBrowse() && SelectedEntry is { IsFolder: false });
        DeleteCommand = Command(DeleteFile, () => CanBrowse() && SelectedEntry is { IsFolder: false });
        PreviewCommand = Command(() => _ = PreviewSelectedAsync(), () => CanBrowse() && CanPreview(SelectedEntry));
        LocalPreviewCommand = Command(ChooseLocalPreview, () => IsIdle);
        CancelCommand = Command(() => operation?.Cancel(), () => IsBusy);
        ClosePreviewCommand = Command(ClearPreview, () => IsIdle);
    }

    private ActionCommand Command(Action action, Func<bool> canExecute) { var command = new ActionCommand(action, canExecute); commands.Add(command); return command; }
    private bool CanBrowse() => IsIdle && connected && SelectedBucket is not null;
    private static bool CanPreview(ObjectEntry? entry) => entry is { IsFolder: false } && FilePreviewService.IsSupported(entry.Name);
    private void RefreshCommands() { foreach (var command in commands) command.Refresh(); }

    public void Initialize()
    {
        try
        {
            foreach (var profile in store.Load()) Profiles.Add(profile);
            storeReady = true; SelectedProfile = Profiles.FirstOrDefault(); RefreshCommands();
        }
        catch { Error = "No se pudieron leer las conexiones guardadas. Conserva connections.json y revisa que uses el mismo usuario de Windows; no se sobrescribirá el archivo."; }
    }

    private void EditProfile(ConnectionProfile? existing)
    {
        var dialog = new ConnectionDialog(existing) { Owner = Application.Current.MainWindow };
        if (dialog.ShowDialog() != true || dialog.Result is not { } profile) return;
        try
        {
            var updated = Profiles.Where(item => item.Id != profile.Id).Append(profile).ToArray();
            store.Save(updated);
            var index = existing is null ? -1 : Profiles.IndexOf(existing);
            if (index < 0) Profiles.Add(profile); else Profiles[index] = profile;
            SelectedProfile = profile; Status = "Conexión guardada. Pulsa Conectar para comprobar el acceso.";
        }
        catch { Error = "No se pudo guardar la conexión cifrada. Revisa el acceso a la carpeta de configuración."; }
    }

    private void RemoveProfile()
    {
        if (SelectedProfile is not { } profile) return;
        if (MessageBox.Show($"¿Quitar la conexión «{profile.Name}»? Los archivos del servidor se conservan.", "Quitar conexión", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try { store.Save(Profiles.Where(item => item.Id != profile.Id)); Profiles.Remove(profile); SelectedProfile = Profiles.FirstOrDefault(); }
        catch { Error = "No se pudo actualizar la configuración de conexiones."; }
    }

    private async Task ConnectAsync(CancellationToken token)
    {
        InvalidateCache();
        connected = false; NotifyConnection();
        ClearPage(); ClearPreview(); Buckets.Clear(); selectedBucket = null; Notify(nameof(SelectedBucket)); NotifyLocation();
        var buckets = await s3.ListBucketsAsync(SelectedProfile!, token);
        foreach (var bucket in buckets) Buckets.Add(bucket);
        connected = true; NotifyConnection(); selectedBucket = Buckets.FirstOrDefault(); Notify(nameof(SelectedBucket)); prefix = "";
        if (selectedBucket is not null) await ReloadAsync(token);
        else { NotifyLocation(); Status = "Conexión correcta. No hay buckets visibles para estas credenciales."; }
    }

    private async Task ReloadAsync(CancellationToken token)
    {
        ClearPreview(); ClearPage(); NotifyLocation();
        var page = await s3.ListAsync(SelectedProfile!, SelectedBucket!, prefix, null, token); SetPage(page);
        StartFolderCount(page);
    }

    private void NotifyLocation() { Notify(nameof(Location)); Notify(nameof(FolderPath)); Notify(nameof(Breadcrumbs)); Notify(nameof(Ancestors)); }
    private void NotifyConnection() { Notify(nameof(ConnectionState)); Notify(nameof(IsConnected)); }
    private void NotifyTotals() { Notify(nameof(PageLabel)); Notify(nameof(FolderCountLabel)); }
    private void CancelFolderCount()
    {
        folderCountOperation?.Cancel(); folderCountOperation = null; folderTotals = null; isCountingFolder = false; NotifyTotals();
    }
    private void StartFolderCount(ObjectPage firstPage)
    {
        CancelFolderCount();
        if (firstPage.NextToken is null)
        {
            folderTotals = new FolderTotals(firstPage.Entries.LongCount(entry => !entry.IsFolder), firstPage.Entries.LongCount(entry => entry.IsFolder), 1);
            NotifyTotals(); return;
        }
        var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        folderCountOperation = cancellation; isCountingFolder = true; NotifyTotals();
        _ = CountFolderAsync(SelectedProfile!, SelectedBucket!, prefix, cancellation);
    }
    private async Task CountFolderAsync(ConnectionProfile profile, string bucket, string folder, CancellationTokenSource cancellation)
    {
        try
        {
            var totals = await s3.CountFolderAsync(profile, bucket, folder, cancellation.Token);
            if (ReferenceEquals(folderCountOperation, cancellation)) folderTotals = totals;
        }
        catch (Exception) { /* Counting must never block navigation or replace an open file's status. */ }
        finally
        {
            if (ReferenceEquals(folderCountOperation, cancellation)) { folderCountOperation = null; isCountingFolder = false; NotifyTotals(); }
            cancellation.Dispose();
        }
    }
    private void ClearPage() { CancelFolderCount(); pageIndex = 0; nextToken = null; pageTokens.Clear(); pageTokens.Add(null); pageEntries = []; Entries.Clear(); SelectedEntry = null; Notify(nameof(PageLabel)); }
    private void SetPage(ObjectPage page) { pageEntries = page.Entries; nextToken = page.NextToken; SelectedEntry = null; ApplyFilter(); NotifyLocation(); Notify(nameof(PageLabel)); Status = page.Entries.Count == 0 ? "Esta carpeta está vacía." : $"{page.Entries.Count} elementos en esta página."; }
    private void ApplyFilter() => Entries = new ObservableCollection<ObjectEntry>(pageEntries.Where(item => item.Name.Contains(Filter, StringComparison.OrdinalIgnoreCase)));
    private void SaveCurrentView()
    {
        if (currentCacheKey is not null && currentPreview is not null)
            previewCache.Put(currentCacheKey, new CachedView(currentPreview, QueryText, LastExecutedQuery, LastAttemptedQuery, QueryError, PreviewSummary));
    }
    private void InvalidateCache() { previewCache.Clear(); currentCacheKey = null; }
    private void ClearPreview()
    {
        SaveCurrentView(); currentCacheKey = null; currentPreview = null;
        previewTarget = null; IsPreviewOpen = false;
        IsTextPreview = false; PreviewText = "";
        PreviewRows = null; PreviewSchema = null; PreviewSummary = ""; QueryError = "";
        PreviewPath = "";
        QueryText = ParquetQuery.Default; LastExecutedQuery = ParquetQuery.Default; LastAttemptedQuery = ParquetQuery.Default; RefreshCommands();
    }

    public void OpenSelected()
    {
        if (!CanBrowse() || SelectedEntry is not { } entry) return;
        if (entry.IsFolder) _ = RunAsync("Abriendo carpeta…", async token => { prefix = entry.Key; await ReloadAsync(token); });
        else if (CanPreview(entry)) _ = PreviewSelectedAsync();
        else Status = "El visor admite archivos Parquet, CSV y TXT. Puedes descargar otros formatos con clic secundario.";
    }

    public void NavigateToPrefix(string target)
    {
        if (!CanBrowse() || target == prefix || !prefix.StartsWith(target, StringComparison.Ordinal) || (target.Length > 0 && !target.EndsWith('/'))) return;
        _ = RunAsync("Abriendo carpeta…", async token => { prefix = target; await ReloadAsync(token); });
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
        PreviewRows = result.Rows.DefaultView; PreviewSchema = result.Schema.DefaultView;
        PreviewTitle = name; IsPreviewOpen = true;
        PreviewPath = previewTarget is { } target ? target.Profile is null ? target.Key : $"s3://{target.Bucket}/{target.Key}" : result.Source;
        PreviewSummary = IsTextPreview
            ? $"{PreviewText.Length:N0} caracteres{(result.IsTruncated ? " · vista parcial: se alcanzó el límite de texto" : " · texto completo") }"
            : $"{result.Rows.Rows.Count} filas en la vista previa · {result.Schema.Rows.Count} columnas en el archivo";
        Status = IsTextPreview ? "Vista previa de texto." : "Máximo 200 filas, 64 columnas y 512 caracteres por celda.";
    }

    public IReadOnlyList<SqlSuggestion> QuerySuggestions(string prefix) => SqlCompletion.Suggest(
        PreviewSchema?.Table?.Rows.Cast<DataRow>().Select(row => ((string)row[0], (string)row[1])) ?? [], prefix);

    private async Task ExecuteQueryAsync()
    {
        if (previewTarget is not { } target || IsBusy || !IsTabularPreview) return;
        var sql = QueryText;
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
                    ? await files.QueryLocalAsync(target.Key, sql, timeout.Token)
                    : await files.QueryRemoteAsync(target.Profile, target.Bucket!, target.Key, sql, timeout.Token);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            { throw new ParquetQueryException("La consulta superó 60 segundos. Reduce los datos consultados o añade un filtro."); }
            LastExecutedQuery = sql;
            ShowPreview(result, PreviewTitle);
            PreviewSummary = $"{result.Rows.Rows.Count} filas · {result.Rows.Columns.Count} columnas en el resultado · {clock.ElapsedMilliseconds:N0} ms";
            Status = "Consulta terminada. Se muestran hasta 200 filas del resultado.";
            SaveCurrentView();
        });
    }

    private void ToggleTheme()
    {
        try { ThemeManager.Apply(!IsDarkTheme, persist: true); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { Status = "El tema cambió, pero no se pudo guardar la preferencia en este equipo."; }
        Notify(nameof(IsDarkTheme)); Notify(nameof(ThemeAction));
    }

    private void DeleteFile()
    {
        if (SelectedEntry is not { IsFolder: false } entry || SelectedProfile is not { } profile || SelectedBucket is not { } bucket) return;
        if (MessageBox.Show(Application.Current.MainWindow,
            $"¿Eliminar «{entry.Name}» del bucket «{bucket}»?\n\nConexión: {profile.Name}\nClave: {entry.Key}",
            "Eliminar archivo", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        _ = RunAsync($"Eliminando {entry.Name}…", async token =>
        {
            InvalidateCache();
            CancelFolderCount();
            await s3.DeleteAsync(profile, bucket, entry.Key, token);
            if (previewTarget is { } opened && opened.Profile?.Id == profile.Id && opened.Bucket == bucket && opened.Key == entry.Key) ClearPreview();
            // Retain other open previews while refreshing this page after deletion.
            pageTokens.Clear(); pageTokens.Add(null); pageIndex = 0;
            var page = await s3.ListAsync(profile, bucket, prefix, null, token); SetPage(page); StartFolderCount(page);
            Status = $"Archivo eliminado: {entry.Name}";
        });
    }

    private void ChooseUpload()
    {
        var dialog = new OpenFileDialog { Multiselect = true, Title = "Subir archivos a la carpeta actual" };
        if (dialog.ShowDialog() != true) return;
        if (MessageBox.Show($"Subir {dialog.FileNames.Length} archivo(s) a {Location}. Si ya existe una clave con el mismo nombre, se reemplazará su contenido actual. ¿Continuar?", "Subir archivos", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _ = RunAsync("Subiendo archivos…", async token =>
        {
            InvalidateCache();
            CancelFolderCount();
            var reporter = new Progress<TransferProgress>(value => Progress = value.Percent);
            var completed = 0;
            foreach (var path in dialog.FileNames)
            {
                Status = $"Subiendo {++completed}/{dialog.FileNames.Length}: {System.IO.Path.GetFileName(path)}";
                await s3.UploadAsync(SelectedProfile!, SelectedBucket!, prefix + System.IO.Path.GetFileName(path), path, reporter, token);
            }
            await ReloadAsync(token); Status = $"Carga terminada: {completed} archivo(s).";
        });
    }

    private void ChooseDownload()
    {
        if (SelectedEntry is not { IsFolder: false } entry) return;
        var dialog = new SaveFileDialog { FileName = System.IO.Path.GetFileName(entry.Name), Title = "Descargar archivo", OverwritePrompt = true };
        if (dialog.ShowDialog() != true) return;
        _ = RunAsync($"Descargando {entry.Name}…", async token =>
        {
            await s3.DownloadAsync(SelectedProfile!, SelectedBucket!, entry.Key, dialog.FileName, new Progress<TransferProgress>(value => Progress = value.Percent), token);
            Status = $"Descarga terminada: {dialog.FileName}";
        });
    }

    private async Task RunAsync(string message, Func<CancellationToken, Task> action)
    {
        if (IsBusy) return;
        using var cancellation = new CancellationTokenSource(); operation = cancellation;
        IsBusy = true; Error = ""; Status = message; Progress = 0;
        try { await action(cancellation.Token); }
        catch (ParquetQueryException exception)
        {
            if (IsPreviewOpen) QueryError = exception.Message; else Error = exception.Message;
            Status = "La consulta no se completó. Se conserva el último resultado disponible.";
        }
        catch (OperationCanceledException) { Status = "Operación cancelada. Las cargas ya terminadas se conservan."; }
        catch (AmazonS3Exception exception)
        {
            Error = exception.StatusCode == System.Net.HttpStatusCode.Forbidden
                ? "Acceso denegado. Revisa las credenciales y los permisos; si solo tienes acceso a un bucket, indícalo en la conexión."
                : $"No se pudo completar la operación S3 ({exception.ErrorCode ?? exception.StatusCode.ToString()}). Revisa endpoint, red y permisos.";
            Status = "La operación no se completó.";
        }
        catch (Exception exception)
        {
            // Provider exceptions can include SQL containing connection details; never display raw SQL/errors.
            Error = exception is ArgumentException ? exception.Message : "La operación no se completó. Revisa la conexión, el formato del archivo y la disponibilidad de httpfs para tablas remotas.";
            Status = "La operación no se completó.";
        }
        finally { operation = null; IsBusy = false; }
    }

    public void Cancel() => operation?.Cancel();
    public void StopBackgroundWork() => CancelFolderCount();
    public void LoadDesignPreview(bool populated = true)
    {
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
    private sealed record CachedView(ParquetPreview Result, string Sql, string ExecutedSql, string AttemptedSql, string Error, string Summary);
}
