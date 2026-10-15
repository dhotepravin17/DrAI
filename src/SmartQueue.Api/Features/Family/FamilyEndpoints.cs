using Microsoft.EntityFrameworkCore;
using SmartQueue.Api.Common;
using SmartQueue.Api.Domain;
using SmartQueue.Api.Features.Notifications;
using SmartQueue.Api.Features.Analytics;
using SmartQueue.Api.Infrastructure;

namespace SmartQueue.Api.Features.Family;

/// <summary>FR-04 family profiles and adult account linking (PRD §12).</summary>
public static class FamilyEndpoints
{
    public const int MaxFamilyProfiles = 6;

    public sealed record PersonDto(
        Guid PatientId, string FullName, Relationship Relationship, PersonKind Kind, DateOnly? DateOfBirth, Gender? Gender,
        bool IsMinor, string HistoryAccess, Guid? LinkId);
    public sealed record FamilyRequest(string? FullName, Relationship? Relationship, DateOnly? DateOfBirth, Gender? Gender, bool GuardianConsent);
    public sealed record ProfileRequest(string? FullName, DateOnly? DateOfBirth, Gender? Gender, string? City, string? Email);
    public sealed record LinkRequest(string? MobileNumber, Relationship? Relationship);
    public sealed record LinkDto(
        Guid LinkId, InvitationStatus Status, Relationship Relationship, string OtherPartyName, string? InviteeMobileMasked,
        DateTimeOffset CreatedAt, DateTimeOffset? RespondedAt);
    public sealed record LinksResponse(IReadOnlyList<LinkDto> Sent, IReadOnlyList<LinkDto> Received, IReadOnlyList<LinkDto> SharingMyHistory);

    public static RouteGroupBuilder MapFamilyEndpoints(this RouteGroupBuilder api)
    {
        var me = api.MapGroup("/me").WithTags("Family").RequireAuthorization();
        me.MapGet("/people", People);
        me.MapPut("/profile", UpdateProfile);
        me.MapPost("/family", AddFamily);
        me.MapPut("/family/{id:guid}", UpdateFamily);
        me.MapDelete("/family/{id:guid}", ArchiveFamily);
        me.MapGet("/links", Links);
        me.MapPost("/links", Invite);
        me.MapPost("/links/{id:guid}/accept", Accept);
        me.MapPost("/links/{id:guid}/decline", Decline);
        me.MapDelete("/links/{id:guid}", Revoke);
        return api;
    }

    private static async Task<IResult> People(HttpContext http, PatientAccess access, TimeProvider clock, CancellationToken ct)
    {
        var today = ClinicTime.Today(clock);
        var people = await access.PeopleAsync(http.User.GetUserId(), ct);
        return Results.Ok(people.Select(p => ToDto(p, today)).ToList());
    }

    private static PersonDto ToDto(Person p, DateOnly today)
    {
        var minor = p.Patient.IsMinorOn(today);
        var access = p.Kind == PersonKind.FamilyProfile && !minor ? "BookedByMe" : "Full";
        return new PersonDto(p.Patient.Id, p.Patient.FullName, p.Relationship, p.Kind, p.Patient.DateOfBirth, p.Patient.Gender, minor, access, p.LinkId);
    }

    private static Validator ValidatePerson(string? fullName, DateOnly? dob, DateOnly today)
    {
        var v = new Validator().Required("fullName", fullName, 120);
        if (dob is { } d && (d > today || d < today.AddYears(-120))) v.Add("dateOfBirth", "Enter a valid date of birth.");
        return v;
    }

    private static async Task<IResult> UpdateProfile(
        ProfileRequest r, HttpContext http, AppDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var userId = http.User.GetUserId();
        var v = ValidatePerson(r.FullName, r.DateOfBirth, ClinicTime.Today(clock)).Optional("city", r.City, 80).Email("email", r.Email);
        if (!v.IsValid) return v.ToResult();

        var user = await db.Users.SingleAsync(u => u.Id == userId, ct);
        var self = await db.Patients.SingleAsync(p => p.UserId == userId, ct);
        user.FullName = self.FullName = r.FullName!.Trim();
        user.Email = r.Email.TrimToNull();
        self.DateOfBirth = r.DateOfBirth;
        self.Gender = r.Gender;
        self.City = r.City.TrimToNull();
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> AddFamily(
        FamilyRequest r, HttpContext http, AppDbContext db, PatientAccess access, ProductAnalytics analytics, TimeProvider clock,
        CancellationToken ct)
    {
        var userId = http.User.GetUserId();
        var today = ClinicTime.Today(clock);
        var v = ValidatePerson(r.FullName, r.DateOfBirth, today);
        if (r.Relationship is null or Relationship.Self) v.Add("relationship", "Choose how this person is related to you.");
        var minor = r.DateOfBirth is { } dob && dob.AddYears(18) > today;
        if (minor && !r.GuardianConsent) v.Add("guardianConsent", "Confirm you are this child's parent or guardian.");
        if (!v.IsValid) return v.ToResult();

        var count = await db.Patients.CountAsync(p => p.ManagedByUserId == userId && p.UserId == null && p.ArchivedAt == null, ct);
        if (count >= MaxFamilyProfiles)
            throw ApiException.Conflict($"You can add up to {MaxFamilyProfiles} family members.");

        var name = r.FullName!.Trim();
        if (await db.Patients.AnyAsync(p => p.ManagedByUserId == userId && p.ArchivedAt == null && p.FullName == name &&
                                            p.DateOfBirth == r.DateOfBirth, ct))
            throw ApiException.Conflict($"{name} is already in your family list.");

        var now = clock.GetUtcNow();
        var patient = new Patient
        {
            FullName = name,
            ManagedByUserId = userId,
            CreatedByUserId = userId,
            Relationship = r.Relationship!.Value,
            DateOfBirth = r.DateOfBirth,
            Gender = r.Gender,
            GuardianConsentAt = minor ? now : null,
            CreatedAt = now,
        };
        db.Patients.Add(patient);
        analytics.Track(ProductEventNames.FamilyMemberAdded, new { userId, relationship = patient.Relationship.ToString() }, patient.Relationship.ToString());
        await db.SaveChangesAsync(ct);
        var person = (await access.PeopleAsync(userId, ct)).Single(p => p.Patient.Id == patient.Id);
        return Results.Created($"/api/v1/me/family/{patient.Id}", ToDto(person, today));
    }

    private static async Task<IResult> UpdateFamily(
        Guid id, FamilyRequest r, HttpContext http, AppDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var userId = http.User.GetUserId();
        var today = ClinicTime.Today(clock);
        var patient = await db.Patients.FirstOrDefaultAsync(p => p.Id == id && p.ManagedByUserId == userId && p.UserId == null && p.ArchivedAt == null, ct);
        if (patient is null) return Results.NotFound();

        var v = ValidatePerson(r.FullName, r.DateOfBirth, today);
        if (r.Relationship is null or Relationship.Self) v.Add("relationship", "Choose how this person is related to you.");
        var minor = r.DateOfBirth is { } dob && dob.AddYears(18) > today;
        if (minor && patient.GuardianConsentAt is null && !r.GuardianConsent) v.Add("guardianConsent", "Confirm you are this child's parent or guardian.");
        if (!v.IsValid) return v.ToResult();

        patient.FullName = r.FullName!.Trim();
        patient.Relationship = r.Relationship!.Value;
        patient.DateOfBirth = r.DateOfBirth;
        patient.Gender = r.Gender;
        if (minor) patient.GuardianConsentAt ??= clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    /// <summary>Removing a profile hides it; completed visits and audit history are kept (PRD §12.4).</summary>
    private static async Task<IResult> ArchiveFamily(Guid id, HttpContext http, AppDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var userId = http.User.GetUserId();
        var patient = await db.Patients.FirstOrDefaultAsync(p => p.Id == id && p.ManagedByUserId == userId && p.UserId == null && p.ArchivedAt == null, ct);
        if (patient is null) return Results.NotFound();
        var active = await db.Appointments.AnyAsync(a => a.PatientId == id &&
            (a.Status == AppointmentStatus.Booked || a.Status == AppointmentStatus.CheckedIn || a.Status == AppointmentStatus.Called ||
             a.Status == AppointmentStatus.InConsultation || a.Status == AppointmentStatus.Missed || a.Status == AppointmentStatus.Requeued), ct);
        if (active) throw ApiException.Conflict($"{patient.FullName} has an upcoming appointment. Cancel it before removing them.");
        patient.ArchivedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> Links(HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var userId = http.User.GetUserId();
        var mobile = await db.Users.Where(u => u.Id == userId).Select(u => u.MobileNumber).SingleAsync(ct);
        var selfId = await db.Patients.Where(p => p.UserId == userId).Select(p => p.Id).FirstOrDefaultAsync(ct);

        var sent = await (from l in db.PatientLinks.AsNoTracking()
                          where l.ViewerUserId == userId && l.Status != InvitationStatus.Revoked
                          join p in db.Patients on l.PatientId equals p.Id into lp
                          from p in lp.DefaultIfEmpty()
                          orderby l.CreatedAt descending
                          select new { l, Name = p != null ? p.FullName : null }).ToListAsync(ct);
        var received = await (from l in db.PatientLinks.AsNoTracking()
                              where l.InviteeMobile == mobile && l.Status == InvitationStatus.Pending && l.ViewerUserId != userId
                              join u in db.Users on l.ViewerUserId equals u.Id
                              select new { l, u.FullName }).ToListAsync(ct);
        var sharing = await (from l in db.PatientLinks.AsNoTracking()
                             where l.PatientId == selfId && l.Status == InvitationStatus.Active
                             join u in db.Users on l.ViewerUserId equals u.Id
                             select new { l, u.FullName }).ToListAsync(ct);

        return Results.Ok(new LinksResponse(
            sent.Select(x => new LinkDto(x.l.Id, x.l.Status, x.l.Relationship, x.Name ?? "Invitation sent", MobileNumber.Mask(x.l.InviteeMobile), x.l.CreatedAt, x.l.RespondedAt)).ToList(),
            received.Select(x => new LinkDto(x.l.Id, x.l.Status, x.l.Relationship, x.FullName, null, x.l.CreatedAt, x.l.RespondedAt)).ToList(),
            sharing.Select(x => new LinkDto(x.l.Id, x.l.Status, x.l.Relationship, x.FullName, null, x.l.CreatedAt, x.l.RespondedAt)).ToList()));
    }

    private static async Task<IResult> Invite(
        LinkRequest r, HttpContext http, AppDbContext db, NotificationOutbox outbox, TimeProvider clock, CancellationToken ct)
    {
        var userId = http.User.GetUserId();
        var v = new Validator();
        if (!MobileNumber.TryNormalize(r.MobileNumber, out var mobile)) v.Add("mobileNumber", "Enter the family member's 10-digit mobile number.");
        if (r.Relationship is null or Relationship.Self) v.Add("relationship", "Choose how this person is related to you.");
        if (!v.IsValid) return v.ToResult();

        if (await db.PatientLinks.AnyAsync(l => l.ViewerUserId == userId && l.InviteeMobile == mobile && l.Status == InvitationStatus.Pending, ct))
            throw ApiException.Conflict("You have already sent an invitation to this number.");

        var me = await db.Users.SingleAsync(u => u.Id == userId, ct);
        var link = new PatientLink { ViewerUserId = userId, InviteeMobile = mobile, Relationship = r.Relationship!.Value, CreatedAt = clock.GetUtcNow() };
        db.PatientLinks.Add(link);

        var recipients = await db.Users.Where(u => u.MobileNumber == mobile && u.IsActive && u.Id != userId).Select(u => u.Id).ToListAsync(ct);
        await outbox.EnqueueAsync(NotificationEvents.LinkInvitation, $"link:{link.Id}", new NotificationPayload(
            recipients, recipients.Count == 0 ? mobile : null, "Family access request",
            $"{me.FullName} asked to see your visit history on Smart Doctor. Open the app to accept or decline.", null, "/family"), ct);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/v1/me/links/{link.Id}", new { link.Id, link.Status });
    }

    private static async Task<IResult> Accept(Guid id, HttpContext http, AppDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var userId = http.User.GetUserId();
        var (link, mobile) = await LoadReceived(db, id, userId, ct);
        if (link is null) return Results.NotFound();
        var self = await db.Patients.FirstOrDefaultAsync(p => p.UserId == userId, ct)
                   ?? throw ApiException.Conflict("Create your patient profile before accepting.");
        link.Status = InvitationStatus.Active;
        link.PatientId = self.Id;
        link.RespondedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> Decline(Guid id, HttpContext http, AppDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var (link, _) = await LoadReceived(db, id, http.User.GetUserId(), ct);
        if (link is null) return Results.NotFound();
        link.Status = InvitationStatus.Declined;
        link.RespondedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<(PatientLink? Link, string Mobile)> LoadReceived(AppDbContext db, Guid id, Guid userId, CancellationToken ct)
    {
        var mobile = await db.Users.Where(u => u.Id == userId).Select(u => u.MobileNumber).SingleAsync(ct);
        var link = await db.PatientLinks.FirstOrDefaultAsync(l =>
            l.Id == id && l.InviteeMobile == mobile && l.Status == InvitationStatus.Pending && l.ViewerUserId != userId, ct);
        return (link, mobile);
    }

    /// <summary>Either side can end the link: the viewer, or the adult whose history is shared.</summary>
    private static async Task<IResult> Revoke(Guid id, HttpContext http, AppDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var userId = http.User.GetUserId();
        var selfId = await db.Patients.Where(p => p.UserId == userId).Select(p => (Guid?)p.Id).FirstOrDefaultAsync(ct);
        var link = await db.PatientLinks.FirstOrDefaultAsync(l => l.Id == id &&
            (l.ViewerUserId == userId || (selfId != null && l.PatientId == selfId)) &&
            (l.Status == InvitationStatus.Active || l.Status == InvitationStatus.Pending), ct);
        if (link is null) return Results.NotFound();
        link.Status = InvitationStatus.Revoked;
        link.RevokedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }
}
