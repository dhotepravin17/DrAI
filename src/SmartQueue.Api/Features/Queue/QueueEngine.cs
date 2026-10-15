using Microsoft.EntityFrameworkCore;
using SmartQueue.Api.Common;
using SmartQueue.Api.Domain;
using SmartQueue.Api.Features.Analytics;
using SmartQueue.Api.Features.Admin;
using SmartQueue.Api.Features.Notifications;
using SmartQueue.Api.Infrastructure;

namespace SmartQueue.Api.Features.Queue;

public interface IQueueEvents
{
    Task SessionChangedAsync(Guid sessionId);
}

/// <summary>
/// The single authority for queue state (PRD §36: ordering, tokens and requeue counting are decided on the backend).
/// Each operation locks the session row, loads the session's appointments, applies the change, runs the automatic
/// rules (requeue, turn-approaching alerts) and commits, so concurrent operations on one queue are serialized.
/// </summary>
public sealed class QueueEngine(
    AppDbContext db, TimeProvider clock, NotificationOutbox outbox, AuditLogger audit, ProductAnalytics analytics, IQueueEvents events,
    ILogger<QueueEngine> logger)
{
    public async Task<T> RunAsync<T>(Guid sessionId, Guid? actorUserId, Func<QueueScope, Task<T>> action, CancellationToken ct)
    {
        T result;
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            var locked = await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE [QueueSessions] SET [LockVersion] = [LockVersion] + 1 WHERE [Id] = {sessionId}", ct);
            if (locked == 0) throw ApiException.NotFound("Appointment session not found.");

            // Anything tracked before the lock may be stale; read fresh state now that the lock is held.
            foreach (var entry in db.ChangeTracker.Entries()
                         .Where(e => e.Entity is QueueSession s && s.Id == sessionId || e.Entity is Appointment a && a.QueueSessionId == sessionId)
                         .ToList())
                entry.State = EntityState.Detached;

            var session = await db.QueueSessions.SingleAsync(s => s.Id == sessionId, ct);
            var doctor = await db.Doctors.AsNoTracking().SingleAsync(d => d.Id == session.DoctorId, ct);
            var appointments = await db.Appointments.Where(a => a.QueueSessionId == sessionId).ToListAsync(ct);
            var patientIds = appointments.Select(a => a.PatientId).Distinct().ToList();
            var patients = await db.Patients.AsNoTracking().Where(p => patientIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);

            var scope = new QueueScope(db, outbox, audit, analytics, clock.GetUtcNow(), session, doctor, appointments, patients, actorUserId);
            result = await action(scope);
            await scope.ApplyAutomaticRulesAsync(ct);

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }

        try
        {
            await events.SessionChangedAsync(sessionId);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Could not publish real-time update for session {SessionId}.", sessionId);
        }
        return result;
    }
}

public sealed class QueueScope
{
    private readonly AppDbContext _db;
    private readonly NotificationOutbox _outbox;
    private readonly AuditLogger _audit;
    private readonly ProductAnalytics _analytics;
    private readonly List<Appointment> _appointments;
    private readonly Dictionary<Guid, Patient> _patients;
    private readonly Guid? _actor;

    internal QueueScope(
        AppDbContext db, NotificationOutbox outbox, AuditLogger audit, ProductAnalytics analytics, DateTimeOffset now, QueueSession session, Doctor doctor,
        List<Appointment> appointments, Dictionary<Guid, Patient> patients, Guid? actor)
    {
        _db = db;
        _outbox = outbox;
        _audit = audit;
        _analytics = analytics;
        Now = now;
        Session = session;
        Doctor = doctor;
        _appointments = appointments;
        _patients = patients;
        _actor = actor;
    }

    public DateTimeOffset Now { get; }
    public QueueSession Session { get; }
    public Doctor Doctor { get; }
    public IReadOnlyList<Appointment> Appointments => _appointments;
    public IReadOnlyDictionary<Guid, Patient> Patients => _patients;
    public List<Appointment> Waiting => QueueOrdering.Waiting(_appointments);
    public Appointment? BeingServed => _appointments.FirstOrDefault(a => a.IsBeingServed);

    public Appointment Get(Guid appointmentId) =>
        _appointments.FirstOrDefault(a => a.Id == appointmentId) ?? throw ApiException.NotFound("Appointment not found in this session.");

    // ---- Booking ----

    public async Task<Appointment> BookAsync(
        Patient patient, Guid? bookedBy, BookingSource source, CancellationToken ct,
        EmergencyReason? emergency = null, string? emergencyNote = null)
    {
        if (Session.IsFinished) throw ApiException.Conflict("This session is closed.");
        if (source == BookingSource.Online)
        {
            if (!Session.AcceptsOnlineBookings) throw ApiException.Conflict("This session is not accepting online bookings right now.");
            if (Now >= Session.EndsAt) throw ApiException.Conflict("This session has ended.");
            var booked = _appointments.Count(a => a.Status != AppointmentStatus.Cancelled);
            if (booked >= Session.Capacity) throw ApiException.Conflict("This session is fully booked.", "Try another session.");
        }
        if (_appointments.Any(a => a.PatientId == patient.Id && a.IsActive))
            throw ApiException.Conflict($"{patient.FullName} already has an active appointment in this session.");

        Session.TokenCounter++;
        Session.NextSequence++;
        var number = (await _db.Database.SqlQuery<long>(
            $"SELECT NEXT VALUE FOR [AppointmentReferenceNumbers] AS [Value]").ToListAsync(ct)).Single();

        var appointment = new Appointment
        {
            Reference = $"APT-{ClinicTime.Local(Now).Year}-{number:D6}",
            QueueSessionId = Session.Id,
            DoctorId = Session.DoctorId,
            ClinicId = Session.ClinicId,
            PatientId = patient.Id,
            Date = Session.Date,
            SessionName = Session.Name,
            BookedByUserId = bookedBy,
            Source = source,
            TokenNumber = Session.TokenCounter,
            QueueSequence = Session.NextSequence,
            CheckedInAt = source == BookingSource.WalkIn ? Now : null,
            Status = source == BookingSource.WalkIn ? AppointmentStatus.CheckedIn : AppointmentStatus.Booked,
            CreatedAt = Now,
            UpdatedAt = Now,
        };
        _db.Appointments.Add(appointment);
        _appointments.Add(appointment);
        _patients[patient.Id] = patient;
        AddEvent(appointment, "Booked", null, appointment.Status, source.ToString());
        _analytics.Track(ProductEventNames.AppointmentBooked,
            new { appointmentId = appointment.Id, doctorId = Doctor.Id, source = source.ToString(), forSelf = patient.UserId is not null && patient.UserId == bookedBy },
            source.ToString());

        if (emergency is not null) Prioritize(appointment, emergency.Value, emergencyNote);

        await NotifyAsync(appointment, NotificationEvents.AppointmentConfirmed, $"confirmed:{appointment.Id}",
            "Appointment confirmed",
            $"Token {appointment.Token} confirmed for {patient.FullName} with {Doctor.FullName}, {ClinicTime.Describe(Session.Date, Session.Name)}.", ct);
        return appointment;
    }

    // ---- Session controls ----

    public void Open()
    {
        if (Session.State is not (SessionState.Scheduled or SessionState.Paused))
            throw ApiException.Conflict($"A {Session.State} session cannot be opened.");
        Session.State = SessionState.Open;
        if (Session.OpenedAt is null)
            _analytics.Track(ProductEventNames.QueueSessionOpened, new { doctorId = Doctor.Id, sessionId = Session.Id, booked = Booked() });
        Session.OpenedAt ??= Now;
        _audit.Record("OpenQueue", "QueueSession", Session.Id.ToString());
    }

    public void Pause()
    {
        if (Session.State != SessionState.Open) throw ApiException.Conflict("Only open appointments can be paused.");
        Session.State = SessionState.Paused;
        _audit.Record("PauseQueue", "QueueSession", Session.Id.ToString());
    }

    public void Resume()
    {
        if (Session.State != SessionState.Paused) throw ApiException.Conflict("Only paused appointments can be resumed.");
        Session.State = SessionState.Open;
        _audit.Record("ResumeQueue", "QueueSession", Session.Id.ToString());
    }

    public async Task CloseAsync(CancellationToken ct)
    {
        if (Session.IsFinished) throw ApiException.Conflict("This session is already closed.");
        if (BeingServed is { } served)
            throw ApiException.Conflict($"Complete or mark {served.Token} as missed before closing the session.");

        foreach (var a in _appointments.Where(a => a.Status == AppointmentStatus.Missed).ToList())
            Transition(a, AppointmentStatus.NoShow, "SessionClosed");
        foreach (var a in _appointments.Where(a => a.IsWaiting).ToList())
            await CancelAsync(a, CancellationReason.SessionClosed, "The session was closed by the clinic.", ct);

        Session.State = SessionState.Closed;
        Session.ClosedAt = Now;
        _audit.Record("CloseQueue", "QueueSession", Session.Id.ToString());
        _analytics.Track(ProductEventNames.QueueSessionClosed, new
        {
            doctorId = Doctor.Id, sessionId = Session.Id, booked = Booked(),
            completed = _appointments.Count(a => a.Status == AppointmentStatus.Completed),
            noShows = _appointments.Count(a => a.Status == AppointmentStatus.NoShow),
        }, value: Session.CompletionCount);
    }

    /// <summary>The clinic is closed for this session: cancel everyone still waiting and stop the session (PRD FR-08, FR-16).</summary>
    public async Task CancelSessionAsync(string reason, CancellationToken ct)
    {
        if (Session.IsFinished) return;
        if (BeingServed is { } served)
            throw ApiException.Conflict($"Finish the consultation for {served.Token} before closing this session.");
        foreach (var a in _appointments.Where(a => a.IsWaiting || a.Status == AppointmentStatus.Missed).ToList())
            await CancelAsync(a, CancellationReason.ClinicClosed, reason, ct);
        Session.State = SessionState.Cancelled;
        Session.ClosedAt = Now;
        _audit.Record("CancelSession", "QueueSession", Session.Id.ToString(), details: reason);
    }

    public async Task ChangeHoursAsync(DateTimeOffset startsAt, DateTimeOffset endsAt, int capacity, CancellationToken ct)
    {
        if (Session.IsFinished) return;
        Session.StartsAt = startsAt;
        Session.EndsAt = endsAt;
        Session.Capacity = capacity;
        var local = $"{ClinicTime.Local(startsAt):h:mm tt}–{ClinicTime.Local(endsAt):h:mm tt}";
        foreach (var a in _appointments.Where(a => a.IsWaiting).ToList())
            await NotifyAsync(a, NotificationEvents.ScheduleChanged, $"hours:{a.Id}:{Session.LockVersion}", "Schedule change",
                $"{Doctor.FullName}'s {Session.Name} session on {Session.Date:d MMM} now runs {local}. Your token {a.Token} is unchanged.", ct);
    }

    public async Task SetDelayAsync(int minutes, string message, CancellationToken ct)
    {
        if (Session.IsFinished) return;
        Session.DelayMinutes = minutes;
        foreach (var a in _appointments.Where(a => a.IsWaiting).ToList())
            await NotifyAsync(a, NotificationEvents.QueueDelay, $"delay:{a.Id}:{Session.LockVersion}", "Appointments delayed",
                $"{Doctor.FullName}'s appointments are running about {minutes} min late. {message}", ct);
    }

    public async Task NotifyWaitingAsync(string eventType, string key, string title, string body, CancellationToken ct)
    {
        foreach (var a in _appointments.Where(a => a.IsWaiting).ToList())
            await NotifyAsync(a, eventType, $"{key}:{a.Id}", title, body, ct);
    }

    // ---- Patient actions ----

    public async Task CallAsync(Appointment? target, CancellationToken ct)
    {
        if (Session.State == SessionState.Scheduled) Open();
        if (Session.State != SessionState.Open) throw ApiException.Conflict("Resume appointments to call patients.");
        if (BeingServed is { } served)
            throw ApiException.Conflict($"Complete or mark {served.Token} as missed before calling the next patient.");

        target ??= Waiting.FirstOrDefault() ?? throw ApiException.Conflict("No patients are waiting.");
        if (!target.IsWaiting) throw ApiException.Conflict($"{target.Token} is not waiting.");

        Transition(target, AppointmentStatus.Called, target.Id == Waiting[0].Id ? null : "Called out of order");
        target.CalledAt = Now;
        target.CallCount++;
        await NotifyAsync(target, NotificationEvents.PatientCalled, $"called:{target.Id}:{target.CallCount}",
            "It's your turn", $"{target.Token}, please proceed to {Doctor.FullName}'s cabin.", ct, TimeSpan.FromMinutes(5));
    }

    public void StartConsultation(Appointment a)
    {
        Transition(a, AppointmentStatus.InConsultation);
        a.ConsultStartedAt = Now;
    }

    public void Complete(Appointment a, string? followUpNote)
    {
        if (!a.IsBeingServed) throw ApiException.Conflict($"{a.Token} has not been called.");
        Session.CompletionCount++;
        Transition(a, AppointmentStatus.Completed);
        a.CompletedAt = Now;
        a.FollowUpNote = followUpNote.TrimToNull();
    }

    public async Task MarkMissedAsync(Appointment a, CancellationToken ct)
    {
        if (a.Status != AppointmentStatus.Called) throw ApiException.Conflict($"Only a called patient can be marked missed.");
        var allowedAt = a.CalledAt!.Value.AddMinutes(Doctor.GracePeriodMinutes);
        if (Now < allowedAt)
        {
            var wait = (int)Math.Ceiling((allowedAt - Now).TotalMinutes);
            throw ApiException.Conflict($"Wait {wait} more minute{(wait == 1 ? "" : "s")} before marking {a.Token} as missed.",
                $"The grace period is {Doctor.GracePeriodMinutes} minutes.");
        }

        a.MissCount++;
        if (a.MissCount >= 2 && Doctor.SecondMissRule == SecondMissRule.NoShow)
        {
            Transition(a, AppointmentStatus.NoShow, "Missed a second time");
            return;
        }

        Transition(a, AppointmentStatus.Missed);
        a.MissedAtCompletion = Session.CompletionCount;
        await NotifyAsync(a, NotificationEvents.TurnMissed, $"missed:{a.Id}:{a.MissCount}",
            "You missed your turn",
            $"{a.Token} missed the call. You will be placed back in the appointment list after {Doctor.RequeueAfterCompletions} patients.", ct);
    }

    public async Task RequeueNowAsync(Appointment a, CancellationToken ct)
    {
        if (a.Status != AppointmentStatus.Missed) throw ApiException.Conflict($"{a.Token} is not waiting to be added back.");
        await RequeueAsync(a, "Added back early by clinic", ct);
    }

    public void CheckIn(Appointment a)
    {
        if (a.Status == AppointmentStatus.Booked) Transition(a, AppointmentStatus.CheckedIn);
        else if (a.Status is not (AppointmentStatus.Requeued or AppointmentStatus.Missed or AppointmentStatus.CheckedIn))
            throw ApiException.Conflict($"{a.Token} cannot be checked in.");
        a.CheckedInAt ??= Now;
        a.UpdatedAt = Now;
    }

    public void Prioritize(Appointment a, EmergencyReason reason, string? note)
    {
        if (!a.IsWaiting) throw ApiException.Conflict($"Only a waiting patient can be prioritized.");
        a.Priority = QueuePriority.Emergency;
        a.EmergencyReason = reason;
        a.EmergencyNote = note.TrimToNull();
        a.PrioritizedAt = Now;
        Session.NextSequence++;
        a.PrioritySequence = Session.NextSequence;
        a.UpdatedAt = Now;
        AddEvent(a, "Prioritized", a.Status, a.Status, reason.ToString());
        _analytics.Track(ProductEventNames.EmergencyPrioritized, new { appointmentId = a.Id, reasonCategory = reason.ToString() }, reason.ToString());
        _reprioritized = true;
    }

    public void Defer(Appointment a)
    {
        if (!a.IsWaiting) throw ApiException.Conflict($"Only a waiting patient can be moved.");
        Session.NextSequence++;
        a.QueueSequence = Session.NextSequence;
        a.Priority = QueuePriority.Normal;
        a.PrioritizedAt = null;
        a.PrioritySequence = null;
        a.UpdatedAt = Now;
        AddEvent(a, "Deferred", a.Status, a.Status, "Moved to the end of the list");
    }

    public async Task CancelAsync(Appointment a, CancellationReason reason, string? note, CancellationToken ct)
    {
        if (!AppointmentStateMachine.CanTransition(a.Status, AppointmentStatus.Cancelled))
            throw ApiException.Conflict($"{a.Token} can no longer be cancelled.");
        Transition(a, AppointmentStatus.Cancelled, note ?? reason.ToString());
        a.CancelledAt = Now;
        a.CancellationReason = reason;
        var cancelledBy = reason == CancellationReason.Patient ? "Patient" : "Clinic";
        _analytics.Track(ProductEventNames.AppointmentCancelled, new { appointmentId = a.Id, cancelledBy, reason = reason.ToString() }, cancelledBy);
        var patient = _patients[a.PatientId];
        var body = reason switch
        {
            CancellationReason.Patient => $"Appointment {a.Token} for {patient.FullName} with {Doctor.FullName} is cancelled.",
            CancellationReason.ClinicClosed => $"{Doctor.FullName}'s clinic is closed on {Session.Date:d MMM}. Appointment {a.Token} is cancelled. Rebook from the app.",
            _ => $"Appointment {a.Token} with {Doctor.FullName} on {ClinicTime.Describe(Session.Date, Session.Name)} was cancelled by the clinic.",
        };
        await NotifyAsync(a, NotificationEvents.AppointmentCancelled, $"cancelled:{a.Id}", "Appointment cancelled", body, ct);
    }

    // ---- Automatic rules ----

    private bool _reprioritized;

    internal async Task ApplyAutomaticRulesAsync(CancellationToken ct)
    {
        if (Session.IsFinished) return;

        // Missed-turn requeue (PRD FR-15, §31.2): after N completions since the miss, or at once if nobody else is waiting.
        foreach (var missed in _appointments.Where(a => a.Status == AppointmentStatus.Missed).OrderBy(a => a.MissedAtCompletion).ToList())
        {
            var completedSince = Session.CompletionCount - (missed.MissedAtCompletion ?? Session.CompletionCount);
            if (completedSince >= Doctor.RequeueAfterCompletions)
                await RequeueAsync(missed, $"{completedSince} consultations completed since the miss", ct);
            else if (Waiting.Count == 0)
                await RequeueAsync(missed, "No other patients waiting", ct);
        }

        if (_reprioritized)
        {
            foreach (var a in Waiting.Where(a => a.Priority == QueuePriority.Normal && a.TurnApproachingNotifiedAt is not null))
                await NotifyAsync(a, NotificationEvents.Prioritized, $"reordered:{a.Id}:{Session.LockVersion}",
                    "Appointment update", $"An urgent case is being seen first. {a.Token} may take a little longer.", ct);
        }

        // Turn-approaching alert (PRD FR-11): once, when the patient is within the threshold and the doctor is seeing patients.
        if (Session.State == SessionState.Open)
        {
            var waiting = Waiting;
            for (var i = 0; i < waiting.Count && i <= Doctor.TurnApproachingThreshold; i++)
            {
                var a = waiting[i];
                if (a.TurnApproachingNotifiedAt is not null) continue;
                a.TurnApproachingNotifiedAt = Now;
                var ahead = i == 0 ? "You are next" : $"{i} patient{(i == 1 ? "" : "s")} ahead of you";
                await NotifyAsync(a, NotificationEvents.TurnApproaching, $"approaching:{a.Id}:{a.CallCount}",
                    "Your turn is near", $"{ahead} ({a.Token}, {Doctor.FullName}). Please reach the clinic.", ct);
            }
        }
    }

    private async Task RequeueAsync(Appointment a, string reason, CancellationToken ct)
    {
        Session.NextSequence++;
        a.QueueSequence = Session.NextSequence;
        a.TurnApproachingNotifiedAt = null;
        Transition(a, AppointmentStatus.Requeued, reason);
        var position = QueueOrdering.PatientsAhead(Waiting, a.Id) + 1;
        await NotifyAsync(a, NotificationEvents.Requeued, $"requeued:{a.Id}:{a.MissCount}",
            "Back in the list", $"{a.Token} is back in the appointment list with {Doctor.FullName}. Position: {position}.", ct);
    }

    // ---- Helpers ----

    private void Transition(Appointment a, AppointmentStatus to, string? reason = null)
    {
        if (!AppointmentStateMachine.CanTransition(a.Status, to))
            throw ApiException.Conflict($"{a.Token} cannot change from {a.Status} to {to}.");
        var from = a.Status;
        a.Status = to;
        a.UpdatedAt = Now;
        AddEvent(a, to.ToString(), from, to, reason);
        TrackTransition(a, to);
    }

    private int Booked() => _appointments.Count(a => a.Status != AppointmentStatus.Cancelled);

    /// <summary>Product events for state changes (PRD §38). Runs before the caller sets timestamps such as CalledAt.</summary>
    private void TrackTransition(Appointment a, AppointmentStatus to)
    {
        static double Minutes(DateTimeOffset from, DateTimeOffset to) => Math.Round(Math.Max(0, (to - from).TotalMinutes), 1);
        switch (to)
        {
            case AppointmentStatus.CheckedIn:
                _analytics.Track(ProductEventNames.PatientCheckedIn, new { appointmentId = a.Id });
                break;
            case AppointmentStatus.Called:
                var waited = Minutes(a.CheckedInAt ?? a.CreatedAt, Now);
                _analytics.Track(ProductEventNames.PatientCalled, new { appointmentId = a.Id, waitMinutes = waited, callCount = a.CallCount + 1 }, value: waited);
                break;
            case AppointmentStatus.Completed:
                var duration = Minutes(a.ConsultStartedAt ?? a.CalledAt ?? Now, Now);
                _analytics.Track(ProductEventNames.ConsultationCompleted, new { appointmentId = a.Id, durationMinutes = duration }, value: duration);
                break;
            case AppointmentStatus.Missed:
                _analytics.Track(ProductEventNames.TurnMissed, new { appointmentId = a.Id, missCount = a.MissCount });
                break;
            case AppointmentStatus.Requeued:
                _analytics.Track(ProductEventNames.TurnRequeued, new { appointmentId = a.Id });
                break;
            case AppointmentStatus.NoShow:
                _analytics.Track(ProductEventNames.NoShow, new { appointmentId = a.Id });
                break;
        }
    }

    private void AddEvent(Appointment a, string type, AppointmentStatus? from, AppointmentStatus? to, string? reason) =>
        _db.AppointmentEvents.Add(new AppointmentEvent
        {
            AppointmentId = a.Id,
            QueueSessionId = Session.Id,
            Type = type,
            FromStatus = from,
            ToStatus = to,
            ActorUserId = _actor,
            Reason = reason is { Length: > 300 } ? reason[..300] : reason,
            CompletionCount = Session.CompletionCount,
            OccurredAt = Now,
        });

    private Task NotifyAsync(Appointment a, string eventType, string dedupKey, string title, string body, CancellationToken ct,
        TimeSpan? expiresAfter = null) =>
        _outbox.EnqueueAsync(eventType, dedupKey, NotificationOutbox.ForAppointment(a, _patients[a.PatientId], title, body), ct, expiresAfter);
}
