using FlorisDeV.BackupApi.Options;
using FlorisDeV.BackupApi.Services;
using FlorisDeV.BackupContracts.Api.Requests;
using FlorisDeV.BackupContracts.Api.Responses;
using FlorisDeV.BackupContracts.State;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace FlorisDeV.BackupApi.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public partial class BackupController(
    IBackupRunService backupRunService,
    IDeviceAuthorizationService deviceAuthorizationService,
    IOptions<SasSecurityOptions> sasSecurityOptions,
    ILogger<BackupController> logger
) : ControllerBase
{
    [HttpPost("/api/devices/{deviceId:guid}/backup/start-run")]
    [ProducesResponseType(typeof(StartBackupRunResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<StartBackupRunResponse>> StartBackupRun(
        Guid deviceId,
        CancellationToken cancellationToken
    )
    {
        using var scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["DeviceId"] = deviceId,
            ["Operation"] = "StartBackupRun"
        });

        // Validate request
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        await deviceAuthorizationService.AuthorizeDeviceAsync(User, deviceId, cancellationToken);

        LogStartingBackupRun(logger, deviceId);

        var clientIp = sasSecurityOptions.Value.EnableIpRestriction
            ? HttpContext.Connection.RemoteIpAddress?.ToString()
            : null;

        // Start backup-run - let exceptions bubble up to GlobalExceptionFilter
        var result = await backupRunService.StartBackupRunAsync(deviceId, clientIp, cancellationToken);

        var response = new StartBackupRunResponse
        {
            DeviceId = result.Run.DeviceId,
            RunId = result.Run.RunId,
            StartedAt = result.Run.StartedAt,
            Status = result.Run.Status,
            SasUrlInfo = result.SasUrl,
            ManifestSasUrlInfo = result.ManifestSasUrl
        };

        LogBackupRunStartedSuccess(logger, result.Run.RunId, result.Run.DeviceId);

        return Ok(response);
    }

    [HttpPost("/api/devices/{deviceId:guid}/backup/runs/{runId:guid}/refresh-sas")]
    [ProducesResponseType(typeof(RefreshSasUrlResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<RefreshSasUrlResponse>> RefreshBackupRunSas(
        Guid deviceId,
        Guid runId,
        CancellationToken cancellationToken)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["DeviceId"] = deviceId,
            ["RunId"] = runId,
            ["Operation"] = "RefreshBackupRunSas"
        });

        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        await deviceAuthorizationService.AuthorizeDeviceAsync(User, deviceId, cancellationToken);

        LogRefreshingBackupRunSas(logger, runId, deviceId);

        var clientIp = sasSecurityOptions.Value.EnableIpRestriction
            ? HttpContext.Connection.RemoteIpAddress?.ToString()
            : null;

        // Re-issue SAS URLs for the existing run - let exceptions bubble up to GlobalExceptionFilter
        var result = await backupRunService.RefreshSasUrlsAsync(deviceId, runId, clientIp, cancellationToken);

        var response = new RefreshSasUrlResponse
        {
            DeviceId = result.DeviceId,
            RunId = result.RunId,
            SasUrlInfo = result.SasUrl,
            ManifestSasUrlInfo = result.ManifestSasUrl
        };

        LogBackupRunSasRefreshed(logger, runId, deviceId);

        return Ok(response);
    }

    [HttpPost("/api/devices/{deviceId:guid}/backup/commit-run")]
    [ProducesResponseType(typeof(CommitBackupRunResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<CommitBackupRunResponse>> CommitBackupRun(
        Guid deviceId,
        [FromBody] CommitBackupRunRequest request,
        CancellationToken cancellationToken)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["DeviceId"] = deviceId,
            ["Operation"] = "CommitBackupRun"
        });

        // Validate request
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        await deviceAuthorizationService.AuthorizeDeviceAsync(User, deviceId, cancellationToken);

        LogStartCommitBackupRun(logger, request.RunId, deviceId);

        // Create commit job for async processing - let exceptions bubble up to GlobalExceptionFilter
        var commitJob = await backupRunService.CommitBackupRunAsync(
            deviceId,
            request.RunId,
            cancellationToken);

        var response = new CommitBackupRunResponse
        {
            CommitId = commitJob.CommitId,
            DeviceId = commitJob.DeviceId,
            RunId = commitJob.RunId,
            Status = commitJob.Status,
            CreatedAt = commitJob.CreatedAt
        };

        LogCommitBackupRunAccepted(logger, commitJob.CommitId, request.RunId, deviceId);

        return AcceptedAtAction(
            nameof(GetCommitStatus),
            new { deviceId, commitId = commitJob.CommitId },
            response);
    }

    [HttpGet("/api/devices/{deviceId:guid}/backup/commit-status/{commitId:guid}")]
    [ProducesResponseType(typeof(CommitStatusResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<CommitStatusResponse>> GetCommitStatus(
        Guid deviceId,
        Guid commitId,
        CancellationToken cancellationToken)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["CommitId"] = commitId,
            ["Operation"] = "GetCommitStatus"
        });

        LogGetCommitStatus(logger, commitId);

        await deviceAuthorizationService.AuthorizeDeviceAsync(User, deviceId, cancellationToken);

        // Get commit job status - let exceptions bubble up to GlobalExceptionFilter
        var commitJob = await backupRunService.GetCommitStatusAsync(commitId, cancellationToken);

        if (commitJob.DeviceId != deviceId)
        {
            return NotFound();
        }

        var response = new CommitStatusResponse
        {
            CommitId = commitJob.CommitId,
            DeviceId = commitJob.DeviceId,
            RunId = commitJob.RunId,
            Status = commitJob.Status,
            Error = commitJob.Error,
            CreatedAt = commitJob.CreatedAt,
            UpdatedAt = commitJob.UpdatedAt,
            CompletedAt = commitJob.CompletedAt,
            TotalFiles = commitJob.TotalFiles,
            FilesProcessed = commitJob.Status is CommitJobStatus.Succeeded or CommitJobStatus.CompletedWithErrors ? commitJob.FilesProcessed : null,
            FilesFailed = commitJob.FilesFailed,
            AttemptCount = commitJob.AttemptCount,
            FailureCategory = commitJob.FailureCategory,
            LastErrorAt = commitJob.LastErrorAt,
            NextRetryAt = commitJob.NextRetryAt,
            DeadLetteredAt = commitJob.DeadLetteredAt
        };

        LogCommitStatusRetrieved(logger, commitId, commitJob.Status);

        return Ok(response);
    }

    [HttpGet("/api/devices/{deviceId:guid}/backup/commit-status/{commitId:guid}/failed-files")]
    [ProducesResponseType(typeof(ListCommitFileProgressResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ListCommitFileProgressResponse>> ListFailedCommitFiles(
        Guid deviceId,
        Guid commitId,
        [FromQuery] int pageSize = 100,
        [FromQuery] string? continuationToken = null,
        CancellationToken cancellationToken = default)
    {
        await deviceAuthorizationService.AuthorizeDeviceAsync(User, deviceId, cancellationToken);

        var commitJob = await backupRunService.GetCommitStatusAsync(commitId, cancellationToken);
        if (commitJob.DeviceId != deviceId)
        {
            return NotFound();
        }

        var page = await backupRunService.GetFailedCommitFilesPageAsync(
            deviceId,
            commitId,
            pageSize,
            continuationToken,
            cancellationToken);

        return Ok(new ListCommitFileProgressResponse
        {
            CommitId = commitId,
            DeviceId = deviceId,
            RunId = commitJob.RunId,
            Files = page.Files.Select(file => new CommitFileProgressResponse
            {
                CommitId = file.CommitId,
                DeviceId = file.DeviceId,
                RunId = file.RunId,
                UniqueFileId = file.UniqueFileId,
                LogicalPath = file.LogicalPath,
                Status = file.Status,
                UpdatedAt = file.UpdatedAt,
                Error = file.Error
            }).ToArray(),
            PageSize = page.PageSize,
            ContinuationToken = page.ContinuationToken,
            NextContinuationToken = page.NextContinuationToken
        });
    }

    #region Logging

    [LoggerMessage(LogLevel.Information, "Starting backup run for device {deviceId}")]
    static partial void LogStartingBackupRun(ILogger<BackupController> logger, Guid deviceId);

    [LoggerMessage(LogLevel.Information, "Backup run {runId} started successfully for device {deviceId}")]
    static partial void LogBackupRunStartedSuccess(ILogger<BackupController> logger,
        Guid runId, Guid deviceId);

    [LoggerMessage(LogLevel.Information, "Refreshing SAS URLs for backup run {runId} on device {deviceId}")]
    static partial void LogRefreshingBackupRunSas(ILogger<BackupController> logger, Guid runId, Guid deviceId);

    [LoggerMessage(LogLevel.Information, "Refreshed SAS URLs for backup run {runId} on device {deviceId}")]
    static partial void LogBackupRunSasRefreshed(ILogger<BackupController> logger, Guid runId, Guid deviceId);

    [LoggerMessage(LogLevel.Information, "Committing backup run {runId} for device {deviceId}")]
    static partial void LogStartCommitBackupRun(ILogger<BackupController> logger, Guid runId, Guid deviceId);

    [LoggerMessage(LogLevel.Information, "Backup run {runId} for device {deviceId} accepted for commit. CommitId: {commitId}")]
    static partial void LogCommitBackupRunAccepted(ILogger<BackupController> logger, Guid commitId, Guid runId, Guid deviceId);

    [LoggerMessage(LogLevel.Information, "Retrieving commit status for commit {commitId}")]
    static partial void LogGetCommitStatus(ILogger<BackupController> logger, Guid commitId);

    [LoggerMessage(LogLevel.Information, "Commit {commitId} status: {status}")]
    static partial void LogCommitStatusRetrieved(ILogger<BackupController> logger, Guid commitId, CommitJobStatus status);

    #endregion
}
