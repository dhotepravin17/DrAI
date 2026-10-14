using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SmartQueue.Api.Domain;
using SmartQueue.Api.Infrastructure;

namespace SmartQueue.Api.Features.Notifications;

/// <summary>What the dispatcher needs to deliver one notification. Content must not include medical details (PRD §19.3).</summary>
public sealed record NotificationPayload(
    IReadOnlyList<Guid> UserIds, string? SmsMobile, string Title, string Body, Guid? AppointmentId, string? Url);

public static class NotificationEvents
{
    public const string AppointmentConfirmed = "AppointmentConfirmed";
    public const string TurnApproaching = "TurnApproaching";
    public const string PatientCalled = "PatientCalled";
    public const string TurnMissed = "TurnMissed";
    public const string Requeued = "Requeued";
    public const string AppointmentCancelled = "AppointmentCancelled";
    public const string ClinicClosed = "ClinicClosed";
    public const string QueueDelay = "QueueDelay";
    public const string ScheduleChanged = "ScheduleChanged";
    public const string Prioritized = "QueueReordered";
    public const string DoctorVerified = "DoctorVerified";
    public const string DoctorRejected = "DoctorRejected";
    public const string LinkInvitation = "LinkInvitation";
    public const string StaffInvitation = "StaffInvitation";
}

/// <summary>Writes notifications to the outbox in the caller's unit of work, so they are saved only if the change is.</summary>
public sealed class NotificationOutbox(AppDbContext db, TimeProvider clock)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task EnqueueAsync(string eventType, string dedupKey, NotificationPayload payload, CancellationToken ct,
        TimeSpan? expiresAfter = null)
    {
        if (payload.UserIds.Count == 0 && payload.SmsMobile is null) return;
        if (db.OutboxMessages.Local.Any(m => m.DedupKey == dedupKey) ||
            await db.OutboxMessages.AnyAsync(m => m.DedupKey == dedupKey, ct))
            return;

        var now = clock.GetUtcNow();
        db.OutboxMessages.Add(new OutboxMessage
        {
            EventType = eventType,
            DedupKey = dedupKey,
            PayloadJson = JsonSerializer.Serialize(payload, Json),
            CreatedAt = now,
            NextAttemptAt = now,
            ExpiresAt = expiresAfter is { } e ? now.Add(e) : null,
        });
    }

    public static NotificationPayload? Deserialize(string json) => JsonSerializer.Deserialize<NotificationPayload>(json, Json);

    /// <summary>Who hears about an appointment: the patient's own account, the managing account, and an online booker.</summary>
    public static (List<Guid> UserIds, string? Sms) RecipientsFor(Appointment appointment, Patient patient)
    {
        var ids = new List<Guid>();
        if (patient.UserId is { } own) ids.Add(own);
        if (patient.ManagedByUserId is { } manager) ids.Add(manager);
        if (appointment.Source == BookingSource.Online && appointment.BookedByUserId is { } booker) ids.Add(booker);
        ids = ids.Distinct().ToList();
        return (ids, ids.Count == 0 ? patient.ContactMobile : null);
    }

    public static NotificationPayload ForAppointment(Appointment appointment, Patient patient, string title, string body)
    {
        var (ids, sms) = RecipientsFor(appointment, patient);
        return new NotificationPayload(ids, sms, title, body, appointment.Id, $"/appointments/{appointment.Id}");
    }
}
