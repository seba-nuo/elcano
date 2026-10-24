using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Transfer;
using System.Diagnostics;

namespace MinioDesk.Core;

public sealed class S3Service : IS3Service
{
    public static AmazonS3Client CreateClient(ConnectionProfile profile)
    {
        profile.Validate();
        return new AmazonS3Client(new BasicAWSCredentials(profile.AccessKey, profile.SecretKey), new AmazonS3Config
        {
            ServiceURL = profile.Endpoint.TrimEnd('/'), ForcePathStyle = true,
            AuthenticationRegion = profile.Region, Timeout = TimeSpan.FromSeconds(45), MaxErrorRetry = 2
        });
    }

    public async Task<IReadOnlyList<string>> ListBucketsAsync(ConnectionProfile profile, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(profile.Bucket))
        {
            // This also supports credentials that cannot list all buckets.
            await ListAsync(profile, profile.Bucket, "", null, cancellationToken);
            return [profile.Bucket];
        }
        using var client = CreateClient(profile);
        var result = await client.ListBucketsAsync(new ListBucketsRequest(), cancellationToken);
        return (result.Buckets ?? []).Select(bucket => bucket.BucketName).OrderBy(name => name, StringComparer.Ordinal).ToArray();
    }

    public async Task<ObjectPage> ListAsync(ConnectionProfile profile, string bucket, string prefix, string? token, CancellationToken cancellationToken)
    {
        using var client = CreateClient(profile);
        var result = await client.ListObjectsV2Async(new ListObjectsV2Request
        {
            BucketName = bucket, Prefix = prefix, Delimiter = "/", MaxKeys = 200, ContinuationToken = token
        }, cancellationToken);
        var entries = (result.CommonPrefixes ?? []).Select(key => new ObjectEntry(key, key[prefix.Length..].TrimEnd('/'), true, 0, null))
            .Concat((result.S3Objects ?? []).Where(item => item.Key != prefix).Select(item =>
                new ObjectEntry(item.Key, item.Key[prefix.Length..], false, item.Size ?? 0, item.LastModified)))
            .OrderByDescending(entry => entry.IsFolder).ThenBy(entry => entry.Name, StringComparer.Ordinal).ToArray();
        return new ObjectPage(entries, result.IsTruncated == true ? result.NextContinuationToken : null);
    }

    public async Task UploadAsync(ConnectionProfile profile, string bucket, string key, string path,
        IProgress<TransferProgress>? progress, CancellationToken cancellationToken)
    {
        using var client = CreateClient(profile);
        using var transfer = new TransferUtility(client, new TransferUtilityConfig { ConcurrentServiceRequests = 2 });
        var clock = Stopwatch.StartNew();
        var request = new TransferUtilityUploadRequest { BucketName = bucket, Key = key, FilePath = path, PartSize = 8 * 1024 * 1024 };
        request.UploadProgressEvent += (_, args) =>
        {
            if (clock.ElapsedMilliseconds < 100 && args.TransferredBytes < args.TotalBytes) return;
            clock.Restart();
            progress?.Report(new TransferProgress(args.TransferredBytes, args.TotalBytes));
        };
        await transfer.UploadAsync(request, cancellationToken);
    }

    public async Task<FolderTotals> CountFolderAsync(ConnectionProfile profile, string bucket, string prefix, CancellationToken token)
    {
        using var client = CreateClient(profile);
        long files = 0, folders = 0, listed = 0;
        string? continuation = null;
        do
        {
            var page = await client.ListObjectsV2Async(new ListObjectsV2Request
            { BucketName = bucket, Prefix = prefix, Delimiter = "/", MaxKeys = 1000, ContinuationToken = continuation }, token);
            token.ThrowIfCancellationRequested();
            var objects = page.S3Objects ?? [];
            var prefixes = page.CommonPrefixes ?? [];
            files += objects.LongCount(item => item.Key != prefix);
            folders += prefixes.Count;
            // Include the current folder marker: it also consumes a listing slot.
            listed += objects.Count + prefixes.Count;
            continuation = page.IsTruncated == true ? page.NextContinuationToken : null;
            if (page.IsTruncated == true && string.IsNullOrEmpty(continuation))
                throw new InvalidDataException("El servidor no devolvió un token para continuar el recuento.");
        } while (continuation is not null);
        return new FolderTotals(files, folders, Math.Max(1, (listed + 199) / 200));
    }

    public async Task DownloadAsync(ConnectionProfile profile, string bucket, string key, string path,
        IProgress<TransferProgress>? progress, CancellationToken cancellationToken)
    {
        using var client = CreateClient(profile);
        using var response = await client.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = key }, cancellationToken);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, true))
            {
                var buffer = new byte[64 * 1024];
                long done = 0;
                var clock = Stopwatch.StartNew();
                int count;
                while ((count = await response.ResponseStream.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                    done += count;
                    if (clock.ElapsedMilliseconds >= 100) { progress?.Report(new TransferProgress(done, response.ContentLength)); clock.Restart(); }
                }
                progress?.Report(new TransferProgress(done, response.ContentLength));
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public async Task DeleteAsync(ConnectionProfile profile, string bucket, string key, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(key) || key.EndsWith('/'))
            throw new ArgumentException("Selecciona un archivo; no se eliminan carpetas ni prefijos.");
        using var client = CreateClient(profile);
        await client.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = key }, cancellationToken);
    }
}
