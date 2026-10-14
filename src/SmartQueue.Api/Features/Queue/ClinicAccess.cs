using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using SmartQueue.Api.Common;
using SmartQueue.Api.Domain;
using SmartQueue.Api.Infrastructure;

namespace SmartQueue.Api.Features.Queue;

public sealed record ClinicActor(Guid UserId, Doctor Doctor, Guid ClinicId, bool IsDoctor);

public sealed record ClinicContext(Guid DoctorId, string DoctorName, string? DoctorCode, Guid ClinicId, string ClinicName, string Role);

/// <summary>Resolves which doctor's clinic the signed-in user may operate: their own (as a verified doctor) or one they are staff for.</summary>
public sealed class ClinicAccess(AppDbContext db)
{
    public async Task<List<ClinicContext>> ContextsAsync(Guid userId, CancellationToken ct)
    {
        var result = new List<ClinicContext>();
        var own = await db.Doctors.AsNoTracking().Include(d => d.ClinicAssignments).ThenInclude(a => a.Clinic)
            .FirstOrDefaultAsync(d => d.UserId == userId && d.Status == DoctorRegistrationStatus.Verified, ct);
        if (own?.ClinicAssignments.FirstOrDefault(a => a.IsPrimary) is { } ownClinic)
            result.Add(new ClinicContext(own.Id, own.FullName, own.DoctorCode, ownClinic.ClinicId, ownClinic.Clinic.Name, "Doctor"));

        var staff = await (from r in db.UserRoleAssignments.AsNoTracking()
                           where r.UserId == userId && r.Role == RoleName.Receptionist && r.RevokedAt == null && r.DoctorId != null
                           join d in db.Doctors on r.DoctorId equals d.Id
                           where d.Status == DoctorRegistrationStatus.Verified
                           join c in db.Clinics on r.ClinicId equals c.Id
                           select new ClinicContext(d.Id, d.FullName, d.DoctorCode, c.Id, c.Name, "Receptionist")).ToListAsync(ct);
        result.AddRange(staff.Where(s => result.All(r => r.DoctorId != s.DoctorId)));
        return result;
    }

    public async Task<ClinicActor> ResolveAsync(ClaimsPrincipal principal, Guid? doctorId, CancellationToken ct, bool doctorOnly = false)
    {
        var userId = principal.GetUserId();
        var contexts = await ContextsAsync(userId, ct);
        var context = doctorId is { } id
            ? contexts.FirstOrDefault(c => c.DoctorId == id)
            : contexts.Count == 1 ? contexts[0] : contexts.FirstOrDefault(c => c.Role == "Doctor");

        if (context is null)
            throw new ApiException(StatusCodes.Status403Forbidden,
                contexts.Count == 0 ? "You do not manage a verified doctor's clinic." : "You do not have access to this doctor's clinic.");
        if (doctorOnly && context.Role != "Doctor")
            throw new ApiException(StatusCodes.Status403Forbidden, "Only the doctor can change this.");

        var doctor = await db.Doctors.AsNoTracking().SingleAsync(d => d.Id == context.DoctorId, ct);
        return new ClinicActor(userId, doctor, context.ClinicId, context.Role == "Doctor");
    }

    public async Task<bool> CanOperateSessionAsync(Guid userId, Guid sessionId, CancellationToken ct)
    {
        var doctorId = await db.QueueSessions.Where(s => s.Id == sessionId).Select(s => (Guid?)s.DoctorId).FirstOrDefaultAsync(ct);
        return doctorId is not null && (await ContextsAsync(userId, ct)).Any(c => c.DoctorId == doctorId);
    }
}
