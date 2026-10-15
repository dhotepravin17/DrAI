using Microsoft.EntityFrameworkCore;
using SmartQueue.Api.Common;
using SmartQueue.Api.Domain;
using SmartQueue.Api.Features.Admin;
using SmartQueue.Api.Features.Notifications;
using SmartQueue.Api.Features.Queue;
using SmartQueue.Api.Features.Plans;
using SmartQueue.Api.Infrastructure;

namespace SmartQueue.Api.Features.Staff;

/// <summary>PRD §7.4: doctors invite receptionists by mobile number; access is scoped to that doctor and clinic and can be revoked.</summary>
public static class StaffEndpoints
{

    public sealed record StaffMember(long AssignmentId, Guid UserId, string FullName, string MobileMasked, DateTimeOffset GrantedAt);
    public sealed record StaffInvitationDto(Guid InvitationId, string FullName, string MobileMasked, InvitationStatus Status, DateTimeOffset CreatedAt);
    public sealed record StaffResponse(IReadOnlyList<StaffMember> Members, IReadOnlyList<StaffInvitationDto> Pending);
    public sealed record InviteRequest(string? MobileNumber, string? FullName);
    public sealed record ReceivedInvitation(Guid InvitationId, string DoctorName, string? DoctorCode, string ClinicName, DateTimeOffset CreatedAt);

    public static RouteGroupBuilder MapStaffEndpoints(this RouteGroupBuilder api)
    {
        var clinic = api.MapGroup("/clinic/staff").WithTags("Clinic staff").RequireAuthorization();
        clinic.MapGet("/", List);
        clinic.MapPost("/", Invite);
        clinic.MapDelete("/invitations/{id:guid}", CancelInvitation);
        clinic.MapDelete("/{assignmentId:long}", Revoke);

        var me = api.MapGroup("/me/staff-invitations").WithTags("Clinic staff").RequireAuthorization();
        me.MapGet("/", Received);
        me.MapPost("/{id:guid}/accept", Accept);
        me.MapPost("/{id:guid}/decline", Decline);
        return api;
    }

    private static async Task<StaffResponse> Load(AppDbContext db, Guid doctorId, CancellationToken ct)
    {
        var members = await (from r in db.UserRoleAssignments.AsNoTracking()
                             where r.DoctorId == doctorId && r.Role == RoleName.Receptionist && r.RevokedAt == null
                             join u in db.Users on r.UserId equals u.Id
                             select new { r, u }).ToListAsync(ct);
        var pending = await db.StaffInvitations.AsNoTracking()
            .Where(i => i.DoctorId == doctorId && i.Status == InvitationStatus.Pending).OrderBy(i => i.CreatedAt).ToListAsync(ct);
        return new StaffResponse(
            members.Select(m => new StaffMember(m.r.Id, m.u.Id, m.u.FullName, MobileNumber.Mask(m.u.MobileNumber), m.r.GrantedAt)).ToList(),
            pending.Select(i => new StaffInvitationDto(i.Id, i.FullName, MobileNumber.Mask(i.MobileNumber), i.Status, i.CreatedAt)).ToList());
    }

    private static async Task<IResult> List(Guid? doctorId, HttpContext http, ClinicAccess access, AppDbContext db, CancellationToken ct)
    {
        var actor = await access.ResolveAsync(http.User, doctorId, ct, doctorOnly: true);
        return Results.Ok(await Load(db, actor.Doctor.Id, ct));
    }

    private static async Task<IResult> Invite(
        InviteRequest r, Guid? doctorId, HttpContext http, ClinicAccess access, AppDbContext db, NotificationOutbox outbox,
        AuditLogger audit, PlanService plans, TimeProvider clock, CancellationToken ct)
    {
        var actor = await access.ResolveAsync(http.User, doctorId, ct, doctorOnly: true);
        var v = new Validator().Required("fullName", r.FullName, 120);
        if (!MobileNumber.TryNormalize(r.MobileNumber, out var mobile)) v.Add("mobileNumber", "Enter a valid 10-digit mobile number.");
        if (!v.IsValid) return v.ToResult();

        var current = await Load(db, actor.Doctor.Id, ct);
        var limit = plans.LimitsFor(actor.Doctor).StaffAccounts;
        if (current.Members.Count + current.Pending.Count >= limit)
            throw ApiException.Conflict($"Your {actor.Doctor.Plan} plan includes up to {limit} staff account{(limit == 1 ? "" : "s")}.",
                actor.Doctor.Plan == DoctorPlan.Free ? "Upgrade to Pro for up to 5 staff accounts." : null);
        if (await db.StaffInvitations.AnyAsync(i => i.DoctorId == actor.Doctor.Id && i.MobileNumber == mobile && i.Status == InvitationStatus.Pending, ct))
            throw ApiException.Conflict("This number already has a pending invitation.");

        var invitation = new StaffInvitation
        {
            DoctorId = actor.Doctor.Id, ClinicId = actor.ClinicId, MobileNumber = mobile, FullName = r.FullName!.Trim(),
            InvitedByUserId = actor.UserId, CreatedAt = clock.GetUtcNow(),
        };
        db.StaffInvitations.Add(invitation);
        var recipients = await db.Users.Where(u => u.MobileNumber == mobile && u.IsActive).Select(u => u.Id).ToListAsync(ct);
        await outbox.EnqueueAsync(NotificationEvents.StaffInvitation, $"staff:{invitation.Id}", new NotificationPayload(
            recipients, recipients.Count == 0 ? mobile : null, "Clinic staff invitation",
            $"{actor.Doctor.FullName} invited you to manage their clinic appointments. Sign in to accept.", null, "/account"), ct);
        audit.Record("InviteStaff", "Doctor", actor.Doctor.Id.ToString(), details: MobileNumber.Mask(mobile));
        await db.SaveChangesAsync(ct);
        return Results.Ok(await Load(db, actor.Doctor.Id, ct));
    }

    private static async Task<IResult> CancelInvitation(
        Guid id, Guid? doctorId, HttpContext http, ClinicAccess access, AppDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var actor = await access.ResolveAsync(http.User, doctorId, ct, doctorOnly: true);
        var invitation = await db.StaffInvitations.FirstOrDefaultAsync(i => i.Id == id && i.DoctorId == actor.Doctor.Id && i.Status == InvitationStatus.Pending, ct);
        if (invitation is null) return Results.NotFound();
        invitation.Status = InvitationStatus.Revoked;
        invitation.RespondedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return Results.Ok(await Load(db, actor.Doctor.Id, ct));
    }

    private static async Task<IResult> Revoke(
        long assignmentId, Guid? doctorId, HttpContext http, ClinicAccess access, AppDbContext db, AuditLogger audit,
        TimeProvider clock, CancellationToken ct)
    {
        var actor = await access.ResolveAsync(http.User, doctorId, ct, doctorOnly: true);
        var assignment = await db.UserRoleAssignments.FirstOrDefaultAsync(r =>
            r.Id == assignmentId && r.DoctorId == actor.Doctor.Id && r.Role == RoleName.Receptionist && r.RevokedAt == null, ct);
        if (assignment is null) return Results.NotFound();
        assignment.RevokedAt = clock.GetUtcNow();
        audit.Record("RevokeStaff", "Doctor", actor.Doctor.Id.ToString(), details: assignment.UserId.ToString());
        await db.SaveChangesAsync(ct);
        return Results.Ok(await Load(db, actor.Doctor.Id, ct));
    }

    private static async Task<IResult> Received(HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var userId = http.User.GetUserId();
        var mobile = await db.Users.Where(u => u.Id == userId).Select(u => u.MobileNumber).SingleAsync(ct);
        var list = await (from i in db.StaffInvitations.AsNoTracking()
                          where i.MobileNumber == mobile && i.Status == InvitationStatus.Pending
                          join d in db.Doctors on i.DoctorId equals d.Id
                          where d.UserId != userId
                          join c in db.Clinics on i.ClinicId equals c.Id
                          select new ReceivedInvitation(i.Id, d.FullName, d.DoctorCode, c.Name, i.CreatedAt)).ToListAsync(ct);
        return Results.Ok(list);
    }

    private static async Task<IResult> Accept(Guid id, HttpContext http, AppDbContext db, AuditLogger audit, TimeProvider clock, CancellationToken ct)
    {
        var userId = http.User.GetUserId();
        var invitation = await LoadReceived(db, id, userId, ct);
        if (invitation is null) return Results.NotFound();
        var now = clock.GetUtcNow();
        invitation.Status = InvitationStatus.Active;
        invitation.AcceptedByUserId = userId;
        invitation.RespondedAt = now;
        if (!await db.UserRoleAssignments.AnyAsync(r => r.UserId == userId && r.DoctorId == invitation.DoctorId &&
                                                        r.Role == RoleName.Receptionist && r.RevokedAt == null, ct))
        {
            db.UserRoleAssignments.Add(new UserRoleAssignment
            {
                UserId = userId, Role = RoleName.Receptionist, DoctorId = invitation.DoctorId, ClinicId = invitation.ClinicId,
                GrantedAt = now, GrantedByUserId = invitation.InvitedByUserId,
            });
        }
        audit.Record("AcceptStaffInvitation", "Doctor", invitation.DoctorId.ToString(), details: userId.ToString());
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> Decline(Guid id, HttpContext http, AppDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var invitation = await LoadReceived(db, id, http.User.GetUserId(), ct);
        if (invitation is null) return Results.NotFound();
        invitation.Status = InvitationStatus.Declined;
        invitation.RespondedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<StaffInvitation?> LoadReceived(AppDbContext db, Guid id, Guid userId, CancellationToken ct)
    {
        var mobile = await db.Users.Where(u => u.Id == userId).Select(u => u.MobileNumber).SingleAsync(ct);
        return await db.StaffInvitations.FirstOrDefaultAsync(i => i.Id == id && i.MobileNumber == mobile && i.Status == InvitationStatus.Pending, ct);
    }
}
