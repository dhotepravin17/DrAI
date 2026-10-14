using Microsoft.Extensions.Options;
using SmartQueue.Api.Common;
using SmartQueue.Api.Domain;

namespace SmartQueue.Api.Features.Plans;

/// <summary>
/// Per-plan limits for the doctor's subscription (PRD §39). The numbers are proposals pending OQ-05, so they are
/// configuration rather than code. Security, privacy and audit features never depend on the plan.
/// </summary>
public sealed class PlanOptions
{
    public const string Section = "Plans";

    public PlanLimit Free { get; set; } = new() { StaffAccounts = 1, HistoryDays = 90, Reports = false };
    public PlanLimit Pro { get; set; } = new() { StaffAccounts = 5, HistoryDays = null, Reports = true };
    public PlanLimit Premium { get; set; } = new() { StaffAccounts = 50, HistoryDays = null, Reports = true };

    public PlanLimit For(DoctorPlan plan) => plan switch
    {
        DoctorPlan.Pro => Pro,
        DoctorPlan.Premium => Premium,
        _ => Free,
    };
}

public sealed class PlanLimit
{
    /// <summary>Receptionist accounts, counting pending invitations.</summary>
    public int StaffAccounts { get; set; }

    /// <summary>How far back the doctor's queue history reaches; null means unlimited.</summary>
    public int? HistoryDays { get; set; }

    /// <summary>Clinic reports and analytics.</summary>
    public bool Reports { get; set; }
}

public sealed class PlanService(IOptions<PlanOptions> options, TimeProvider clock)
{
    public PlanLimit LimitsFor(Doctor doctor) => options.Value.For(doctor.Plan);

    /// <summary>The earliest date the doctor's plan lets them search, or null for no limit.</summary>
    public DateOnly? HistoryFloor(Doctor doctor) =>
        LimitsFor(doctor).HistoryDays is { } days ? ClinicTime.Today(clock).AddDays(-days) : null;

    public void EnsureReports(Doctor doctor)
    {
        if (!LimitsFor(doctor).Reports)
            throw new ApiException(StatusCodes.Status403Forbidden, "Clinic reports are part of the Pro plan.",
                "Contact support to upgrade your plan.");
    }
}
