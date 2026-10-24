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
    public void Initialize()
    {
        try
        {
            foreach (var profile in store.Load()) Profiles.Add(profile);
            storeReady = true; SelectedProfile = Profiles.FirstOrDefault(); RefreshCommands();
        }
        catch (Exception exception) { RecordFailure(exception, nameof(Initialize)); Error = "No se pudieron leer las conexiones guardadas. Conserva connections.json y revisa que uses el mismo usuario de Windows; no se sobrescribirá el archivo."; }
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
        catch (Exception exception) { RecordFailure(exception, nameof(EditProfile)); Error = "No se pudo guardar la conexión cifrada. Revisa el acceso a la carpeta de configuración."; }
    }

    private void RemoveProfile()
    {
        if (SelectedProfile is not { } profile) return;
        if (MessageBox.Show($"¿Quitar la conexión «{profile.Name}»? Los archivos del servidor se conservan.", "Quitar conexión", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try { store.Save(Profiles.Where(item => item.Id != profile.Id)); Profiles.Remove(profile); SelectedProfile = Profiles.FirstOrDefault(); }
        catch (Exception exception) { RecordFailure(exception, nameof(RemoveProfile)); Error = "No se pudo actualizar la configuración de conexiones."; }
    }

    private async Task ConnectAsync(CancellationToken token)
    {
        InvalidateCache();
        connected = false; NotifyConnection();
        ClearPage(); ClearPreview(); Buckets.Clear(); selectedBucket = null; Notify(nameof(SelectedBucket)); NotifyLocation();
        IsExplorerLoading = true;
        var buckets = await s3.ListBucketsAsync(SelectedProfile!, token);
        foreach (var bucket in buckets) Buckets.Add(bucket);
        connected = true; NotifyConnection(); selectedBucket = Buckets.FirstOrDefault(); Notify(nameof(SelectedBucket)); prefix = "";
        if (selectedBucket is not null) await ReloadAsync(token);
        else { NotifyLocation(); Status = "Conexión correcta. No hay buckets visibles para estas credenciales."; }
    }

}
