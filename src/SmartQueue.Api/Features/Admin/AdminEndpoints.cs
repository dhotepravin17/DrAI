using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SmartQueue.Api.Common;
using SmartQueue.Api.Domain;
using SmartQueue.Api.Features.Analytics;
using SmartQueue.Api.Features.Notifications;
using SmartQueue.Api.Infrastructure;

namespace SmartQueue.Api.Features.Admin;

/// <summary>FR-19 (Phase 1 slice): doctor verification, account status, city registry, and audit log review.</summary>
public static partial class AdminEndpoints
{
    public const int PageSize = 25;

    public sealed record DoctorRow(
        Guid DoctorId, string FullName, string Specialty, string PrimaryCityCode, DoctorRegistrationStatus Status,
        string? DoctorCode, DateTimeOffset? SubmittedAt, bool PossibleDuplicate);
    public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int Total);
    public sealed record DeclarationDto(string Version, bool Accepted, DateTimeOffset AcceptedAt, string SubmissionReference, string? IpAddress);
    public sealed record RelatedDoctor(Guid DoctorId, string FullName, DoctorRegistrationStatus Status, string? DoctorCode);
    public sealed record DoctorDetail(
        Guid DoctorId, Guid UserId, string FullName, string? Email, string MobileNumberMasked,
        string Specialty, string? Qualifications, string MedicalRegistrationId, string MedicalRegistrationAuthority,
        string PrimaryCityCode, DoctorRegistrationStatus Status, string? DoctorCode,
        string? ClinicName, string? ClinicAddress, string? ClinicPublicPhone,
        string? SubmissionReference, DateTimeOffset? SubmittedAt, DateTimeOffset? VerifiedAt, string? VerificationReference,
        RejectionReasonCategory? RejectionReason, string? StatusNote, bool PossibleDuplicate,
        IReadOnlyList<DeclarationDto> Declarations, IReadOnlyList<RelatedDoctor> SameRegistrationNumber, byte[] RowVersion, DoctorPlan Plan);
    public sealed record VerifyRequest(string? VerificationReference, string? Note, byte[]? RowVersion);
    public sealed record RejectRequest(RejectionReasonCategory? Reason, string? Note, byte[]? RowVersion);
    public sealed record StatusNoteRequest(string? Note, byte[]? RowVersion);
    public sealed record CityRow(string Code, string Name, string State, bool IsActive, int IssuedDoctorIds);
    public sealed record CreateCityRequest(string? Code, string? Name, string? State);
    public sealed record AuditRow(
        long Id, Guid? ActorUserId, string Action, string EntityType, string EntityId,
        string? Purpose, string? Details, DateTimeOffset OccurredAt);

    public static RouteGroupBuilder MapAdminEndpoints(this RouteGroupBuilder api)
    {
        var admin = api.MapGroup("/admin").WithTags("Super Admin").RequireAuthorization(AuthPolicies.SuperAdmin);

        admin.MapGet("/doctors", ListDoctors);
        admin.MapGet("/doctors/{id:guid}", GetDoctor);
        admin.MapPost("/doctors/{id:guid}/verify", Verify);
        admin.MapPost("/doctors/{id:guid}/reject", Reject);
        admin.MapPost("/doctors/{id:guid}/suspend", (Guid id, StatusNoteRequest r, AppDbContext db, AuditLogger audit, TimeProvider clock, CancellationToken ct) =>
            ChangeStatus(id, DoctorRegistrationStatus.Suspended, r, noteRequired: true, db, audit, clock, ct));
        admin.MapPost("/doctors/{id:guid}/reinstate", (Guid id, StatusNoteRequest r, AppDbContext db, AuditLogger audit, TimeProvider clock, CancellationToken ct) =>
            ChangeStatus(id, DoctorRegistrationStatus.Verified, r, noteRequired: false, db, audit, clock, ct));
        admin.MapPost("/doctors/{id:guid}/deactivate", (Guid id, StatusNoteRequest r, AppDbContext db, AuditLogger audit, TimeProvider clock, CancellationToken ct) =>
            ChangeStatus(id, DoctorRegistrationStatus.Deactivated, r, noteRequired: true, db, audit, clock, ct));

        admin.MapGet("/cities", ListCities);
        admin.MapPost("/cities", CreateCity);
        admin.MapGet("/audit-logs", ListAuditLogs);
        return api;
    }

    private static async Task<IResult> ListDoctors(
        AppDbContext db, DoctorRegistrationStatus? status, string? q, int? page, CancellationToken ct)
    {
        var pageNumber = Math.Max(1, page ?? 1);
        var query = db.Doctors.AsNoTracking().AsQueryable();
        if (status is not null) query = query.Where(d => d.Status == status);
        else query = query.Where(d => d.Status != DoctorRegistrationStatus.Draft);

        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim();
            query = query.Where(d => d.FullName.Contains(term) || d.DoctorCode == term ||
                                     d.MedicalRegistrationId == term || d.Specialty.Contains(term) || d.PrimaryCityCode == term);
        }

        query = status == DoctorRegistrationStatus.PendingVerification
            ? query.OrderBy(d => d.SubmittedAt)
            : query.OrderByDescending(d => d.UpdatedAt);

        var total = await query.CountAsync(ct);
        var items = await query.Skip((pageNumber - 1) * PageSize).Take(PageSize)
            .Select(d => new DoctorRow(d.Id, d.FullName, d.Specialty, d.PrimaryCityCode, d.Status, d.DoctorCode, d.SubmittedAt, d.PossibleDuplicate))
            .ToListAsync(ct);
        return Results.Ok(new PagedResult<DoctorRow>(items, pageNumber, PageSize, total));
    }

    private static async Task<IResult> GetDoctor(Guid id, AppDbContext db, AuditLogger audit, CancellationToken ct)
    {
        var d = await db.Doctors.AsNoTracking()
            .Include(x => x.Declarations)
            .Include(x => x.ClinicAssignments).ThenInclude(a => a.Clinic)
            .FirstOrDefaultAsync(x => x.Id == id, ct);
        if (d is null) return Results.NotFound();

        var mobile = await db.Users.Where(u => u.Id == d.UserId).Select(u => u.MobileNumber).FirstAsync(ct);
        var related = await db.Doctors.AsNoTracking()
            .Where(x => x.Id != d.Id && x.MedicalRegistrationAuthority == d.MedicalRegistrationAuthority &&
                        x.MedicalRegistrationId == d.MedicalRegistrationId && x.Status != DoctorRegistrationStatus.Draft)
            .Select(x => new RelatedDoctor(x.Id, x.FullName, x.Status, x.DoctorCode))
            .ToListAsync(ct);

        audit.Record("ViewDoctorRegistration", "Doctor", d.Id.ToString(), purpose: "Verification review");
        await db.SaveChangesAsync(ct);

        var clinic = d.ClinicAssignments.FirstOrDefault(a => a.IsPrimary)?.Clinic;
        return Results.Ok(new DoctorDetail(
            d.Id, d.UserId, d.FullName, d.Email, MobileNumber.Mask(mobile),
            d.Specialty, d.Qualifications, d.MedicalRegistrationId, d.MedicalRegistrationAuthority,
            d.PrimaryCityCode, d.Status, d.DoctorCode, clinic?.Name, clinic?.AddressLine, clinic?.PublicPhone,
            d.SubmissionReference, d.SubmittedAt, d.VerifiedAt, d.VerificationReference, d.RejectionReason, d.StatusNote,
            d.PossibleDuplicate,
            d.Declarations.OrderByDescending(x => x.AcceptedAt)
                .Select(x => new DeclarationDto(x.DeclarationVersion, x.Accepted, x.AcceptedAt, x.SubmissionReference, x.IpAddress)).ToList(),
            related, d.RowVersion, d.Plan));
    }

    private static async Task<IResult> Verify(
        Guid id, VerifyRequest request, HttpContext http, AppDbContext db, AuditLogger audit, NotificationOutbox outbox, ProductAnalytics analytics,
        DoctorCodeGenerator codes, TimeProvider clock, CancellationToken ct)
    {
        var v = new Validator().Optional("verificationReference", request.VerificationReference, 100).Optional("note", request.Note, 500);
        if (!v.IsValid) return v.ToResult();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var doctor = await LoadForUpdate(db, id, request.RowVersion, ct);
        EnsureTransition(doctor, DoctorRegistrationStatus.Verified);
        if (doctor.Status != DoctorRegistrationStatus.PendingVerification)
            throw ApiException.Conflict("Only pending registrations can be verified. Use reinstate for suspended doctors.");

        var now = clock.GetUtcNow();
        var firstVerification = doctor.DoctorCode is null;
        doctor.DoctorCode ??= await codes.NextAsync(doctor.PrimaryCityCode, ct);
        doctor.Status = DoctorRegistrationStatus.Verified;
        doctor.VerifiedAt = now;
        doctor.VerifiedByUserId = http.User.GetUserId();
        doctor.VerificationReference = request.VerificationReference.TrimToNull();
        doctor.RejectionReason = null;
        doctor.StatusNote = request.Note.TrimToNull();
        doctor.UpdatedAt = now;

        audit.Record("VerifyDoctor", "Doctor", doctor.Id.ToString(),
            details: firstVerification ? $"Issued Doctor ID {doctor.DoctorCode}" : $"Re-verified {doctor.DoctorCode}");
        analytics.Track(ProductEventNames.DoctorVerified, new { doctorId = doctor.Id, reviewDurationHours = ReviewHours(doctor, now) }, value: ReviewHours(doctor, now));
        await outbox.EnqueueAsync(NotificationEvents.DoctorVerified, $"verified:{doctor.Id}", new NotificationPayload(
            [doctor.UserId], null, "Registration verified",
            $"Your registration is verified. Your Doctor ID is {doctor.DoctorCode}. Set up your clinic hours to start taking bookings.",
            null, "/clinic/schedule"), ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Results.Ok(new { doctor.Id, doctor.Status, doctor.DoctorCode });
    }

    private static async Task<IResult> Reject(
        Guid id, RejectRequest request, AppDbContext db, AuditLogger audit, NotificationOutbox outbox, ProductAnalytics analytics,
        TimeProvider clock, CancellationToken ct)
    {
        var v = new Validator().Optional("note", request.Note, 500);
        if (request.Reason is null) v.Add("reason", "Choose a rejection reason.");
        if (request.Reason == RejectionReasonCategory.Other && string.IsNullOrWhiteSpace(request.Note))
            v.Add("note", "Explain the reason when choosing Other.");
        if (!v.IsValid) return v.ToResult();

        var doctor = await LoadForUpdate(db, id, request.RowVersion, ct);
        EnsureTransition(doctor, DoctorRegistrationStatus.Rejected);
        doctor.Status = DoctorRegistrationStatus.Rejected;
        doctor.RejectionReason = request.Reason;
        doctor.StatusNote = request.Note.TrimToNull();
        doctor.UpdatedAt = clock.GetUtcNow();

        audit.Record("RejectDoctor", "Doctor", doctor.Id.ToString(), details: $"{request.Reason}: {doctor.StatusNote}");
        analytics.Track(ProductEventNames.DoctorRejected, new { doctorId = doctor.Id, reason = request.Reason.ToString(), reviewDurationHours = ReviewHours(doctor, doctor.UpdatedAt) },
            request.Reason.ToString(), ReviewHours(doctor, doctor.UpdatedAt));
        await outbox.EnqueueAsync(NotificationEvents.DoctorRejected, $"rejected:{doctor.Id}:{doctor.UpdatedAt.ToUnixTimeSeconds()}",
            new NotificationPayload([doctor.UserId], null, "Registration not verified",
                "We could not verify your registration. Open your registration to see the reason and resubmit.", null, "/doctor/register"), ct);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { doctor.Id, doctor.Status, doctor.DoctorCode });
    }

    private static async Task<IResult> ChangeStatus(
        Guid id, DoctorRegistrationStatus target, StatusNoteRequest request, bool noteRequired,
        AppDbContext db, AuditLogger audit, TimeProvider clock, CancellationToken ct)
    {
        var v = new Validator();
        if (noteRequired) v.Required("note", request.Note, 500); else v.Optional("note", request.Note, 500);
        if (!v.IsValid) return v.ToResult();

        var doctor = await LoadForUpdate(db, id, request.RowVersion, ct);
        EnsureTransition(doctor, target);
        if (target == DoctorRegistrationStatus.Verified && doctor.Status != DoctorRegistrationStatus.Suspended)
            throw ApiException.Conflict("Only suspended doctors can be reinstated.");

        var previous = doctor.Status;
        doctor.Status = target;
        doctor.StatusNote = request.Note.TrimToNull();
        doctor.UpdatedAt = clock.GetUtcNow();

        var action = target switch
        {
            DoctorRegistrationStatus.Suspended => "SuspendDoctor",
            DoctorRegistrationStatus.Deactivated => "DeactivateDoctor",
            _ => "ReinstateDoctor",
        };
        audit.Record(action, "Doctor", doctor.Id.ToString(), details: $"{previous} -> {target}: {doctor.StatusNote}");
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { doctor.Id, doctor.Status, doctor.DoctorCode });
    }

    private static async Task<IResult> ListCities(AppDbContext db, CancellationToken ct)
    {
        var rows = await (from c in db.CityCodes.AsNoTracking()
                          join s in db.DoctorCodeSequences on c.Code equals s.CityCode
                          orderby c.Name
                          select new CityRow(c.Code, c.Name, c.State, c.IsActive, s.LastValue)).ToListAsync(ct);
        return Results.Ok(rows);
    }

    private static async Task<IResult> CreateCity(CreateCityRequest request, AppDbContext db, AuditLogger audit, CancellationToken ct)
    {
        var code = request.Code?.Trim().ToUpperInvariant();
        var v = new Validator().Required("name", request.Name, 80).Required("state", request.State, 80);
        if (code is null || !CityCodePattern().IsMatch(code)) v.Add("code", "Use exactly three letters, for example NGP.");
        if (!v.IsValid) return v.ToResult();

        if (await db.CityCodes.AnyAsync(c => c.Code == code, ct))
            throw ApiException.Conflict($"City code {code} already exists.");
        var name = request.Name!.Trim();
        if (await db.CityCodes.AnyAsync(c => c.Name == name, ct))
            throw ApiException.Conflict($"A city named {name} already exists.");

        db.CityCodes.Add(new CityCode { Code = code!, Name = name, State = request.State!.Trim() });
        db.DoctorCodeSequences.Add(new DoctorCodeSequence { CityCode = code!, LastValue = 0 });
        audit.Record("CreateCity", "CityCode", code!, details: name);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/v1/admin/cities/{code}", new CityRow(code!, name, request.State!.Trim(), true, 0));
    }

    private static async Task<IResult> ListAuditLogs(
        AppDbContext db, string? entityType, string? entityId, int? page, CancellationToken ct)
    {
        var pageNumber = Math.Max(1, page ?? 1);
        var query = db.AdminAuditLogs.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(entityType)) query = query.Where(a => a.EntityType == entityType);
        if (!string.IsNullOrWhiteSpace(entityId)) query = query.Where(a => a.EntityId == entityId);

        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(a => a.Id).Skip((pageNumber - 1) * PageSize).Take(PageSize)
            .Select(a => new AuditRow(a.Id, a.ActorUserId, a.Action, a.EntityType, a.EntityId, a.Purpose, a.Details, a.OccurredAt))
            .ToListAsync(ct);
        return Results.Ok(new PagedResult<AuditRow>(items, pageNumber, PageSize, total));
    }

    private static double? ReviewHours(Doctor doctor, DateTimeOffset decidedAt) =>
        doctor.SubmittedAt is { } submitted ? Math.Round((decidedAt - submitted).TotalHours, 1) : null;

    private static async Task<Doctor> LoadForUpdate(AppDbContext db, Guid id, byte[]? rowVersion, CancellationToken ct)
    {
        var doctor = await db.Doctors.FirstOrDefaultAsync(d => d.Id == id, ct) ?? throw ApiException.NotFound("Doctor not found.");
        if (rowVersion is { Length: > 0 })
            db.Entry(doctor).Property(d => d.RowVersion).OriginalValue = rowVersion;
        return doctor;
    }

    private static void EnsureTransition(Doctor doctor, DoctorRegistrationStatus target)
    {
        if (!DoctorRegistrationStateMachine.CanTransition(doctor.Status, target))
            throw ApiException.Conflict($"Cannot change a doctor from {doctor.Status} to {target}.");
    }

    [GeneratedRegex("^[A-Z]{3}$")]
    private static partial Regex CityCodePattern();
}
