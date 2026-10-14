namespace SmartQueue.Api.Domain;

/// <summary>A recurring weekly session, for example Monday Morning 09:00–13:00 with 40 online bookings.</summary>
public class SessionTemplate
{
    public long Id { get; set; }
    public Guid DoctorId { get; set; }
    public Guid ClinicId { get; set; }
    public DayOfWeek DayOfWeek { get; set; }
    public required string Name { get; set; }
    public TimeOnly StartTime { get; set; }
    public TimeOnly EndTime { get; set; }
    public int Capacity { get; set; }
}

/// <summary>Date-specific rules that take precedence over the weekly template (PRD FR-08).</summary>
public class ScheduleException
{
    public long Id { get; set; }
    public Guid DoctorId { get; set; }
    public Guid ClinicId { get; set; }
    public ScheduleExceptionKind Kind { get; set; }
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }

    /// <summary>Null means every session on the affected dates.</summary>
    public string? SessionName { get; set; }

    public TimeOnly? StartTime { get; set; }
    public TimeOnly? EndTime { get; set; }
    public int? Capacity { get; set; }
    public string? Reason { get; set; }
    public Guid? AnnouncementId { get; set; }
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? RemovedAt { get; set; }

    public bool Covers(DateOnly date, string sessionName) =>
        RemovedAt is null && date >= StartDate && date <= EndDate &&
        (SessionName is null || string.Equals(SessionName, sessionName, StringComparison.OrdinalIgnoreCase));
}

public class Announcement
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid DoctorId { get; set; }
    public Guid ClinicId { get; set; }
    public AnnouncementType Type { get; set; }
    public required string Message { get; set; }
    public DateTimeOffset StartsAt { get; set; }
    public DateTimeOffset? EndsAt { get; set; }
    public int? DelayMinutes { get; set; }
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? RemovedAt { get; set; }

    public bool ClosesClinic => Type is AnnouncementType.OutOfClinic or AnnouncementType.Leave
        or AnnouncementType.Holiday or AnnouncementType.ClinicClosure;

    public bool IsCurrentOrUpcoming(DateTimeOffset now) => RemovedAt is null && (EndsAt is null ? StartsAt.AddDays(1) > now : EndsAt > now);
}
