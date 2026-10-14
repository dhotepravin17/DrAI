using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using SmartQueue.Api.Domain;
using SmartQueue.Api.Infrastructure;
using SmartQueue.Api.Tests.Infrastructure;

namespace SmartQueue.Api.Tests;

[Collection(ApiCollection.Name)]
public class DoctorRegistrationTests(ApiFactory factory)
{
    private readonly TestApi _api = new(factory);

    [Fact]
    public async Task Submission_is_blocked_without_declaration()
    {
        var draft = await _api.CreateDraftDoctorAsync("NGP");

        var response = await TestApi.Submit(draft.Session.Client, accepted: false);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var status = await (await draft.Session.Client.GetAsync("/api/v1/doctor-registration")).JsonAsync();
        Assert.Equal("Draft", status.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Submission_is_blocked_for_outdated_declaration_version()
    {
        var draft = await _api.CreateDraftDoctorAsync("NGP");
        var response = await TestApi.Submit(draft.Session.Client, version: "DOC-DECL-0.1");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Submission_records_declaration_version_and_timestamp()
    {
        var draft = await _api.CreateDraftDoctorAsync("NGP");

        var response = await TestApi.Submit(draft.Session.Client);

        response.EnsureSuccessStatusCode();
        var body = await response.JsonAsync();
        Assert.Equal("PendingVerification", body.GetProperty("status").GetString());
        Assert.False(body.GetProperty("canAcceptBookings").GetBoolean());
        Assert.Equal(JsonValueKindNull, body.GetProperty("doctorCode").ValueKind);

        var declaration = await factory.WithDbAsync(db => db.DoctorDeclarations.SingleAsync(d => d.DoctorId == draft.DoctorId));
        Assert.True(declaration.Accepted);
        Assert.Equal(LegalTexts.DoctorDeclarationVersion, declaration.DeclarationVersion);
        Assert.Equal(body.GetProperty("submissionReference").GetString(), declaration.SubmissionReference);
        Assert.True(declaration.AcceptedAt > factory.Clock.GetUtcNow().AddMinutes(-5));
    }

    [Fact]
    public async Task Medical_registration_id_is_mandatory()
    {
        var session = await _api.CreatePatientAsync();
        var body = new
        {
            fullName = "Dr. No Number",
            specialty = "General Physician",
            medicalRegistrationId = "",
            medicalRegistrationAuthority = "Maharashtra Medical Council",
            primaryCityCode = "NGP",
            clinicName = "Clinic",
            clinicAddress = "Address",
        };

        var response = await session.Client.PutAsJsonAsync("/api/v1/doctor-registration", body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await response.JsonAsync()).GetProperty("errors").TryGetProperty("medicalRegistrationId", out _));
    }

    [Fact]
    public async Task Rejects_unknown_city_specialty_and_authority()
    {
        var session = await _api.CreatePatientAsync();
        var body = new
        {
            fullName = "Dr. Test",
            specialty = "Astrologer",
            medicalRegistrationId = "X1",
            medicalRegistrationAuthority = "Made Up Council",
            primaryCityCode = "ZZZ",
            clinicName = "Clinic",
            clinicAddress = "Address",
        };

        var response = await session.Client.PutAsJsonAsync("/api/v1/doctor-registration", body);

        var errors = (await response.JsonAsync()).GetProperty("errors");
        Assert.True(errors.TryGetProperty("specialty", out _));
        Assert.True(errors.TryGetProperty("medicalRegistrationAuthority", out _));
        Assert.True(errors.TryGetProperty("primaryCityCode", out _));
    }

    [Fact]
    public async Task Submitted_registration_cannot_be_edited()
    {
        var draft = await _api.CreateSubmittedDoctorAsync("NGP");
        var response = await draft.Session.Client.PutAsJsonAsync("/api/v1/doctor-registration", TestApi.RegistrationBody("NGP"));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Pending_doctor_is_not_searchable_and_cannot_accept_bookings()
    {
        var draft = await _api.CreateSubmittedDoctorAsync("NGP", "Dr. Pending Unique Kulkarni");

        var search = await (await _api.Anonymous().GetAsync("/api/v1/doctors/search?q=Pending Unique Kulkarni")).JsonAsync();

        Assert.Equal(0, search.GetProperty("total").GetInt32());
        var doctor = await factory.WithDbAsync(db => db.Doctors.SingleAsync(d => d.Id == draft.DoctorId));
        Assert.False(doctor.CanAcceptBookings);
    }

    [Fact]
    public async Task Duplicate_registration_number_is_flagged_for_review()
    {
        var first = await _api.CreateSubmittedDoctorAsync("NGP", "Dr. Original", "MMC-DUP-001");
        var second = await _api.CreateSubmittedDoctorAsync("NGP", "Dr. Copy", "MMC-DUP-001");

        var flags = await factory.WithDbAsync(db => db.Doctors
            .Where(d => d.Id == first.DoctorId || d.Id == second.DoctorId)
            .ToDictionaryAsync(d => d.Id, d => d.PossibleDuplicate));

        Assert.False(flags[first.DoctorId]);
        Assert.True(flags[second.DoctorId]);
    }

    [Fact]
    public async Task Registration_grants_doctor_role_and_shows_on_me()
    {
        var draft = await _api.CreateDraftDoctorAsync("NGP");

        var role = await factory.WithDbAsync(db => db.UserRoleAssignments
            .SingleAsync(r => r.UserId == draft.Session.UserId && r.Role == RoleName.Doctor));
        Assert.Equal(draft.DoctorId, role.DoctorId);

        var me = await (await draft.Session.Client.GetAsync("/api/v1/me")).JsonAsync();
        Assert.Equal("Draft", me.GetProperty("doctor").GetProperty("status").GetString());
    }

    private const System.Text.Json.JsonValueKind JsonValueKindNull = System.Text.Json.JsonValueKind.Null;
}
