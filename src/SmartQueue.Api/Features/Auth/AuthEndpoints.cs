using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SmartQueue.Api.Common;
using SmartQueue.Api.Domain;
using SmartQueue.Api.Features.Admin;
using SmartQueue.Api.Features.Analytics;
using SmartQueue.Api.Infrastructure;

namespace SmartQueue.Api.Features.Auth;

public static class AuthEndpoints
{
    public sealed record OtpRequest(string? MobileNumber);
    public sealed record OtpRequestResponse(DateTimeOffset ExpiresAt, string? DevelopmentCode);
    public sealed record OtpVerifyRequest(string? MobileNumber, string? Code);
    public sealed record AccountSummary(Guid UserId, string FullName, IReadOnlyList<RoleName> Roles);
    public sealed record OtpVerifyResponse(string PhoneTicket, IReadOnlyList<AccountSummary> Accounts);
    public sealed record SessionRequest(string? PhoneTicket, Guid UserId);
    public sealed record CreateAccountRequest(
        string? PhoneTicket, string? FullName, string? PrivacyNoticeVersion, string? TermsVersion,
        DateOnly? DateOfBirth, Gender? Gender, string? City, string? Email);
    public sealed record TokenResponse(string AccessToken, DateTimeOffset ExpiresAt, Guid UserId, IReadOnlyList<RoleName> Roles);
    public sealed record MfaEnrollment(string Secret, string OtpAuthUri);
    public sealed record MfaChallenge(bool MfaRequired, string MfaTicket, MfaEnrollment? Enrollment);
    public sealed record MfaRequest(string? MfaTicket, string? Code);

    public const string TotpProtectorPurpose = "SmartQueue.Totp.v1";

    public static RouteGroupBuilder MapAuthEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/auth").WithTags("Auth").RequireRateLimiting(RateLimitPolicies.Otp);

        group.MapPost("/otp/request", RequestOtp);
        group.MapPost("/otp/verify", VerifyOtp);
        group.MapPost("/session", CreateSession);
        group.MapPost("/mfa", VerifyMfa);
        group.MapPost("/accounts", CreateAccount);
        return api;
    }

    private static async Task<IResult> RequestOtp(
        OtpRequest request, OtpService otp, IOptions<OtpOptions> options, IHostEnvironment env, CancellationToken ct)
    {
        if (!MobileNumber.TryNormalize(request.MobileNumber, out var mobile))
            return new Validator().Add("mobileNumber", "Enter a valid 10-digit Indian mobile number.").ToResult();

        var (expiresAt, code) = await otp.RequestAsync(mobile, ct);
        var expose = options.Value.ExposeCodeInResponse && env.IsDevelopment();
        return Results.Accepted(value: new OtpRequestResponse(expiresAt, expose ? code : null));
    }

    private static async Task<IResult> VerifyOtp(
        OtpVerifyRequest request, OtpService otp, TokenService tokens, AppDbContext db, CancellationToken ct)
    {
        if (!MobileNumber.TryNormalize(request.MobileNumber, out var mobile) || string.IsNullOrWhiteSpace(request.Code))
            return new Validator().Add("code", "Enter the mobile number and the 6-digit code.").ToResult();

        var result = await otp.VerifyAsync(mobile, request.Code, ct);
        return result switch
        {
            OtpVerifyResult.Locked => Results.Problem(
                statusCode: StatusCodes.Status429TooManyRequests, title: "Too many wrong attempts.", detail: "Request a new code."),
            OtpVerifyResult.InvalidOrExpired => Results.Problem(
                statusCode: StatusCodes.Status400BadRequest, title: "The code is incorrect or has expired."),
            _ => Results.Ok(new OtpVerifyResponse(tokens.CreatePhoneTicket(mobile), await AccountsFor(db, mobile, ct))),
        };
    }

    private static async Task<IResult> CreateSession(
        SessionRequest request, TokenService tokens, AppDbContext db, IDataProtectionProvider protection, CancellationToken ct)
    {
        var mobile = await tokens.ValidatePhoneTicketAsync(request.PhoneTicket);
        if (mobile is null) return PhoneTicketInvalid();

        var user = await db.Users.Include(u => u.RoleAssignments)
            .FirstOrDefaultAsync(u => u.Id == request.UserId && u.MobileNumber == mobile && u.IsActive, ct);
        if (user is null) return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Account not found for this number.");

        if (ActiveRoles(user).Contains(RoleName.SuperAdmin))
        {
            MfaEnrollment? enrollment = null;
            if (user.TotpEnabledAt is null)
            {
                var protector = protection.CreateProtector(TotpProtectorPurpose);
                user.TotpSecretProtected ??= protector.Protect(Totp.NewSecret());
                await db.SaveChangesAsync(ct);
                var secret = protector.Unprotect(user.TotpSecretProtected);
                enrollment = new MfaEnrollment(secret, Totp.OtpAuthUri(secret, MobileNumber.Mask(user.MobileNumber)));
            }
            return Results.Ok(new MfaChallenge(true, tokens.CreateMfaTicket(user.Id), enrollment));
        }

        return Results.Ok(Issue(tokens, user));
    }

    /// <summary>Second step for Super Admins: a TOTP code from their authenticator app (PRD §27.4).</summary>
    private static async Task<IResult> VerifyMfa(
        MfaRequest request, TokenService tokens, AppDbContext db, IDataProtectionProvider protection, AuditLogger audit,
        TimeProvider clock, HttpContext http, CancellationToken ct)
    {
        var userId = await tokens.ValidateMfaTicketAsync(request.MfaTicket);
        if (userId is null) return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Sign-in has expired.", detail: "Start again.");

        var user = await db.Users.Include(u => u.RoleAssignments).FirstOrDefaultAsync(u => u.Id == userId && u.IsActive, ct);
        if (user?.TotpSecretProtected is null) return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Sign-in has expired.");

        var now = clock.GetUtcNow();
        if (user.TotpLockedUntil > now) return MfaLocked(user.TotpLockedUntil.Value, now);

        var secret = protection.CreateProtector(TotpProtectorPurpose).Unprotect(user.TotpSecretProtected);
        var step = Totp.Verify(secret, request.Code, now, user.TotpLastUsedStep);
        if (step is null)
        {
            // Counted atomically so parallel guesses with the same or different MFA tickets all count toward the limit.
            var failures = (await db.Database.SqlQuery<int>(
                    $"UPDATE [Users] SET [TotpFailedAttempts] = [TotpFailedAttempts] + 1 OUTPUT inserted.[TotpFailedAttempts] AS [Value] WHERE [Id] = {user.Id}")
                .ToListAsync(ct)).Single();
            if (failures < MaxTotpFailures)
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The authenticator code is incorrect.");

            var lockedUntil = now.Add(TotpLockout);
            await db.Users.Where(u => u.Id == user.Id).ExecuteUpdateAsync(s => s
                .SetProperty(u => u.TotpFailedAttempts, 0)
                .SetProperty(u => u.TotpLockedUntil, lockedUntil), ct);
            audit.Record("AdminMfaLocked", "User", user.Id.ToString(), details: $"{failures} incorrect authenticator codes", actorUserId: user.Id);
            await db.SaveChangesAsync(ct);
            return MfaLocked(lockedUntil, now);
        }

        user.TotpLastUsedStep = step;
        user.TotpFailedAttempts = 0;
        user.TotpLockedUntil = null;
        user.TotpEnabledAt ??= now;
        audit.Record("AdminSignIn", "User", user.Id.ToString(), actorUserId: user.Id);
        await db.SaveChangesAsync(ct);
        return Results.Ok(Issue(tokens, user));
    }

    public const int MaxTotpFailures = 5;
    public static readonly TimeSpan TotpLockout = TimeSpan.FromMinutes(15);

    private static IResult MfaLocked(DateTimeOffset until, DateTimeOffset now)
    {
        var minutes = Math.Max(1, (int)Math.Ceiling((until - now).TotalMinutes));
        return Results.Problem(statusCode: StatusCodes.Status429TooManyRequests, title: "Too many incorrect authenticator codes.",
            detail: $"Sign-in is locked. Try again in {minutes} minute{(minutes == 1 ? "" : "s")}.");
    }

    private static async Task<IResult> CreateAccount(
        CreateAccountRequest request, TokenService tokens, AppDbContext db, IOptions<OtpOptions> options, ProductAnalytics analytics,
        TimeProvider clock, HttpContext http, CancellationToken ct)
    {
        var mobile = await tokens.ValidatePhoneTicketAsync(request.PhoneTicket);
        if (mobile is null) return PhoneTicketInvalid();

        var v = new Validator()
            .Required("fullName", request.FullName, 120)
            .Optional("city", request.City, 80)
            .Email("email", request.Email);
        if (request.PrivacyNoticeVersion != LegalTexts.PrivacyNoticeVersion)
            v.Add("privacyNoticeVersion", "Accept the current privacy notice to continue.");
        if (request.TermsVersion != LegalTexts.TermsVersion)
            v.Add("termsVersion", "Accept the current terms of use to continue.");
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        if (request.DateOfBirth is { } dob && (dob > today || dob < today.AddYears(-120)))
            v.Add("dateOfBirth", "Enter a valid date of birth.");
        if (!v.IsValid) return v.ToResult();

        var existing = await db.Users.CountAsync(u => u.MobileNumber == mobile, ct);
        if (existing >= options.Value.MaxAccountsPerMobile)
            return Results.Problem(statusCode: StatusCodes.Status409Conflict,
                title: "This mobile number already has the maximum number of accounts.");

        var now = clock.GetUtcNow();
        var user = new User { MobileNumber = mobile, FullName = request.FullName!.Trim(), Email = request.Email.TrimToNull(), CreatedAt = now };
        db.Users.Add(user);
        db.Patients.Add(new Patient
        {
            UserId = user.Id,
            ManagedByUserId = user.Id,
            Relationship = Relationship.Self,
            CreatedByUserId = user.Id,
            FullName = user.FullName,
            DateOfBirth = request.DateOfBirth,
            Gender = request.Gender,
            City = request.City.TrimToNull(),
            CreatedAt = now,
        });
        var ip = http.ClientIp();
        db.ConsentRecords.AddRange(
            new ConsentRecord { UserId = user.Id, Type = ConsentType.PrivacyNotice, Version = LegalTexts.PrivacyNoticeVersion, GrantedAt = now, IpAddress = ip },
            new ConsentRecord { UserId = user.Id, Type = ConsentType.TermsOfUse, Version = LegalTexts.TermsVersion, GrantedAt = now, IpAddress = ip });
        analytics.Track(ProductEventNames.PatientRegistered, new { userId = user.Id, channel = "Web" }, "Web");
        await db.SaveChangesAsync(ct);

        return Results.Ok(Issue(tokens, user));
    }

    private static TokenResponse Issue(TokenService tokens, User user)
    {
        var roles = ActiveRoles(user);
        var token = tokens.CreateAccessToken(user, roles);
        return new TokenResponse(token.AccessToken, token.ExpiresAt, user.Id, roles);
    }

    private static List<RoleName> ActiveRoles(User user) =>
        user.RoleAssignments.Where(r => r.RevokedAt == null).Select(r => r.Role).Distinct().ToList();

    private static async Task<List<AccountSummary>> AccountsFor(AppDbContext db, string mobile, CancellationToken ct)
    {
        var users = await db.Users.Include(u => u.RoleAssignments)
            .Where(u => u.MobileNumber == mobile && u.IsActive)
            .OrderBy(u => u.CreatedAt).ThenBy(u => u.FullName)
            .ToListAsync(ct);
        return users.Select(u => new AccountSummary(u.Id, u.FullName, ActiveRoles(u))).ToList();
    }

    private static IResult PhoneTicketInvalid() => Results.Problem(
        statusCode: StatusCodes.Status401Unauthorized, title: "Mobile verification has expired.", detail: "Verify your number again.");
}
