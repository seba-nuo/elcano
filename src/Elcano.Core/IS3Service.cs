namespace Elcano.Core;

public interface IS3Service
{
    Task<IReadOnlyList<string>> ListBucketsAsync(ConnectionProfile profile, CancellationToken cancellationToken);
    Task<ObjectPage> ListAsync(ConnectionProfile profile, string bucket, string prefix, string? token, CancellationToken cancellationToken);
    Task<FolderTotals> CountFolderAsync(ConnectionProfile profile, string bucket, string prefix, CancellationToken token);
    Task UploadAsync(ConnectionProfile profile, string bucket, string key, string path, IProgress<TransferProgress>? progress, CancellationToken cancellationToken);
    Task DownloadAsync(ConnectionProfile profile, string bucket, string key, string path, IProgress<TransferProgress>? progress, CancellationToken cancellationToken);
    Task DeleteAsync(ConnectionProfile profile, string bucket, string key, CancellationToken cancellationToken);
}
