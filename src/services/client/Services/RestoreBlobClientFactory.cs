using Azure.Storage.Blobs;

namespace FlorisDeV.BackupClient.Services;

public interface IRestoreBlobClientFactory
{
    BlobContainerClient Create(Uri url);
}

public sealed class RestoreBlobClientFactory : IRestoreBlobClientFactory
{
    public BlobContainerClient Create(Uri url) => new(url);
}
