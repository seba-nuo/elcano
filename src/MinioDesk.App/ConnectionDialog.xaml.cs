using System.Windows;
using MinioDesk.Core;

namespace MinioDesk.App;

public partial class ConnectionDialog : Window
{
    private readonly Guid id;
    private readonly CancellationTokenSource cancellation = new();
    public ConnectionProfile? Result { get; private set; }
    public ConnectionDialog(ConnectionProfile? existing)
    {
        InitializeComponent();
        id = existing?.Id ?? Guid.NewGuid();
        NameInput.Text = existing?.Name ?? "MinIO local";
        EndpointInput.Text = existing?.Endpoint ?? "http://localhost:9000";
        RegionInput.Text = existing?.Region ?? "us-east-1";
        BucketInput.Text = existing?.Bucket ?? "";
        AccessInput.Text = existing?.AccessKey ?? "";
        SecretInput.Password = existing?.SecretKey ?? "";
        Closed += (_, _) => cancellation.Cancel();
    }
    private ConnectionProfile ReadProfile()
    {
        var profile = new ConnectionProfile { Id = id, Name = NameInput.Text.Trim(), Endpoint = EndpointInput.Text.Trim().TrimEnd('/'),
            Region = RegionInput.Text.Trim(), Bucket = BucketInput.Text.Trim(), AccessKey = AccessInput.Text.Trim(), SecretKey = SecretInput.Password };
        profile.Validate(); return profile;
    }
    private void UseFirstLocal(object sender, RoutedEventArgs e) { EndpointInput.Text = "http://localhost:9000"; NameInput.Text = "MinIO local"; }
    private void UseSecondLocal(object sender, RoutedEventArgs e) { EndpointInput.Text = "http://localhost:9100"; NameInput.Text = "MinIO secundario"; }
    private void SaveConnection(object sender, RoutedEventArgs e)
    {
        try { Result = ReadProfile(); DialogResult = true; }
        catch (ArgumentException exception) { Feedback.Text = exception.Message; }
    }
    private async void TestConnection(object sender, RoutedEventArgs e)
    {
        try
        {
            var profile = ReadProfile();
            Fields.IsEnabled = false; TestButton.IsEnabled = false; SaveButton.IsEnabled = false;
            Feedback.Text = "Comprobando acceso…";
            var buckets = await new S3Service().ListBucketsAsync(profile, cancellation.Token);
            Feedback.Text = $"Conexión correcta: {buckets.Count} bucket(s) autorizado(s).";
        }
        catch (ArgumentException exception) { Feedback.Text = exception.Message; }
        catch (OperationCanceledException) { Feedback.Text = "Prueba cancelada."; }
        catch { Feedback.Text = "No se pudo conectar. Revisa endpoint, credenciales y permisos. Si el acceso está limitado, indica un bucket fijo."; }
        finally { Fields.IsEnabled = true; TestButton.IsEnabled = true; SaveButton.IsEnabled = true; }
    }
}
