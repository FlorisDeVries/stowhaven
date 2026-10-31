namespace FlorisDeV.BackupClient.Dashboard;

/// <summary>Portable observations; null totals and estimates mean the producer does not know them.</summary>
public sealed record DashboardLiveProgress
{
    public bool Active { get; init; }
    public bool ApiWaking { get; init; }
    public bool Scanning { get; init; }
    public bool Uploading { get; init; }
    public DateTimeOffset HeartbeatAt { get; init; }
    public int FilesUnchanged { get; init; }
    public int FilesUploaded { get; init; }
    public int FilesSkipped { get; init; }
    public int FilesDeferred { get; init; }
    public int FilesFailed { get; init; }
    public long UploadedBytes { get; init; }
    public string? CurrentTarget { get; init; }
    public double ElapsedSeconds { get; init; }
    public double AverageBytesPerSecond { get; init; }
    public double AverageFilesPerSecond { get; init; }
    public int? TotalFilesToScan { get; init; }
    public long? TotalBytesToUpload { get; init; }
    public double? UploadPercent { get; init; }
    public double? EstimatedSecondsRemaining { get; init; }
    public string[] Warnings { get; init; } = [];

    internal string[] TrustedWarnings() => new[]
    {
        FilesSkipped > 0 ? "Some files were skipped. Review the selected files and retry." : null,
        FilesDeferred > 0 ? "Some files were deferred and need another backup attempt." : null,
        FilesFailed > 0 ? "Some files failed to upload and need another backup attempt." : null
    }.OfType<string>().ToArray();
}

// One independent heartbeat owns all live writes. File callbacks only update bounded in-memory counters.
internal sealed class DashboardLiveAttempt(DashboardOptions options, DashboardSnapshot initial, TimeProvider time) : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private DashboardSnapshot _snapshot = initial;
    private readonly DateTimeOffset _startedAt = initial.StartedAt;
    private DashboardLiveProgress _progress = new() { Active = true, ApiWaking = true };
    private Task _heartbeat = Task.CompletedTask;
    private DateTimeOffset? _lastWriteAt;
    private readonly PeriodicTimer _timer = new(TimeSpan.FromSeconds(1), time);

    internal async Task StartAsync()
    {
        await WriteAsync();
        _heartbeat = HeartbeatAsync();
    }
    internal void Update(Func<DashboardLiveProgress, DashboardLiveProgress> update, int? scanned = null)
    {
        lock (_gate)
        {
            _progress = update(_progress);
            if (scanned != null) _snapshot = _snapshot with { FilesScanned = scanned.Value };
        }
    }
    internal void ScanStarted(DateTimeOffset started, string fingerprint, string evidence)
    {
        lock (_gate)
        {
            _snapshot = _snapshot with { ScanStartedAt = started, SelectionFingerprint = fingerprint, ServerBackupEvidence = evidence };
            _progress = _progress with { ApiWaking = false, Scanning = true };
        }
    }
    internal void RunEvidence(FlorisDeV.BackupClient.Models.BackupRunResult[] runs, FlorisDeV.BackupClient.Models.BackupScanEvidence? scan)
    {
        lock (_gate) _snapshot = _snapshot with { Runs = runs, CurrentScan = scan };
    }
    internal int FilesScanned { get { lock (_gate) return _snapshot.FilesScanned; } }

    internal DashboardLiveProgress Capture(bool active)
    {
        lock (_gate)
        {
            var now = time.GetUtcNow();
            var elapsed = Math.Max(0, (now - _startedAt).TotalSeconds);
            return _progress with { Active = active, ApiWaking = active && _progress.ApiWaking,
                Scanning = active && _progress.Scanning, Uploading = active && _progress.Uploading,
                HeartbeatAt = now, ElapsedSeconds = elapsed,
                AverageBytesPerSecond = elapsed > 0 ? _progress.UploadedBytes / elapsed : 0,
                AverageFilesPerSecond = elapsed > 0 ? _progress.FilesUploaded / elapsed : 0, Warnings = _progress.TrustedWarnings() };
        }
    }
    private async Task WriteAsync()
    {
        DashboardSnapshot snapshot;
        lock (_gate)
        {
            var now = time.GetUtcNow();
            if (_lastWriteAt is { } last && now - last < TimeSpan.FromSeconds(1)) return;
            _lastWriteAt = now;
            var progress = Capture(true);
            snapshot = _snapshot with { ObservedAt = progress.HeartbeatAt, Live = progress };
        }
        try { await DashboardReporting.WriteAsync(options, snapshot); }
        catch (Exception) { /* Optional telemetry cannot stop backup or the heartbeat. */ }
    }
    private async Task HeartbeatAsync()
    {
        try { while (await _timer.WaitForNextTickAsync(_stop.Token)) await WriteAsync(); }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _timer.Dispose();
        await _heartbeat; // A terminal writer must never race a late active heartbeat.
        _stop.Dispose();
    }
}
