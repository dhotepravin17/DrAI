using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using SmartQueue.Api.Domain;
using SmartQueue.Api.Features.Auth;
using SmartQueue.Api.Infrastructure;

namespace SmartQueue.Api.Tests.Infrastructure;

public sealed record Session(HttpClient Client, Guid UserId, string Mobile);

public sealed record DoctorDraft(Session Session, Guid DoctorId, string MedicalRegistrationId);

/// <summary>Drives the public API the way the web app does, so tests exercise real endpoints.</summary>
public sealed class TestApi(ApiFactory factory)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private static int _mobileCounter;
    private static int _cityCounter;

    public ApiFactory Factory => factory;

    public HttpClient Anonymous() => factory.CreateClient();

    public static string NextMobile() => "98" + Interlocked.Increment(ref _mobileCounter).ToString("D8");

    /// <summary>Allocates XAA, XAB, ... so each test owns a fresh Doctor ID sequence.</summary>
    public static string NextCityCode()
    {
        var n = Interlocked.Increment(ref _cityCounter);
        return $"X{(char)('A' + n / 26 % 26)}{(char)('A' + n % 26)}";
    }

    public async Task<string> VerifyPhoneAsync(string mobile)
    {
        var client = Anonymous();
        (await client.PostAsJsonAsync("/api/v1/auth/otp/request", new { mobileNumber = mobile })).EnsureSuccessStatusCode();
        var verify = await client.PostAsJsonAsync("/api/v1/auth/otp/verify",
            new { mobileNumber = mobile, code = factory.Sms.LastCodeFor(mobile) });
        verify.EnsureSuccessStatusCode();
        return (await verify.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("phoneTicket").GetString()!;
    }

    public async Task<Session> CreatePatientAsync(string fullName = "Rahul Patil", string? mobile = null)
    {
        mobile ??= NextMobile();
        var ticket = await VerifyPhoneAsync(mobile);
        var response = await Anonymous().PostAsJsonAsync("/api/v1/auth/accounts", new
        {
            phoneTicket = ticket,
            fullName,
            privacyNoticeVersion = LegalTexts.PrivacyNoticeVersion,
            termsVersion = LegalTexts.TermsVersion,
            city = "Nagpur",
        });
        response.EnsureSuccessStatusCode();
        return await ToSessionAsync(response, mobile);
    }

    /// <summary>Shared for the whole run: the per-number OTP limit (5 per hour) applies to the admin's number too.</summary>
    public Task<Session> LoginSuperAdminAsync()
    {
        lock (factory) return factory.AdminSession ??= LoginSuperAdminCoreAsync();
    }

    private Task<Session> LoginSuperAdminCoreAsync() => LoginAdminAsync(ApiFactory.SuperAdminMobile);

    /// <summary>Phone OTP, then the TOTP step (enrolling on first use) as a Super Admin would.</summary>
    public async Task<Session> LoginAdminAsync(string mobile)
    {
        var challenge = await AdminChallengeAsync(mobile);
        var secret = factory.TotpSecrets.GetOrAdd(mobile, _ => challenge.GetProperty("enrollment").GetProperty("secret").GetString()!);
        var code = Totp.Compute(secret, Totp.CurrentStep(factory.Clock.GetUtcNow()));
        var response = await Anonymous().PostAsJsonAsync("/api/v1/auth/mfa",
            new { mfaTicket = challenge.GetProperty("mfaTicket").GetString(), code });
        response.EnsureSuccessStatusCode();
        return await ToSessionAsync(response, mobile);
    }

    public async Task<JsonElement> AdminChallengeAsync(string mobile)
    {
        var ticket = await VerifyPhoneAsync(mobile);
        var userId = await factory.WithDbAsync(db => Task.FromResult(
            db.Users.Single(u => u.MobileNumber == "+91" + mobile && u.RoleAssignments.Any(r => r.Role == RoleName.SuperAdmin)).Id));
        var response = await Anonymous().PostAsJsonAsync("/api/v1/auth/session", new { phoneTicket = ticket, userId });
        response.EnsureSuccessStatusCode();
        var challenge = await response.JsonAsync();
        Assert.True(challenge.GetProperty("mfaRequired").GetBoolean());
        return challenge;
    }

    public async Task<string> CreateCityAsync(Session admin)
    {
        var code = NextCityCode();
        var response = await admin.Client.PostAsJsonAsync("/api/v1/admin/cities",
            new { code, name = $"Test City {code}", state = "Maharashtra" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return code;
    }

    public static object RegistrationBody(string cityCode, string fullName = "Dr. Amit Sharma", string? registrationId = null) => new
    {
        fullName,
        email = "doctor@example.com",
        specialty = "General Physician",
        qualifications = "MBBS, MD",
        medicalRegistrationId = registrationId ?? $"MMC-{Guid.NewGuid().ToString("N")[..8]}",
        medicalRegistrationAuthority = "Maharashtra Medical Council",
        primaryCityCode = cityCode,
        clinicName = "Sharma Clinic",
        clinicAddress = "12 Main Road, Dharampeth",
        clinicPublicPhone = "0712-2500000",
    };

    public async Task<DoctorDraft> CreateDraftDoctorAsync(string cityCode, string fullName = "Dr. Amit Sharma", string? registrationId = null)
    {
        var session = await CreatePatientAsync(fullName);
        var body = RegistrationBody(cityCode, fullName, registrationId);
        var response = await session.Client.PutAsJsonAsync("/api/v1/doctor-registration", body);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        return new DoctorDraft(session, json.GetProperty("doctorId").GetGuid(), json.GetProperty("medicalRegistrationId").GetString()!);
    }

    public async Task<DoctorDraft> CreateSubmittedDoctorAsync(string cityCode, string fullName = "Dr. Amit Sharma", string? registrationId = null)
    {
        var draft = await CreateDraftDoctorAsync(cityCode, fullName, registrationId);
        var response = await Submit(draft.Session.Client);
        response.EnsureSuccessStatusCode();
        return draft;
    }

    public static Task<HttpResponseMessage> Submit(HttpClient client, bool accepted = true, string? version = null) =>
        client.PostAsJsonAsync("/api/v1/doctor-registration/submit",
            new { declarationAccepted = accepted, declarationVersion = version ?? LegalTexts.DoctorDeclarationVersion });

    public static async Task<string> VerifyAsync(Session admin, Guid doctorId)
    {
        var response = await admin.Client.PostAsJsonAsync($"/api/v1/admin/doctors/{doctorId}/verify", new { verificationReference = "NMR lookup" });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("doctorCode").GetString()!;
    }

    private async Task<Session> ToSessionAsync(HttpResponseMessage response, string mobile)
    {
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        var client = Anonymous();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", json.GetProperty("accessToken").GetString());
        return new Session(client, json.GetProperty("userId").GetGuid(), mobile);
    }
}

public static class JsonExtensions
{
    public static Task<JsonElement> JsonAsync(this HttpResponseMessage response) =>
        response.Content.ReadFromJsonAsync<JsonElement>(TestApi.Json);
}
