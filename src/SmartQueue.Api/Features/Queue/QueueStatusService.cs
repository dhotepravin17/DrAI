using Microsoft.EntityFrameworkCore;
using SmartQueue.Api.Common;
using SmartQueue.Api.Domain;
using SmartQueue.Api.Infrastructure;

namespace SmartQueue.Api.Features.Queue;

public sealed record NoticeDto(string Type, string Message, DateTimeOffset StartsAt, DateTimeOffset? EndsAt);

/// <summary>What a patient sees for one appointment. Never includes other patients' names or details (PRD FR-10).</summary>
public sealed record LiveStatus(
    Guid AppointmentId, string Reference, string Token, AppointmentStatus Status, Guid PatientId, string PatientName,
    string DoctorName, string? DoctorCode, string ClinicName, string ClinicAddress,
    Guid SessionId, DateOnly Date, string SessionName, DateTimeOffset SessionStartsAt, DateTimeOffset SessionEndsAt,
    SessionState SessionState, int? PatientsAhead, string? NowServing, int? EstimateMinMinutes, int? EstimateMaxMinutes,
    int? RequeueAfterCompletions, bool CanCancel, BookingSource Source, string? FollowUpNote,
    IReadOnlyList<NoticeDto> Notices, DateTimeOffset AsOf);

public sealed class QueueStatusService(AppDbContext db, TimeProvider clock)
{
    public async Task<double> AverageConsultMinutesAsync(Doctor doctor, CancellationToken ct)
    {
        var since = clock.GetUtcNow().AddDays(-30);
        var samples = await db.Appointments.AsNoTracking()
            .Where(a => a.DoctorId == doctor.Id && a.Status == AppointmentStatus.Completed && a.CompletedAt > since && a.CalledAt != null)
            .OrderByDescending(a => a.CompletedAt)
            .Take(WaitEstimator.SampleSize)
            .Select(a => new { a.CalledAt, a.CompletedAt })
            .ToListAsync(ct);
        return WaitEstimator.AverageConsultMinutes(
            samples.Select(s => (s.CompletedAt!.Value - s.CalledAt!.Value).TotalMinutes).ToList(), doctor.AvgConsultMinutes);
    }

    public async Task<List<NoticeDto>> NoticesAsync(Guid doctorId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var list = await db.Announcements.AsNoTracking()
            .Where(a => a.DoctorId == doctorId && a.RemovedAt == null && a.StartsAt <= to && (a.EndsAt == null || a.EndsAt >= from))
            .OrderBy(a => a.StartsAt).ToListAsync(ct);
        return list.Where(a => a.IsCurrentOrUpcoming(now))
            .Select(a => new NoticeDto(a.Type.ToString(), a.Message, a.StartsAt, a.EndsAt)).ToList();
    }

    public async Task<List<LiveStatus>> BuildAsync(IReadOnlyCollection<Appointment> appointments, CancellationToken ct)
    {
        if (appointments.Count == 0) return [];
        var now = clock.GetUtcNow();
        var sessionIds = appointments.Select(a => a.QueueSessionId).Distinct().ToList();
        var sessions = await db.QueueSessions.AsNoTracking().Where(s => sessionIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, ct);
        var activeSessionIds = sessions.Values.Where(s => !s.IsFinished).Select(s => s.Id).ToList();
        var sessionQueues = (await db.Appointments.AsNoTracking().Where(a => activeSessionIds.Contains(a.QueueSessionId)).ToListAsync(ct))
            .GroupBy(a => a.QueueSessionId).ToDictionary(g => g.Key, g => g.ToList());

        var doctorIds = appointments.Select(a => a.DoctorId).Distinct().ToList();
        var doctors = await db.Doctors.AsNoTracking().Where(d => doctorIds.Contains(d.Id)).ToDictionaryAsync(d => d.Id, ct);
        var clinicIds = appointments.Select(a => a.ClinicId).Distinct().ToList();
        var clinics = await db.Clinics.AsNoTracking().Where(c => clinicIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, ct);
        var patientIds = appointments.Select(a => a.PatientId).Distinct().ToList();
        var patients = await db.Patients.AsNoTracking().Where(p => patientIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.FullName, ct);

        var averages = new Dictionary<Guid, double>();
        var result = new List<LiveStatus>();
        foreach (var a in appointments)
        {
            var session = sessions[a.QueueSessionId];
            var doctor = doctors[a.DoctorId];
            var clinic = clinics[a.ClinicId];
            int? ahead = null, min = null, max = null, requeueAfter = null;
            string? nowServing = null;

            if (sessionQueues.TryGetValue(session.Id, out var queue))
            {
                nowServing = queue.FirstOrDefault(x => x.IsBeingServed)?.Token;
                var waiting = QueueOrdering.Waiting(queue);
                ahead = QueueOrdering.PatientsAhead(waiting, a.Id);
                if (ahead is not null)
                {
                    if (!averages.TryGetValue(doctor.Id, out var avg))
                        averages[doctor.Id] = avg = await AverageConsultMinutesAsync(doctor, ct);
                    var estimate = WaitEstimator.Estimate(ahead.Value, nowServing is not null, avg, now, session.StartsAt, session.DelayMinutes);
                    (min, max) = (estimate.MinMinutes, estimate.MaxMinutes);
                }
                var current = queue.First(x => x.Id == a.Id);
                if (current.Status == AppointmentStatus.Missed)
                    requeueAfter = Math.Max(0, doctor.RequeueAfterCompletions - (session.CompletionCount - (current.MissedAtCompletion ?? 0)));
            }

            var notices = await NoticesAsync(doctor.Id, session.StartsAt.AddHours(-12), session.EndsAt, ct);
            if (session.State == SessionState.Paused)
                notices.Insert(0, new NoticeDto("QueuePaused", "The doctor has paused this queue. Your place is kept.", now, null));

            result.Add(new LiveStatus(
                a.Id, a.Reference, a.Token, a.Status, a.PatientId, patients[a.PatientId],
                doctor.FullName, doctor.DoctorCode, clinic.Name, clinic.AddressLine,
                session.Id, session.Date, session.Name, session.StartsAt, session.EndsAt, session.State,
                ahead, nowServing, min, max, requeueAfter,
                AppointmentStateMachine.CanTransition(a.Status, AppointmentStatus.Cancelled) && !session.IsFinished,
                a.Source, a.Status == AppointmentStatus.Completed ? a.FollowUpNote : null, notices, now));
        }
        return result;
    }
}
