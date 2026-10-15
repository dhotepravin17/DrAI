using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SmartQueue.Api.Common;
using SmartQueue.Api.Domain;
using SmartQueue.Api.Features.Family;
using SmartQueue.Api.Features.Queue;
using SmartQueue.Api.Features.Scheduling;
using SmartQueue.Api.Infrastructure;

namespace SmartQueue.Api.Features.Booking;

/// <summary>FR-05 booking for self or family, FR-07 availability, FR-09 tokens, FR-10 live status.</summary>
public static class BookingEndpoints
{
    public sealed record SessionAvailability(
        string Name, DateTimeOffset StartsAt, DateTimeOffset EndsAt, string State, int Capacity, int Booked, int Remaining,
        bool Bookable, string? Reason, string? NowServing, int? Waiting);
    public sealed record DayAvailability(DateOnly Date, IReadOnlyList<SessionAvailability> Sessions);
    public sealed record AvailabilityResponse(string DoctorCode, bool AcceptingBookings, IReadOnlyList<DayAvailability> Days, IReadOnlyList<NoticeDto> Notices);
    public sealed record BookRequest(string? DoctorCode, DateOnly? Date, string? SessionName, Guid? PatientId);

    public static RouteGroupBuilder MapBookingEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/doctors/{doctorCode}/availability", Availability).WithTags("Booking");

        var group = api.MapGroup("/appointments").WithTags("Booking").RequireAuthorization();
        group.MapPost("/", Book);
        group.MapGet("/", Mine);
        group.MapGet("/{id:guid}", Get);
        group.MapPost("/{id:guid}/cancel", Cancel);
        return api;
    }

    private static async Task<IResult> Availability(
        string doctorCode, AppDbContext db, SessionPlanner planner, TimeProvider clock, QueueStatusService status, CancellationToken ct)
    {
        if (!DoctorCodeFormat.LooksLikeDoctorCode(doctorCode)) return Results.NotFound();
        var doctor = await db.Doctors.AsNoTracking()
            .FirstOrDefaultAsync(d => d.DoctorCode == doctorCode && d.Status == DoctorRegistrationStatus.Verified, ct);
        if (doctor is null) return Results.NotFound();

        var clinicId = await planner.PrimaryClinicIdAsync(doctor.Id, ct);
        var now = clock.GetUtcNow();
        var today = ClinicTime.Today(clock);
        var last = today.AddDays(doctor.BookingWindowDays);
        var plans = await planner.PlanAsync(doctor.Id, clinicId, today, last, ct);

        var sessions = await db.QueueSessions.AsNoTracking()
            .Where(s => s.DoctorId == doctor.Id && s.ClinicId == clinicId && s.Date >= today && s.Date <= last).ToListAsync(ct);
        var sessionIds = sessions.Select(s => s.Id).ToList();
        var appointments = await db.Appointments.AsNoTracking()
            .Where(a => sessionIds.Contains(a.QueueSessionId) && a.Status != AppointmentStatus.Cancelled)
            .Select(a => new { a.QueueSessionId, a.Status, a.TokenNumber })
            .ToListAsync(ct);

        var days = plans.GroupBy(p => p.Date).Select(day => new DayAvailability(day.Key, day.Select(plan =>
        {
            var session = sessions.FirstOrDefault(s => s.Date == plan.Date && s.Name == plan.Name);
            var mine = appointments.Where(a => session is not null && a.QueueSessionId == session.Id).ToList();
            var booked = mine.Count;
            var state = plan.Closed || session?.State == SessionState.Cancelled ? "Closed" : (session?.State ?? SessionState.Scheduled).ToString();
            string? reason = null;
            if (plan.Closed || session?.State == SessionState.Cancelled) reason = plan.ClosedReason ?? "Clinic closed";
            else if (session?.State == SessionState.Closed) reason = "Session finished";
            else if (session?.State == SessionState.Paused) reason = "Appointments paused";
            else if (now >= plan.EndsAt) reason = "Session ended";
            else if (booked >= plan.Capacity) reason = "Fully booked";

            var served = mine.FirstOrDefault(a => a.Status is AppointmentStatus.Called or AppointmentStatus.InConsultation);
            var isToday = plan.Date == today && session is not null;
            return new SessionAvailability(plan.Name, plan.StartsAt, plan.EndsAt, state, session?.Capacity ?? plan.Capacity, booked,
                Math.Max(0, (session?.Capacity ?? plan.Capacity) - booked), reason is null && doctor.CanAcceptBookings, reason,
                isToday && served is not null ? $"T-{served.TokenNumber:D3}" : null,
                isToday ? mine.Count(a => a.Status is AppointmentStatus.Booked or AppointmentStatus.CheckedIn or AppointmentStatus.Requeued) : null);
        }).ToList())).ToList();

        var notices = await status.NoticesAsync(doctor.Id, now, ClinicTime.At(last, TimeOnly.MaxValue), ct);
        return Results.Ok(new AvailabilityResponse(doctor.DoctorCode!, doctor.CanAcceptBookings, days, notices));
    }

    private static async Task<IResult> Book(
        BookRequest request, HttpContext http, AppDbContext db, PatientAccess access, SessionPlanner planner, QueueEngine engine,
        QueueStatusService status, TimeProvider clock, CancellationToken ct)
    {
        var key = http.Request.Headers["Idempotency-Key"].ToString().Trim();
        if (key.Length is 0 or > 100)
            return new Validator().Add("idempotencyKey", "Send a unique Idempotency-Key header (up to 100 characters) with each booking.").ToResult();

        var userId = http.User.GetUserId();
        var previous = await db.IdempotencyRecords.AsNoTracking().FirstOrDefaultAsync(r => r.UserId == userId && r.Key == key, ct);
        if (previous is not null) return Results.Ok(await StatusOf(db, status, previous.AppointmentId, ct));

        var v = new Validator();
        if (string.IsNullOrWhiteSpace(request.DoctorCode)) v.Add("doctorCode", "Choose a doctor.");
        if (request.Date is null) v.Add("date", "Choose a date.");
        if (string.IsNullOrWhiteSpace(request.SessionName)) v.Add("sessionName", "Choose a session.");
        if (request.PatientId is null) v.Add("patientId", "Choose who this appointment is for.");
        if (!v.IsValid) return v.ToResult();

        var patient = await access.RequireBookableAsync(userId, request.PatientId!.Value, ct);
        var doctor = await db.Doctors.AsNoTracking().FirstOrDefaultAsync(d => d.DoctorCode == request.DoctorCode, ct)
                     ?? throw ApiException.NotFound("Doctor not found.");
        if (!doctor.CanAcceptBookings) throw ApiException.Conflict("This doctor is not accepting bookings.");

        var today = ClinicTime.Today(clock);
        var date = request.Date!.Value;
        if (date < today || date > today.AddDays(doctor.BookingWindowDays))
            return new Validator().Add("date", $"Bookings open up to {doctor.BookingWindowDays} days ahead.").ToResult();

        var clinicId = await planner.PrimaryClinicIdAsync(doctor.Id, ct);
        var plan = await planner.FindPlannedAsync(doctor.Id, clinicId, date, request.SessionName!, ct)
                   ?? throw ApiException.NotFound("The doctor has no such session on that date.");
        if (plan.Closed) throw ApiException.Conflict("The clinic is closed for this session.", plan.ClosedReason);
        var session = await planner.EnsureSessionAsync(doctor.Id, clinicId, plan, ct);

        Guid appointmentId;
        try
        {
            appointmentId = await engine.RunAsync(session.Id, userId, async scope =>
            {
                var again = await db.IdempotencyRecords.FirstOrDefaultAsync(r => r.UserId == userId && r.Key == key, ct);
                if (again is not null) return again.AppointmentId;
                var appointment = await scope.BookAsync(patient, userId, BookingSource.Online, ct);
                db.IdempotencyRecords.Add(new IdempotencyRecord { UserId = userId, Key = key, AppointmentId = appointment.Id, CreatedAt = scope.Now });
                return appointment.Id;
            }, ct);
        }
        catch (DbUpdateException e) when (e.InnerException is SqlException { Number: 2601 or 2627 })
        {
            var record = await db.IdempotencyRecords.AsNoTracking().FirstOrDefaultAsync(r => r.UserId == userId && r.Key == key, ct);
            if (record is null) throw ApiException.Conflict("This person already has an active appointment in this session.");
            appointmentId = record.AppointmentId;
        }

        return Results.Created($"/api/v1/appointments/{appointmentId}", await StatusOf(db, status, appointmentId, ct));
    }

    private static async Task<IResult> Mine(
        HttpContext http, PatientAccess access, QueueStatusService status, string? view, CancellationToken ct)
    {
        var visible = await access.VisibleAppointmentsAsync(http.User.GetUserId(), ct);
        if (view != "all")
            visible = visible.Where(a => a.Status == AppointmentStatus.Booked || a.Status == AppointmentStatus.CheckedIn ||
                                         a.Status == AppointmentStatus.Called || a.Status == AppointmentStatus.InConsultation ||
                                         a.Status == AppointmentStatus.Missed || a.Status == AppointmentStatus.Requeued);
        var list = await visible.OrderBy(a => a.Date).ThenBy(a => a.TokenNumber).Take(50).ToListAsync(ct);
        return Results.Ok(await status.BuildAsync(list, ct));
    }

    private static async Task<IResult> Get(Guid id, HttpContext http, PatientAccess access, QueueStatusService status, CancellationToken ct)
    {
        var appointment = await (await access.VisibleAppointmentsAsync(http.User.GetUserId(), ct)).FirstOrDefaultAsync(a => a.Id == id, ct);
        return appointment is null ? Results.NotFound() : Results.Ok((await status.BuildAsync([appointment], ct))[0]);
    }

    private static async Task<IResult> Cancel(
        Guid id, HttpContext http, AppDbContext db, PatientAccess access, QueueEngine engine, QueueStatusService status, CancellationToken ct)
    {
        var userId = http.User.GetUserId();
        var appointment = await (await access.VisibleAppointmentsAsync(userId, ct)).FirstOrDefaultAsync(a => a.Id == id, ct);
        if (appointment is null) return Results.NotFound();
        var patient = await db.Patients.AsNoTracking().SingleAsync(p => p.Id == appointment.PatientId, ct);
        if (patient.UserId != userId && patient.ManagedByUserId != userId && appointment.BookedByUserId != userId)
            throw new ApiException(StatusCodes.Status403Forbidden, "Only the patient or the person who booked can cancel this appointment.");

        await engine.RunAsync(appointment.QueueSessionId, userId, async scope =>
        {
            await scope.CancelAsync(scope.Get(id), CancellationReason.Patient, "Cancelled by patient", ct);
            return true;
        }, ct);
        return Results.Ok(await StatusOf(db, status, id, ct));
    }

    private static async Task<LiveStatus> StatusOf(AppDbContext db, QueueStatusService status, Guid appointmentId, CancellationToken ct)
    {
        var appointment = await db.Appointments.AsNoTracking().SingleAsync(a => a.Id == appointmentId, ct);
        return (await status.BuildAsync([appointment], ct))[0];
    }
}
