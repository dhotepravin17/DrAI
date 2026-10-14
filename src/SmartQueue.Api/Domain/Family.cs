namespace SmartQueue.Api.Domain;

/// <summary>
/// An adult's consent for another account to see their visit history (PRD §12.3 "Linked history").
/// Created as an invitation to the adult's mobile number; becomes active when an account on that number accepts.
/// </summary>
public class PatientLink
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid ViewerUserId { get; set; }
    public required string InviteeMobile { get; set; }
    public Relationship Relationship { get; set; }
    public InvitationStatus Status { get; set; } = InvitationStatus.Pending;

    /// <summary>The accepting adult's own patient record.</summary>
    public Guid? PatientId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? RespondedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

/// <summary>A doctor's invitation for a receptionist, accepted by an account on the invited mobile number.</summary>
public class StaffInvitation
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid DoctorId { get; set; }
    public Guid ClinicId { get; set; }
    public required string MobileNumber { get; set; }
    public required string FullName { get; set; }
    public InvitationStatus Status { get; set; } = InvitationStatus.Pending;
    public Guid InvitedByUserId { get; set; }
    public Guid? AcceptedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? RespondedAt { get; set; }
}
