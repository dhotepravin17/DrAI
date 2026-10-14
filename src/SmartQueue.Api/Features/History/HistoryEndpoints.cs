using Microsoft.EntityFrameworkCore;
using SmartQueue.Api.Common;
using SmartQueue.Api.Domain;
using SmartQueue.Api.Features.Family;
using SmartQueue.Api.Features.Queue;
using SmartQueue.Api.Features.Plans;
using SmartQueue.Api.Infrastructure;

namespace SmartQueue.Api.Features.History;

/// <summary>FR-17 patient visit history and FR-18 doctor historical records.</summary>
public static class HistoryEndpoints
{
    public const int PageSize = 20;

    public sealed record VisitDto(
        Guid AppointmentId, string Reference, string Token, AppointmentStatus Status, DateOnly Date, string SessionName,
        Guid PatientId, string PatientName, string DoctorName, string? DoctorCode, string ClinicName, BookingSource Source,
        bool BookedByMe, string? FollowUpNote);
    public sealed record ClinicVisitDto(
        Guid AppointmentId, string Reference, string Token, AppointmentStatus Status, DateOnly Date, string SessionName,
        string PatientName, BookingSource Source, QueuePriority Priority, EmergencyReason? EmergencyReason, int MissCount,
        DateTimeOffset BookedAt, DateTimeOffset? CompletedAt, CancellationReason? CancellationReason, IReadOnlyList<EventDto> Events);
    public sealed record EventDto(string Type, AppointmentStatus? From, AppointmentStatus? To, string? Reason, DateTimeOffset OccurredAt);
    public sealed record Paged<T>(IReadOnlyList<T> Items, int Page, int PageSize, int Total);

    public static RouteGroupBuilder MapHistoryEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/me/visits", MyVisits).WithTags("History").RequireAuthorization();
        api.MapGet("/clinic/appointments", ClinicHistory).WithTags("History").RequireAuthorization();
        return api;
    }

    private static async Task<IResult> MyVisits(
        Guid? patientId, int? page, HttpContext http, PatientAccess access, AppDbContext db, CancellationToken ct)
    {
        var userId = http.User.GetUserId();
        var query = await access.VisibleAppointmentsAsync(userId, ct);
        if (patientId is { } pid) query = query.Where(a => a.PatientId == pid);

        var pageNumber = Math.Max(1, page ?? 1);
        var total = await query.CountAsync(ct);
        var rows = await (from a in query
                          join p in db.Patients on a.PatientId equals p.Id
                          join d in db.Doctors on a.DoctorId equals d.Id
                          join c in db.Clinics on a.ClinicId equals c.Id
                          orderby a.Date descending, a.CreatedAt descending
                          select new { a, PatientName = p.FullName, DoctorName = d.FullName, d.DoctorCode, ClinicName = c.Name })
            .Skip((pageNumber - 1) * PageSize).Take(PageSize).ToListAsync(ct);

        return Results.Ok(new Paged<VisitDto>(rows.Select(r => new VisitDto(
            r.a.Id, r.a.Reference, r.a.Token, r.a.Status, r.a.Date, r.a.SessionName, r.a.PatientId, r.PatientName,
            r.DoctorName, r.DoctorCode, r.ClinicName, r.a.Source, r.a.BookedByUserId == userId,
            r.a.Status == AppointmentStatus.Completed ? r.a.FollowUpNote : null)).ToList(), pageNumber, PageSize, total));
    }

    /// <summary>A doctor (or their staff) sees only appointments with that doctor, never other doctors' records.</summary>
    private static async Task<IResult> ClinicHistory(
        Guid? doctorId, DateOnly? from, DateOnly? to, AppointmentStatus? status, string? token, string? reference,
        string? patientName, string? mobile, int? page, HttpContext http, ClinicAccess access, PlanService plans, AppDbContext db,
        CancellationToken ct)
    {
        var actor = await access.ResolveAsync(http.User, doctorId, ct);
        // Free plans see the last 90 days (PRD §39); a narrower requested range still applies.
        if (plans.HistoryFloor(actor.Doctor) is { } floor && (from is null || from < floor)) from = floor;
        var query = from a in db.Appointments.AsNoTracking()
                    where a.DoctorId == actor.Doctor.Id
                    join p in db.Patients on a.PatientId equals p.Id
                    select new { a, p };

        if (from is not null) query = query.Where(x => x.a.Date >= from);
        if (to is not null) query = query.Where(x => x.a.Date <= to);
        if (status is not null) query = query.Where(x => x.a.Status == status);
        if (!string.IsNullOrWhiteSpace(reference)) query = query.Where(x => x.a.Reference == reference.Trim());
        if (!string.IsNullOrWhiteSpace(token) && int.TryParse(token.Trim().TrimStart('T', 't', '-'), out var tokenNumber))
            query = query.Where(x => x.a.TokenNumber == tokenNumber);
        if (!string.IsNullOrWhiteSpace(patientName)) query = query.Where(x => x.p.FullName.Contains(patientName.Trim()));
        if (!string.IsNullOrWhiteSpace(mobile))
        {
            if (!MobileNumber.TryNormalize(mobile, out var normalized))
                return new Validator().Add("mobile", "Enter a valid 10-digit mobile number.").ToResult();
            var userIds = db.Users.Where(u => u.MobileNumber == normalized).Select(u => u.Id);
            query = query.Where(x => x.p.ContactMobile == normalized ||
                                     (x.p.UserId != null && userIds.Contains(x.p.UserId.Value)) ||
                                     (x.p.ManagedByUserId != null && userIds.Contains(x.p.ManagedByUserId.Value)));
        }

        var pageNumber = Math.Max(1, page ?? 1);
        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(x => x.a.Date).ThenByDescending(x => x.a.TokenNumber)
            .Skip((pageNumber - 1) * PageSize).Take(PageSize).ToListAsync(ct);
        var ids = rows.Select(r => r.a.Id).ToList();
        var events = (await db.AppointmentEvents.AsNoTracking().Where(e => ids.Contains(e.AppointmentId)).OrderBy(e => e.Id).ToListAsync(ct))
            .GroupBy(e => e.AppointmentId).ToDictionary(g => g.Key, g => g.Select(e => new EventDto(e.Type, e.FromStatus, e.ToStatus, e.Reason, e.OccurredAt)).ToList());

        return Results.Ok(new Paged<ClinicVisitDto>(rows.Select(r => new ClinicVisitDto(
            r.a.Id, r.a.Reference, r.a.Token, r.a.Status, r.a.Date, r.a.SessionName, r.p.FullName, r.a.Source, r.a.Priority,
            r.a.EmergencyReason, r.a.MissCount, r.a.CreatedAt, r.a.CompletedAt, r.a.CancellationReason,
            events.GetValueOrDefault(r.a.Id, []))).ToList(), pageNumber, PageSize, total));
    }
}
