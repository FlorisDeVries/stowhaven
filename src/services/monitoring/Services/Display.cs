using System.Globalization;

namespace FlorisDeV.BackupMonitoring.Services;

public static class Display
{
    public static string Short(Guid id) => id.ToString("N")[..8];
    public static string Time(DateTimeOffset? date) => date?.UtcDateTime.ToString("dd MMM yyyy · HH:mm:ss 'UTC'", CultureInfo.InvariantCulture) ?? "—";
    public static string Number(int? value) => value?.ToString("N0", CultureInfo.InvariantCulture) ?? "—";
    public static string Status(object value) => value.ToString() switch
    {
        "CompletedWithErrors" => "With errors",
        "StateUpdated" => "State updated",
        var text => text ?? "Unknown"
    };
    public static string Tone(object value) => value.ToString() switch
    {
        "Succeeded" => "success", "Failed" => "danger", "CompletedWithErrors" => "warning",
        "Processing" => "info", _ => "neutral"
    };
    public static int Percent(int? processed, int total) => total <= 0 ? 0 : (int)Math.Clamp((processed ?? 0) * 100L / total, 0, 100);
    public static string Bytes(long bytes) => bytes >= 1_048_576 ? $"{bytes / 1_048_576d:N1} MB" : $"{bytes / 1024d:N1} KB";
}
