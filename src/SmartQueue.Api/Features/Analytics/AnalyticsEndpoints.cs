using Microsoft.EntityFrameworkCore;
using SmartQueue.Api.Common;
using SmartQueue.Api.Domain;
using SmartQueue.Api.Features.Admin;
using SmartQueue.Api.Features.Plans;
using SmartQueue.Api.Features.Queue;
using SmartQueue.Api.Infrastructure;

namespace SmartQueue.Api.Features.Analytics;

/// <summary>Platform metrics for the Super Admin (PRD §4, §38), plan changes, and clinic reports for Pro plans (§39).</summary>
public static class AnalyticsEndpoints
{
    public const int MaxRangeDays = 366;

    public sealed record EventCount(string Name, string? Dimension, int Count, double? Average);
    public sealed record PlatformMetrics(
        int Bookings, double? OnlineShare, double? ForSelfShare, double? CancellationRate, double? NoShowRate,
        double? AverageConsultMinutes, double? AverageWaitMinutes, double? NotificationFailureRate,
        double? AverageReviewHours, int PatientsRegistered, int FamilyMembersAdded, int Searches, double? ZeroResultSearchShare);
    public sealed record PlatformAnalytics(DateOnly From, DateOnly To, PlatformMetrics Metrics, IReadOnlyList<EventCount> Events);
    public sealed record SetPlanRequest(DoctorPlan? Plan, string? Note);
    public sealed record ClinicReport(
        DateOnly From, DateOnly To, int Appointments, int Completed, int Cancelled, int NoShows, int Missed, int Emergencies,
        IReadOnlyDictionary<string, int> BySource, double? AverageConsultMinutes, double? AverageWaitMinutes,
        IReadOnlyList<DailyRow> Daily);
    public sealed record DailyRow(DateOnly Date, int Appointments, int Completed, int NoShows);

    public static RouteGroupBuilder MapAnalyticsEndpoints(this RouteGroupBuilder api)
    {
        var admin = api.MapGroup("/admin").WithTags("Super Admin").RequireAuthorization(AuthPolicies.SuperAdmin);
        admin.MapGet("/analytics", Platform);
        admin.MapPost("/doctors/{id:guid}/plan", SetPlan);

        api.MapGet("/clinic/reports", Clinic).WithTags("Clinic").RequireAuthorization();
        return api;
    }

    private static (DateOnly From, DateOnly To) Range(DateOnly? from, DateOnly? to, TimeProvider clock, int defaultDays)
    {
        var end = to ?? ClinicTime.Today(clock);
        var start = from ?? end.AddDays(-(defaultDays - 1));
        if (start > end) throw ApiException.BadRequest("The start date must be on or before the end date.");
        if (end.DayNumber - start.DayNumber >= MaxRangeDays) throw ApiException.BadRequest($"Choose a range of at most {MaxRangeDays} days.");
        return (start, end);
    }

    private static async Task<IResult> Platform(DateOnly? from, DateOnly? to, AppDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var (start, end) = Range(from, to, clock, 30);
        var startsAt = ClinicTime.At(start, TimeOnly.MinValue);
        var endsAt = ClinicTime.At(end.AddDays(1), TimeOnly.MinValue);

        var events = await db.ProductEvents.AsNoTracking()
            .Where(e => e.OccurredAt >= startsAt && e.OccurredAt < endsAt)
            .GroupBy(e => new { e.Name, e.Dimension })
            .Select(g => new EventCount(g.Key.Name, g.Key.Dimension, g.Count(), g.Average(e => e.Value)))
            .ToListAsync(ct);
        events = events.OrderBy(e => e.Name).ThenBy(e => e.Dimension).ToList();

        int Count(string name, string? dimension = null) =>
            events.Where(e => e.Name == name && (dimension is null || e.Dimension == dimension)).Sum(e => e.Count);
        double? Average(string name)
        {
            var rows = events.Where(e => e.Name == name && e.Average is not null).ToList();
            var total = rows.Sum(r => r.Count);
            return total == 0 ? null : Math.Round(rows.Sum(r => r.Average!.Value * r.Count) / total, 1);
        }
        static double? Ratio(int part, int whole) => whole == 0 ? null : Math.Round((double)part / whole, 3);

        var bookings = Count(ProductEventNames.AppointmentBooked);
        var forSelf = await db.ProductEvents.AsNoTracking().CountAsync(e =>
            e.Name == ProductEventNames.AppointmentBooked && e.OccurredAt >= startsAt && e.OccurredAt < endsAt &&
            e.PropertiesJson.Contains("\"forSelf\":true"), ct);
        var sent = Count(ProductEventNames.NotificationSent);
        var failed = Count(ProductEventNames.NotificationFailed);
        var searches = Count(ProductEventNames.DoctorSearched);
        var zeroResults = await db.ProductEvents.AsNoTracking().CountAsync(e =>
            e.Name == ProductEventNames.DoctorSearched && e.OccurredAt >= startsAt && e.OccurredAt < endsAt && e.Value == 0, ct);

        var metrics = new PlatformMetrics(
            bookings,
            Ratio(Count(ProductEventNames.AppointmentBooked, nameof(BookingSource.Online)), bookings),
            Ratio(forSelf, bookings),
            Ratio(Count(ProductEventNames.AppointmentCancelled), bookings),
            Ratio(Count(ProductEventNames.NoShow), bookings),
            Average(ProductEventNames.ConsultationCompleted),
            Average(ProductEventNames.PatientCalled),
            Ratio(failed, sent + failed),
            Average(ProductEventNames.DoctorVerified),
            Count(ProductEventNames.PatientRegistered),
            Count(ProductEventNames.FamilyMemberAdded),
            searches,
            Ratio(zeroResults, searches));
        return Results.Ok(new PlatformAnalytics(start, end, metrics, events));
    }

    private static async Task<IResult> SetPlan(
        Guid id, SetPlanRequest request, AppDbContext db, AuditLogger audit, TimeProvider clock, CancellationToken ct)
    {
        var v = new Validator().Optional("note", request.Note, 500);
        if (request.Plan is null) v.Add("plan", "Choose a plan.");
        if (!v.IsValid) return v.ToResult();

        var doctor = await db.Doctors.FirstOrDefaultAsync(d => d.Id == id, ct);
        if (doctor is null) return Results.NotFound();
        var previous = doctor.Plan;
        if (previous == request.Plan) return Results.Ok(new { doctor.Id, doctor.Plan });

        doctor.Plan = request.Plan!.Value;
        doctor.UpdatedAt = clock.GetUtcNow();
        audit.Record("ChangeDoctorPlan", "Doctor", doctor.Id.ToString(), details: $"{previous} -> {doctor.Plan}: {request.Note.TrimToNull()}");
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { doctor.Id, doctor.Plan });
    }

    private static async Task<IResult> Clinic(
        Guid? doctorId, DateOnly? from, DateOnly? to, HttpContext http, ClinicAccess access, PlanService plans,
        AppDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var actor = await access.ResolveAsync(http.User, doctorId, ct);
        plans.EnsureReports(actor.Doctor);
        var (start, end) = Range(from, to, clock, 30);

        var rows = await db.Appointments.AsNoTracking()
            .Where(a => a.DoctorId == actor.Doctor.Id && a.Date >= start && a.Date <= end)
            .Select(a => new
            {
                a.Date, a.Status, a.Source, a.Priority, a.MissCount, a.CreatedAt, a.CheckedInAt, a.CalledAt,
                a.ConsultStartedAt, a.CompletedAt,
            })
            .ToListAsync(ct);

        var consults = rows.Where(r => r.CompletedAt is not null && (r.ConsultStartedAt ?? r.CalledAt) is not null)
            .Select(r => (r.CompletedAt!.Value - (r.ConsultStartedAt ?? r.CalledAt)!.Value).TotalMinutes).ToList();
        var waits = rows.Where(r => r.CalledAt is not null)
            .Select(r => (r.CalledAt!.Value - (r.CheckedInAt ?? r.CreatedAt)).TotalMinutes).Where(m => m >= 0).ToList();

        return Results.Ok(new ClinicReport(
            start, end,
            rows.Count(r => r.Status != AppointmentStatus.Cancelled),
            rows.Count(r => r.Status == AppointmentStatus.Completed),
            rows.Count(r => r.Status == AppointmentStatus.Cancelled),
            rows.Count(r => r.Status == AppointmentStatus.NoShow),
            rows.Count(r => r.MissCount > 0),
            rows.Count(r => r.Priority == QueuePriority.Emergency),
            rows.Where(r => r.Status != AppointmentStatus.Cancelled).GroupBy(r => r.Source.ToString()).ToDictionary(g => g.Key, g => g.Count()),
            consults.Count == 0 ? null : Math.Round(consults.Average(), 1),
            waits.Count == 0 ? null : Math.Round(waits.Average(), 1),
            rows.GroupBy(r => r.Date).OrderBy(g => g.Key).Select(g => new DailyRow(
                g.Key,
                g.Count(r => r.Status != AppointmentStatus.Cancelled),
                g.Count(r => r.Status == AppointmentStatus.Completed),
                g.Count(r => r.Status == AppointmentStatus.NoShow))).ToList()));
    }
}
