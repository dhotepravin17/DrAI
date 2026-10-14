using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using SmartQueue.Api.Domain;
using SmartQueue.Api.Tests.Infrastructure;
using static SmartQueue.Api.Tests.Infrastructure.QueueTestExtensions;

namespace SmartQueue.Api.Tests;

[Collection(ApiCollection.Name)]
public class ClinicOperationsTests(ApiFactory factory)
{
    private readonly TestApi _api = new(factory);

    [Fact]
    public async Task Doctors_cannot_see_or_change_another_doctors_queue_or_history()
    {
        var doctorA = await _api.CreateClinicDoctorAsync();
        var doctorB = await _api.CreateClinicDoctorAsync();
        var booked = await (await _api.CreatePatientAsync("Private Patient")).BookOkAsync(doctorA.Code);
        var session = booked.GetProperty("sessionId").GetGuid();
        var id = booked.GetProperty("appointmentId").GetGuid();

        Assert.Equal(HttpStatusCode.NotFound, (await doctorB.Session.Client.GetAsync($"/api/v1/clinic/sessions/{session}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await doctorB.Session.Client.PostAsJsonAsync($"/api/v1/clinic/sessions/{session}/call-next", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await doctorB.Session.Client.PostAsJsonAsync($"/api/v1/clinic/appointments/{id}/cancel", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await doctorB.Session.Client.GetAsync($"/api/v1/clinic/appointments?doctorId={doctorA.DoctorId}")).StatusCode);

        var historyA = await (await doctorA.Session.Client.GetAsync("/api/v1/clinic/appointments?patientName=Private")).JsonAsync();
        var historyB = await (await doctorB.Session.Client.GetAsync("/api/v1/clinic/appointments?patientName=Private")).JsonAsync();
        Assert.Equal(1, historyA.GetProperty("total").GetInt32());
        Assert.Equal(0, historyB.GetProperty("total").GetInt32());

        var patient = await _api.CreatePatientAsync("Not Staff");
        Assert.Equal(HttpStatusCode.Forbidden, (await patient.Client.GetAsync("/api/v1/clinic/sessions")).StatusCode);
    }

    [Fact]
    public async Task Receptionist_runs_the_queue_but_cannot_change_the_schedule_and_loses_access_when_revoked()
    {
        var doctor = await _api.CreateClinicDoctorAsync();
        var mobile = TestApi.NextMobile();
        var invite = await doctor.Session.PostOkAsync("/api/v1/clinic/staff", new { mobileNumber = mobile, fullName = "Sunita Desk" });
        Assert.Single(invite.GetProperty("pending").EnumerateArray());

        var sunita = await _api.CreatePatientAsync("Sunita Desk", mobile);
        var received = await (await sunita.Client.GetAsync("/api/v1/me/staff-invitations")).JsonAsync();
        var invitationId = received[0].GetProperty("invitationId").GetGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await sunita.Client.PostAsJsonAsync($"/api/v1/me/staff-invitations/{invitationId}/accept", new { })).StatusCode);

        var contexts = await (await sunita.Client.GetAsync("/api/v1/clinic/contexts")).JsonAsync();
        Assert.Equal("Receptionist", contexts[0].GetProperty("role").GetString());

        var desk = new ClinicDoctor(sunita, doctor.DoctorId, doctor.Code);
        var session = await desk.SessionIdAsync();
        await desk.WalkInsAsync(session, "Desk Walk-in");
        var view = await sunita.PostOkAsync($"/api/v1/clinic/sessions/{session}/call-next");
        Assert.Equal("Desk Walk-in", view.GetProperty("nowServing").GetProperty("patientName").GetString());

        Assert.Equal(HttpStatusCode.Forbidden, (await sunita.Client.PutAsJsonAsync("/api/v1/clinic/schedule/sessions", Array.Empty<object>())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await sunita.Client.GetAsync("/api/v1/clinic/staff")).StatusCode);

        var staff = await (await doctor.Session.Client.GetAsync("/api/v1/clinic/staff")).JsonAsync();
        var assignmentId = staff.GetProperty("members")[0].GetProperty("assignmentId").GetInt64();
        Assert.Equal(HttpStatusCode.OK, (await doctor.Session.Client.DeleteAsync($"/api/v1/clinic/staff/{assignmentId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await sunita.Client.GetAsync($"/api/v1/clinic/sessions/{session}")).StatusCode);
    }

    [Fact]
    public async Task Walk_in_lookup_returns_only_names_for_a_mobile_number()
    {
        var doctor = await _api.CreateClinicDoctorAsync();
        var mobile = TestApi.NextMobile();
        var parent = await _api.CreatePatientAsync("Lookup Parent", mobile);
        await parent.Client.PostAsJsonAsync("/api/v1/me/family", new { fullName = "Lookup Kid", relationship = "Son", dateOfBirth = "2018-01-01", guardianConsent = true });

        var response = await doctor.Session.Client.GetAsync($"/api/v1/clinic/patients/lookup?mobile={mobile}");
        var text = await response.Content.ReadAsStringAsync();
        var matches = await response.JsonAsync();

        Assert.Equal(["Lookup Kid", "Lookup Parent"], matches.EnumerateArray().Select(m => m.GetProperty("fullName").GetString()));
        Assert.DoesNotContain(mobile, text);

        var kid = matches[0].GetProperty("patientId").GetGuid();
        var session = await doctor.SessionIdAsync();
        var view = await doctor.Session.PostOkAsync($"/api/v1/clinic/sessions/{session}/walk-ins", new { patientId = kid });
        Assert.Equal("Lookup Kid", view.GetProperty("waiting")[0].GetProperty("patientName").GetString());

        await factory.DispatchNotificationsAsync();
        Assert.Contains(await parent.NotificationsAsync(), n => n.GetProperty("eventType").GetString() == "AppointmentConfirmed");
    }

    [Fact]
    public async Task Holiday_announcement_cancels_affected_bookings_and_notifies_patients()
    {
        var doctor = await _api.CreateClinicDoctorAsync();
        var patient = await _api.CreatePatientAsync("Holiday Patient");
        var tomorrow = Today.AddDays(1);
        var booked = await patient.BookOkAsync(doctor.Code, date: tomorrow);

        var impact = await (await doctor.Session.Client.GetAsync($"/api/v1/clinic/schedule/impact?startDate={tomorrow:yyyy-MM-dd}")).JsonAsync();
        Assert.Equal(1, impact.GetProperty("activeAppointments").GetInt32());

        var start = new DateTimeOffset(tomorrow.ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(5.5));
        var created = await doctor.Session.PostOkAsync("/api/v1/clinic/announcements", new
        {
            type = "Holiday", message = "Clinic closed for Dussehra", startsAt = start, endsAt = start.AddHours(23),
        });

        var status = await (await patient.Client.GetAsync($"/api/v1/appointments/{booked.GetProperty("appointmentId").GetGuid()}")).JsonAsync();
        Assert.Equal("Cancelled", status.GetProperty("status").GetString());

        await factory.DispatchNotificationsAsync();
        var cancelled = Assert.Single(await patient.NotificationsAsync(), n => n.GetProperty("eventType").GetString() == "AppointmentCancelled");
        Assert.Contains("closed", cancelled.GetProperty("body").GetString());

        var availability = await (await _api.Anonymous().GetAsync($"/api/v1/doctors/{doctor.Code}/availability")).JsonAsync();
        var day = availability.GetProperty("days").EnumerateArray().Single(d => d.GetProperty("date").GetString() == tomorrow.ToString("yyyy-MM-dd"));
        Assert.All(day.GetProperty("sessions").EnumerateArray(), s => Assert.False(s.GetProperty("bookable").GetBoolean()));
        Assert.Contains(availability.GetProperty("notices").EnumerateArray(), n => n.GetProperty("message").GetString() == "Clinic closed for Dussehra");

        // Removing the announcement reopens the date for new bookings; the cancelled booking stays cancelled.
        Assert.Equal(HttpStatusCode.NoContent,
            (await doctor.Session.Client.DeleteAsync($"/api/v1/clinic/announcements/{created.GetProperty("id").GetGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await patient.BookAsync(doctor.Code, await patient.SelfPatientIdAsync(), tomorrow)).StatusCode);
    }

    [Fact]
    public async Task Delay_announcement_shifts_estimates_and_notifies_waiting_patients()
    {
        var doctor = await _api.CreateClinicDoctorAsync();
        var patient = await _api.CreatePatientAsync("Delay Patient");
        var booked = await patient.BookOkAsync(doctor.Code, sessionName: "Evening");
        var before = booked.GetProperty("estimateMinMinutes").GetInt32();

        await doctor.Session.PostOkAsync("/api/v1/clinic/announcements", new
        {
            type = "DelayedOpening", message = "Running late after surgery", startsAt = factory.Clock.GetUtcNow(),
            endsAt = factory.Clock.GetUtcNow().AddHours(12), delayMinutes = 45,
        });

        var after = await (await patient.Client.GetAsync($"/api/v1/appointments/{booked.GetProperty("appointmentId").GetGuid()}")).JsonAsync();
        Assert.True(after.GetProperty("estimateMinMinutes").GetInt32() >= before + 40);
        await factory.DispatchNotificationsAsync();
        Assert.Contains(await patient.NotificationsAsync(), n => n.GetProperty("eventType").GetString() == "QueueDelay");
    }

    [Fact]
    public async Task Special_hours_change_a_booked_session_and_template_rules_are_validated()
    {
        var doctor = await _api.CreateClinicDoctorAsync();
        var patient = await _api.CreatePatientAsync("Hours Patient");
        var tomorrow = Today.AddDays(1);
        await patient.BookOkAsync(doctor.Code, date: tomorrow);

        await doctor.Session.PostOkAsync("/api/v1/clinic/schedule/exceptions", new
        {
            kind = "SpecialHours", startDate = tomorrow.ToString("yyyy-MM-dd"), sessionName = "Morning",
            startTime = "10:00", endTime = "12:00", capacity = 20, reason = "Conference",
        });

        var session = await factory.WithDbAsync(db => db.QueueSessions.SingleAsync(s => s.DoctorId == doctor.DoctorId && s.Date == tomorrow && s.Name == "Morning"));
        Assert.Equal(new TimeSpan(4, 30, 0), session.StartsAt.UtcDateTime.TimeOfDay);
        Assert.Equal(20, session.Capacity);
        await factory.DispatchNotificationsAsync();
        Assert.Contains(await patient.NotificationsAsync(), n => n.GetProperty("eventType").GetString() == "ScheduleChanged");

        var overlapping = await doctor.Session.Client.PutAsJsonAsync("/api/v1/clinic/schedule/sessions", new[]
        {
            new { dayOfWeek = "Monday", name = "Morning", startTime = "09:00", endTime = "13:00", capacity = 10 },
            new { dayOfWeek = "Monday", name = "Late", startTime = "12:00", endTime = "15:00", capacity = 10 },
        });
        Assert.Equal(HttpStatusCode.BadRequest, overlapping.StatusCode);

        var settings = await doctor.Session.Client.PutAsJsonAsync("/api/v1/clinic/settings", new
        {
            bookingWindowDays = 3, avgConsultMinutes = 10, gracePeriodMinutes = 2, requeueAfterCompletions = 3,
            secondMissRule = "NoShow", turnApproachingThreshold = 2,
        });
        Assert.Equal(HttpStatusCode.OK, settings.StatusCode);
        var tooFar = await patient.BookAsync(doctor.Code, await patient.SelfPatientIdAsync(), Today.AddDays(5));
        Assert.Equal(HttpStatusCode.BadRequest, tooFar.StatusCode);
    }

    [Fact]
    public async Task Every_queue_action_is_recorded_with_its_actor()
    {
        var doctor = await _api.CreateClinicDoctorAsync();
        var session = await doctor.SessionIdAsync();
        var ids = await doctor.WalkInsAsync(session, "Audit Me");
        await doctor.CallNextAndCompleteAsync(session);

        var events = await factory.WithDbAsync(db => db.AppointmentEvents.Where(e => e.AppointmentId == ids["Audit Me"]).OrderBy(e => e.Id).ToListAsync());
        Assert.Equal(["Booked", "Called", "Completed"], events.Select(e => e.Type));
        Assert.All(events, e => Assert.Equal(doctor.Session.UserId, e.ActorUserId));
        Assert.Equal(AppointmentStatus.Completed, events[^1].ToStatus);
    }
}
