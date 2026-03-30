using Azure.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Sas;

// ─────────────────────────────────────────────────────────────────────────────
// Azurite SAS URL diagnostic – mirrors exactly what SasUrlService + FileUploader
// do in production so failures here map 1-to-1 to production failures.
// ─────────────────────────────────────────────────────────────────────────────

// Well-known Azurite defaults
const string accountName = "devstoreaccount1";
const string accountKey  = "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";
const string blobEndpoint = "http://127.0.0.1:10000/devstoreaccount1";
const string containerName = "backups";

// Replica of the test file (≈ /home/fdev/.bash_logout)
const string testFilePath = "home/fdev/.bash_logout";
const string testFileContent = "# ~/.bash_logout: executed by bash(1) when login shell exits.";

// ─── Step 1: ensure container exists (using account key, no SAS) ────────────
Console.WriteLine("=== Azurite SAS URL diagnostic ===\n");
var credential          = new StorageSharedKeyCredential(accountName, accountKey);
var serviceClient       = new BlobServiceClient(new Uri(blobEndpoint), credential);
var adminContainer      = serviceClient.GetBlobContainerClient(containerName);
await adminContainer.CreateIfNotExistsAsync();
Console.WriteLine($"[setup] Container '{containerName}' ready");

// ─── Simulated values that would come from the API ──────────────────────────
var deviceId = Guid.NewGuid();
var runId    = Guid.NewGuid();
var dirPath  = $"staging/{deviceId}/{runId}";   // same pattern as BackupRunService

// ─── Step 2: build the SAS URL exactly as SasUrlService does ────────────────
Console.WriteLine($"[api]   Building container SAS (Resource=c) for basePath: {dirPath}");

var expiresAt  = DateTimeOffset.UtcNow.AddHours(1);
var sasBuilder = new BlobSasBuilder
{
    BlobContainerName = containerName,
    Resource          = "c",  // Container-level; "d" requires ADLS Gen2 HNS
    StartsOn          = DateTimeOffset.UtcNow.AddMinutes(-5),
    ExpiresOn         = expiresAt,
    Protocol          = SasProtocol.HttpsAndHttp
};
sasBuilder.SetPermissions(BlobContainerSasPermissions.Create | BlobContainerSasPermissions.Write);

var sasToken = sasBuilder.ToSasQueryParameters(credential).ToString();

// Container-level SAS URL (no directory path embedded in URL — path lives in BasePath)
var sasUrl = new Uri($"{blobEndpoint}/{containerName}?{sasToken}");
Console.WriteLine($"[api]   SAS URL  : {sasUrl}");
Console.WriteLine($"[api]   BasePath : {dirPath}\n");

// ─── Step 3: translate Docker hostname → 127.0.0.1 (TranslateStorageUrlForLocalDevelopment)
var translatedUrl = new Uri(
    sasUrl.ToString()
          .Replace("http://azurite:", "http://127.0.0.1:", StringComparison.OrdinalIgnoreCase));
Console.WriteLine($"[client] Translated URL: {translatedUrl}");

// ─── Step 4: construct BlobContainerClient exactly as BackupService does ────
var containerClient = new BlobContainerClient(translatedUrl);
Console.WriteLine($"[client] BlobContainerClient.Name : {containerClient.Name}");
Console.WriteLine($"[client] BlobContainerClient.Uri  : {containerClient.Uri}");

// ─── Step 5: build blobPath as FileUploader does ────────────────────────────
//   storagePath = relative path stripped of leading slash
//   blobPath    = basePath + "/" + storagePath
var storagePath = testFilePath.TrimStart('/');
var blobPath    = $"{dirPath}/{storagePath}";
Console.WriteLine($"[uploader] blobPath: {blobPath}");

var blobClient  = containerClient.GetBlobClient(blobPath);
Console.WriteLine($"[uploader] BlobClient.Uri: {blobClient.Uri}\n");

// ─── Step 6: upload ─────────────────────────────────────────────────────────
Console.WriteLine("--- Approach A: directory-SAS via BlobContainerClient.GetBlobClient (production path) ---");
await TryUploadToClient(blobClient, "approach-A");

// ─── Step 7: container-level SAS as a known-good baseline ───────────────────
Console.WriteLine("\n--- Approach B: container-SAS via BlobContainerClient (known-good baseline) ---");
var containerSasBuilder = new BlobSasBuilder
{
    BlobContainerName = containerName,
    Resource          = "c",
    StartsOn          = DateTimeOffset.UtcNow.AddMinutes(-5),
    ExpiresOn         = DateTimeOffset.UtcNow.AddHours(1),
    Protocol          = SasProtocol.HttpsAndHttp
};
containerSasBuilder.SetPermissions(BlobContainerSasPermissions.Create | BlobContainerSasPermissions.Write);
var containerSasToken = containerSasBuilder.ToSasQueryParameters(credential).ToString();
var containerSasUrl   = new Uri($"{blobEndpoint}/{containerName}?{containerSasToken}");
var containerSasClient = new BlobContainerClient(containerSasUrl);
await TryUploadToClient(containerSasClient.GetBlobClient(blobPath), "approach-B");

// ─── Step 8: direct BlobClient (no BlobContainerClient indirection) ─────────
Console.WriteLine("\n--- Approach C: directory-SAS via direct BlobClient (no container wrapper) ---");
var directBlobUrl    = new Uri($"{blobEndpoint}/{containerName}/{blobPath}?{sasToken}");
var directBlobClient = new BlobClient(directBlobUrl);
Console.WriteLine($"[approach-C] BlobClient.Uri: {directBlobClient.Uri}");
await TryUploadToClient(directBlobClient, "approach-C");

Console.WriteLine("\n=== Diagnostic complete ===");

// ─── Helper ─────────────────────────────────────────────────────────────────
async Task TryUploadToClient(BlobClient client, string label)
{
    try
    {
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(testFileContent));
        await client.UploadAsync(stream, overwrite: true);
        Console.WriteLine($"  [{label}] ✓ SUCCESS");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  [{label}] ✗ FAILED: {ex.Message}");
        if (ex is Azure.RequestFailedException rfe)
            Console.WriteLine($"           Status={rfe.Status}  ErrorCode={rfe.ErrorCode}");
    }
}
