namespace SmartQueue.Api.Domain;

/// <summary>
/// A notification written in the same transaction as the change that caused it and delivered later
/// by the background dispatcher (transactional outbox, PRD §19.4).
/// </summary>
public class OutboxMessage
{
    public long Id { get; set; }
    public required string EventType { get; set; }
    public required string DedupKey { get; set; }
    public required string PayloadJson { get; set; }
    public OutboxStatus Status { get; set; } = OutboxStatus.Pending;
    public int Attempts { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }

    /// <summary>Time-sensitive alerts (such as "Patient called") are not retried after this time.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    public DateTimeOffset? ProcessedAt { get; set; }
    public string? LastError { get; set; }
}

/// <summary>An in-app notification shown in the user's notification list.</summary>
public class Notification
{
    public long Id { get; set; }
    public Guid UserId { get; set; }
    public required string EventType { get; set; }
    public required string Title { get; set; }
    public required string Body { get; set; }
    public Guid? AppointmentId { get; set; }
    public long OutboxMessageId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ReadAt { get; set; }
}

/// <summary>One delivery attempt record per recipient and channel, used for failure monitoring.</summary>
public class NotificationLog
{
    public long Id { get; set; }
    public long OutboxMessageId { get; set; }
    public required string EventType { get; set; }
    public Guid? UserId { get; set; }
    public NotificationChannel Channel { get; set; }
    public DeliveryStatus Status { get; set; }
    public string? Error { get; set; }
    public string? ProviderReference { get; set; }
    public Guid? AppointmentId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public class PushSubscription
{
    public long Id { get; set; }
    public Guid UserId { get; set; }
    public required string Endpoint { get; set; }
    public required string P256dh { get; set; }
    public required string Auth { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
