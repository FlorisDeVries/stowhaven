using FlorisDeV.BackupContracts.State;
using Microsoft.Extensions.Logging;

namespace FlorisDeV.BackupApi.Services;

public partial class ManifestManager
{
    [LoggerMessage(LogLevel.Information, "Updated backup run {runId} for device {deviceId} with status {status}")]
    static partial void LogBackupRunUpdated(ILogger logger, Guid runId, Guid deviceId, BackupRunStatus status);

    [LoggerMessage(LogLevel.Information, "Created backup run {runId} for device {deviceId}")]
    static partial void LogBackupRunCreated(ILogger logger, Guid runId, Guid deviceId);

    [LoggerMessage(LogLevel.Warning, "Concurrent update detected for backup run {runId} of device {deviceId}. ETag: {etag}")]
    static partial void LogConcurrentUpdateDetected(ILogger logger, Guid runId, Guid deviceId, string etag);

    [LoggerMessage(LogLevel.Information, "Committed backup run {runId} for device {deviceId} with status {status}")]
    static partial void LogBackupRunCommitted(ILogger logger, Guid runId, Guid deviceId, BackupRunStatus status);

    [LoggerMessage(LogLevel.Information, "Created commit job {commitId} for device {deviceId}, run {runId}")]
    static partial void LogCommitJobCreated(ILogger logger, Guid commitId, Guid deviceId, Guid runId);

    [LoggerMessage(LogLevel.Information, "Commit job {commitId} already exists for device {deviceId}, run {runId} with status {status}")]
    static partial void LogCommitJobAlreadyExists(ILogger logger, Guid commitId, Guid deviceId, Guid runId, CommitJobStatus status);

    [LoggerMessage(LogLevel.Information, "Claimed commit job {commitId} for processing")]
    static partial void LogCommitJobClaimed(ILogger logger, Guid commitId);

    [LoggerMessage(LogLevel.Information, "Updated commit job {commitId} with status {status}")]
    static partial void LogCommitJobUpdated(ILogger logger, Guid commitId, CommitJobStatus status);

    [LoggerMessage(LogLevel.Warning, "Concurrent update detected for commit job {commitId}. ETag: {etag}")]
    static partial void LogConcurrentCommitJobUpdate(ILogger logger, Guid commitId, string etag);

    [LoggerMessage(LogLevel.Warning, "Concurrent update detected for commit file progress {commitId}/{uniqueFileId}. ETag: {etag}")]
    static partial void LogConcurrentCommitFileUpdate(ILogger logger, Guid commitId, string uniqueFileId, string etag);

    [LoggerMessage(LogLevel.Debug, "Saved commit file progress {commitId}/{uniqueFileId} with status {status}")]
    static partial void LogCommitFileProgressSaved(ILogger logger, Guid commitId, string uniqueFileId, CommitFileStatus status);

    [LoggerMessage(LogLevel.Information, "Saved file entry for path {relativePath} on device {deviceId}")]
    static partial void LogFileEntrySaved(ILogger logger, string relativePath, Guid deviceId);

    [LoggerMessage(LogLevel.Warning, "Concurrent update detected for file entry {relativePath} on device {deviceId}. ETag: {etag}")]
    static partial void LogConcurrentFileEntryUpdate(ILogger logger, string relativePath, Guid deviceId, string etag);

    [LoggerMessage(LogLevel.Information, "Saved file version {uniqueFileId} on device {deviceId}")]
    static partial void LogFileVersionSaved(ILogger logger, string uniqueFileId, Guid deviceId);

    [LoggerMessage(LogLevel.Warning, "Concurrent update detected for file version {uniqueFileId} on device {deviceId}. ETag: {etag}")]
    static partial void LogConcurrentFileVersionUpdate(ILogger logger, string uniqueFileId, Guid deviceId, string etag);

    [LoggerMessage(LogLevel.Information, "Queried all file entries for device {deviceId}")]
    static partial void LogFileEntriesQueried(ILogger logger, Guid deviceId);
}
