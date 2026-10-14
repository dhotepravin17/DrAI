using System.Text.RegularExpressions;

namespace SmartQueue.Api.Domain;

/// <summary>Allowed doctor registration transitions (PRD §29.1).</summary>
public static class DoctorRegistrationStateMachine
{
    private static readonly HashSet<(DoctorRegistrationStatus From, DoctorRegistrationStatus To)> Allowed =
    [
        (DoctorRegistrationStatus.Draft, DoctorRegistrationStatus.PendingVerification),
        (DoctorRegistrationStatus.PendingVerification, DoctorRegistrationStatus.Verified),
        (DoctorRegistrationStatus.PendingVerification, DoctorRegistrationStatus.Rejected),
        (DoctorRegistrationStatus.Rejected, DoctorRegistrationStatus.PendingVerification),
        (DoctorRegistrationStatus.Verified, DoctorRegistrationStatus.Suspended),
        (DoctorRegistrationStatus.Suspended, DoctorRegistrationStatus.Verified),
        (DoctorRegistrationStatus.Verified, DoctorRegistrationStatus.Deactivated),
        (DoctorRegistrationStatus.Suspended, DoctorRegistrationStatus.Deactivated),
    ];

    public static bool CanTransition(DoctorRegistrationStatus from, DoctorRegistrationStatus to) =>
        Allowed.Contains((from, to));

    public static bool IsEditableByDoctor(DoctorRegistrationStatus status) =>
        status is DoctorRegistrationStatus.Draft or DoctorRegistrationStatus.Rejected;
}

public static partial class DoctorCodeFormat
{
    public const int MaxSequence = 9999;
    public const int CapacityWarningThreshold = 9000;

    public static string Format(string cityCode, int sequence) => $"{cityCode}-Dr-{sequence:D4}";

    public static bool LooksLikeDoctorCode(string value) => Pattern().IsMatch(value);

    [GeneratedRegex(@"^[A-Za-z]{3}-dr-\d{4}$", RegexOptions.IgnoreCase)]
    private static partial Regex Pattern();
}
