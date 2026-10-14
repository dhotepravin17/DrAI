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

    public async Task<ScheduleException> AddExceptionAsync(ScheduleException exception, Guid actorUserId, CancellationToken ct)
    {
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

    /// <summary>Removing a closure reopens future sessions for new bookings; cancelled appointments are not restored.</summary>
    public async Task RemoveExceptionsAsync(IReadOnlyList<ScheduleException> exceptions, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var today = ClinicTime.Today(clock);
        foreach (var exception in exceptions)
        {
            exception.RemovedAt = now;
            if (exception.Kind != ScheduleExceptionKind.Closed) continue;
            var sessions = await db.QueueSessions
                .Where(s => s.DoctorId == exception.DoctorId && s.Date >= exception.StartDate && s.Date <= exception.EndDate &&
                            s.Date >= today && s.State == SessionState.Cancelled)
                .ToListAsync(ct);
            foreach (var s in sessions.Where(s => exception.SessionName is null || string.Equals(s.Name, exception.SessionName, StringComparison.OrdinalIgnoreCase)))
            {
                s.State = SessionState.Scheduled;
                s.ClosedAt = null;
            }
        }
        await db.SaveChangesAsync(ct);
    }
}
