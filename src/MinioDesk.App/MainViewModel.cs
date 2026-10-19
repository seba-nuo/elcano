using System.Collections.ObjectModel;
using System.Data;
using System.Windows;
using Amazon.S3;
using Microsoft.Win32;
using MinioDesk.Core;

namespace MinioDesk.App;

public sealed class MainViewModel : Observable
{
    private readonly ProfileStore store = new();
    private readonly S3Service s3 = new();
    private readonly ParquetService parquet = new();
    private readonly List<ActionCommand> commands = [];
    private readonly List<string?> pageTokens = [null];
    private IReadOnlyList<ObjectEntry> pageEntries = [];
    private CancellationTokenSource? operation;
    private bool storeReady;
    private bool connected;
    private int pageIndex;
    private string? nextToken;
    private string prefix = "";
    private string filter = "";
    private bool isBusy;
    private bool previewOpen;
    private double progress;
    private string status = "Añade una conexión para explorar tus archivos.";
    private string error = "";
    private string previewTitle = "Vista previa de Parquet";
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
    public bool IsPreviewOpen { get => previewOpen; private set => Set(ref previewOpen, value); }
    public double Progress { get => progress; private set => Set(ref progress, value); }
    public string Status { get => status; private set => Set(ref status, value); }
    public string Error { get => error; private set { if (Set(ref error, value)) Notify(nameof(HasError)); } }
    public bool HasError => Error.Length > 0;
    public string PreviewTitle { get => previewTitle; private set => Set(ref previewTitle, value); }
    public DataView? PreviewRows { get => previewRows; private set => Set(ref previewRows, value); }
    public DataView? PreviewSchema { get => previewSchema; private set => Set(ref previewSchema, value); }
    public string Location => selectedBucket is null ? "Selecciona una conexión" : $"{selectedBucket} / {prefix}";
    public string ConnectionState => connected ? "Conectado" : "Sin conexión";
    public string EndpointLabel => SelectedProfile?.Endpoint ?? "Tus servidores, en un solo lugar";
    public string PageLabel => $"Página {pageIndex + 1} · {pageEntries.Count} elementos";
    public string Filter { get => filter; set { if (Set(ref filter, value)) ApplyFilter(); } }
    public ConnectionProfile? SelectedProfile
    {
        get => selectedProfile;
        set
        {
            if (!Set(ref selectedProfile, value)) return;
            connected = false; Buckets.Clear(); selectedBucket = null; prefix = "";
            Notify(nameof(SelectedBucket)); Notify(nameof(Location)); Notify(nameof(ConnectionState)); Notify(nameof(EndpointLabel));
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
            Notify(nameof(Location));
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

    public MainViewModel()
    {
        AddConnectionCommand = Command(() => EditProfile(null), () => IsIdle && storeReady);
        EditConnectionCommand = Command(() => EditProfile(SelectedProfile), () => IsIdle && storeReady && SelectedProfile is not null);
        RemoveConnectionCommand = Command(RemoveProfile, () => IsIdle && storeReady && SelectedProfile is not null);
        ConnectCommand = Command(() => _ = RunAsync("Conectando…", ConnectAsync), () => IsIdle && SelectedProfile is not null);
        RefreshCommand = Command(() => _ = RunAsync("Actualizando…", ReloadAsync), CanBrowse);
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
        PreviewCommand = Command(() => _ = PreviewSelectedAsync(), () => CanBrowse() && IsParquet(SelectedEntry));
        LocalPreviewCommand = Command(ChooseLocalPreview, () => IsIdle);
        CancelCommand = Command(() => operation?.Cancel(), () => IsBusy);
        ClosePreviewCommand = Command(ClearPreview, () => IsIdle);
    }

    private ActionCommand Command(Action action, Func<bool> canExecute) { var command = new ActionCommand(action, canExecute); commands.Add(command); return command; }
    private bool CanBrowse() => IsIdle && connected && SelectedBucket is not null;
    private static bool IsParquet(ObjectEntry? entry) => entry is { IsFolder: false } && entry.Name.EndsWith(".parquet", StringComparison.OrdinalIgnoreCase);
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
        connected = false; Notify(nameof(ConnectionState));
        ClearPage(); ClearPreview(); Buckets.Clear(); selectedBucket = null; Notify(nameof(SelectedBucket)); Notify(nameof(Location));
        var buckets = await s3.ListBucketsAsync(SelectedProfile!, token);
        foreach (var bucket in buckets) Buckets.Add(bucket);
        connected = true; Notify(nameof(ConnectionState)); selectedBucket = Buckets.FirstOrDefault(); Notify(nameof(SelectedBucket)); prefix = "";
        if (selectedBucket is not null) await ReloadAsync(token);
        else { Notify(nameof(Location)); Status = "Conexión correcta. No hay buckets visibles para estas credenciales."; }
    }

    private async Task ReloadAsync(CancellationToken token)
    {
        ClearPreview(); ClearPage(); Notify(nameof(Location));
        var page = await s3.ListAsync(SelectedProfile!, SelectedBucket!, prefix, null, token); SetPage(page);
    }

    private void ClearPage() { pageIndex = 0; nextToken = null; pageTokens.Clear(); pageTokens.Add(null); pageEntries = []; Entries.Clear(); SelectedEntry = null; Notify(nameof(PageLabel)); }
    private void SetPage(ObjectPage page) { pageEntries = page.Entries; nextToken = page.NextToken; SelectedEntry = null; ApplyFilter(); Notify(nameof(Location)); Notify(nameof(PageLabel)); Status = page.Entries.Count == 0 ? "Esta carpeta está vacía." : $"{page.Entries.Count} elementos en esta página."; }
    private void ApplyFilter() => Entries = new ObservableCollection<ObjectEntry>(pageEntries.Where(item => item.Name.Contains(Filter, StringComparison.OrdinalIgnoreCase)));
    private void ClearPreview() { PreviewRows = null; PreviewSchema = null; IsPreviewOpen = false; }

    public void OpenSelected()
    {
        if (!CanBrowse() || SelectedEntry is not { } entry) return;
        if (entry.IsFolder) _ = RunAsync("Abriendo carpeta…", async token => { prefix = entry.Key; await ReloadAsync(token); });
        else if (IsParquet(entry)) _ = PreviewSelectedAsync();
    }

    private async Task PreviewSelectedAsync()
    {
        if (SelectedEntry is not { } entry) return;
        await RunAsync("Leyendo Parquet… La primera lectura puede preparar la extensión httpfs.", async token =>
        {
            ClearPreview(); var result = await parquet.PreviewRemoteAsync(SelectedProfile!, SelectedBucket!, entry.Key, token);
            ShowPreview(result, entry.Name);
        });
    }

    private void ChooseLocalPreview()
    {
        var dialog = new OpenFileDialog { Filter = "Archivos Parquet|*.parquet", Title = "Abrir Parquet local" };
        if (dialog.ShowDialog() != true) return;
        _ = RunAsync("Leyendo Parquet local…", async token => { ClearPreview(); ShowPreview(await parquet.PreviewLocalAsync(dialog.FileName, token), System.IO.Path.GetFileName(dialog.FileName)); });
    }

    private void ShowPreview(ParquetPreview result, string name)
    {
        PreviewRows = result.Rows.DefaultView; PreviewSchema = result.Schema.DefaultView;
        PreviewTitle = name; IsPreviewOpen = true;
        Status = $"Vista previa: {result.Rows.Rows.Count} filas · {result.Schema.Rows.Count} columnas. Máximo 200 filas, 64 columnas y 512 caracteres por celda.";
    }

    private void ChooseUpload()
    {
        var dialog = new OpenFileDialog { Multiselect = true, Title = "Subir archivos a la carpeta actual" };
        if (dialog.ShowDialog() != true) return;
        if (MessageBox.Show($"Subir {dialog.FileNames.Length} archivo(s) a {Location}. Si ya existe una clave con el mismo nombre, se reemplazará su contenido actual. ¿Continuar?", "Subir archivos", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _ = RunAsync("Subiendo archivos…", async token =>
        {
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
            Error = exception is ArgumentException ? exception.Message : "La operación no se completó. Revisa la conexión, el archivo y la disponibilidad de httpfs para Parquet remoto.";
            Status = "No se modificó la configuración de conexiones.";
        }
        finally { operation = null; IsBusy = false; }
    }

    public void Cancel() => operation?.Cancel();
    public void LoadDesignPreview()
    {
        Profiles.Add(new ConnectionProfile { Name = "MinIO local", Endpoint = "http://localhost:9000" });
        Profiles.Add(new ConnectionProfile { Name = "MinIO secundario", Endpoint = "http://localhost:9100" });
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
        ShowPreview(new ParquetPreview(designRows, designSchema, "demo"), "ventas.parquet");
        Notify(nameof(SelectedBucket)); Notify(nameof(ConnectionState)); storeReady = true; RefreshCommands();
    }
}
