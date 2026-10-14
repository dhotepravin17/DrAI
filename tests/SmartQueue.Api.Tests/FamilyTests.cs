using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SmartQueue.Api.Tests.Infrastructure;
using static SmartQueue.Api.Tests.Infrastructure.QueueTestExtensions;

namespace SmartQueue.Api.Tests;

[Collection(ApiCollection.Name)]
public class FamilyTests(ApiFactory factory)
{
    private readonly TestApi _api = new(factory);

    private static async Task<Guid> AddFamilyAsync(Session session, string name, string relationship, string? dob, bool consent = true)
    {
        var response = await session.Client.PostAsJsonAsync("/api/v1/me/family",
            new { fullName = name, relationship, dateOfBirth = dob, guardianConsent = consent });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.JsonAsync()).GetProperty("patientId").GetGuid();
    }

    private static async Task<List<JsonElement>> VisitsAsync(Session session) =>
        (await (await session.Client.GetAsync("/api/v1/me/visits")).JsonAsync()).GetProperty("items").EnumerateArray().ToList();

    [Fact]
    public async Task Parent_books_for_a_child_and_the_doctor_sees_the_child()
    {
        var doctor = await _api.CreateClinicDoctorAsync();
        var rahul = await _api.CreatePatientAsync("Rahul Kale");

        var withoutConsent = await rahul.Client.PostAsJsonAsync("/api/v1/me/family",
            new { fullName = "Ananya Kale", relationship = "Daughter", dateOfBirth = "2019-03-02", guardianConsent = false });
        Assert.Equal(HttpStatusCode.BadRequest, withoutConsent.StatusCode);

        var ananya = await AddFamilyAsync(rahul, "Ananya Kale", "Daughter", "2019-03-02");
        var aarav = await AddFamilyAsync(rahul, "Aarav Kale", "Son", "2016-07-11");
        var booked = await rahul.BookOkAsync(doctor.Code, ananya);
        await rahul.BookOkAsync(doctor.Code, aarav);

        Assert.Equal("Ananya Kale", booked.GetProperty("patientName").GetString());
        var view = await doctor.ViewAsync(booked.GetProperty("sessionId").GetGuid());
        var entry = Entry(view, booked.GetProperty("appointmentId").GetGuid());
        Assert.Equal("Ananya Kale", entry.GetProperty("patientName").GetString());
        Assert.Equal("Rahul Kale", entry.GetProperty("bookedByName").GetString());
        Assert.DoesNotContain(AllEntries(view), e => e.GetProperty("patientName").GetString() == "Rahul Kale");

        var visits = await VisitsAsync(rahul);
        Assert.Equal(["Aarav Kale", "Ananya Kale"], visits.Select(v => v.GetProperty("patientName").GetString()).Order());

        var people = await (await rahul.Client.GetAsync("/api/v1/me/people")).JsonAsync();
        Assert.Equal(3, people.GetArrayLength());
        Assert.Equal("Self", people[0].GetProperty("kind").GetString());
    }

    [Fact]
    public async Task Unrelated_accounts_cannot_book_for_see_or_cancel_someone_elses_family()
    {
        var doctor = await _api.CreateClinicDoctorAsync();
        var parent = await _api.CreatePatientAsync("Parent One");
        var child = await AddFamilyAsync(parent, "Child One", "Son", "2020-01-01");
        var booked = await parent.BookOkAsync(doctor.Code, child);
        var id = booked.GetProperty("appointmentId").GetGuid();

        var stranger = await _api.CreatePatientAsync("Stranger");
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.BookAsync(doctor.Code, child)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.Client.GetAsync($"/api/v1/appointments/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.Client.PostAsJsonAsync($"/api/v1/appointments/{id}/cancel", new { })).StatusCode);
        Assert.Empty(await VisitsAsync(stranger));
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.Client.PutAsJsonAsync($"/api/v1/me/family/{child}",
            new { fullName = "Renamed", relationship = "Son" })).StatusCode);
    }

    [Fact]
    public async Task Booking_for_an_adult_does_not_reveal_their_other_visits_until_they_link()
    {
        var doctor = await _api.CreateClinicDoctorAsync();
        var rahul = await _api.CreatePatientAsync("Rahul Adult");
        var priyaProfile = await AddFamilyAsync(rahul, "Priya Adult", "Wife", "1990-05-05");
        await rahul.BookOkAsync(doctor.Code, priyaProfile);

        var priyaMobile = TestApi.NextMobile();
        var priya = await _api.CreatePatientAsync("Priya Adult", priyaMobile);
        var priyaOwn = await priya.BookOkAsync(doctor.Code, sessionName: "Evening");
        var ownId = priyaOwn.GetProperty("appointmentId").GetGuid();

        var visible = await VisitsAsync(rahul);
        Assert.Single(visible);
        Assert.Equal(HttpStatusCode.NotFound, (await rahul.Client.GetAsync($"/api/v1/appointments/{ownId}")).StatusCode);

        var invite = await rahul.Client.PostAsJsonAsync("/api/v1/me/links", new { mobileNumber = priyaMobile, relationship = "Wife" });
        Assert.Equal(HttpStatusCode.Created, invite.StatusCode);
        var linkId = (await invite.JsonAsync()).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NotFound, (await rahul.Client.PostAsJsonAsync($"/api/v1/me/links/{linkId}/accept", new { })).StatusCode);

        var received = await (await priya.Client.GetAsync("/api/v1/me/links")).JsonAsync();
        Assert.Equal("Rahul Adult", received.GetProperty("received")[0].GetProperty("otherPartyName").GetString());
        await priya.PostOkAsync($"/api/v1/me/links/{linkId}/accept");

        Assert.Equal(2, (await VisitsAsync(rahul)).Count);
        Assert.Equal(HttpStatusCode.OK, (await rahul.Client.GetAsync($"/api/v1/appointments/{ownId}")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await priya.Client.DeleteAsync($"/api/v1/me/links/{linkId}")).StatusCode);
        Assert.Single(await VisitsAsync(rahul));
    }

    [Fact]
    public async Task Family_list_is_limited_and_profiles_with_upcoming_visits_cannot_be_removed()
    {
        var doctor = await _api.CreateClinicDoctorAsync();
        var holder = await _api.CreatePatientAsync("Big Family");
        var ids = new List<Guid>();
        for (var i = 0; i < 6; i++) ids.Add(await AddFamilyAsync(holder, $"Member {i}", "Other", null));
        var seventh = await holder.Client.PostAsJsonAsync("/api/v1/me/family", new { fullName = "Member 7", relationship = "Other" });
        Assert.Equal(HttpStatusCode.Conflict, seventh.StatusCode);

        await holder.BookOkAsync(doctor.Code, ids[0]);
        Assert.Equal(HttpStatusCode.Conflict, (await holder.Client.DeleteAsync($"/api/v1/me/family/{ids[0]}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await holder.Client.DeleteAsync($"/api/v1/me/family/{ids[1]}")).StatusCode);
        Assert.Equal(6, (await (await holder.Client.GetAsync("/api/v1/me/people")).JsonAsync()).GetArrayLength());
    }

    [Fact]
    public async Task Completed_visits_appear_in_the_right_persons_history_with_follow_up()
    {
        var doctor = await _api.CreateClinicDoctorAsync();
        var parent = await _api.CreatePatientAsync("History Parent");
        var child = await AddFamilyAsync(parent, "History Child", "Daughter", "2021-02-02");
        var booked = await parent.BookOkAsync(doctor.Code, child);
        var session = booked.GetProperty("sessionId").GetGuid();
        var id = booked.GetProperty("appointmentId").GetGuid();

        await doctor.Session.PostOkAsync($"/api/v1/clinic/sessions/{session}/call-next");
        await doctor.ActAsync(id, "complete", new { followUpNote = "Review in one week" });

        var visit = Assert.Single(await VisitsAsync(parent));
        Assert.Equal("History Child", visit.GetProperty("patientName").GetString());
        Assert.Equal("Completed", visit.GetProperty("status").GetString());
        Assert.Equal("Review in one week", visit.GetProperty("followUpNote").GetString());

        var filtered = await (await parent.Client.GetAsync($"/api/v1/me/visits?patientId={await parent.SelfPatientIdAsync()}")).JsonAsync();
        Assert.Equal(0, filtered.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Export_contains_the_accounts_own_data()
    {
        var doctor = await _api.CreateClinicDoctorAsync();
        var holder = await _api.CreatePatientAsync("Export Me");
        await AddFamilyAsync(holder, "Export Child", "Son", "2022-01-01");
        await holder.BookOkAsync(doctor.Code);

        var export = await (await holder.Client.GetAsync("/api/v1/me/export")).JsonAsync();

        Assert.Equal("Export Me", export.GetProperty("account").GetProperty("fullName").GetString());
        Assert.Equal(2, export.GetProperty("people").GetArrayLength());
        Assert.Equal(1, export.GetProperty("appointments").GetArrayLength());
        Assert.Equal(2, export.GetProperty("consents").GetArrayLength());
    }
}
