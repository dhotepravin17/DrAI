namespace SmartQueue.Api.Domain;

/// <summary>A login account. One mobile number may be shared by several accounts (PRD §31.5).</summary>
public class User
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string MobileNumber { get; set; }
    public required string FullName { get; set; }
    public string? Email { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Data-protected TOTP secret for Super Admin two-factor sign-in (PRD §27.4).</summary>
    public string? TotpSecretProtected { get; set; }
    public DateTimeOffset? TotpEnabledAt { get; set; }
    public long? TotpLastUsedStep { get; set; }

    /// <summary>Consecutive wrong authenticator codes; the account locks for a while when it reaches the limit.</summary>
    public int TotpFailedAttempts { get; set; }
    public DateTimeOffset? TotpLockedUntil { get; set; }

    public List<UserRoleAssignment> RoleAssignments { get; set; } = [];
}

public class UserRoleAssignment
{
    public long Id { get; set; }
    public Guid UserId { get; set; }
    public RoleName Role { get; set; }
    public Guid? DoctorId { get; set; }
    public Guid? ClinicId { get; set; }
    public DateTimeOffset GrantedAt { get; set; }
    public Guid? GrantedByUserId { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

public class OtpChallenge
{
    public long Id { get; set; }
    public required string MobileNumber { get; set; }
    public required string CodeHash { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public int FailedAttempts { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }
}

public class ConsentRecord
{
    public long Id { get; set; }
    public Guid UserId { get; set; }
    public ConsentType Type { get; set; }
    public required string Version { get; set; }
    public DateTimeOffset GrantedAt { get; set; }
    public DateTimeOffset? WithdrawnAt { get; set; }
    public string? IpAddress { get; set; }
}

/// <summary>
/// A person who can receive a consultation. The account holder's own record has <see cref="UserId"/> set;
/// a family profile has <see cref="ManagedByUserId"/> set; a clinic-registered walk-in has neither.
/// </summary>
public class Patient
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid? UserId { get; set; }
    public Guid? ManagedByUserId { get; set; }
    public Relationship Relationship { get; set; } = Relationship.Self;
    public Guid CreatedByUserId { get; set; }
    public required string FullName { get; set; }
    public DateOnly? DateOfBirth { get; set; }
    public Gender? Gender { get; set; }
    public string? City { get; set; }

    /// <summary>Contact number for clinic-registered patients without an account.</summary>
    public string? ContactMobile { get; set; }

    /// <summary>When a guardian confirmed consent to process a minor's data (DPDP Act children's data).</summary>
    public DateTimeOffset? GuardianConsentAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }

    public bool IsMinorOn(DateOnly date) => DateOfBirth is { } dob && dob.AddYears(18) > date;
}

public class AdminAuditLog
{
    public long Id { get; set; }
    public Guid? ActorUserId { get; set; }
    public required string Action { get; set; }
    public required string EntityType { get; set; }
    public required string EntityId { get; set; }
    public string? Purpose { get; set; }
    public string? Details { get; set; }
    public string? IpAddress { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}
