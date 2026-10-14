namespace SmartQueue.Api.Domain;

public enum DoctorRegistrationStatus
{
    Draft,
    PendingVerification,
    Verified,
    Rejected,
    Suspended,
    Deactivated,
}

public enum RoleName
{
    SuperAdmin,
    Doctor,
    Receptionist,
}

public enum ConsentType
{
    PrivacyNotice,
    TermsOfUse,
}

public enum Gender
{
    Female,
    Male,
    Other,
    PreferNotToSay,
}

public enum Relationship
{
    Self,
    Son,
    Daughter,
    Wife,
    Husband,
    Mother,
    Father,
    Other,
}

public enum InvitationStatus
{
    Pending,
    Active,
    Declined,
    Revoked,
}

public enum SessionState
{
    Scheduled,
    Open,
    Paused,
    Closed,
    Cancelled,
}

public enum AppointmentStatus
{
    Booked,
    CheckedIn,
    Called,
    InConsultation,
    Completed,
    Missed,
    Requeued,
    NoShow,
    Cancelled,
}

public enum BookingSource
{
    Online,
    WalkIn,
    StaffBooked,
}

public enum QueuePriority
{
    Normal,
    Emergency,
}

public enum EmergencyReason
{
    AcutePain,
    BreathingDifficulty,
    Injury,
    ChildWithHighFever,
    DoctorsDiscretion,
    Other,
}

public enum CancellationReason
{
    Patient,
    Clinic,
    ClinicClosed,
    SessionClosed,
}

public enum SecondMissRule
{
    NoShow,
    RequeueAgain,
}

public enum ScheduleExceptionKind
{
    Closed,
    SpecialHours,
}

public enum AnnouncementType
{
    OutOfClinic,
    Leave,
    Holiday,
    ClinicClosure,
    DelayedOpening,
    ChangedHours,
    General,
}

public enum NotificationChannel
{
    InApp,
    WebPush,
    Sms,
}

public enum DeliveryStatus
{
    Sent,
    Failed,
    Skipped,
}

public enum OutboxStatus
{
    Pending,
    Processed,
    Failed,
}

public enum RejectionReasonCategory
{
    InvalidRegistrationNumber,
    DetailsMismatch,
    DuplicateRegistration,
    InsufficientEvidence,
    Other,
}

/// <summary>The doctor's subscription plan (PRD §39). Limits per plan are configured in <c>Plans</c>.</summary>
public enum DoctorPlan
{
    Free,
    Pro,
    Premium,
}
