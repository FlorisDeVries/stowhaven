using System.Net;
using System.Text.Json;
using System.ComponentModel.DataAnnotations;
using Azure.Storage.Blobs;
using Azure.Storage.Sas;
using Azure.Identity;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using BackupApi.Models;

namespace BackupApi;

public class SasUrlFunctions
{
    private readonly ILogger<SasUrlFunctions> _logger;
    private readonly string _dataStorageAccount;
    private readonly string _dataContainer;

    public SasUrlFunctions(ILogger<SasUrlFunctions> logger)
    {
        _logger = logger;
        _dataStorageAccount = Environment.GetEnvironmentVariable("DATA_STORAGE_ACCOUNT") 
            ?? throw new InvalidOperationException("DATA_STORAGE_ACCOUNT environment variable is required");
        _dataContainer = Environment.GetEnvironmentVariable("DATA_CONTAINER") ?? "backups";
    }

    /// <summary>
    /// Generates a SAS URL for uploading a blob to Azure Blob Storage.
    /// </summary>
    /// <param name="req"></param>
    /// <returns></returns>
    [Function("GetSasUpload")]
    public async Task<HttpResponseData> GetSasUpload(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "get-sas-upload")] HttpRequestData req)
    {
        SasRequest? requestData = null;
        try
        {
            // Validate API key
            if (!IsValidApiKey(req))
            {
                _logger.LogWarning("Unauthorized access attempt from {RemoteIpAddress}", 
                    req.Headers.GetValues("X-Forwarded-For").FirstOrDefault() ?? "unknown");
                var unauthorizedResponse = req.CreateResponse(HttpStatusCode.Unauthorized);
                await unauthorizedResponse.WriteStringAsync("Unauthorized");
                return unauthorizedResponse;
            }

            var requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            requestData = JsonSerializer.Deserialize<SasRequest>(requestBody, new JsonSerializerOptions 
            { 
                PropertyNameCaseInsensitive = true 
            });

            if (!TryGetValidPath(requestData, out var validPath))
            {
                _logger.LogWarning("Invalid path provided: {Path}", requestData?.Path);
                var badResponse = req.CreateResponse(HttpStatusCode.BadRequest);
                await badResponse.WriteStringAsync("Invalid path format");
                return badResponse;
            }

            var permissions = BlobSasPermissions.Create | BlobSasPermissions.Write | BlobSasPermissions.Add;
            var ttlMinutes = requestData?.TtlMinutes ?? 60;
            var sasUrl = await GenerateSasUrl(validPath, permissions, ttlMinutes);

            var response = req.CreateResponse(HttpStatusCode.OK);
            response.Headers.Add("Content-Type", "application/json");
            
            var responseData = new SasResponse 
            { 
                SasUrl = sasUrl,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(ttlMinutes),
                TtlMinutes = ttlMinutes
            };
            await response.WriteStringAsync(JsonSerializer.Serialize(responseData));
            
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating upload SAS URL for path: {Path}", requestData?.Path);
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            errorResponse.Headers.Add("Content-Type", "application/json");
            
            var error = new ErrorResponse 
            { 
                Error = "Failed to generate upload SAS URL",
                Details = ex.Message
            };
            await errorResponse.WriteStringAsync(JsonSerializer.Serialize(error));
            return errorResponse;
        }
    }

    /// <summary>
    /// Generates a SAS URL for downloading a blob from Azure Blob Storage.
    /// </summary>
    /// <param name="req"></param>
    /// <returns></returns>
    [Function("GetSasDownload")]
    public async Task<HttpResponseData> GetSasDownload(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "get-sas-download")] HttpRequestData req)
    {
        SasRequest? requestData = null;
        try
        {
            // Validate API key
            if (!IsValidApiKey(req))
            {
                _logger.LogWarning("Unauthorized access attempt from {RemoteIpAddress}", 
                    req.Headers.GetValues("X-Forwarded-For").FirstOrDefault() ?? "unknown");
                var unauthorizedResponse = req.CreateResponse(HttpStatusCode.Unauthorized);
                await unauthorizedResponse.WriteStringAsync("Unauthorized");
                return unauthorizedResponse;
            }
            var requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            requestData = JsonSerializer.Deserialize<SasRequest>(requestBody, new JsonSerializerOptions 
            { 
                PropertyNameCaseInsensitive = true 
            });

            if (!TryGetValidPath(requestData, out var validPath))
            {
                _logger.LogWarning("Invalid path provided: {Path}", requestData?.Path);
                var badResponse = req.CreateResponse(HttpStatusCode.BadRequest);
                await badResponse.WriteStringAsync("Invalid path format");
                return badResponse;
            }

            var permissions = BlobSasPermissions.Read;
            var ttlMinutes = requestData?.TtlMinutes ?? 60;
            var sasUrl = await GenerateSasUrl(validPath, permissions, ttlMinutes);

            var response = req.CreateResponse(HttpStatusCode.OK);
            response.Headers.Add("Content-Type", "application/json");
            
            var responseData = new SasResponse 
            { 
                SasUrl = sasUrl,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(ttlMinutes),
                TtlMinutes = ttlMinutes
            };
            await response.WriteStringAsync(JsonSerializer.Serialize(responseData));
            
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating download SAS URL for path: {Path}", requestData?.Path);
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            errorResponse.Headers.Add("Content-Type", "application/json");
            
            var error = new ErrorResponse 
            { 
                Error = "Failed to generate download SAS URL",
                Details = ex.Message
            };
            await errorResponse.WriteStringAsync(JsonSerializer.Serialize(error));
            return errorResponse;
        }
    }

    private async Task<string> GenerateSasUrl(string path, BlobSasPermissions permissions, int ttlMinutes)
    {
        var ttl = Math.Max(1, Math.Min(ttlMinutes, 240));
        var expiry = DateTimeOffset.UtcNow.AddMinutes(ttl);

        var credential = new DefaultAzureCredential();
        var blobServiceClient = new BlobServiceClient(
            new Uri($"https://{_dataStorageAccount}.blob.core.windows.net"),
            credential);

        // Get user delegation key for SAS generation
        var userDelegationKey = await blobServiceClient.GetUserDelegationKeyAsync(
            startsOn: DateTimeOffset.UtcNow.AddMinutes(-5),
            expiresOn: DateTimeOffset.UtcNow.AddHours(2));

        var blobClient = blobServiceClient.GetBlobContainerClient(_dataContainer).GetBlobClient(path);

        var sasBuilder = new BlobSasBuilder
        {
            BlobContainerName = _dataContainer,
            BlobName = path,
            Resource = "b",
            ExpiresOn = expiry
        };
        sasBuilder.SetPermissions(permissions);

        var sasToken = sasBuilder.ToSasQueryParameters(userDelegationKey, _dataStorageAccount);
        return $"{blobClient.Uri}?{sasToken}";
    }

    private bool IsValidApiKey(HttpRequestData req)
    {
        var expectedApiKey = Environment.GetEnvironmentVariable("API_KEY");
        if (string.IsNullOrEmpty(expectedApiKey))
        {
            _logger.LogError("API_KEY environment variable is not configured");
            return false;
        }

        // Check x-api-key header first, then code query parameter (for Function key compatibility)
        var providedKey = req.Headers.GetValues("x-api-key").FirstOrDefault()
            ?? req.Query["code"];

        return !string.IsNullOrEmpty(providedKey) && providedKey == expectedApiKey;
    }

    private static bool IsValidPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        // Prevent path traversal attacks
        if (path.Contains("..") || path.Contains("\\") || path.StartsWith("/"))
            return false;

        // Check for reasonable length
        if (path.Length > 1024)
            return false;

        // Allow only safe characters
        return path.All(c => char.IsLetterOrDigit(c) || "._-/".Contains(c));
    }

    private static bool TryGetValidPath(SasRequest? request, out string validPath)
    {
        validPath = string.Empty;
        if (request?.Path == null || !IsValidPath(request.Path))
            return false;
        
        validPath = request.Path;
        return true;
    }
}
