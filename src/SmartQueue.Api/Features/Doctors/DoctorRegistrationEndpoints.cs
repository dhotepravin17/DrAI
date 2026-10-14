using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using SmartQueue.Api.Common;
using SmartQueue.Api.Features.Analytics;
using SmartQueue.Api.Domain;
using SmartQueue.Api.Infrastructure;

namespace SmartQueue.Api.Features.Doctors;

/// <summary>FR-01: a signed-in user registers as a doctor and submits for verification.</summary>
public static class DoctorRegistrationEndpoints
{
    public sealed record SaveRegistrationRequest(
        string? FullName, string? Email, string? Specialty, string? Qualifications,
        string? MedicalRegistrationId, string? MedicalRegistrationAuthority, string? PrimaryCityCode,
        string? ClinicName, string? ClinicAddress, string? ClinicPublicPhone);

    public sealed record SubmitRequest(bool DeclarationAccepted, string? DeclarationVersion);

    public sealed record RegistrationResponse(
        Guid DoctorId, DoctorRegistrationStatus Status, string? DoctorCode,
        string FullName, string? Email, string Specialty, string? Qualifications,
        string MedicalRegistrationId, string MedicalRegistrationAuthority, string PrimaryCityCode,
        string? ClinicName, string? ClinicAddress, string? ClinicPublicPhone,
        string? SubmissionReference, DateTimeOffset? SubmittedAt,
        RejectionReasonCategory? RejectionReason, string? StatusNote, bool CanAcceptBookings);

    public static RouteGroupBuilder MapDoctorRegistrationEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/doctor-registration").WithTags("Doctor registration").RequireAuthorization();
        group.MapGet("/", Get);
        group.MapPut("/", Save);
        group.MapPost("/submit", Submit);
        return api;
    }

    private static async Task<IResult> Get(HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var doctor = await LoadOwn(db, http.User.GetUserId(), ct);
        return doctor is null ? Results.NotFound() : Results.Ok(ToResponse(doctor));
    }

    private static async Task<IResult> Save(
        SaveRegistrationRequest request, HttpContext http, AppDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var v = new Validator()
            .Required("fullName", request.FullName, 120)
            .Email("email", request.Email)
            .Optional("qualifications", request.Qualifications, 200)
            .Required("medicalRegistrationId", request.MedicalRegistrationId, 40)
            .Required("clinicName", request.ClinicName, 120)
            .Required("clinicAddress", request.ClinicAddress, 300)
            .Optional("clinicPublicPhone", request.ClinicPublicPhone, 20);
        if (!ReferenceData.Specialties.Contains(request.Specialty))
            v.Add("specialty", "Choose a specialty from the list.");
        if (!ReferenceData.MedicalAuthorities.Contains(request.MedicalRegistrationAuthority))
            v.Add("medicalRegistrationAuthority", "Choose the council that issued your registration.");
        var cityCode = request.PrimaryCityCode?.Trim().ToUpperInvariant();
        if (cityCode is null || !await db.CityCodes.AnyAsync(c => c.Code == cityCode && c.IsActive, ct))
            v.Add("primaryCityCode", "Choose a city from the list.");
        if (!v.IsValid) return v.ToResult();

        var userId = http.User.GetUserId();
        var now = clock.GetUtcNow();
        var doctor = await LoadOwn(db, userId, ct);

        if (doctor is null)
        {
            doctor = new Doctor
            {
                UserId = userId,
                FullName = "",
                Specialty = "",
                MedicalRegistrationId = "",
                MedicalRegistrationAuthority = "",
                PrimaryCityCode = cityCode!,
                CreatedAt = now,
            };
            db.Doctors.Add(doctor);
            db.UserRoleAssignments.Add(new UserRoleAssignment
            {
                UserId = userId, Role = RoleName.Doctor, DoctorId = doctor.Id, GrantedAt = now, GrantedByUserId = userId,
            });
        }
        else if (!DoctorRegistrationStateMachine.IsEditableByDoctor(doctor.Status))
        {
            throw ApiException.Conflict("This registration can no longer be edited.",
                "Registrations can be edited only while in Draft or after rejection.");
        }

        doctor.FullName = request.FullName!.Trim();
        doctor.Email = request.Email.TrimToNull();
        doctor.Specialty = request.Specialty!;
        doctor.Qualifications = request.Qualifications.TrimToNull();
        doctor.MedicalRegistrationId = request.MedicalRegistrationId!.Trim().ToUpperInvariant();
        doctor.MedicalRegistrationAuthority = request.MedicalRegistrationAuthority!;
        doctor.PrimaryCityCode = cityCode!;
        doctor.UpdatedAt = now;

        var primary = doctor.ClinicAssignments.FirstOrDefault(a => a.IsPrimary);
        if (primary is null)
        {
            primary = new DoctorClinicAssignment
            {
                DoctorId = doctor.Id,
                IsPrimary = true,
                Clinic = new Clinic { Name = "", AddressLine = "", CityCode = cityCode!, CreatedAt = now },
            };
            doctor.ClinicAssignments.Add(primary);
        }
        primary.Clinic.Name = request.ClinicName!.Trim();
        primary.Clinic.AddressLine = request.ClinicAddress!.Trim();
        primary.Clinic.CityCode = cityCode!;
        primary.Clinic.PublicPhone = request.ClinicPublicPhone.TrimToNull();

        await db.SaveChangesAsync(ct);
        return Results.Ok(ToResponse(doctor));
    }

    private static async Task<IResult> Submit(
        SubmitRequest request, HttpContext http, AppDbContext db, ProductAnalytics analytics, TimeProvider clock, CancellationToken ct)
    {
        if (!request.DeclarationAccepted)
            return new Validator().Add("declarationAccepted", "You must accept the declaration to submit.").ToResult();
        if (request.DeclarationVersion != LegalTexts.DoctorDeclarationVersion)
            return new Validator().Add("declarationVersion", "The declaration has changed. Review and accept the current version.").ToResult();

        var doctor = await LoadOwn(db, http.User.GetUserId(), ct) ?? throw ApiException.NotFound("Save your registration details first.");
        if (!DoctorRegistrationStateMachine.CanTransition(doctor.Status, DoctorRegistrationStatus.PendingVerification))
            throw ApiException.Conflict($"A registration in status {doctor.Status} cannot be submitted.");

        var now = clock.GetUtcNow();
        var reference = $"REG-{now:yyyyMMdd}-{Convert.ToHexString(RandomNumberGenerator.GetBytes(3))}";

        doctor.PossibleDuplicate = await db.Doctors.AnyAsync(d =>
            d.Id != doctor.Id &&
            d.MedicalRegistrationAuthority == doctor.MedicalRegistrationAuthority &&
            d.MedicalRegistrationId == doctor.MedicalRegistrationId &&
            d.Status != DoctorRegistrationStatus.Rejected &&
            d.Status != DoctorRegistrationStatus.Deactivated &&
            d.Status != DoctorRegistrationStatus.Draft, ct);
        doctor.Status = DoctorRegistrationStatus.PendingVerification;
        doctor.SubmissionReference = reference;
        doctor.SubmittedAt = now;
        doctor.RejectionReason = null;
        doctor.StatusNote = null;
        doctor.UpdatedAt = now;
        doctor.Declarations.Add(new DoctorDeclaration
        {
            DoctorId = doctor.Id,
            DeclarationVersion = LegalTexts.DoctorDeclarationVersion,
            Accepted = true,
            AcceptedAt = now,
            SubmissionReference = reference,
            IpAddress = http.ClientIp(),
            UserAgent = http.Request.Headers.UserAgent.ToString() is { Length: > 0 } ua ? ua[..Math.Min(ua.Length, 400)] : null,
        });
        analytics.Track(ProductEventNames.DoctorRegistrationSubmitted, new { doctorId = doctor.Id, city = doctor.PrimaryCityCode }, doctor.PrimaryCityCode);

        await db.SaveChangesAsync(ct);
        return Results.Ok(ToResponse(doctor));
    }

    private static Task<Doctor?> LoadOwn(AppDbContext db, Guid userId, CancellationToken ct) =>
        db.Doctors.Include(d => d.ClinicAssignments).ThenInclude(a => a.Clinic)
            .FirstOrDefaultAsync(d => d.UserId == userId, ct);

    private static RegistrationResponse ToResponse(Doctor d)
    {
        var clinic = d.ClinicAssignments.FirstOrDefault(a => a.IsPrimary)?.Clinic;
        return new RegistrationResponse(
            d.Id, d.Status, d.DoctorCode, d.FullName, d.Email, d.Specialty, d.Qualifications,
            d.MedicalRegistrationId, d.MedicalRegistrationAuthority, d.PrimaryCityCode,
            clinic?.Name, clinic?.AddressLine, clinic?.PublicPhone,
            d.SubmissionReference, d.SubmittedAt, d.RejectionReason, d.StatusNote, d.CanAcceptBookings);
    }
}
