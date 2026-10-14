using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using SmartQueue.Api.Infrastructure;
using SmartQueue.Api.Tests.Infrastructure;

namespace SmartQueue.Api.Tests;

[Collection(ApiCollection.Name)]
public class AuthTests(ApiFactory factory)
{
    private readonly TestApi _api = new(factory);

    [Fact]
    public async Task Patient_registers_with_otp_and_sees_own_profile()
    {
        var session = await _api.CreatePatientAsync("Rahul Patil");

        var me = await (await session.Client.GetAsync("/api/v1/me")).JsonAsync();

        Assert.Equal("Rahul Patil", me.GetProperty("fullName").GetString());
        Assert.Equal("Rahul Patil", me.GetProperty("patient").GetProperty("fullName").GetString());
        Assert.DoesNotContain(session.Mobile, me.GetProperty("mobileNumberMasked").GetString());
        Assert.Equal(0, me.GetProperty("roles").GetArrayLength());

        var consents = await factory.WithDbAsync(db => db.ConsentRecords.CountAsync(c => c.UserId == session.UserId));
        Assert.Equal(2, consents);
    }

    [Fact]
    public async Task Rejects_invalid_mobile_number()
    {
        var response = await _api.Anonymous().PostAsJsonAsync("/api/v1/auth/otp/request", new { mobileNumber = "12345" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Family_members_can_share_a_mobile_number()
    {
        var mobile = TestApi.NextMobile();
        await _api.CreatePatientAsync("Rahul Patil", mobile);
        await _api.CreatePatientAsync("Priya Patil", mobile);

        var client = _api.Anonymous();
        await client.PostAsJsonAsync("/api/v1/auth/otp/request", new { mobileNumber = mobile });
        var verify = await (await client.PostAsJsonAsync("/api/v1/auth/otp/verify",
            new { mobileNumber = mobile, code = factory.Sms.LastCodeFor(mobile) })).JsonAsync();

        var names = verify.GetProperty("accounts").EnumerateArray().Select(a => a.GetProperty("fullName").GetString()).ToList();
        Assert.Equal(["Priya Patil", "Rahul Patil"], names.Order());
    }

    [Fact]
    public async Task Otp_locks_after_five_wrong_attempts()
    {
        var mobile = TestApi.NextMobile();
        var client = _api.Anonymous();
        await client.PostAsJsonAsync("/api/v1/auth/otp/request", new { mobileNumber = mobile });
        var correct = factory.Sms.LastCodeFor(mobile);
        var wrong = correct == "000000" ? "111111" : "000000";

        HttpResponseMessage last = null!;
        for (var i = 0; i < 5; i++)
            last = await client.PostAsJsonAsync("/api/v1/auth/otp/verify", new { mobileNumber = mobile, code = wrong });
        Assert.Equal(HttpStatusCode.TooManyRequests, last.StatusCode);

        var afterLock = await client.PostAsJsonAsync("/api/v1/auth/otp/verify", new { mobileNumber = mobile, code = correct });
        Assert.Equal(HttpStatusCode.TooManyRequests, afterLock.StatusCode);
    }

    [Fact]
    public async Task Otp_requests_are_limited_per_number_per_hour()
    {
        var mobile = TestApi.NextMobile();
        var client = _api.Anonymous();
        for (var i = 0; i < 5; i++)
            (await client.PostAsJsonAsync("/api/v1/auth/otp/request", new { mobileNumber = mobile })).EnsureSuccessStatusCode();

        var sixth = await client.PostAsJsonAsync("/api/v1/auth/otp/request", new { mobileNumber = mobile });
        Assert.Equal(HttpStatusCode.TooManyRequests, sixth.StatusCode);
    }

    [Fact]
    public async Task Otp_cannot_be_reused()
    {
        var mobile = TestApi.NextMobile();
        var client = _api.Anonymous();
        await client.PostAsJsonAsync("/api/v1/auth/otp/request", new { mobileNumber = mobile });
        var code = factory.Sms.LastCodeFor(mobile);

        (await client.PostAsJsonAsync("/api/v1/auth/otp/verify", new { mobileNumber = mobile, code })).EnsureSuccessStatusCode();
        var reuse = await client.PostAsJsonAsync("/api/v1/auth/otp/verify", new { mobileNumber = mobile, code });
        Assert.Equal(HttpStatusCode.BadRequest, reuse.StatusCode);
    }

    [Fact]
    public async Task Account_creation_requires_current_privacy_and_terms_consent()
    {
        var ticket = await _api.VerifyPhoneAsync(TestApi.NextMobile());
        var response = await _api.Anonymous().PostAsJsonAsync("/api/v1/auth/accounts", new
        {
            phoneTicket = ticket,
            fullName = "No Consent",
            privacyNoticeVersion = "PRIV-0.9",
            termsVersion = LegalTexts.TermsVersion,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await response.JsonAsync()).GetProperty("errors");
        Assert.True(errors.TryGetProperty("privacyNoticeVersion", out _));
    }

    [Fact]
    public async Task Forged_phone_ticket_is_rejected()
    {
        var response = await _api.Anonymous().PostAsJsonAsync("/api/v1/auth/session",
            new { phoneTicket = "not-a-token", userId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Me_requires_authentication()
    {
        var response = await _api.Anonymous().GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
