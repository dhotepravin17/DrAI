using Microsoft.EntityFrameworkCore;
using SmartQueue.Api.Common;
using SmartQueue.Api.Domain;
using SmartQueue.Api.Features.Scheduling;
using SmartQueue.Api.Infrastructure;

namespace SmartQueue.Api.Features.Queue;

/// <summary>FR-12 doctor dashboard, FR-13 walk-ins, FR-14 emergency priority, FR-15 missed turns. Doctor or assigned receptionist.</summary>
public static class ClinicQueueEndpoints
{
    public sealed record SessionSummary(
        Guid SessionId, DateOnly Date, string Name, DateTimeOffset StartsAt, DateTimeOffset EndsAt, SessionState State,
        int Capacity, int Booked, int Waiting, int Completed, int Missed, string? NowServing, int DelayMinutes);

    public sealed record QueueEntry(
        Guid AppointmentId, string Reference, string Token, AppointmentStatus Status, Guid PatientId, string PatientName, int? PatientAge,
        BookingSource Source, string? BookedByName, DateTimeOffset BookedAt, DateTimeOffset? CheckedInAt, DateTimeOffset? CalledAt,
        int? Position, QueuePriority Priority, EmergencyReason? EmergencyReason, string? EmergencyNote, int MissCount,
        int? RequeueAfterCompletions, DateTimeOffset? MissAllowedAt, DateTimeOffset? CompletedAt, CancellationReason? CancellationReason);

    public sealed record QueueView(
        SessionSummary Session, QueueEntry? NowServing, IReadOnlyList<QueueEntry> Waiting, IReadOnlyList<QueueEntry> Missed,
        IReadOnlyList<QueueEntry> Done, double AvgConsultMinutes, int GracePeriodMinutes, int RequeueAfterCompletions);

    public sealed record CompleteRequest(string? FollowUpNote);
    public sealed record PrioritizeRequest(EmergencyReason? Reason, string? Note);
    public sealed record CancelRequest(string? Note);
    public sealed record WalkInRequest(Guid? PatientId, string? FullName, string? MobileNumber, int? AgeYears, PrioritizeRequest? Emergency);
    public sealed record PatientMatch(Guid PatientId, string FullName, Relationship Relationship, int? AgeYears);

    public static RouteGroupBuilder MapClinicQueueEndpoints(this RouteGroupBuilder api)
    {
        var clinic = api.MapGroup("/clinic").WithTags("Clinic queue").RequireAuthorization();

        clinic.MapGet("/contexts", async (HttpContext http, ClinicAccess access, CancellationToken ct) =>
            Results.Ok(await access.ContextsAsync(http.User.GetUserId(), ct)));
        clinic.MapGet("/sessions", Sessions);
        clinic.MapGet("/sessions/{id:guid}", (Guid id, Guid? doctorId, HttpContext http, ClinicAccess access, AppDbContext db,
            QueueStatusService status, TimeProvider clock, CancellationToken ct) => View(id, doctorId, http, access, db, status, clock, ct));

        MapSessionAction(clinic, "open", (s, _) => { s.Open(); return Task.CompletedTask; });
        MapSessionAction(clinic, "pause", (s, _) => { s.Pause(); return Task.CompletedTask; });
        MapSessionAction(clinic, "resume", (s, _) => { s.Resume(); return Task.CompletedTask; });
        MapSessionAction(clinic, "close", (s, ct) => s.CloseAsync(ct));
        MapSessionAction(clinic, "call-next", (s, ct) => s.CallAsync(null, ct));

        MapAppointmentAction<object>(clinic, "call", (s, a, _, ct) => s.CallAsync(a, ct));
        MapAppointmentAction<object>(clinic, "start", (s, a, _, _) => { s.StartConsultation(a); return Task.CompletedTask; });
        MapAppointmentAction<CompleteRequest>(clinic, "complete", (s, a, r, _) => { s.Complete(a, r?.FollowUpNote); return Task.CompletedTask; });
        MapAppointmentAction<object>(clinic, "miss", (s, a, _, ct) => s.MarkMissedAsync(a, ct));
        MapAppointmentAction<object>(clinic, "requeue", (s, a, _, ct) => s.RequeueNowAsync(a, ct));
        MapAppointmentAction<object>(clinic, "check-in", (s, a, _, _) => { s.CheckIn(a); return Task.CompletedTask; });
        MapAppointmentAction<object>(clinic, "defer", (s, a, _, _) => { s.Defer(a); return Task.CompletedTask; });
        MapAppointmentAction<CancelRequest>(clinic, "cancel", (s, a, r, ct) => s.CancelAsync(a, CancellationReason.Clinic, r?.Note.TrimToNull(), ct));
        MapAppointmentAction<PrioritizeRequest>(clinic, "prioritize", (s, a, r, _) =>
        {
            if (r?.Reason is null) throw ApiException.BadRequest("Choose a reason for prioritizing this patient.");
            s.Prioritize(a, r.Reason.Value, LimitNote(r.Note));
            return Task.CompletedTask;
        });

        clinic.MapPost("/sessions/{id:guid}/walk-ins", WalkIn);
        clinic.MapGet("/patients/lookup", Lookup);
        return api;
    }

    private static string? LimitNote(string? note)
    {
        var trimmed = note.TrimToNull();
        return trimmed is { Length: > 200 } ? throw ApiException.BadRequest("Keep the note under 200 characters.") : trimmed;
    }

    private static void MapSessionAction(RouteGroupBuilder clinic, string action, Func<QueueScope, CancellationToken, Task> apply) =>
        clinic.MapPost($"/sessions/{{id:guid}}/{action}", async (
            Guid id, Guid? doctorId, HttpContext http, ClinicAccess access, AppDbContext db, QueueEngine engine,
            QueueStatusService status, TimeProvider clock, CancellationToken ct) =>
        {
            var actor = await access.ResolveAsync(http.User, doctorId, ct);
            await EnsureOwnSession(db, actor, id, ct);
            await engine.RunAsync(id, actor.UserId, async scope => { await apply(scope, ct); return true; }, ct);
            return await View(id, actor.Doctor.Id, http, access, db, status, clock, ct);
        });

    private static void MapAppointmentAction<TBody>(
        RouteGroupBuilder clinic, string action, Func<QueueScope, Appointment, TBody?, CancellationToken, Task> apply) where TBody : class =>
        clinic.MapPost($"/appointments/{{id:guid}}/{action}", async (
            Guid id, Guid? doctorId, HttpContext http, ClinicAccess access, AppDbContext db, QueueEngine engine,
            QueueStatusService status, TimeProvider clock, CancellationToken ct) =>
        {
            TBody? body = null;
            if (typeof(TBody) != typeof(object) && http.Request.ContentLength is > 0)
                body = await http.Request.ReadFromJsonAsync<TBody>(ct);

            var actor = await access.ResolveAsync(http.User, doctorId, ct);
            var sessionId = await db.Appointments.Where(a => a.Id == id && a.DoctorId == actor.Doctor.Id)
                .Select(a => (Guid?)a.QueueSessionId).FirstOrDefaultAsync(ct) ?? throw ApiException.NotFound("Appointment not found.");
            await engine.RunAsync(sessionId, actor.UserId, async scope => { await apply(scope, scope.Get(id), body, ct); return true; }, ct);
            return await View(sessionId, actor.Doctor.Id, http, access, db, status, clock, ct);
        });

    private static async Task EnsureOwnSession(AppDbContext db, ClinicActor actor, Guid sessionId, CancellationToken ct)
    {
        if (!await db.QueueSessions.AnyAsync(s => s.Id == sessionId && s.DoctorId == actor.Doctor.Id, ct))
            throw ApiException.NotFound("Queue session not found.");
    }

    private static async Task<IResult> Sessions(
        DateOnly? date, Guid? doctorId, HttpContext http, ClinicAccess access, SessionPlanner planner, AppDbContext db,
        TimeProvider clock, CancellationToken ct)
    {
        var actor = await access.ResolveAsync(http.User, doctorId, ct);
        var day = date ?? ClinicTime.Today(clock);
        foreach (var plan in await planner.PlanAsync(actor.Doctor.Id, actor.ClinicId, day, day, ct))
            await planner.EnsureSessionAsync(actor.Doctor.Id, actor.ClinicId, plan, ct);

        var sessions = await db.QueueSessions.AsNoTracking()
            .Where(s => s.DoctorId == actor.Doctor.Id && s.Date == day).OrderBy(s => s.StartsAt).ToListAsync(ct);
        var ids = sessions.Select(s => s.Id).ToList();
        var appointments = await db.Appointments.AsNoTracking().Where(a => ids.Contains(a.QueueSessionId)).ToListAsync(ct);
        return Results.Ok(sessions.Select(s => Summarize(s, appointments.Where(a => a.QueueSessionId == s.Id).ToList())).ToList());
    }

    private static SessionSummary Summarize(QueueSession s, List<Appointment> list) => new(
        s.Id, s.Date, s.Name, s.StartsAt, s.EndsAt, s.State, s.Capacity,
        list.Count(a => a.Status != AppointmentStatus.Cancelled),
        list.Count(a => a.IsWaiting),
        list.Count(a => a.Status == AppointmentStatus.Completed),
        list.Count(a => a.Status == AppointmentStatus.Missed),
        list.FirstOrDefault(a => a.IsBeingServed)?.Token,
        s.DelayMinutes);

    private static async Task<IResult> View(
        Guid id, Guid? doctorId, HttpContext http, ClinicAccess access, AppDbContext db, QueueStatusService status,
        TimeProvider clock, CancellationToken ct)
    {
        var actor = await access.ResolveAsync(http.User, doctorId, ct);
        var session = await db.QueueSessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id && s.DoctorId == actor.Doctor.Id, ct);
        if (session is null) return Results.NotFound();

        var appointments = await db.Appointments.AsNoTracking().Where(a => a.QueueSessionId == id).ToListAsync(ct);
        var patientIds = appointments.Select(a => a.PatientId).Distinct().ToList();
        var patients = await db.Patients.AsNoTracking().Where(p => patientIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        var bookerIds = appointments.Where(a => a.BookedByUserId != null).Select(a => a.BookedByUserId!.Value).Distinct().ToList();
        var bookers = await db.Users.AsNoTracking().Where(u => bookerIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.FullName, ct);

        var doctor = actor.Doctor;
        var today = ClinicTime.Today(clock);
        var waiting = QueueOrdering.Waiting(appointments);

        QueueEntry ToEntry(Appointment a)
        {
            var patient = patients[a.PatientId];
            var age = patient.DateOfBirth is { } dob ? today.Year - dob.Year - (today < dob.AddYears(today.Year - dob.Year) ? 1 : 0) : (int?)null;
            string? bookedBy = a.Source == BookingSource.Online && a.BookedByUserId is { } b && patient.UserId != b && bookers.TryGetValue(b, out var n) ? n : null;
            int? requeueAfter = a.Status == AppointmentStatus.Missed
                ? Math.Max(0, doctor.RequeueAfterCompletions - (session.CompletionCount - (a.MissedAtCompletion ?? 0)))
                : null;
            return new QueueEntry(a.Id, a.Reference, a.Token, a.Status, a.PatientId, patient.FullName, age, a.Source, bookedBy, a.CreatedAt,
                a.CheckedInAt, a.CalledAt, a.IsWaiting ? QueueOrdering.PatientsAhead(waiting, a.Id) + 1 : null, a.Priority, a.EmergencyReason,
                a.EmergencyNote, a.MissCount, requeueAfter,
                a.Status == AppointmentStatus.Called ? a.CalledAt?.AddMinutes(doctor.GracePeriodMinutes) : null, a.CompletedAt, a.CancellationReason);
        }

        var served = appointments.FirstOrDefault(a => a.IsBeingServed);
        return Results.Ok(new QueueView(
            Summarize(session, appointments),
            served is null ? null : ToEntry(served),
            waiting.Select(ToEntry).ToList(),
            appointments.Where(a => a.Status == AppointmentStatus.Missed).OrderBy(a => a.MissedAtCompletion).Select(ToEntry).ToList(),
            appointments.Where(a => a.Status is AppointmentStatus.Completed or AppointmentStatus.Cancelled or AppointmentStatus.NoShow)
                .OrderByDescending(a => a.UpdatedAt).Select(ToEntry).ToList(),
            Math.Round(await status.AverageConsultMinutesAsync(doctor, ct), 1),
            doctor.GracePeriodMinutes,
            doctor.RequeueAfterCompletions));
    }

    private static async Task<IResult> WalkIn(
        Guid id, Guid? doctorId, WalkInRequest request, HttpContext http, ClinicAccess access, AppDbContext db, QueueEngine engine,
        QueueStatusService status, TimeProvider clock, CancellationToken ct)
    {
        var actor = await access.ResolveAsync(http.User, doctorId, ct);
        await EnsureOwnSession(db, actor, id, ct);
        var now = clock.GetUtcNow();
        var today = ClinicTime.Today(clock);

        // Validate everything before creating a patient record, so a rejected request leaves nothing behind.
        if (request.Emergency is { Reason: null })
            return new Validator().Add("emergency.reason", "Choose a reason for emergency priority.").ToResult();
        var emergencyNote = LimitNote(request.Emergency?.Note);

        Patient patient;
        if (request.PatientId is { } existingId)
        {
            patient = await db.Patients.AsNoTracking().FirstOrDefaultAsync(p => p.Id == existingId && p.ArchivedAt == null, ct)
                      ?? throw ApiException.NotFound("Patient not found.");
        }
        else
        {
            var v = new Validator().Required("fullName", request.FullName, 120);
            string? mobile = null;
            if (!string.IsNullOrWhiteSpace(request.MobileNumber))
            {
                if (MobileNumber.TryNormalize(request.MobileNumber, out var normalized)) mobile = normalized;
                else v.Add("mobileNumber", "Enter a valid 10-digit mobile number or leave it empty.");
            }
            if (request.AgeYears is < 0 or > 120) v.Add("ageYears", "Enter an age between 0 and 120.");
            if (!v.IsValid) return v.ToResult();

            patient = new Patient
            {
                FullName = request.FullName!.Trim(),
                ContactMobile = mobile,
                DateOfBirth = request.AgeYears is { } age ? today.AddYears(-age) : null,
                Relationship = Relationship.Self,
                CreatedByUserId = actor.UserId,
                CreatedAt = now,
            };
            // Saved by the queue engine in the same transaction as the booking: if booking fails, no patient is created.
            db.Patients.Add(patient);
        }

        await engine.RunAsync(id, actor.UserId, async scope =>
        {
            var source = scope.Session.Date == today ? BookingSource.WalkIn : BookingSource.StaffBooked;
            await scope.BookAsync(patient, actor.UserId, source, ct, request.Emergency?.Reason, emergencyNote);
            return true;
        }, ct);
        return await View(id, actor.Doctor.Id, http, access, db, status, clock, ct);
    }

    /// <summary>Names linked to a mobile number, so staff can pick an existing patient (PRD FR-13). No other details are returned.</summary>
    private static async Task<IResult> Lookup(
        string? mobile, Guid? doctorId, HttpContext http, ClinicAccess access, AppDbContext db, TimeProvider clock, CancellationToken ct)
    {
        await access.ResolveAsync(http.User, doctorId, ct);
        if (!MobileNumber.TryNormalize(mobile, out var normalized))
            return new Validator().Add("mobile", "Enter a valid 10-digit mobile number.").ToResult();

        var userIds = await db.Users.Where(u => u.MobileNumber == normalized && u.IsActive).Select(u => u.Id).ToListAsync(ct);
        var matches = await db.Patients.AsNoTracking()
            .Where(p => p.ArchivedAt == null && ((p.UserId != null && userIds.Contains(p.UserId.Value)) ||
                                                 (p.UserId == null && p.ManagedByUserId != null && userIds.Contains(p.ManagedByUserId.Value)) ||
                                                 p.ContactMobile == normalized))
            .OrderBy(p => p.FullName).Take(20).ToListAsync(ct);
        var today = ClinicTime.Today(clock);
        return Results.Ok(matches.Select(p => new PatientMatch(p.Id, p.FullName, p.Relationship,
            p.DateOfBirth is { } dob ? today.Year - dob.Year - (today < dob.AddYears(today.Year - dob.Year) ? 1 : 0) : null)).ToList());
    }
}
