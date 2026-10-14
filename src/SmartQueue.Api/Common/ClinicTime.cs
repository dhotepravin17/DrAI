namespace SmartQueue.Api.Common;

/// <summary>All clinics operate in Indian Standard Time (PRD §6.1). Times are stored as UTC offsets.</summary>
public static class ClinicTime
{
    public static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");

    public static DateTimeOffset Local(DateTimeOffset instant) => TimeZoneInfo.ConvertTime(instant, Zone);

    public static DateOnly Today(TimeProvider clock) => DateOnly.FromDateTime(Local(clock.GetUtcNow()).DateTime);

    public static DateOnly DateOf(DateTimeOffset instant) => DateOnly.FromDateTime(Local(instant).DateTime);

    public static DateTimeOffset At(DateOnly date, TimeOnly time)
    {
        var local = date.ToDateTime(time);
        return new DateTimeOffset(local, Zone.GetUtcOffset(local));
    }

    public static string Describe(DateOnly date, string sessionName) => $"{date:ddd d MMM} {sessionName}";
}
