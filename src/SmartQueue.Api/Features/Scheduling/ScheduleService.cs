using Microsoft.EntityFrameworkCore;
using SmartQueue.Api.Common;
using SmartQueue.Api.Domain;
using SmartQueue.Api.Features.Queue;
using SmartQueue.Api.Infrastructure;

namespace SmartQueue.Api.Features.Scheduling;

/// <summary>Applies date exceptions to sessions that already have bookings, notifying affected patients.</summary>
public sealed class ScheduleService(AppDbContext db, QueueEngine engine, TimeProvider clock)
{
    public async Task<List<QueueSession>> AffectedSessionsAsync(
        Guid doctorId, Guid clinicId, DateOnly start, DateOnly end, string? sessionName, CancellationToken ct)
    {
        var today = ClinicTime.Today(clock);
        var sessions = await db.QueueSessions.AsNoTracking()
            .Where(s => s.DoctorId == doctorId && s.ClinicId == clinicId && s.Date >= start && s.Date <= end && s.Date >= today &&
                        s.State != SessionState.Closed && s.State != SessionState.Cancelled)
            .ToListAsync(ct);
        return sessions.Where(s => sessionName is null || string.Equals(s.Name, sessionName, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public async Task<int> CountActiveAppointmentsAsync(IEnumerable<QueueSession> sessions, CancellationToken ct)
    {
        var ids = sessions.Select(s => s.Id).ToList();
        return await db.Appointments.CountAsync(a => ids.Contains(a.QueueSessionId) &&
            (a.Status == AppointmentStatus.Booked || a.Status == AppointmentStatus.CheckedIn ||
             a.Status == AppointmentStatus.Requeued || a.Status == AppointmentStatus.Missed), ct);
    }

    /// <summary>
    /// A closure cannot be applied while a patient is being seen in one of its sessions; checking before anything is saved
    /// keeps the closure from being half-applied (exception stored, some sessions still open).
    /// </summary>
    public async Task EnsureCanCloseAsync(
        Guid doctorId, Guid clinicId, DateOnly start, DateOnly end, string? sessionName, CancellationToken ct)
    {
        var ids = (await AffectedSessionsAsync(doctorId, clinicId, start, end, sessionName, ct)).Select(s => s.Id).ToList();
        var served = await db.Appointments.AsNoTracking()
            .Where(a => ids.Contains(a.QueueSessionId) &&
                        (a.Status == AppointmentStatus.Called || a.Status == AppointmentStatus.InConsultation))
            .Select(a => new { a.TokenNumber, a.Date, a.SessionName })
            .FirstOrDefaultAsync(ct);
        if (served is not null)
            throw ApiException.Conflict(
                $"T-{served.TokenNumber:D3} is being seen in the {served.SessionName} session on {served.Date:d MMM}.",
                "Complete the consultation or mark the patient missed, then close the clinic.");
    }

    public async Task<ScheduleException> AddExceptionAsync(ScheduleException exception, Guid actorUserId, CancellationToken ct)
    {
        if (exception.Kind == ScheduleExceptionKind.Closed)
            await EnsureCanCloseAsync(exception.DoctorId, exception.ClinicId, exception.StartDate, exception.EndDate, exception.SessionName, ct);

        exception.CreatedAt = clock.GetUtcNow();
        exception.CreatedByUserId = actorUserId;
        db.ScheduleExceptions.Add(exception);
        await db.SaveChangesAsync(ct);

        var affected = await AffectedSessionsAsync(exception.DoctorId, exception.ClinicId, exception.StartDate, exception.EndDate,
            exception.SessionName, ct);
        foreach (var session in affected)
        {
            if (exception.Kind == ScheduleExceptionKind.Closed)
            {
                await engine.RunAsync(session.Id, actorUserId, async scope =>
                {
                    await scope.CancelSessionAsync(exception.Reason ?? "Clinic closed", ct);
                    return true;
                }, ct);
            }
            else if (exception is { StartTime: { } startTime, EndTime: { } endTime })
            {
                await engine.RunAsync(session.Id, actorUserId, async scope =>
                {
                    await scope.ChangeHoursAsync(ClinicTime.At(session.Date, startTime), ClinicTime.At(session.Date, endTime),
                        exception.Capacity ?? session.Capacity, ct);
                    return true;
                }, ct);
            }
        }
        return exception;
    }

    /// <summary>
    /// Removing a closure reopens future sessions for new bookings, unless another active closure still covers them.
    /// Cancelled appointments are not restored.
    /// </summary>
    public async Task RemoveExceptionsAsync(IReadOnlyList<ScheduleException> exceptions, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var today = ClinicTime.Today(clock);
        foreach (var exception in exceptions) exception.RemovedAt = now;

        var removedIds = exceptions.Select(e => e.Id).ToList();
        foreach (var exception in exceptions.Where(e => e.Kind == ScheduleExceptionKind.Closed))
        {
            var sessions = (await db.QueueSessions
                    .Where(s => s.DoctorId == exception.DoctorId && s.ClinicId == exception.ClinicId &&
                                s.Date >= exception.StartDate && s.Date <= exception.EndDate &&
                                s.Date >= today && s.State == SessionState.Cancelled)
                    .ToListAsync(ct))
                .Where(s => Covers(exception, s)).ToList();
            if (sessions.Count == 0) continue;

            var first = sessions.Min(s => s.Date);
            var last = sessions.Max(s => s.Date);
            var stillClosed = await db.ScheduleExceptions.AsNoTracking()
                .Where(e => e.DoctorId == exception.DoctorId && e.ClinicId == exception.ClinicId && e.RemovedAt == null &&
                            e.Kind == ScheduleExceptionKind.Closed && !removedIds.Contains(e.Id) &&
                            e.StartDate <= last && e.EndDate >= first)
                .ToListAsync(ct);

            foreach (var s in sessions.Where(s => !stillClosed.Any(other => Covers(other, s))))
            {
                s.State = SessionState.Scheduled;
                s.ClosedAt = null;
            }
        }
        await db.SaveChangesAsync(ct);
    }

    private static bool Covers(ScheduleException e, QueueSession s) =>
        s.Date >= e.StartDate && s.Date <= e.EndDate &&
        (e.SessionName is null || string.Equals(s.Name, e.SessionName, StringComparison.OrdinalIgnoreCase));
}
