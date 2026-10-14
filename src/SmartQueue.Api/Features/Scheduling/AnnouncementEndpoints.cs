using Microsoft.EntityFrameworkCore;
using SmartQueue.Api.Common;
using SmartQueue.Api.Domain;
using SmartQueue.Api.Features.Admin;
using SmartQueue.Api.Features.Notifications;
using SmartQueue.Api.Features.Queue;
using SmartQueue.Api.Infrastructure;

namespace SmartQueue.Api.Features.Scheduling;

/// <summary>
/// FR-16 announcements. Closure types (out of clinic, leave, holiday, closure) also close the schedule for those dates,
/// cancelling affected bookings and notifying patients. A delay shifts estimates and notifies waiting patients.
/// </summary>
public static class AnnouncementEndpoints
{
    public sealed record AnnouncementRequest(AnnouncementType? Type, string? Message, DateTimeOffset? StartsAt, DateTimeOffset? EndsAt, int? DelayMinutes);
    public sealed record AnnouncementDto(
        Guid Id, AnnouncementType Type, string Message, DateTimeOffset StartsAt, DateTimeOffset? EndsAt, int? DelayMinutes,
        bool IsCurrent, DateTimeOffset CreatedAt);

    public static RouteGroupBuilder MapAnnouncementEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/clinic/announcements").WithTags("Clinic announcements").RequireAuthorization();
        group.MapGet("/", List);
        group.MapPost("/", Create);
        group.MapDelete("/{id:guid}", Remove);
        return api;
    }

    private static async Task<IResult> List(Guid? doctorId, HttpContext http, ClinicAccess access, AppDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var actor = await access.ResolveAsync(http.User, doctorId, ct);
        var now = clock.GetUtcNow();
        var since = now.AddDays(-30);
        var items = await db.Announcements.AsNoTracking()
            .Where(a => a.DoctorId == actor.Doctor.Id && a.RemovedAt == null && (a.EndsAt == null || a.EndsAt > since))
            .OrderByDescending(a => a.StartsAt).Take(50).ToListAsync(ct);
        return Results.Ok(items.Select(a => ToDto(a, now)).ToList());
    }

    private static AnnouncementDto ToDto(Announcement a, DateTimeOffset now) =>
        new(a.Id, a.Type, a.Message, a.StartsAt, a.EndsAt, a.DelayMinutes, a.IsCurrentOrUpcoming(now), a.CreatedAt);

    private static async Task<IResult> Create(
        AnnouncementRequest r, Guid? doctorId, HttpContext http, ClinicAccess access, AppDbContext db, ScheduleService schedule,
        QueueEngine engine, AuditLogger audit, TimeProvider clock, CancellationToken ct)
    {
        var actor = await access.ResolveAsync(http.User, doctorId, ct);
        var now = clock.GetUtcNow();
        var v = new Validator().Required("message", r.Message, 280);
        if (r.Type is null) v.Add("type", "Choose the type of announcement.");
        if (r.StartsAt is null) v.Add("startsAt", "Choose when it starts.");
        if (r.EndsAt is not null && r.EndsAt < r.StartsAt) v.Add("endsAt", "The end must be after the start.");
        if (r.EndsAt is not null && r.EndsAt < now) v.Add("endsAt", "The end must be in the future.");
        if (r.Type == AnnouncementType.DelayedOpening && r.DelayMinutes is not (>= 5 and <= 240))
            v.Add("delayMinutes", "Enter a delay between 5 and 240 minutes.");
        if (r.StartsAt is { } s && r.EndsAt is { } e && (e - s).TotalDays > 90) v.Add("endsAt", "Announcements can cover at most 90 days.");
        if (!v.IsValid) return v.ToResult();

        var announcement = new Announcement
        {
            DoctorId = actor.Doctor.Id,
            ClinicId = actor.ClinicId,
            Type = r.Type!.Value,
            Message = r.Message!.Trim(),
            StartsAt = r.StartsAt!.Value,
            EndsAt = r.EndsAt,
            DelayMinutes = r.Type == AnnouncementType.DelayedOpening ? r.DelayMinutes : null,
            CreatedByUserId = actor.UserId,
            CreatedAt = now,
        };
        var startDate = ClinicTime.DateOf(announcement.StartsAt);
        var endDate = ClinicTime.DateOf(announcement.EndsAt ?? announcement.StartsAt);
        if (announcement.ClosesClinic)
            await schedule.EnsureCanCloseAsync(actor.Doctor.Id, actor.ClinicId, startDate, endDate, null, ct);

        db.Announcements.Add(announcement);
        audit.Record("PublishAnnouncement", "Doctor", actor.Doctor.Id.ToString(), details: $"{announcement.Type}: {announcement.Message}");
        await db.SaveChangesAsync(ct);

        var windowEnd = announcement.EndsAt ?? ClinicTime.At(endDate, TimeOnly.MaxValue);

        if (announcement.ClosesClinic)
        {
            await schedule.AddExceptionAsync(new ScheduleException
            {
                DoctorId = actor.Doctor.Id,
                ClinicId = actor.ClinicId,
                Kind = ScheduleExceptionKind.Closed,
                StartDate = startDate,
                EndDate = endDate,
                Reason = announcement.Message,
                AnnouncementId = announcement.Id,
            }, actor.UserId, ct);
        }
        else if (announcement.Type is AnnouncementType.DelayedOpening or AnnouncementType.ChangedHours)
        {
            var sessions = (await schedule.AffectedSessionsAsync(actor.Doctor.Id, actor.ClinicId, startDate, endDate, null, ct))
                .Where(s => s.EndsAt > announcement.StartsAt && s.StartsAt < windowEnd).ToList();
            foreach (var session in sessions)
            {
                await engine.RunAsync(session.Id, actor.UserId, async scope =>
                {
                    if (announcement.Type == AnnouncementType.DelayedOpening)
                        await scope.SetDelayAsync(announcement.DelayMinutes!.Value, announcement.Message, ct);
                    else
                        await scope.NotifyWaitingAsync(NotificationEvents.ScheduleChanged, $"announcement:{announcement.Id}",
                            "Schedule change", $"{scope.Doctor.FullName}: {announcement.Message}", ct);
                    return true;
                }, ct);
            }
        }

        return Results.Ok(ToDto(announcement, now));
    }

    private static async Task<IResult> Remove(
        Guid id, Guid? doctorId, HttpContext http, ClinicAccess access, AppDbContext db, ScheduleService schedule, QueueEngine engine,
        AuditLogger audit, TimeProvider clock, CancellationToken ct)
    {
        var actor = await access.ResolveAsync(http.User, doctorId, ct);
        var announcement = await db.Announcements.FirstOrDefaultAsync(a => a.Id == id && a.DoctorId == actor.Doctor.Id && a.RemovedAt == null, ct);
        if (announcement is null) return Results.NotFound();

        announcement.RemovedAt = clock.GetUtcNow();
        var linked = await db.ScheduleExceptions.Where(e => e.AnnouncementId == id && e.RemovedAt == null).ToListAsync(ct);
        await schedule.RemoveExceptionsAsync(linked, ct);
        audit.Record("RemoveAnnouncement", "Doctor", actor.Doctor.Id.ToString(), details: id.ToString());
        await db.SaveChangesAsync(ct);

        if (announcement.Type == AnnouncementType.DelayedOpening)
            await RecalculateDelaysAsync(announcement, actor.UserId, db, schedule, engine, ct);
        return Results.NoContent();
    }

    /// <summary>
    /// After a delay notice is removed, each session it covered gets the largest delay among the notices still active for
    /// it (0 when none). Changes go through the queue engine so they are serialized with queue actions and pushed live.
    /// </summary>
    private static async Task RecalculateDelaysAsync(
        Announcement removed, Guid actorUserId, AppDbContext db, ScheduleService schedule, QueueEngine engine, CancellationToken ct)
    {
        var (start, end, windowEnd) = Window(removed);
        var sessions = (await schedule.AffectedSessionsAsync(removed.DoctorId, removed.ClinicId, start, end, null, ct))
            .Where(s => s.DelayMinutes > 0 && s.EndsAt > removed.StartsAt && s.StartsAt < windowEnd).ToList();
        if (sessions.Count == 0) return;

        var others = (await db.Announcements.AsNoTracking()
                .Where(a => a.DoctorId == removed.DoctorId && a.ClinicId == removed.ClinicId && a.RemovedAt == null &&
                            a.Type == AnnouncementType.DelayedOpening && a.Id != removed.Id)
                .ToListAsync(ct))
            .Select(a => (Announcement: a, Window: Window(a))).ToList();

        foreach (var session in sessions)
        {
            var remaining = others
                .Where(o => session.EndsAt > o.Announcement.StartsAt && session.StartsAt < o.Window.WindowEnd)
                .Select(o => o.Announcement.DelayMinutes ?? 0)
                .DefaultIfEmpty(0).Max();
            if (remaining == session.DelayMinutes) continue;
            await engine.RunAsync(session.Id, actorUserId, scope =>
            {
                if (!scope.Session.IsFinished) scope.Session.DelayMinutes = remaining;
                return Task.FromResult(true);
            }, ct);
        }
    }

    private static (DateOnly Start, DateOnly End, DateTimeOffset WindowEnd) Window(Announcement a)
    {
        var start = ClinicTime.DateOf(a.StartsAt);
        var end = ClinicTime.DateOf(a.EndsAt ?? a.StartsAt);
        return (start, end, a.EndsAt ?? ClinicTime.At(end, TimeOnly.MaxValue));
    }
}
