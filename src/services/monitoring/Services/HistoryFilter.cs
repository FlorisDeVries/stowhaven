namespace FlorisDeV.BackupMonitoring.Services;

public sealed record HistoryFilter(string? Status = null, Guid? DeviceId = null, string? Since = null);

public sealed class PageCursor
{
    private readonly Stack<string?> previous = new();
    public string? Current { get; private set; }
    public int Page => previous.Count + 1;
    public bool CanGoBack => previous.Count > 0;
    public void Next(string token) { previous.Push(Current); Current = token; }
    public void Back() { Current = previous.Pop(); }
    public void Reset() { previous.Clear(); Current = null; }
}
