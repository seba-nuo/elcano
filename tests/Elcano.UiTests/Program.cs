using System.ComponentModel;
using System.Data;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DuckDB.NET.Data;
using Elcano.App;
using Elcano.Core;

namespace Elcano.UiTests;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var output = Path.GetFullPath(args.FirstOrDefault() ?? "TestResults/UI"); Directory.CreateDirectory(output);
        var app = new Application();
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Elcano;component/Themes/Controls.xaml", UriKind.Relative) });
        ThemeManager.Apply(true);
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var storage = new FakeStorage(); storage.Objects["a/sub/archivo.txt"] = [1]; storage.Objects["b/archivo.txt"] = [1]; storage.Objects["denegado/x.txt"] = [1];
        for (var i = 0; i < 450; i++) storage.Objects[$"pages/{i:D4}.txt"] = [1];
        var vm = new MainViewModel(storage, new ProfileStore(output), new ClientPreferencesStore(output), new DiagnosticLog(Path.Combine(output, "logs")));
        vm.PrefetchEnabled = false;
        var window = new MainWindow(vm) { Left = -16000, Top = -16000, ShowInTaskbar = false, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual };
        window.Show(); Pump(window);
        try
        {
            Check(window.Title == "Elcano" && typeof(MainWindow).Assembly.GetName().Name == "Elcano", "La ventana y el ejecutable usan el nombre Elcano");
            var file = Path.Combine(output, "ui.parquet");
            using (var db = new DuckDBConnection("Data Source=:memory:"))
            {
                db.Open(); using var command = db.CreateCommand();
                command.CommandText = $"COPY (SELECT i AS importe, i AS \"total-ventas\", NULL::VARCHAR AS nulo, '' AS vacio FROM (VALUES (10),(2),(100)) t(i)) TO {ParquetService.Literal(file.Replace('\\', '/'))} (FORMAT PARQUET);"; command.ExecuteNonQuery();
            }
            Wait(vm.OpenLocalAsync(file)); Pump(window);
            var grid = (DataGrid)window.FindName("PreviewGrid");
            Check(grid.Columns.Select(column => column.Header?.ToString()).SequenceEqual(new[] { "importe", "total-ventas", "nulo", "vacio" }), "La tabla conserva las columnas dinámicas del Parquet");
            typeof(DataGrid).GetMethod("PerformSort", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(grid, [grid.Columns[0]]); Pump(window);
            Check(grid.Items.Cast<DataRowView>().Select(row => row[0].ToString()).SequenceEqual(new[] { "2", "10", "100" }), "La tabla ordena números según su tipo");
            Check(grid.Columns[0].SortDirection == ListSortDirection.Ascending, "La cabecera refleja la dirección de orden");
            var editor = (SqlEditor)window.FindName("QueryEditor"); var list = (ListBox)window.FindName("CompletionList");
            editor.Text = "SELECT \"total-ventas\" FROM archivo"; editor.CaretIndex = 21;
            var suggestion = new SqlSuggestion("total-ventas", "\"total-ventas\"", "BIGINT"); list.ItemsSource = new[] { suggestion }; list.SelectedItem = suggestion;
            typeof(MainWindow).GetMethod("InsertCompletion", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null);
            Check(editor.Text == "SELECT \"total-ventas\" FROM archivo", "Autocompletar no duplica el identificador entre comillas");
            vm.QueryText = ParquetQuery.Default; Pump(window);
            Check(Descendants(grid).OfType<TextBox>().Any(text => text.Text == "NULL" && text.FontStyle == FontStyles.Italic), "NULL tiene representación distinta del texto vacío");
            foreach (var dark in new[] { true, false })
            {
                ThemeManager.Apply(dark); window.Width = 1080; window.Height = 680; Pump(window);
                var headers = Descendants(grid).OfType<DataGridColumnHeader>().Where(header => header.Column is not null).SelectMany(Descendants).OfType<TextBlock>().Select(text => text.Text).ToArray();
                Check(headers.Contains("total-ventas"), "El nombre de la columna se conserva en ambos temas");
                Save(window, Path.Combine(output, dark ? "dark.png" : "light.png"));
            }
            var schema = new DataTable(); schema.Columns.Add("Columna"); schema.Columns.Add("Tipo");
            for (var i = 0; i < 70; i++) schema.Rows.Add("col_" + i, "BIGINT");
            var picker = new ColumnsDialog(schema, ["col_69"]) { Owner = window, Left = -16000, Top = -16000, WindowStartupLocation = WindowStartupLocation.Manual };
            app.Dispatcher.BeginInvoke(() => { Check(picker.SelectedColumns.SequenceEqual(new[] { "col_69" }), "Selector permite elegir columnas posteriores a la 64"); picker.DialogResult = true; });
            Check(picker.ShowDialog() == true, "Selector confirma la elección de columnas");
            vm.SelectedProfile = new ConnectionProfile { Name = "Pruebas", Endpoint = "http://localhost:9000", AccessKey = "test", SecretKey = "test-secret" };
            vm.ConnectCommand.Execute(null); Wait(Idle(vm));
            vm.Filter = "pages"; vm.SelectedEntry = vm.Entries.Single(); vm.OpenSelected(); Wait(Idle(vm));
            Check(vm.Filter == "" && vm.Entries.Count == 200, "Una carpeta nueva no hereda un filtro que oculte sus archivos");
            vm.NextPageCommand.Execute(null); Wait(Idle(vm)); vm.SelectedEntry = vm.Entries[10]; var selected = vm.SelectedEntry.Key; vm.RememberExplorerScroll(10);
            vm.UpCommand.Execute(null); Wait(Idle(vm));
            Check(vm.Filter == "pages" && vm.SelectedEntry?.Key == "pages/", "Volver conserva filtro y selección del padre");
            vm.OpenSelected(); Wait(Idle(vm));
            Check(vm.PageLabel == "Página 2 de 3" && vm.SelectedEntry?.Key == selected && vm.ExplorerScrollOffset == 10, "Volver conserva página, selección y posición del explorador");
            vm.Filter = "sin-resultados"; Check(vm.Entries.Count == 0 && vm.ExplorerEmptyMessage.Contains("Sin coincidencias"), "El estado vacío distingue un filtro sin resultados"); vm.ClearFilterCommand.Execute(null);
            Check(vm.Entries.Count == 200, "El filtro se puede limpiar con una acción");
            Check(vm.ExplorerCacheLabel.StartsWith("Caché"), "Se identifica un listado recuperado de memoria");
            vm.UpCommand.Execute(null); Wait(Idle(vm)); vm.Filter = "";
            var before = storage.Calls.GetValueOrDefault("b/"); Wait(Task.Delay(2200));
            Check(storage.Calls.GetValueOrDefault("b/") == before, "Desactivar la precarga evita peticiones especulativas");
            vm.PrefetchEnabled = true; Wait(Until(() => storage.Calls.GetValueOrDefault("b/") > before)); vm.PrefetchEnabled = false;
            Check(!new ClientPreferencesStore(output).Load().PrefetchEnabled, "La preferencia se guarda al cambiarla");
            vm.SelectedEntry = vm.Entries.Single(entry => entry.Key == "denegado/"); vm.OpenSelected(); Wait(Idle(vm));
            Check(vm.HasError && vm.DiagnosticDetails.Length > 0 && !vm.DiagnosticDetails.Contains("test-secret") && !vm.DiagnosticDetails.Contains("SELECT"), "Los errores ofrecen un diagnóstico sin secretos ni consultas");
            Console.WriteLine("Pruebas WPF completas. Capturas: " + output); return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
        finally { vm.StopBackgroundWork(); window.Close(); app.Shutdown(); }
    }
    private static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); Console.WriteLine("PASS " + message); }
    private static void Pump(Window window) { window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle); window.UpdateLayout(); }
    private static void Wait(Task task)
    { if (!task.IsCompleted) { var frame = new DispatcherFrame(); _ = task.ContinueWith(_ => Application.Current.Dispatcher.BeginInvoke(() => frame.Continue = false)); Dispatcher.PushFrame(frame); } task.GetAwaiter().GetResult(); }
    private static async Task Idle(MainViewModel vm) => await Until(() => vm.IsIdle);
    private static async Task Until(Func<bool> condition) { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)); while (!condition()) await Task.Delay(10, timeout.Token); }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    { for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) { var child = VisualTreeHelper.GetChild(parent, i); yield return child; foreach (var nested in Descendants(child)) yield return nested; } }
    private static void Save(Window window, string path)
    { var content = (FrameworkElement)window.Content; var bitmap = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(content); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(path); encoder.Save(stream); }
}
