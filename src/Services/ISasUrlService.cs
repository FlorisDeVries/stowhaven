using BackupApi.Models;

namespace BackupApi.Services;

public interface ISasUrlService
{
    Task<SasResponse> GenerateUploadSasUrlAsync(string path, int? ttlMinutes = null);
    Task<SasResponse> GenerateDownloadSasUrlAsync(string path, int? ttlMinutes = null);
}
