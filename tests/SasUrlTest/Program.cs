using Azure.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Sas;

Console.WriteLine("=== Azurite SAS URL Test ===\n");

// Azurite connection details (well-known credentials)
const string azuriteConnectionString = "DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;BlobEndpoint=http://127.0.0.1:10000/devstoreaccount1;";
const string containerName = "backups";
const string accountName = "devstoreaccount1";
const string accountKey = "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

// Create blob service client
var blobServiceClient = new BlobServiceClient(azuriteConnectionString);
var containerClient = blobServiceClient.GetBlobContainerClient(containerName);

// Ensure container exists
await containerClient.CreateIfNotExistsAsync();
Console.WriteLine($"✓ Container '{containerName}' ready\n");

// Test scenarios
await TestContainerLevelSas();
await TestDirectoryLevelSas();
await TestPathRestrictions();

async Task TestContainerLevelSas()
{
    Console.WriteLine("--- Test 1: Container-Level SAS ---");
    
    var sasBuilder = new BlobSasBuilder
    {
        BlobContainerName = containerName,
        Resource = "c", // Container
        StartsOn = DateTimeOffset.UtcNow.AddMinutes(-10),
        ExpiresOn = DateTimeOffset.UtcNow.AddHours(4),
        Protocol = SasProtocol.HttpsAndHttp
    };
    sasBuilder.SetPermissions(BlobContainerSasPermissions.Read | BlobContainerSasPermissions.Add | BlobContainerSasPermissions.Create | BlobContainerSasPermissions.Write);
    
    var credential = new StorageSharedKeyCredential(accountName, accountKey);
    var sasToken = sasBuilder.ToSasQueryParameters(credential).ToString();
    var sasUrl = $"http://127.0.0.1:10000/devstoreaccount1/{containerName}?{sasToken}";
    
    Console.WriteLine($"SAS URL: {sasUrl}\n");
    
    // Try uploading to different paths
    await TryUpload(sasUrl, "test-container-level.txt", "Container level test");
    await TryUpload(sasUrl, "staging/device1/run1/file1.txt", "Container level with path");
    await TryUpload(sasUrl, "staging/device2/run1/file2.txt", "Container level different device");
    
    Console.WriteLine();
}

async Task TestDirectoryLevelSas()
{
    Console.WriteLine("--- Test 2: Directory-Level SAS ---");
    
    var sasBuilder = new BlobSasBuilder
    {
        BlobContainerName = containerName,
        BlobName = "staging/device1/run1/", // Directory path
        Resource = "d", // Directory
        StartsOn = DateTimeOffset.UtcNow.AddMinutes(-5),
        ExpiresOn = DateTimeOffset.UtcNow.AddHours(1),
        Protocol = SasProtocol.HttpsAndHttp
    };
    sasBuilder.SetPermissions(BlobSasPermissions.Read | BlobSasPermissions.Create | BlobSasPermissions.Write);
    
    var credential = new StorageSharedKeyCredential(accountName, accountKey);
    var sasToken = sasBuilder.ToSasQueryParameters(credential).ToString();
    var sasUrl = $"http://127.0.0.1:10000/devstoreaccount1/{containerName}/staging/device1/run1/?{sasToken}";
    
    Console.WriteLine($"SAS URL: {sasUrl}\n");
    
    // Try uploading within allowed path
    await TryUpload(sasUrl, "file1.txt", "Directory level - within path");
    await TryUpload(sasUrl, "subdir/file2.txt", "Directory level - subdir within path");
    
    // Try uploading outside allowed path (should fail with proper SAS)
    await TryUpload(sasUrl, "../run2/file3.txt", "Directory level - parent escape attempt");
    await TryUpload(sasUrl, "../../device2/run1/file4.txt", "Directory level - different device attempt");
    
    Console.WriteLine();
}

async Task TestPathRestrictions()
{
    Console.WriteLine("--- Test 3: Path Restriction Enforcement ---");
    
    // Create a directory-level SAS for device1/run1
    var sasBuilder = new BlobSasBuilder
    {
        BlobContainerName = containerName,
        BlobName = "staging/device1/run1/",
        Resource = "d",
        StartsOn = DateTimeOffset.UtcNow.AddMinutes(-5),
        ExpiresOn = DateTimeOffset.UtcNow.AddHours(1),
        Protocol = SasProtocol.HttpsAndHttp
    };
    sasBuilder.SetPermissions(BlobSasPermissions.Read | BlobSasPermissions.Create | BlobSasPermissions.Write);
    
    var credential = new StorageSharedKeyCredential(accountName, accountKey);
    var sasToken = sasBuilder.ToSasQueryParameters(credential).ToString();
    
    // Try to use SAS token on different path
    var unauthorizedUrl = $"http://127.0.0.1:10000/devstoreaccount1/{containerName}/staging/device2/run1/?{sasToken}";
    Console.WriteLine("Attempting to use device1 SAS token on device2 path...");
    await TryUpload(unauthorizedUrl, "hack.txt", "Cross-device access attempt");
    
    Console.WriteLine();
}

async Task TryUpload(string sasUrl, string blobPath, string description)
{
    try
    {
        Console.WriteLine($"  [{description}]");
        Console.WriteLine($"  Blob: {blobPath}");
        
        // Parse URL to get base path - IMPORTANT: preserve port!
        var uri = new Uri(sasUrl);
        Console.WriteLine($"  Original URI: {uri}");
        Console.WriteLine($"  Host: {uri.Host}, Port: {uri.Port}");
        
        var baseUrl = $"{uri.Scheme}://{uri.Host}:{uri.Port}{uri.AbsolutePath}";
        var sasToken = uri.Query;
        
        // Create blob URL
        var blobUrl = $"{baseUrl.TrimEnd('/')}/{blobPath}{sasToken}";
        Console.WriteLine($"  Target URL: {blobUrl}");
        
        var blobClient = new BlobClient(new Uri(blobUrl));
        Console.WriteLine($"  BlobClient URI: {blobClient.Uri}");
        
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes($"Test content: {description}"));
        await blobClient.UploadAsync(stream, overwrite: true);
        
        Console.WriteLine($"  ✓ SUCCESS\n");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  ✗ FAILED: {ex.Message}\n");
    }
}

Console.WriteLine("=== Test Complete ===");
