using System.Globalization;

namespace Atsm.Diagnostics;

public static class DiagnosticDuration
{
    public static string Format(TimeSpan? duration)
    {
        if (duration is null) return "—";
        var signed = duration.Value.Ticks;
        var ticks = signed < 0 ? (ulong)(-(signed + 1)) + 1 : (ulong)signed;
        return string.Create(CultureInfo.InvariantCulture, $"{(signed < 0 ? "-" : "")}{ticks / TimeSpan.TicksPerHour:00}:{ticks / TimeSpan.TicksPerMinute % 60:00}:{ticks / TimeSpan.TicksPerSecond % 60:00}.{ticks / TimeSpan.TicksPerMillisecond % 1000:000}");
    }
}
