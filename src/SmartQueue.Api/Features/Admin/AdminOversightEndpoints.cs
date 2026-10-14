using Microsoft.EntityFrameworkCore;
using SmartQueue.Api.Common;
using SmartQueue.Api.Domain;
using SmartQueue.Api.Features.Analytics;
using SmartQueue.Api.Infrastructure;

namespace SmartQueue.Api.Features.Admin;

/// <summary>
/// FR-19 platform oversight: patient search, platform-wide history (purpose required and audited, PRD D-07),
/// active queues and notification failures.
/// </summary>
public static class AdminOversightEndpoints
{
    public const int PageSize = 25;

    public sealed record PatientRow(
        Guid PatientId, string FullName, string Kind, Relationship Relationship, string? AccountMobileMasked, int Appointments, DateTimeOffset CreatedAt);
    public sealed record EventDto(string Type, AppointmentStatus? From, AppointmentStatus? To, string? Reason, Guid? ActorUserId, DateTimeOffset OccurredAt);
    public sealed record AppointmentRow(
        Guid AppointmentId, string Reference, string Token, AppointmentStatus Status, DateOnly Date, string SessionName,
        string PatientName, string DoctorName, string? DoctorCode, BookingSource Source, QueuePriority Priority,
        EmergencyReason? EmergencyReason, int MissCount, CancellationReason? CancellationReason, DateTimeOffset BookedAt,
        IReadOnlyList<EventDto> Events);
    public sealed record PatientHistory(
        Guid PatientId, string FullName, string Kind, DateOnly? DateOfBirth, string? AccountMobileMasked, DateTimeOffset CreatedAt,
        IReadOnlyList<AppointmentRow> Appointments);
    public sealed record QueueRow(
        Guid SessionId, string DoctorName, string? DoctorCode, DateOnly Date, string Name, SessionState State,
        int Booked, int Waiting, int Completed, int Missed, string? NowServing);
    public sealed record FailureRow(
        long Id, string EventType, NotificationChannel Channel, DeliveryStatus Status, string? Error, Guid? UserId, Guid? AppointmentId, DateTimeOffset CreatedAt);
    public sealed record Overview(int PendingDoctors, int VerifiedDoctors, int OpenQueuesToday, int AppointmentsToday, int NotificationFailures24h, int OutboxBacklog);

    public static RouteGroupBuilder MapAdminOversightEndpoints(this RouteGroupBuilder api)
    {
        var admin = api.MapGroup("/admin").WithTags("Super Admin").RequireAuthorization(AuthPolicies.SuperAdmin);
        admin.MapGet("/overview", GetOverview);
        admin.MapGet("/patients", SearchPatients);
        admin.MapGet("/patients/{id:guid}/history", PatientHistoryAsync);
        admin.MapGet("/doctors/{id:guid}/appointments", DoctorAppointments);
        admin.MapGet("/queues", Queues);
        admin.MapGet("/notification-failures", Failures);
        return api;
    }

    private static async Task<IResult> GetOverview(AppDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var today = ClinicTime.Today(clock);
        var since = clock.GetUtcNow().AddHours(-24);
        return Results.Ok(new Overview(
            await db.Doctors.CountAsync(d => d.Status == DoctorRegistrationStatus.PendingVerification, ct),
            await db.Doctors.CountAsync(d => d.Status == DoctorRegistrationStatus.Verified, ct),
            await db.QueueSessions.CountAsync(s => s.Date == today && (s.State == SessionState.Open || s.State == SessionState.Paused), ct),
            await db.Appointments.CountAsync(a => a.Date == today && a.Status != AppointmentStatus.Cancelled, ct),
            await db.NotificationLogs.CountAsync(l => l.Status == DeliveryStatus.Failed && l.CreatedAt > since, ct)
                + await db.OutboxMessages.CountAsync(m => m.Status == OutboxStatus.Failed && m.CreatedAt > since, ct),
            await db.OutboxMessages.CountAsync(m => m.Status == OutboxStatus.Pending, ct)));
    }

    private static async Task<IResult> SearchPatients(string? q, int? page, AppDbContext db, CancellationToken ct)
    {
        var term = q?.Trim() ?? "";
        if (term.Length < 2) return new Validator().Add("q", "Enter at least 2 characters of a name or a mobile number.").ToResult();

        var query = from p in db.Patients.AsNoTracking()
                    join u in db.Users on (p.UserId ?? p.ManagedByUserId) equals u.Id into pu
                    from u in pu.DefaultIfEmpty()
                    select new { p, Mobile = u != null ? u.MobileNumber : p.ContactMobile };
        query = MobileNumber.TryNormalize(term, out var mobile)
            ? query.Where(x => x.Mobile == mobile)
            : query.Where(x => x.p.FullName.Contains(term));

        var pageNumber = Math.Max(1, page ?? 1);
        var total = await query.CountAsync(ct);
        var rows = await query.OrderBy(x => x.p.FullName).Skip((pageNumber - 1) * PageSize).Take(PageSize)
            .Select(x => new { x.p, x.Mobile, Count = db.Appointments.Count(a => a.PatientId == x.p.Id) })
            .ToListAsync(ct);
        return Results.Ok(new AdminEndpoints.PagedResult<PatientRow>(
            rows.Select(r => new PatientRow(r.p.Id, r.p.FullName, Kind(r.p), r.p.Relationship,
                r.Mobile is null ? null : MobileNumber.Mask(r.Mobile), r.Count, r.p.CreatedAt)).ToList(), pageNumber, PageSize, total));
    }

    private static string Kind(Patient p) => p.UserId is not null ? "Account holder" : p.ManagedByUserId is not null ? "Family profile" : "Clinic-registered";

    private static Validator RequirePurpose(string? purpose) =>
        purpose is { Length: >= 5 and <= 200 } ? new Validator() : new Validator().Add("purpose", "State why you need this record (5–200 characters).");

    private static async Task<IResult> PatientHistoryAsync(
        Guid id, string? purpose, string? ticket, AppDbContext db, AuditLogger audit, ProductAnalytics analytics, HttpContext http,
        CancellationToken ct)
    {
        var v = RequirePurpose(purpose?.Trim());
        if (!v.IsValid) return v.ToResult();

        var patient = await db.Patients.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);
        if (patient is null) return Results.NotFound();
        var mobile = await db.Users.Where(u => u.Id == (patient.UserId ?? patient.ManagedByUserId)).Select(u => u.MobileNumber).FirstOrDefaultAsync(ct)
                     ?? patient.ContactMobile;

        audit.Record("ViewPatientHistory", "Patient", id.ToString(), purpose: purpose!.Trim(), details: ticket.TrimToNull());
        analytics.Track(ProductEventNames.AdminRecordAccessed, new { adminId = http.User.GetUserId(), purpose = "PatientHistory", patientId = id }, "PatientHistory");
        await db.SaveChangesAsync(ct);

        var appointments = await Appointments(db, db.Appointments.Where(a => a.PatientId == id), ct);
        return Results.Ok(new PatientHistory(patient.Id, patient.FullName, Kind(patient), patient.DateOfBirth,
            mobile is null ? null : MobileNumber.Mask(mobile), patient.CreatedAt, appointments));
    }

    private static async Task<IResult> DoctorAppointments(
        Guid id, string? purpose, DateOnly? from, DateOnly? to, AppDbContext db, AuditLogger audit, ProductAnalytics analytics,
        HttpContext http, CancellationToken ct)
    {
        var v = RequirePurpose(purpose?.Trim());
        if (!v.IsValid) return v.ToResult();
        if (!await db.Doctors.AnyAsync(d => d.Id == id, ct)) return Results.NotFound();

        audit.Record("ViewDoctorHistory", "Doctor", id.ToString(), purpose: purpose!.Trim());
        analytics.Track(ProductEventNames.AdminRecordAccessed, new { adminId = http.User.GetUserId(), purpose = "DoctorHistory", doctorId = id }, "DoctorHistory");
        await db.SaveChangesAsync(ct);

        var query = db.Appointments.Where(a => a.DoctorId == id);
        if (from is not null) query = query.Where(a => a.Date >= from);
        if (to is not null) query = query.Where(a => a.Date <= to);
        return Results.Ok(await Appointments(db, query, ct, take: 200));
    }

    private static async Task<List<AppointmentRow>> Appointments(AppDbContext db, IQueryable<Appointment> source, CancellationToken ct, int take = 500)
    {
        var rows = await (from a in source.AsNoTracking()
                          join p in db.Patients on a.PatientId equals p.Id
                          join d in db.Doctors on a.DoctorId equals d.Id
                          orderby a.Date descending, a.TokenNumber descending
                          select new { a, PatientName = p.FullName, DoctorName = d.FullName, d.DoctorCode }).Take(take).ToListAsync(ct);
        var ids = rows.Select(r => r.a.Id).ToList();
        var events = (await db.AppointmentEvents.AsNoTracking().Where(e => ids.Contains(e.AppointmentId)).OrderBy(e => e.Id).ToListAsync(ct))
            .GroupBy(e => e.AppointmentId)
            .ToDictionary(g => g.Key, g => g.Select(e => new EventDto(e.Type, e.FromStatus, e.ToStatus, e.Reason, e.ActorUserId, e.OccurredAt)).ToList());
        return rows.Select(r => new AppointmentRow(r.a.Id, r.a.Reference, r.a.Token, r.a.Status, r.a.Date, r.a.SessionName, r.PatientName,
            r.DoctorName, r.DoctorCode, r.a.Source, r.a.Priority, r.a.EmergencyReason, r.a.MissCount, r.a.CancellationReason, r.a.CreatedAt,
            events.GetValueOrDefault(r.a.Id, []))).ToList();
    }

    private static async Task<IResult> Queues(DateOnly? date, AppDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var day = date ?? ClinicTime.Today(clock);
        var sessions = await (from s in db.QueueSessions.AsNoTracking()
                              where s.Date == day
                              join d in db.Doctors on s.DoctorId equals d.Id
                              orderby s.StartsAt
                              select new { s, d.FullName, d.DoctorCode }).ToListAsync(ct);
        var ids = sessions.Select(x => x.s.Id).ToList();
        var appointments = await db.Appointments.AsNoTracking().Where(a => ids.Contains(a.QueueSessionId)).ToListAsync(ct);
        return Results.Ok(sessions.Select(x =>
        {
            var list = appointments.Where(a => a.QueueSessionId == x.s.Id).ToList();
            return new QueueRow(x.s.Id, x.FullName, x.DoctorCode, x.s.Date, x.s.Name, x.s.State,
                list.Count(a => a.Status != AppointmentStatus.Cancelled), list.Count(a => a.IsWaiting),
                list.Count(a => a.Status == AppointmentStatus.Completed), list.Count(a => a.Status == AppointmentStatus.Missed),
                list.FirstOrDefault(a => a.IsBeingServed)?.Token);
        }).ToList());
    }

    private static async Task<IResult> Failures(int? page, AppDbContext db, CancellationToken ct)
    {
        var pageNumber = Math.Max(1, page ?? 1);
        var query = db.NotificationLogs.AsNoTracking().Where(l => l.Status == DeliveryStatus.Failed);
        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(l => l.Id).Skip((pageNumber - 1) * PageSize).Take(PageSize)
            .Select(l => new FailureRow(l.Id, l.EventType, l.Channel, l.Status, l.Error, l.UserId, l.AppointmentId, l.CreatedAt))
            .ToListAsync(ct);
        return Results.Ok(new AdminEndpoints.PagedResult<FailureRow>(items, pageNumber, PageSize, total));
    }
}
