using FlorisDeV.BackupClient.Models;
using FlorisDeV.BackupContracts.Api.Responses;
using FlorisDeV.BackupContracts.State;

namespace FlorisDeV.BackupClient.Services;

public partial class BackupService
{
    private AttemptEvidence? _attemptEvidence;

    private sealed class AttemptEvidence(Guid id)
    {
        public Guid Id { get; } = id;
        public System.Collections.Concurrent.ConcurrentDictionary<Guid, BackupRunResult> Runs { get; } = [];
        public BackupScanEvidence? Scan { get; set; }
        public BackupStats? Stats { get; set; }
        public BackupRunResult? ScanBase { get; set; }
        public PendingBackupRun? PendingHeader { get; set; }
    }

    private async Task ObservePendingAsync(PendingBackupRun pending, CancellationToken cancellationToken)
    {
        if (_attemptEvidence is not { } attempt) return;
        attempt.PendingHeader = pending;
        var evidence = pending.Evidence ?? new BackupRunResult { RunId = pending.RunId };
        attempt.Runs[pending.RunId] = evidence with
        {
            RunId = pending.RunId, CommitId = pending.CommitId,
            AttemptIds = (evidence.AttemptIds ?? []).Append(attempt.Id).Distinct().ToArray(),
            ServerProcessing = pending.CommitId.HasValue || pending.ManifestUploaded ? "pending" : "notSubmitted"
        };
        attempt.ScanBase = pending.Evidence;
        attempt.PendingHeader = pending with { Evidence = attempt.Runs[pending.RunId] };
        await backupStateService.SavePendingBackupRunAsync(attempt.PendingHeader, cancellationToken);
        PublishRunEvidence();
    }

    private BackupRunResult? CaptureRun(Guid runId, Guid? commitId)
    {
        if (_attemptEvidence is not { } attempt) return null;
        if (!attempt.Runs.TryGetValue(runId, out var run))
            run = new BackupRunResult
            {
                RunId = runId, OriginAttemptId = attempt.Id, AttemptIds = [attempt.Id], ServerProcessing = "notSubmitted",
                ScanStartedAt = attempt.Scan?.StartedAt, SelectionFingerprint = attempt.Scan?.SelectionFingerprint,
                FilesSkipped = 0, FilesDeferred = 0, FilesFailed = 0, ScanCompleted = false
            };
        // Only the run currently being scanned receives this attempt's scan counters.
        if (attempt.Stats is { } stats && run.LocalCompletion != "completed")
        {
            var basis = attempt.ScanBase;
            run = run with
            {
                FilesSkipped = run.FilesSkipped == null ? null : (basis?.FilesSkipped ?? 0) + stats.SkippedCount + stats.SkippedChangedCount,
                FilesDeferred = run.FilesDeferred == null ? null : (basis?.FilesDeferred ?? 0) + stats.SkippedSasExpiredCount,
                FilesFailed = run.FilesFailed == null ? null : (basis?.FilesFailed ?? 0) + stats.TotalUploadFailures + (run.ServerFilesFailed ?? 0),
                ScanCompleted = run.ScanCompleted == null ? null : basis == null ? attempt.Scan?.Completed == true : basis.ScanCompleted == true && attempt.Scan?.Completed == true
            };
        }
        run = run with
        {
            CommitId = commitId ?? run.CommitId,
            ServerProcessing = commitId != null && run.ServerProcessing is "unknown" or "notSubmitted" ? "pending" : run.ServerProcessing,
            Cleanliness = run.FilesSkipped > 0 || run.FilesDeferred > 0 || run.FilesFailed > 0 ? "degraded" : run.Cleanliness
        };
        attempt.Runs[runId] = run;
        PublishRunEvidence();
        return run;
    }

    private void ObserveRunCompletion(Guid runId, CommitStatusResponse status, bool localCompleted)
    {
        if (_attemptEvidence is not { } attempt) return;
        var run = attempt.Runs.GetValueOrDefault(runId) ?? CaptureRun(runId, status.CommitId)!;
        var failed = status.Status == CommitJobStatus.Failed;
        var errors = status.Status == CommitJobStatus.CompletedWithErrors;
        var degraded = errors || run.FilesSkipped > 0 || run.FilesDeferred > 0 || run.FilesFailed > 0;
        var clean = !failed && localCompleted && !degraded && run.ScanCompleted == true &&
            run.ScanStartedAt.HasValue && run.SelectionFingerprint != null && run.FilesSkipped == 0 && run.FilesDeferred == 0 && run.FilesFailed == 0;
        attempt.Runs[runId] = run with
        {
            CommitId = status.CommitId,
            ServerProcessing = failed ? "failed" : errors ? "completedWithErrors" : "succeeded",
            LocalCompletion = failed ? "notCompleted" : localCompleted ? "completed" : "pending",
            Outcome = failed ? "failed" : !localCompleted ? "pending" : degraded ? "completedWithErrors" : "succeeded",
            FilesFailed = run.FilesFailed.HasValue ? run.FilesFailed - (run.ServerFilesFailed ?? 0) + (status.FilesFailed ?? 0) : null,
            ServerFilesFailed = status.FilesFailed,
            Cleanliness = degraded ? "degraded" : clean ? "clean" : "unknown"
        };
        PublishRunEvidence();
    }

    private void ObserveDiscardedRun(Guid runId)
    {
        if (_attemptEvidence is not { } attempt) return;
        attempt.Runs[runId] = attempt.Runs[runId] with { Outcome = "failed", LocalCompletion = "notCompleted" };
        attempt.PendingHeader = null;
        attempt.ScanBase = null;
        PublishRunEvidence();
    }

    private void PublishRunEvidence()
    {
        if (_attemptEvidence is { } attempt) _dashboardLive?.RunEvidence(attempt.Runs.Values.ToArray(), attempt.Scan);
    }

    private async Task PreserveInterruptedEvidenceAsync()
    {
        if (_attemptEvidence is not { PendingHeader: { } pending } attempt) return;
        var durable = await backupStateService.GetPendingBackupRunAsync(pending.DeviceId, CancellationToken.None);
        if (durable?.RunId != pending.RunId) return; // Completion may already have committed; never resurrect it.
        var evidence = CaptureRun(pending.RunId, pending.CommitId);
        // Cancellation of an attempt must not erase the earlier scan's degradation. This updates
        // only the recovery header; real local completion remains one existing atomic transaction.
        await backupStateService.SavePendingBackupRunAsync(pending with { Evidence = evidence }, CancellationToken.None);
    }

    private BackupResult AttachEvidence(BackupResult result)
    {
        if (_attemptEvidence is not { } attempt) return result;
        var degraded = attempt.Runs.Values.Any(x => x.Outcome is "failed" or "completedWithErrors" || x.Cleanliness == "degraded");
        return result with
        {
            AttemptId = attempt.Id, Runs = attempt.Runs.Values.ToArray(), CurrentScan = attempt.Scan,
            Outcome = degraded && result.Outcome is BackupOutcome.Succeeded or BackupOutcome.NoChanges ? BackupOutcome.CompletedWithErrors : result.Outcome
        };
    }
}
