using System.Net;
using System.Net.Http.Json;
using SmartQueue.Api.Tests.Infrastructure;

namespace SmartQueue.Api.Tests;

[Collection(ApiCollection.Name)]
public class DoctorVerificationTests(ApiFactory factory)
{
    private readonly TestApi _api = new(factory);

    [Fact]
    public async Task Verified_doctors_receive_sequential_city_codes()
    {
        var admin = await _api.LoginSuperAdminAsync();
        var city = await _api.CreateCityAsync(admin);
        var first = await _api.CreateSubmittedDoctorAsync(city);
        var second = await _api.CreateSubmittedDoctorAsync(city);

        Assert.Equal($"{city}-Dr-0001", await TestApi.VerifyAsync(admin, first.DoctorId));
        Assert.Equal($"{city}-Dr-0002", await TestApi.VerifyAsync(admin, second.DoctorId));
    }

    [Fact]
    public async Task Rejected_doctor_does_not_consume_a_doctor_id()
    {
        var admin = await _api.LoginSuperAdminAsync();
        var city = await _api.CreateCityAsync(admin);
        var rejected = await _api.CreateSubmittedDoctorAsync(city);
        var accepted = await _api.CreateSubmittedDoctorAsync(city);

        var reject = await admin.Client.PostAsJsonAsync($"/api/v1/admin/doctors/{rejected.DoctorId}/reject",
            new { reason = "DetailsMismatch", note = "Name does not match register" });
        reject.EnsureSuccessStatusCode();
        Assert.Equal($"{city}-Dr-0001", await TestApi.VerifyAsync(admin, accepted.DoctorId));

        // The rejected doctor can correct details and resubmit, then gets the next number.
        var status = await (await rejected.Session.Client.GetAsync("/api/v1/doctor-registration")).JsonAsync();
        Assert.Equal("Rejected", status.GetProperty("status").GetString());
        Assert.Equal("DetailsMismatch", status.GetProperty("rejectionReason").GetString());
        (await rejected.Session.Client.PutAsJsonAsync("/api/v1/doctor-registration", TestApi.RegistrationBody(city))).EnsureSuccessStatusCode();
        (await TestApi.Submit(rejected.Session.Client)).EnsureSuccessStatusCode();
        Assert.Equal($"{city}-Dr-0002", await TestApi.VerifyAsync(admin, rejected.DoctorId));
    }

    [Fact]
    public async Task Concurrent_verifications_never_issue_duplicate_codes()
    {
        var admin = await _api.LoginSuperAdminAsync();
        var city = await _api.CreateCityAsync(admin);
        var doctors = new List<DoctorDraft>();
        for (var i = 0; i < 20; i++) doctors.Add(await _api.CreateSubmittedDoctorAsync(city, $"Dr. Parallel {i}"));

        var codes = await Task.WhenAll(doctors.Select(d => TestApi.VerifyAsync(admin, d.DoctorId)));

        var expected = Enumerable.Range(1, 20).Select(n => $"{city}-Dr-{n:D4}").ToHashSet();
        Assert.True(expected.SetEquals(codes));
    }

    [Fact]
    public async Task Verified_doctor_is_searchable_by_code_name_and_city_without_private_details()
    {
        var admin = await _api.LoginSuperAdminAsync();
        var city = await _api.CreateCityAsync(admin);
        var doctor = await _api.CreateSubmittedDoctorAsync(city, "Dr. Searchable Deshmukh", "MMC-PRIVATE-42");
        var code = await TestApi.VerifyAsync(admin, doctor.DoctorId);
        var anon = _api.Anonymous();

        var byCode = await (await anon.GetAsync($"/api/v1/doctors/search?q={code.ToLowerInvariant()}")).JsonAsync();
        Assert.Equal(code, byCode.GetProperty("items")[0].GetProperty("doctorCode").GetString());

        var byName = await (await anon.GetAsync("/api/v1/doctors/search?q=dr. searchable")).JsonAsync();
        Assert.Contains(byName.GetProperty("items").EnumerateArray(), i => i.GetProperty("doctorCode").GetString() == code);

        var byCity = await (await anon.GetAsync($"/api/v1/doctors/search?city={city}")).JsonAsync();
        Assert.Equal(1, byCity.GetProperty("total").GetInt32());

        var profileResponse = await anon.GetAsync($"/api/v1/doctors/{code}");
        var profileJson = await profileResponse.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, profileResponse.StatusCode);
        Assert.Contains("Sharma Clinic", profileJson);
        Assert.DoesNotContain("MMC-PRIVATE-42", profileJson);
        Assert.DoesNotContain("medicalRegistration", profileJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("MMC-PRIVATE-42", await (await anon.GetAsync($"/api/v1/doctors/search?q={code}")).Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Suspended_doctor_leaves_search_and_keeps_code_when_reinstated()
    {
        var admin = await _api.LoginSuperAdminAsync();
        var city = await _api.CreateCityAsync(admin);
        var doctor = await _api.CreateSubmittedDoctorAsync(city);
        var code = await TestApi.VerifyAsync(admin, doctor.DoctorId);

        (await admin.Client.PostAsJsonAsync($"/api/v1/admin/doctors/{doctor.DoctorId}/suspend", new { note = "Complaint under review" })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound, (await _api.Anonymous().GetAsync($"/api/v1/doctors/{code}")).StatusCode);

        var reinstate = await (await admin.Client.PostAsJsonAsync($"/api/v1/admin/doctors/{doctor.DoctorId}/reinstate", new { })).JsonAsync();
        Assert.Equal(code, reinstate.GetProperty("doctorCode").GetString());
        Assert.Equal(HttpStatusCode.OK, (await _api.Anonymous().GetAsync($"/api/v1/doctors/{code}")).StatusCode);

        (await admin.Client.PostAsJsonAsync($"/api/v1/admin/doctors/{doctor.DoctorId}/deactivate", new { note = "Left platform" })).EnsureSuccessStatusCode();
        var reactivate = await admin.Client.PostAsJsonAsync($"/api/v1/admin/doctors/{doctor.DoctorId}/reinstate", new { });
        Assert.Equal(HttpStatusCode.Conflict, reactivate.StatusCode);
    }

    [Fact]
    public async Task Suspension_requires_a_note()
    {
        var admin = await _api.LoginSuperAdminAsync();
        var doctor = await _api.CreateSubmittedDoctorAsync("NGP");
        await TestApi.VerifyAsync(admin, doctor.DoctorId);

        var response = await admin.Client.PostAsJsonAsync($"/api/v1/admin/doctors/{doctor.DoctorId}/suspend", new { note = "" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Draft_cannot_be_verified()
    {
        var admin = await _api.LoginSuperAdminAsync();
        var draft = await _api.CreateDraftDoctorAsync("NGP");

        var response = await admin.Client.PostAsJsonAsync($"/api/v1/admin/doctors/{draft.DoctorId}/verify", new { });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Stale_row_version_is_rejected()
    {
        var admin = await _api.LoginSuperAdminAsync();
        var doctor = await _api.CreateSubmittedDoctorAsync("NGP");
        var detail = await (await admin.Client.GetAsync($"/api/v1/admin/doctors/{doctor.DoctorId}")).JsonAsync();
        var rowVersion = detail.GetProperty("rowVersion").GetBytesFromBase64();

        // Another admin rejects and the doctor resubmits, so the doctor is pending again but the row has changed.
        (await admin.Client.PostAsJsonAsync($"/api/v1/admin/doctors/{doctor.DoctorId}/reject", new { reason = "Other", note = "First decision" })).EnsureSuccessStatusCode();
        (await TestApi.Submit(doctor.Session.Client)).EnsureSuccessStatusCode();

        var stale = await admin.Client.PostAsJsonAsync($"/api/v1/admin/doctors/{doctor.DoctorId}/verify", new { rowVersion });

        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var current = await (await admin.Client.GetAsync($"/api/v1/admin/doctors/{doctor.DoctorId}")).JsonAsync();
        Assert.Equal("PendingVerification", current.GetProperty("status").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, current.GetProperty("doctorCode").ValueKind);
    }

    [Fact]
    public async Task Admin_actions_and_record_views_are_audited()
    {
        var admin = await _api.LoginSuperAdminAsync();
        var doctor = await _api.CreateSubmittedDoctorAsync("NGP");

        var detail = await (await admin.Client.GetAsync($"/api/v1/admin/doctors/{doctor.DoctorId}")).JsonAsync();
        Assert.Equal(doctor.MedicalRegistrationId, detail.GetProperty("medicalRegistrationId").GetString());
        Assert.Equal(1, detail.GetProperty("declarations").GetArrayLength());
        await TestApi.VerifyAsync(admin, doctor.DoctorId);

        var logs = await (await admin.Client.GetAsync($"/api/v1/admin/audit-logs?entityType=Doctor&entityId={doctor.DoctorId}")).JsonAsync();
        var actions = logs.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("action").GetString()).ToList();
        Assert.Equal(["VerifyDoctor", "ViewDoctorRegistration"], actions);
        Assert.All(logs.GetProperty("items").EnumerateArray(),
            i => Assert.Equal(admin.UserId, i.GetProperty("actorUserId").GetGuid()));
    }

    [Fact]
    public async Task Non_admins_cannot_use_admin_endpoints()
    {
        var patient = await _api.CreatePatientAsync();
        var doctor = await _api.CreateSubmittedDoctorAsync("NGP");

        Assert.Equal(HttpStatusCode.Forbidden, (await patient.Client.GetAsync("/api/v1/admin/doctors")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await doctor.Session.Client.PostAsJsonAsync($"/api/v1/admin/doctors/{doctor.DoctorId}/verify", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _api.Anonymous().GetAsync("/api/v1/admin/doctors")).StatusCode);
    }

    [Fact]
    public async Task Admin_cannot_create_duplicate_or_malformed_city_codes()
    {
        var admin = await _api.LoginSuperAdminAsync();

        var duplicate = await admin.Client.PostAsJsonAsync("/api/v1/admin/cities", new { code = "NGP", name = "Nagpur Again", state = "Maharashtra" });
        var malformed = await admin.Client.PostAsJsonAsync("/api/v1/admin/cities", new { code = "NG1", name = "Bad", state = "Maharashtra" });

        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
    }
}
