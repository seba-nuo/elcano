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
    private void DeleteSelected()
    {
        if (SelectedEntry is { IsFolder: true } folder) { _ = DeleteFolderAsync(folder); return; }
        DeleteFile();
    }

    private async Task DeleteFolderAsync(ObjectEntry entry)
    {
        if (SelectedProfile is not { } profile || SelectedBucket is not { } bucket) return;
        await RunAsync("Revisando el contenido de la carpeta…", async token =>
        {
            using var plan = await folders.PrepareAsync(profile, bucket, entry.Key, token);
            token.ThrowIfCancellationRequested();
            if (MessageBox.Show(Application.Current.MainWindow,
                $"¿Eliminar la carpeta «{entry.Name}» y sus {plan.Files:N0} archivos, incluidas todas sus subcarpetas?\n\nConexión: {profile.Name}\nRuta: s3://{bucket}/{entry.Key}\n\nSi cancelas durante la eliminación, lo ya eliminado no se restaura.",
                "Eliminar carpeta y su contenido", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            { Status = "Eliminación de carpeta cancelada."; return; }
            InvalidateCache(); CancelFolderCount();
            if (previewTarget is { } opened && opened.Profile?.Id == profile.Id && opened.Bucket == bucket && opened.Key.StartsWith(entry.Key, StringComparison.Ordinal)) ClearPreview();
            try
            {
                var reporter = new Progress<FolderProgress>(value =>
                { Progress = value.Percent; Status = $"Eliminando carpeta: {value.Completed:N0} de {value.Total:N0} objetos."; });
                await folders.DeleteAsync(profile, plan, reporter, token);
                Status = $"Carpeta eliminada: {entry.Name} · {plan.Files:N0} archivos.";
            }
            finally { await RefreshAfterFolderChangeAsync(profile, bucket); }
        });
    }

    private async Task RefreshAfterFolderChangeAsync(ConnectionProfile profile, string bucket)
    {
        // Use a separate token to reflect completed batches even after a cancellation.
        using var refresh = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var previousStatus = Status;
        try
        {
            pageTokens.Clear(); pageTokens.Add(null); pageIndex = 0;
            var page = await LoadPageAsync(profile, bucket, prefix, null, refresh.Token);
            SetPage(page); StartFolderCount(page); Status = previousStatus;
        }
        catch { Status = previousStatus + " Pulsa Actualizar para renovar la lista."; }
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
            var page = await LoadPageAsync(profile, bucket, prefix, null, token); SetPage(page); StartFolderCount(page);
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
        if (SelectedEntry is { IsFolder: true } folder) { ChooseFolderDownload(folder); return; }
        if (SelectedEntry is not { IsFolder: false } entry) return;
        var dialog = new SaveFileDialog { FileName = System.IO.Path.GetFileName(entry.Name), Title = "Descargar archivo", OverwritePrompt = true };
        if (dialog.ShowDialog() != true) return;
        _ = RunAsync($"Descargando {entry.Name}…", async token =>
        {
            await s3.DownloadAsync(SelectedProfile!, SelectedBucket!, entry.Key, dialog.FileName, new Progress<TransferProgress>(value => Progress = value.Percent), token);
            Status = $"Descarga terminada: {dialog.FileName}";
        });
    }

    private void ChooseFolderDownload(ObjectEntry entry)
    {
        if (SelectedProfile is not { } profile || SelectedBucket is not { } bucket) return;
        var name = new string(entry.Name.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character).ToArray());
        var dialog = new SaveFileDialog { FileName = (name.Length > 0 ? name : "carpeta") + ".zip", Filter = "Archivo ZIP|*.zip", DefaultExt = ".zip", AddExtension = true, Title = "Descargar carpeta completa", OverwritePrompt = true };
        if (dialog.ShowDialog() != true) return;
        _ = RunAsync("Revisando archivos para descargar la carpeta…", async token =>
        {
            using var plan = await folders.PrepareAsync(profile, bucket, entry.Key, token);
            var reporter = new Progress<FolderProgress>(value =>
            { Progress = value.Percent; Status = $"Descargando carpeta: {value.Completed:N0} de {value.Total:N0} objetos · {ObjectEntry.FormatSize(value.Bytes)}."; });
            await folders.DownloadZipAsync(profile, plan, dialog.FileName, reporter, token);
            Status = $"Carpeta descargada: {dialog.FileName}";
        });
    }

}
