namespace SmartQueue.Api.Domain;

public class Doctor
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserId { get; set; }
    public required string FullName { get; set; }
    public string? Email { get; set; }
    public required string Specialty { get; set; }
    public string? Qualifications { get; set; }
    public required string MedicalRegistrationId { get; set; }
    public required string MedicalRegistrationAuthority { get; set; }
    public required string PrimaryCityCode { get; set; }
    public DoctorRegistrationStatus Status { get; set; } = DoctorRegistrationStatus.Draft;

    /// <summary>Application Doctor ID such as NGP-Dr-0001. Issued on first verification and never reused.</summary>
    public string? DoctorCode { get; set; }

    public bool PossibleDuplicate { get; set; }
    public string? SubmissionReference { get; set; }
    public DateTimeOffset? SubmittedAt { get; set; }
    public DateTimeOffset? VerifiedAt { get; set; }
    public Guid? VerifiedByUserId { get; set; }
    public string? VerificationReference { get; set; }
    public RejectionReasonCategory? RejectionReason { get; set; }
    public string? StatusNote { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public byte[] RowVersion { get; set; } = [];

    public DoctorPlan Plan { get; set; } = DoctorPlan.Free;

    // Queue settings (PRD §31 defaults).
    public int BookingWindowDays { get; set; } = 7;
    public int AvgConsultMinutes { get; set; } = 8;
    public int GracePeriodMinutes { get; set; } = 3;
    public int RequeueAfterCompletions { get; set; } = 5;
    public SecondMissRule SecondMissRule { get; set; } = SecondMissRule.NoShow;
    public int TurnApproachingThreshold { get; set; } = 3;

    public List<DoctorDeclaration> Declarations { get; set; } = [];
    public List<DoctorClinicAssignment> ClinicAssignments { get; set; } = [];

    public bool CanAcceptBookings => Status == DoctorRegistrationStatus.Verified;
}

public class DoctorDeclaration
{
    public long Id { get; set; }
    public Guid DoctorId { get; set; }
    public required string DeclarationVersion { get; set; }
    public bool Accepted { get; set; }
    public DateTimeOffset AcceptedAt { get; set; }
    public required string SubmissionReference { get; set; }
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
}

public class CityCode
{
    public required string Code { get; set; }
    public required string Name { get; set; }
    public required string State { get; set; }
    public bool IsActive { get; set; } = true;
}

public class DoctorCodeSequence
{
    public required string CityCode { get; set; }
    public int LastValue { get; set; }
}

public class Clinic
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string Name { get; set; }
    public required string AddressLine { get; set; }
    public required string CityCode { get; set; }
    public string? PublicPhone { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public class DoctorClinicAssignment
{
    public long Id { get; set; }
    public Guid DoctorId { get; set; }
    public Guid ClinicId { get; set; }
    public bool IsPrimary { get; set; }
    public Clinic Clinic { get; set; } = null!;
}
