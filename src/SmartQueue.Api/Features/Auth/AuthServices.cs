using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using SmartQueue.Api.Common;
using SmartQueue.Api.Domain;
using SmartQueue.Api.Infrastructure;

namespace SmartQueue.Api.Features.Auth;

public sealed class JwtOptions
{
    public const string Section = "Jwt";
    public string Issuer { get; set; } = "smartqueue";
    public string SigningKey { get; set; } = "";
    public TimeSpan PatientTokenLifetime { get; set; } = TimeSpan.FromDays(30);
    public TimeSpan StaffTokenLifetime { get; set; } = TimeSpan.FromHours(12);
    public TimeSpan AdminTokenLifetime { get; set; } = TimeSpan.FromHours(1);
    public TimeSpan PhoneTicketLifetime { get; set; } = TimeSpan.FromMinutes(10);
}

public sealed class OtpOptions
{
    public const string Section = "Otp";
    public TimeSpan Validity { get; set; } = TimeSpan.FromMinutes(5);
    public int MaxRequestsPerHour { get; set; } = 5;
    public int MaxFailedAttempts { get; set; } = 5;
    public int MaxAccountsPerMobile { get; set; } = 5;

    /// <summary>Host name used in the SMS "@domain #code" line for one-time-code autofill, for example app.example.in.</summary>
    public string? WebOtpDomain { get; set; }

    /// <summary>Development only: return the OTP in the API response so the app can be tried without an SMS gateway.</summary>
    public bool ExposeCodeInResponse { get; set; }
}

public sealed class AdminAuthOptions
{
    public const string Section = "Auth";

    /// <summary>
    /// Super Admins confirm sign-in with an authenticator app code after the phone code (PRD §27.4). On by default;
    /// development turns it off in appsettings.Development.json.
    /// </summary>
    public bool AdminMfaRequired { get; set; } = true;
}

public sealed record IssuedToken(string AccessToken, DateTimeOffset ExpiresAt);

/// <summary>Issues tokens using the real system clock, because token validation always uses it.</summary>
public sealed class TokenService(IOptions<JwtOptions> options)
{
    public const string AccessAudience = "smartqueue-api";
    public const string PhoneTicketAudience = "smartqueue-phone-ticket";
    public const string MfaTicketAudience = "smartqueue-mfa-ticket";
    private static readonly TimeProvider clock = TimeProvider.System;

    private readonly JsonWebTokenHandler _handler = new();
    private JwtOptions Options => options.Value;

    public IssuedToken CreateAccessToken(User user, IReadOnlyCollection<RoleName> roles)
    {
        var lifetime = roles.Contains(RoleName.SuperAdmin) ? Options.AdminTokenLifetime
            : roles.Count > 0 ? Options.StaffTokenLifetime
            : Options.PatientTokenLifetime;

        var claims = new List<Claim> { new("sub", user.Id.ToString()), new("name", user.FullName) };
        claims.AddRange(roles.Distinct().Select(r => new Claim("role", r.ToString())));
        return Issue(claims, AccessAudience, lifetime);
    }

    public string CreatePhoneTicket(string mobileNumber) =>
        Issue([new Claim("mobile", mobileNumber)], PhoneTicketAudience, Options.PhoneTicketLifetime).AccessToken;

    public Task<string?> ValidatePhoneTicketAsync(string? ticket) => ReadClaimAsync(ticket, PhoneTicketAudience, "mobile");

    /// <summary>Proof that the phone step succeeded for a Super Admin account; the TOTP step must follow within 5 minutes.</summary>
    public string CreateMfaTicket(Guid userId) =>
        Issue([new Claim("sub", userId.ToString())], MfaTicketAudience, TimeSpan.FromMinutes(5)).AccessToken;

    public async Task<Guid?> ValidateMfaTicketAsync(string? ticket) =>
        Guid.TryParse(await ReadClaimAsync(ticket, MfaTicketAudience, "sub"), out var id) ? id : null;

    private async Task<string?> ReadClaimAsync(string? ticket, string audience, string claim)
    {
        if (string.IsNullOrWhiteSpace(ticket)) return null;
        var result = await _handler.ValidateTokenAsync(ticket, CreateValidationParameters(Options, audience));
        return result.IsValid && result.Claims.TryGetValue(claim, out var value) ? value as string : null;
    }

    public static TokenValidationParameters CreateValidationParameters(JwtOptions o, string audience) => new()
    {
        ValidIssuer = o.Issuer,
        ValidAudience = audience,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(o.SigningKey)),
        ValidateIssuerSigningKey = true,
        ClockSkew = TimeSpan.FromSeconds(30),
        NameClaimType = "sub",
        RoleClaimType = "role",
    };

    private IssuedToken Issue(IEnumerable<Claim> claims, string audience, TimeSpan lifetime)
    {
        var now = clock.GetUtcNow();
        var expires = now.Add(lifetime);
        var token = _handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Options.Issuer,
            Audience = audience,
            Subject = new ClaimsIdentity(claims),
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Options.SigningKey)), SecurityAlgorithms.HmacSha256),
        });
        return new IssuedToken(token, expires);
    }
}

public interface ISmsSender
{
    /// <summary>False when no provider is configured; notification SMS are then recorded as skipped.</summary>
    bool IsConfigured { get; }

    Task SendOtpAsync(string mobileNumber, string code, CancellationToken ct);

    /// <returns>The provider's message reference, when it returns one.</returns>
    /// <exception cref="SmsDeliveryException">The provider rejected the message or could not be reached.</exception>
    Task<string?> SendTextAsync(string mobileNumber, string text, CancellationToken ct);
}

public static class SmsTexts
{
    /// <summary>
    /// The last line binds the code to the site so browsers can autofill it (WebOTP and autocomplete="one-time-code").
    /// Templates must also be registered on the DLT platform before production use.
    /// </summary>
    public static string Otp(string code, string? domain) =>
        domain is null
            ? $"{code} is your Smart Doctor verification code. It expires in 5 minutes."
            : $"{code} is your Smart Doctor verification code. It expires in 5 minutes.\n\n@{domain} #{code}";
}

/// <summary>Used when no SMS provider is configured (<c>Sms:Provider</c> is not set): development and tests.</summary>
public sealed class LoggingSmsSender(ILogger<LoggingSmsSender> logger, IHostEnvironment env, IOptions<OtpOptions> options) : ISmsSender
{
    public bool IsConfigured => false;

    public Task SendOtpAsync(string mobileNumber, string code, CancellationToken ct)
    {
        if (env.IsDevelopment())
            logger.LogInformation("SMS to {Mobile}: {Text}", MobileNumber.Mask(mobileNumber), SmsTexts.Otp(code, options.Value.WebOtpDomain));
        else
            logger.LogWarning("No SMS provider configured; OTP for {Mobile} was not delivered.", MobileNumber.Mask(mobileNumber));
        return Task.CompletedTask;
    }

    public Task<string?> SendTextAsync(string mobileNumber, string text, CancellationToken ct)
    {
        logger.LogInformation("No SMS provider configured; message to {Mobile} not sent.", MobileNumber.Mask(mobileNumber));
        return Task.FromResult<string?>(null);
    }
}

public enum OtpVerifyResult { Success, InvalidOrExpired, Locked }

public sealed class OtpService(
    AppDbContext db, ISmsSender sms, IOptions<OtpOptions> options, IOptions<JwtOptions> jwt, TimeProvider clock)
{
    private OtpOptions Options => options.Value;

    /// <returns>The expiry time and the generated code (callers decide whether the code may be exposed).</returns>
    public async Task<(DateTimeOffset ExpiresAt, string Code)> RequestAsync(string mobileNumber, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var hourAgo = now.AddHours(-1);
        var recent = await db.OtpChallenges.CountAsync(c => c.MobileNumber == mobileNumber && c.CreatedAt > hourAgo, ct);
        if (recent >= Options.MaxRequestsPerHour)
            throw new ApiException(StatusCodes.Status429TooManyRequests, "Too many OTP requests.", "Try again later.");

        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        var challenge = new OtpChallenge
        {
            MobileNumber = mobileNumber,
            CodeHash = Hash(mobileNumber, code),
            CreatedAt = now,
            ExpiresAt = now.Add(Options.Validity),
        };
        db.OtpChallenges.Add(challenge);
        await db.SaveChangesAsync(ct);
        try
        {
            await sms.SendOtpAsync(mobileNumber, code, ct);
        }
        catch (SmsDeliveryException)
        {
            throw new ApiException(StatusCodes.Status503ServiceUnavailable, "We could not send the code right now.", "Try again in a minute.");
        }
        return (challenge.ExpiresAt, code);
    }

    public async Task<OtpVerifyResult> VerifyAsync(string mobileNumber, string code, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var challenge = await db.OtpChallenges
            .Where(c => c.MobileNumber == mobileNumber && c.ConsumedAt == null && c.ExpiresAt > now)
            .OrderByDescending(c => c.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (challenge is null) return OtpVerifyResult.InvalidOrExpired;
        if (challenge.FailedAttempts >= Options.MaxFailedAttempts) return OtpVerifyResult.Locked;

        var expected = Convert.FromHexString(challenge.CodeHash);
        var actual = Convert.FromHexString(Hash(mobileNumber, code.Trim()));
        if (!CryptographicOperations.FixedTimeEquals(expected, actual))
        {
            challenge.FailedAttempts++;
            await db.SaveChangesAsync(ct);
            return challenge.FailedAttempts >= Options.MaxFailedAttempts ? OtpVerifyResult.Locked : OtpVerifyResult.InvalidOrExpired;
        }

        challenge.ConsumedAt = now;
        await db.SaveChangesAsync(ct);
        return OtpVerifyResult.Success;
    }

    private string Hash(string mobileNumber, string code)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(jwt.Value.SigningKey));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes($"{mobileNumber}:{code}")));
    }
}
