using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SmartQueue.Api.Common;
using SmartQueue.Api.Domain;
using SmartQueue.Api.Infrastructure;

namespace SmartQueue.Api.Features.Scheduling;

public sealed record PlannedSession(
    DateOnly Date, string Name, DateTimeOffset StartsAt, DateTimeOffset EndsAt, int Capacity, bool Closed, string? ClosedReason);

/// <summary>Turns weekly templates and date exceptions into concrete sessions, and materializes queue sessions on demand.</summary>
public sealed class SessionPlanner(AppDbContext db, TimeProvider clock)
{
    public async Task<Guid> PrimaryClinicIdAsync(Guid doctorId, CancellationToken ct)
    {
        var clinicId = await db.DoctorClinicAssignments.Where(a => a.DoctorId == doctorId && a.IsPrimary)
            .Select(a => a.ClinicId).FirstOrDefaultAsync(ct);
        return clinicId != Guid.Empty ? clinicId : throw ApiException.Conflict("The doctor has no primary clinic.");
    }

    public async Task<List<PlannedSession>> PlanAsync(Guid doctorId, Guid clinicId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var templates = await db.SessionTemplates.AsNoTracking()
            .Where(t => t.DoctorId == doctorId && t.ClinicId == clinicId).ToListAsync(ct);
        var exceptions = await db.ScheduleExceptions.AsNoTracking()
            .Where(e => e.DoctorId == doctorId && e.ClinicId == clinicId && e.RemovedAt == null && e.StartDate <= to && e.EndDate >= from)
            .OrderBy(e => e.Id).ToListAsync(ct);

        var result = new List<PlannedSession>();
        for (var date = from; date <= to; date = date.AddDays(1))
        {
            var names = templates.Where(t => t.DayOfWeek == date.DayOfWeek).Select(t => t.Name)
                .Concat(exceptions.Where(e => e.Kind == ScheduleExceptionKind.SpecialHours && e.SessionName != null && e.Covers(date, e.SessionName))
                    .Select(e => e.SessionName!))
                .Distinct(StringComparer.OrdinalIgnoreCase);

            foreach (var name in names)
            {
                var template = templates.FirstOrDefault(t => t.DayOfWeek == date.DayOfWeek && string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
                var special = exceptions.LastOrDefault(e => e.Kind == ScheduleExceptionKind.SpecialHours && e.Covers(date, name));
                var closed = exceptions.LastOrDefault(e => e.Kind == ScheduleExceptionKind.Closed && e.Covers(date, name));

                var start = special?.StartTime ?? template?.StartTime;
                var end = special?.EndTime ?? template?.EndTime;
                var capacity = special?.Capacity ?? template?.Capacity;
                if (start is null || end is null || capacity is null) continue;

                result.Add(new PlannedSession(date, template?.Name ?? name, ClinicTime.At(date, start.Value), ClinicTime.At(date, end.Value),
                    capacity.Value, closed is not null, closed?.Reason));
            }
        }
        return result.OrderBy(s => s.StartsAt).ToList();
    }

    public async Task<PlannedSession?> FindPlannedAsync(Guid doctorId, Guid clinicId, DateOnly date, string name, CancellationToken ct) =>
        (await PlanAsync(doctorId, clinicId, date, date, ct))
            .FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Returns the queue session for a planned session, creating it on first use.</summary>
    public async Task<QueueSession> EnsureSessionAsync(Guid doctorId, Guid clinicId, PlannedSession plan, CancellationToken ct)
    {
        var existing = await Find(doctorId, clinicId, plan, ct);
        if (existing is not null) return existing;

        var session = new QueueSession
        {
            DoctorId = doctorId,
            ClinicId = clinicId,
            Date = plan.Date,
            Name = plan.Name,
            StartsAt = plan.StartsAt,
            EndsAt = plan.EndsAt,
            Capacity = plan.Capacity,
            State = plan.Closed ? SessionState.Cancelled : SessionState.Scheduled,
            CreatedAt = clock.GetUtcNow(),
        };
        db.QueueSessions.Add(session);
        try
        {
            await db.SaveChangesAsync(ct);
            return session;
        }
        catch (DbUpdateException e) when (e.InnerException is SqlException { Number: 2601 or 2627 })
        {
            // Another request created it at the same moment.
            db.Entry(session).State = EntityState.Detached;
            var created = await Find(doctorId, clinicId, plan, ct);
            if (created is null) throw;
            return created;
        }
    }

    private Task<QueueSession?> Find(Guid doctorId, Guid clinicId, PlannedSession plan, CancellationToken ct) =>
        db.QueueSessions.AsNoTracking().FirstOrDefaultAsync(s =>
            s.DoctorId == doctorId && s.ClinicId == clinicId && s.Date == plan.Date && s.Name == plan.Name, ct);
}
