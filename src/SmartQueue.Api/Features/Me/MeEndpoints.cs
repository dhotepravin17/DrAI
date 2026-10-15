using Microsoft.EntityFrameworkCore;
using SmartQueue.Api.Common;
using SmartQueue.Api.Domain;
using SmartQueue.Api.Features.Family;
using SmartQueue.Api.Infrastructure;

namespace SmartQueue.Api.Features.Me;

public static class MeEndpoints
{
    public sealed record PatientProfile(Guid PatientId, string FullName, DateOnly? DateOfBirth, Gender? Gender, string? City);
    public sealed record DoctorSummary(Guid DoctorId, DoctorRegistrationStatus Status, string? DoctorCode);
    public sealed record MeResponse(
        Guid UserId, string FullName, string MobileNumberMasked, string? Email,
        IReadOnlyList<RoleName> Roles, PatientProfile? Patient, DoctorSummary? Doctor);

    public static RouteGroupBuilder MapMeEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/me", GetMe).WithTags("Me").RequireAuthorization();
        api.MapGet("/me/export", Export).WithTags("Me").RequireAuthorization();
        return api;
    }

    /// <summary>A copy of the personal data held for this account (DPDP Act right of access, PRD §34).</summary>
    private static async Task<IResult> Export(HttpContext http, AppDbContext db, PatientAccess access, TimeProvider clock, CancellationToken ct)
    {
        var userId = http.User.GetUserId();
        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == userId, ct);
        var consents = await db.ConsentRecords.AsNoTracking().Where(c => c.UserId == userId)
            .Select(c => new { c.Type, c.Version, c.GrantedAt, c.WithdrawnAt }).ToListAsync(ct);
        var people = (await access.PeopleAsync(userId, ct))
            .Select(p => new { p.Patient.Id, p.Patient.FullName, p.Kind, p.Relationship, p.Patient.DateOfBirth, p.Patient.Gender, p.Patient.City }).ToList();
        var appointments = await (await access.VisibleAppointmentsAsync(userId, ct))
            .Join(db.Doctors, a => a.DoctorId, d => d.Id, (a, d) => new
            {
                a.Reference, a.Date, a.SessionName, Token = "T-" + a.TokenNumber, a.Status, a.PatientId, Doctor = d.FullName, a.Source, a.CreatedAt,
            })
            .OrderByDescending(a => a.Date).ToListAsync(ct);
        var links = await db.PatientLinks.AsNoTracking().Where(l => l.ViewerUserId == userId)
            .Select(l => new { l.Relationship, l.Status, l.CreatedAt, l.RespondedAt, l.RevokedAt }).ToListAsync(ct);

        http.Response.Headers.ContentDisposition = "attachment; filename=\"valmedo-data.json\"";
        return Results.Ok(new
        {
            exportedAt = clock.GetUtcNow(),
            account = new { user.Id, user.FullName, user.MobileNumber, user.Email, user.CreatedAt },
            consents,
            people,
            appointments,
            familyAccessRequests = links,
        });
    }

    private static async Task<IResult> GetMe(HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var userId = http.User.GetUserId();
        var user = await db.Users.AsNoTracking().Include(u => u.RoleAssignments)
            .FirstOrDefaultAsync(u => u.Id == userId && u.IsActive, ct);
        if (user is null) return Results.Unauthorized();

        var patient = await db.Patients.AsNoTracking()
            .Where(p => p.UserId == userId && p.ArchivedAt == null)
            .Select(p => new PatientProfile(p.Id, p.FullName, p.DateOfBirth, p.Gender, p.City))
            .FirstOrDefaultAsync(ct);
        var doctor = await db.Doctors.AsNoTracking()
            .Where(d => d.UserId == userId)
            .Select(d => new DoctorSummary(d.Id, d.Status, d.DoctorCode))
            .FirstOrDefaultAsync(ct);

        var roles = user.RoleAssignments.Where(r => r.RevokedAt == null).Select(r => r.Role).Distinct().ToList();
        return Results.Ok(new MeResponse(
            user.Id, user.FullName, MobileNumber.Mask(user.MobileNumber), user.Email, roles, patient, doctor));
    }
}
