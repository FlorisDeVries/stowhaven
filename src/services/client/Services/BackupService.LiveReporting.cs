using Azure.Storage.Blobs;
using FlorisDeV.BackupClient.Dashboard;
using FlorisDeV.BackupClient.Models;

namespace FlorisDeV.BackupClient.Services;

public partial class BackupService
{
    private DashboardLiveAttempt? _dashboardLive;

    private void ObserveLiveCounts(BackupStats stats)
        => _dashboardLive?.Update(p => p with { FilesUnchanged = stats.UnchangedCount,
            FilesSkipped = stats.SkippedCount + stats.SkippedChangedCount,
            FilesDeferred = stats.SkippedSasExpiredCount, FilesFailed = stats.TotalUploadFailures }, stats.TotalScanned);

    private async Task<UploadBatchResult> UploadObservedAsync(BlobContainerClient client, IReadOnlyList<TaggedFile> files, CancellationToken token)
    {
        _dashboardLive?.Update(p => p with { Uploading = true });
        try
        {
            var result = await uploader.UploadFilesAsync(client, files, token);
            foreach (var file in result.FailedFiles.Take(result.OtherFailureCount)) Diagnose("failed", file);
            _diagnostics?.Missing("failed", Math.Max(0, result.OtherFailureCount - result.FailedFiles.Count));
            _dashboardLive?.Update(p => p with { FilesUploaded = p.FilesUploaded + result.Uploaded.Count,
                UploadedBytes = p.UploadedBytes + result.Uploaded.Sum(file => file.GetUploadSizeBytes()) });
            return result;
        }
        finally { _dashboardLive?.Update(p => p with { Uploading = false }); }
    }
}
