namespace SmartQueue.Api.Domain;

/// <summary>The queue for one doctor, clinic, date and session. All queue changes lock this row first.</summary>
public class QueueSession
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid DoctorId { get; set; }
    public Guid ClinicId { get; set; }
    public DateOnly Date { get; set; }
    public required string Name { get; set; }
    public DateTimeOffset StartsAt { get; set; }
    public DateTimeOffset EndsAt { get; set; }
    public int Capacity { get; set; }
    public SessionState State { get; set; } = SessionState.Scheduled;
    public int TokenCounter { get; set; }
    public int CompletionCount { get; set; }
    public long NextSequence { get; set; }
    public int DelayMinutes { get; set; }
    public DateTimeOffset? OpenedAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public int LockVersion { get; set; }

    public bool AcceptsOnlineBookings => State is SessionState.Scheduled or SessionState.Open;
    public bool IsFinished => State is SessionState.Closed or SessionState.Cancelled;
}

public class Appointment
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string Reference { get; set; }
    public Guid QueueSessionId { get; set; }
    public Guid DoctorId { get; set; }
    public Guid ClinicId { get; set; }
    public Guid PatientId { get; set; }
    public DateOnly Date { get; set; }
    public required string SessionName { get; set; }

    /// <summary>The account or staff member who created the booking. The receiving person is <see cref="PatientId"/>.</summary>
    public Guid? BookedByUserId { get; set; }

    public BookingSource Source { get; set; }
    public AppointmentStatus Status { get; set; } = AppointmentStatus.Booked;
    public QueuePriority Priority { get; set; } = QueuePriority.Normal;
    public EmergencyReason? EmergencyReason { get; set; }
    public string? EmergencyNote { get; set; }
    public DateTimeOffset? PrioritizedAt { get; set; }

    /// <summary>Taken from the session sequence when prioritized, so emergencies are served in the order they were flagged.</summary>
    public long? PrioritySequence { get; set; }
    public int TokenNumber { get; set; }
    public long QueueSequence { get; set; }
    public int MissCount { get; set; }

    /// <summary>The session's completion count when the turn was missed; requeue happens N completions later.</summary>
    public int? MissedAtCompletion { get; set; }

    public int CallCount { get; set; }
    public DateTimeOffset? CalledAt { get; set; }
    public DateTimeOffset? CheckedInAt { get; set; }
    public DateTimeOffset? ConsultStartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
    public CancellationReason? CancellationReason { get; set; }
    public DateTimeOffset? TurnApproachingNotifiedAt { get; set; }
    public string? FollowUpNote { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public string Token => $"T-{TokenNumber:D3}";

    public bool IsWaiting => Status is AppointmentStatus.Booked or AppointmentStatus.CheckedIn or AppointmentStatus.Requeued;
    public bool IsBeingServed => Status is AppointmentStatus.Called or AppointmentStatus.InConsultation;
    public bool IsActive => IsWaiting || IsBeingServed || Status == AppointmentStatus.Missed;
}

/// <summary>Immutable history of every appointment state change (PRD §29.2).</summary>
public class AppointmentEvent
{
    public long Id { get; set; }
    public Guid AppointmentId { get; set; }
    public Guid QueueSessionId { get; set; }
    public required string Type { get; set; }
    public AppointmentStatus? FromStatus { get; set; }
    public AppointmentStatus? ToStatus { get; set; }
    public Guid? ActorUserId { get; set; }
    public string? Reason { get; set; }
    public int CompletionCount { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}

public class IdempotencyRecord
{
    public long Id { get; set; }
    public Guid UserId { get; set; }
    public required string Key { get; set; }
    public Guid AppointmentId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
