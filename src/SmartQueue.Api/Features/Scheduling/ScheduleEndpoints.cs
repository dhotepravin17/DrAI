using Microsoft.EntityFrameworkCore;
using SmartQueue.Api.Common;
using SmartQueue.Api.Domain;
using SmartQueue.Api.Features.Admin;
using SmartQueue.Api.Features.Queue;
using SmartQueue.Api.Infrastructure;

namespace SmartQueue.Api.Features.Scheduling;

/// <summary>FR-08: weekly sessions, queue settings and date exceptions. Changes are doctor-only.</summary>
public static class ScheduleEndpoints
{
    public sealed record TemplateDto(DayOfWeek DayOfWeek, string Name, TimeOnly StartTime, TimeOnly EndTime, int Capacity);
    public sealed record SettingsDto(
        int BookingWindowDays, int AvgConsultMinutes, int GracePeriodMinutes, int RequeueAfterCompletions,
        SecondMissRule SecondMissRule, int TurnApproachingThreshold);
    public sealed record ExceptionDto(
        long Id, ScheduleExceptionKind Kind, DateOnly StartDate, DateOnly EndDate, string? SessionName, TimeOnly? StartTime,
        TimeOnly? EndTime, int? Capacity, string? Reason, Guid? AnnouncementId);
    public sealed record ScheduleResponse(SettingsDto Settings, IReadOnlyList<TemplateDto> Sessions, IReadOnlyList<ExceptionDto> Exceptions);
    public sealed record ExceptionRequest(
        ScheduleExceptionKind? Kind, DateOnly? StartDate, DateOnly? EndDate, string? SessionName, TimeOnly? StartTime, TimeOnly? EndTime,
        int? Capacity, string? Reason);
    public sealed record ImpactResponse(int Sessions, int ActiveAppointments);

    public static RouteGroupBuilder MapScheduleEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/clinic").WithTags("Clinic schedule").RequireAuthorization();
        group.MapGet("/schedule", Get);
        group.MapPut("/schedule/sessions", SaveTemplates);
        group.MapPut("/settings", SaveSettings);
        group.MapGet("/schedule/impact", Impact);
        group.MapPost("/schedule/exceptions", AddException);
        group.MapDelete("/schedule/exceptions/{id:long}", RemoveException);
        return api;
    }

    private static async Task<IResult> Get(Guid? doctorId, HttpContext http, ClinicAccess access, AppDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var actor = await access.ResolveAsync(http.User, doctorId, ct);
        return Results.Ok(await Load(db, actor, clock, ct));
    }

    private static async Task<ScheduleResponse> Load(AppDbContext db, ClinicActor actor, TimeProvider clock, CancellationToken ct)
    {
        var d = await db.Doctors.AsNoTracking().SingleAsync(x => x.Id == actor.Doctor.Id, ct);
        var templates = await db.SessionTemplates.AsNoTracking().Where(t => t.DoctorId == d.Id && t.ClinicId == actor.ClinicId).ToListAsync(ct);
        var since = ClinicTime.Today(clock).AddDays(-7);
        var exceptions = await db.ScheduleExceptions.AsNoTracking()
            .Where(e => e.DoctorId == d.Id && e.RemovedAt == null && e.EndDate >= since).OrderBy(e => e.StartDate).ToListAsync(ct);
        return new ScheduleResponse(
            new SettingsDto(d.BookingWindowDays, d.AvgConsultMinutes, d.GracePeriodMinutes, d.RequeueAfterCompletions, d.SecondMissRule, d.TurnApproachingThreshold),
            templates.OrderBy(t => ((int)t.DayOfWeek + 6) % 7).ThenBy(t => t.StartTime)
                .Select(t => new TemplateDto(t.DayOfWeek, t.Name, t.StartTime, t.EndTime, t.Capacity)).ToList(),
            exceptions.Select(e => new ExceptionDto(e.Id, e.Kind, e.StartDate, e.EndDate, e.SessionName, e.StartTime, e.EndTime, e.Capacity, e.Reason, e.AnnouncementId)).ToList());
    }

    /// <summary>
    /// Replaces the weekly template. Dates that already have a queue keep their times and bookings;
    /// use a date exception to change those (this keeps confirmed appointments intact).
    /// </summary>
    private static async Task<IResult> SaveTemplates(
        List<TemplateDto> sessions, Guid? doctorId, HttpContext http, ClinicAccess access, AppDbContext db, AuditLogger audit,
        TimeProvider clock, CancellationToken ct)
    {
        var actor = await access.ResolveAsync(http.User, doctorId, ct, doctorOnly: true);
        var v = new Validator();
        if (sessions.Count > 42) v.Add("sessions", "Use at most 6 sessions per day.");
        for (var i = 0; i < sessions.Count; i++)
        {
            var s = sessions[i];
            if (string.IsNullOrWhiteSpace(s.Name) || s.Name.Trim().Length > 30) v.Add($"sessions[{i}].name", "Name the session (up to 30 characters).");
            if (s.EndTime <= s.StartTime) v.Add($"sessions[{i}].endTime", "The session must end after it starts.");
            if (s.Capacity is < 1 or > 500) v.Add($"sessions[{i}].capacity", "Capacity must be between 1 and 500.");
        }
        foreach (var day in sessions.GroupBy(s => s.DayOfWeek))
        {
            var ordered = day.OrderBy(s => s.StartTime).ToList();
            for (var i = 1; i < ordered.Count; i++)
                if (ordered[i].StartTime < ordered[i - 1].EndTime) v.Add("sessions", $"Sessions on {day.Key} overlap.");
            if (day.Select(s => s.Name.Trim().ToLowerInvariant()).Distinct().Count() != day.Count())
                v.Add("sessions", $"Session names on {day.Key} must be different.");
        }
        if (!v.IsValid) return v.ToResult();

        db.SessionTemplates.RemoveRange(db.SessionTemplates.Where(t => t.DoctorId == actor.Doctor.Id && t.ClinicId == actor.ClinicId));
        db.SessionTemplates.AddRange(sessions.Select(s => new SessionTemplate
        {
            DoctorId = actor.Doctor.Id, ClinicId = actor.ClinicId, DayOfWeek = s.DayOfWeek, Name = s.Name.Trim(),
            StartTime = s.StartTime, EndTime = s.EndTime, Capacity = s.Capacity,
        }));
        audit.Record("UpdateWeeklySchedule", "Doctor", actor.Doctor.Id.ToString(), details: $"{sessions.Count} sessions");
        await db.SaveChangesAsync(ct);
        return Results.Ok(await Load(db, actor, clock, ct));
    }

    private static async Task<IResult> SaveSettings(
        SettingsDto s, Guid? doctorId, HttpContext http, ClinicAccess access, AppDbContext db, AuditLogger audit, TimeProvider clock, CancellationToken ct)
    {
        var actor = await access.ResolveAsync(http.User, doctorId, ct, doctorOnly: true);
        var v = new Validator();
        if (s.BookingWindowDays is < 0 or > 30) v.Add("bookingWindowDays", "Between 0 and 30 days.");
        if (s.AvgConsultMinutes is < 1 or > 60) v.Add("avgConsultMinutes", "Between 1 and 60 minutes.");
        if (s.GracePeriodMinutes is < 0 or > 15) v.Add("gracePeriodMinutes", "Between 0 and 15 minutes.");
        if (s.RequeueAfterCompletions is < 1 or > 10) v.Add("requeueAfterCompletions", "Between 1 and 10 patients.");
        if (s.TurnApproachingThreshold is < 1 or > 10) v.Add("turnApproachingThreshold", "Between 1 and 10 patients.");
        if (!v.IsValid) return v.ToResult();

        var doctor = await db.Doctors.SingleAsync(d => d.Id == actor.Doctor.Id, ct);
        doctor.BookingWindowDays = s.BookingWindowDays;
        doctor.AvgConsultMinutes = s.AvgConsultMinutes;
        doctor.GracePeriodMinutes = s.GracePeriodMinutes;
        doctor.RequeueAfterCompletions = s.RequeueAfterCompletions;
        doctor.SecondMissRule = s.SecondMissRule;
        doctor.TurnApproachingThreshold = s.TurnApproachingThreshold;
        doctor.UpdatedAt = clock.GetUtcNow();
        audit.Record("UpdateQueueSettings", "Doctor", doctor.Id.ToString(), details: s.ToString());
        await db.SaveChangesAsync(ct);
        return Results.Ok(await Load(db, actor, clock, ct));
    }

    private static async Task<IResult> Impact(
        DateOnly startDate, DateOnly? endDate, string? sessionName, Guid? doctorId, HttpContext http, ClinicAccess access,
        ScheduleService schedule, CancellationToken ct)
    {
        var actor = await access.ResolveAsync(http.User, doctorId, ct);
        var sessions = await schedule.AffectedSessionsAsync(actor.Doctor.Id, actor.ClinicId, startDate, endDate ?? startDate, sessionName.TrimToNull(), ct);
        return Results.Ok(new ImpactResponse(sessions.Count, await schedule.CountActiveAppointmentsAsync(sessions, ct)));
    }

    private static async Task<IResult> AddException(
        ExceptionRequest r, Guid? doctorId, HttpContext http, ClinicAccess access, AppDbContext db, ScheduleService schedule,
        AuditLogger audit, TimeProvider clock, CancellationToken ct)
    {
        var actor = await access.ResolveAsync(http.User, doctorId, ct, doctorOnly: true);
        var today = ClinicTime.Today(clock);
        var v = new Validator().Optional("reason", r.Reason, 280);
        if (r.Kind is null) v.Add("kind", "Choose Closed or Special hours.");
        if (r.StartDate is null) v.Add("startDate", "Choose a date.");
        else if (r.StartDate < today) v.Add("startDate", "Choose today or a future date.");
        var end = r.EndDate ?? r.StartDate;
        if (end < r.StartDate) v.Add("endDate", "The end date must be on or after the start date.");
        if (end > r.StartDate?.AddDays(90)) v.Add("endDate", "Use at most 90 days in one exception.");
        if (r.Kind == ScheduleExceptionKind.SpecialHours)
        {
            if (string.IsNullOrWhiteSpace(r.SessionName)) v.Add("sessionName", "Name the session these hours apply to.");
            if (r.StartTime is null || r.EndTime is null || r.EndTime <= r.StartTime) v.Add("endTime", "Enter a start and a later end time.");
            if (r.Capacity is < 1 or > 500) v.Add("capacity", "Capacity must be between 1 and 500.");
        }
        if (!v.IsValid) return v.ToResult();

        var exception = await schedule.AddExceptionAsync(new ScheduleException
        {
            DoctorId = actor.Doctor.Id,
            ClinicId = actor.ClinicId,
            Kind = r.Kind!.Value,
            StartDate = r.StartDate!.Value,
            EndDate = end!.Value,
            SessionName = r.SessionName.TrimToNull(),
            StartTime = r.Kind == ScheduleExceptionKind.SpecialHours ? r.StartTime : null,
            EndTime = r.Kind == ScheduleExceptionKind.SpecialHours ? r.EndTime : null,
            Capacity = r.Kind == ScheduleExceptionKind.SpecialHours ? r.Capacity : null,
            Reason = r.Reason.TrimToNull(),
        }, actor.UserId, ct);
        audit.Record("AddScheduleException", "Doctor", actor.Doctor.Id.ToString(), details: $"{exception.Kind} {exception.StartDate}..{exception.EndDate}");
        await db.SaveChangesAsync(ct);
        return Results.Ok(await Load(db, actor, clock, ct));
    }

    private static async Task<IResult> RemoveException(
        long id, Guid? doctorId, HttpContext http, ClinicAccess access, AppDbContext db, ScheduleService schedule, AuditLogger audit,
        TimeProvider clock, CancellationToken ct)
    {
        var actor = await access.ResolveAsync(http.User, doctorId, ct, doctorOnly: true);
        var exception = await db.ScheduleExceptions.FirstOrDefaultAsync(e => e.Id == id && e.DoctorId == actor.Doctor.Id && e.RemovedAt == null, ct);
        if (exception is null) return Results.NotFound();
        await schedule.RemoveExceptionsAsync([exception], ct);
        audit.Record("RemoveScheduleException", "Doctor", actor.Doctor.Id.ToString(), details: id.ToString());
        await db.SaveChangesAsync(ct);
        return Results.Ok(await Load(db, actor, clock, ct));
    }
}
