using System.Text.Json;
using SmartQueue.Api.Domain;
using SmartQueue.Api.Infrastructure;

namespace SmartQueue.Api.Features.Analytics;

/// <summary>Event names from PRD §38.</summary>
public static class ProductEventNames
{
    public const string DoctorRegistrationSubmitted = "doctor_registration_submitted";
    public const string DoctorVerified = "doctor_verified";
    public const string DoctorRejected = "doctor_rejected";
    public const string QueueSessionOpened = "queue_session_opened";
    public const string QueueSessionClosed = "queue_session_closed";
    public const string PatientRegistered = "patient_registered";
    public const string FamilyMemberAdded = "family_member_added";
    public const string DoctorSearched = "doctor_searched";
    public const string AppointmentBooked = "appointment_booked";
    public const string AppointmentCancelled = "appointment_cancelled";
    public const string PatientCheckedIn = "patient_checked_in";
    public const string PatientCalled = "patient_called";
    public const string ConsultationCompleted = "consultation_completed";
    public const string TurnMissed = "turn_missed";
    public const string TurnRequeued = "turn_requeued";
    public const string NoShow = "no_show";
    public const string EmergencyPrioritized = "emergency_prioritized";
    public const string NotificationSent = "notification_sent";
    public const string NotificationFailed = "notification_failed";
    public const string AdminRecordAccessed = "admin_record_accessed";
}

/// <summary>
/// Adds product events to the current unit of work, so an event is stored only if the change it describes is.
/// Properties must be internal IDs and categories only (PRD §38): never names, mobile numbers or free-text notes.
/// </summary>
public sealed class ProductAnalytics(AppDbContext db, TimeProvider clock)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public void Track(string name, object properties, string? dimension = null, double? value = null) =>
        db.ProductEvents.Add(Create(name, properties, dimension, value, clock.GetUtcNow()));

    public static ProductEvent Create(string name, object properties, string? dimension, double? value, DateTimeOffset at)
    {
        var json = JsonSerializer.Serialize(properties, Json);
        return new ProductEvent
        {
            Name = name,
            Dimension = dimension is { Length: > 60 } ? dimension[..60] : dimension,
            Value = value,
            PropertiesJson = json.Length > 2000 ? json[..2000] : json,
            OccurredAt = at,
        };
    }
}
