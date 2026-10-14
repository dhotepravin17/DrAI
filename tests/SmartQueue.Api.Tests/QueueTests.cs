using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using SmartQueue.Api.Domain;
using SmartQueue.Api.Tests.Infrastructure;
using static SmartQueue.Api.Tests.Infrastructure.QueueTestExtensions;

namespace SmartQueue.Api.Tests;

[Collection(ApiCollection.Name)]
public class QueueTests(ApiFactory factory)
{
    private readonly TestApi _api = new(factory);

    [Fact]
    public async Task Patient_books_for_self_and_sees_token_position_and_estimate()
    {
        var doctor = await _api.CreateClinicDoctorAsync();
        var first = await (await _api.CreatePatientAsync("First Patient")).BookOkAsync(doctor.Code);
        var second = await (await _api.CreatePatientAsync("Second Patient")).BookOkAsync(doctor.Code);

        Assert.Equal("T-001", first.GetProperty("token").GetString());
        Assert.Equal("T-002", second.GetProperty("token").GetString());
        Assert.Equal(1, second.GetProperty("patientsAhead").GetInt32());
        Assert.Equal("Second Patient", second.GetProperty("patientName").GetString());
        Assert.StartsWith("APT-2026-", second.GetProperty("reference").GetString());
        Assert.True(second.GetProperty("estimateMaxMinutes").GetInt32() > second.GetProperty("estimateMinMinutes").GetInt32());
        Assert.DoesNotContain("First Patient", second.GetRawText());
    }

    [Fact]
    public async Task Concurrent_bookings_never_share_a_token()
    {
        var doctor = await _api.CreateClinicDoctorAsync();
        var patients = new List<Session>();
        for (var i = 0; i < 20; i++) patients.Add(await _api.CreatePatientAsync($"Parallel {i}"));
        var ids = await Task.WhenAll(patients.Select(p => p.SelfPatientIdAsync()));

        var responses = await Task.WhenAll(patients.Select((p, i) => p.BookAsync(doctor.Code, ids[i])));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        var tokens = await Task.WhenAll(responses.Select(async r => (await r.JsonAsync()).GetProperty("token").GetString()));
        Assert.Equal(Enumerable.Range(1, 20).Select(n => $"T-{n:D3}").ToHashSet(), tokens.ToHashSet());
    }

    [Fact]
    public async Task Online_booking_stops_at_capacity_even_under_concurrency()
    {
        var doctor = await _api.CreateClinicDoctorAsync(eveningCapacity: 3);
        var patients = new List<Session>();
        for (var i = 0; i < 6; i++) patients.Add(await _api.CreatePatientAsync($"Capacity {i}"));
        var ids = await Task.WhenAll(patients.Select(p => p.SelfPatientIdAsync()));

        var responses = await Task.WhenAll(patients.Select((p, i) => p.BookAsync(doctor.Code, ids[i], sessionName: "Evening")));

        Assert.Equal(3, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(3, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));

        // Staff can still add a walk-in beyond online capacity.
        var evening = await doctor.SessionIdAsync("Evening");
        await doctor.WalkInsAsync(evening, "Over Capacity Walk-in");
        Assert.Equal(4, (await doctor.ViewAsync(evening)).GetProperty("session").GetProperty("booked").GetInt32());
    }

    [Fact]
    public async Task Repeated_requests_with_the_same_key_create_one_appointment()
    {
        var doctor = await _api.CreateClinicDoctorAsync();
        var patient = await _api.CreatePatientAsync();
        var patientId = await patient.SelfPatientIdAsync();
        var key = Guid.NewGuid().ToString();

        var responses = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => patient.BookAsync(doctor.Code, patientId, key: key)));

        Assert.All(responses, r => Assert.True(r.IsSuccessStatusCode, r.StatusCode.ToString()));
        var ids = await Task.WhenAll(responses.Select(async r => (await r.JsonAsync()).GetProperty("appointmentId").GetGuid()));
        Assert.Single(ids.Distinct());
        Assert.Equal(1, await factory.WithDbAsync(db => db.Appointments.CountAsync(a => a.PatientId == patientId)));

        var duplicate = await patient.BookAsync(doctor.Code, patientId);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
    }

    [Fact]
    public async Task Booking_requires_an_idempotency_key_and_a_date_in_the_window()
    {
        var doctor = await _api.CreateClinicDoctorAsync();
        var patient = await _api.CreatePatientAsync();
        var patientId = await patient.SelfPatientIdAsync();

        var noKey = await patient.Client.PostAsJsonAsync("/api/v1/appointments",
            new { doctorCode = doctor.Code, date = "2026-10-14", sessionName = "Morning", patientId });
        Assert.Equal(HttpStatusCode.BadRequest, noKey.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await patient.BookAsync(doctor.Code, patientId, Today.AddDays(-1))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await patient.BookAsync(doctor.Code, patientId, Today.AddDays(8))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await patient.BookAsync(doctor.Code, patientId, sessionName: "Night")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await patient.BookAsync(doctor.Code, patientId, Today.AddDays(7))).StatusCode);
    }

    [Fact]
    public async Task Cancelled_tokens_are_not_reused()
    {
        var doctor = await _api.CreateClinicDoctorAsync();
        var first = await _api.CreatePatientAsync("Cancels");
        var booked = await first.BookOkAsync(doctor.Code);

        var cancelled = await first.PostOkAsync($"/api/v1/appointments/{booked.GetProperty("appointmentId").GetGuid()}/cancel");
        var next = await (await _api.CreatePatientAsync("Next")).BookOkAsync(doctor.Code);

        Assert.Equal("Cancelled", cancelled.GetProperty("status").GetString());
        Assert.Equal("T-002", next.GetProperty("token").GetString());
        Assert.Equal(0, next.GetProperty("patientsAhead").GetInt32());
    }

    [Fact]
    public async Task Missed_patient_is_requeued_after_five_other_consultations()
    {
        var doctor = await _api.CreateClinicDoctorAsync();
        var session = await doctor.SessionIdAsync();
        var ids = await doctor.WalkInsAsync(session, "Asha", "Bina", "Chetan", "Deepa", "Esha", "Farhan", "Gita");

        await doctor.Session.PostOkAsync($"/api/v1/clinic/sessions/{session}/call-next");
        var early = await doctor.Session.Client.PostAsJsonAsync($"/api/v1/clinic/appointments/{ids["Asha"]}/miss", new { });
        Assert.Equal(HttpStatusCode.Conflict, early.StatusCode);

        factory.Clock.Advance(TimeSpan.FromMinutes(4));
        var view = await doctor.ActAsync(ids["Asha"], "miss");
        Assert.Equal("Missed", Entry(view, ids["Asha"]).GetProperty("status").GetString());
        Assert.Equal(5, Entry(view, ids["Asha"]).GetProperty("requeueAfterCompletions").GetInt32());

        for (var i = 0; i < 4; i++) view = await doctor.CallNextAndCompleteAsync(session);
        Assert.Equal("Missed", Entry(view, ids["Asha"]).GetProperty("status").GetString());
        Assert.Equal(1, Entry(view, ids["Asha"]).GetProperty("requeueAfterCompletions").GetInt32());

        view = await doctor.CallNextAndCompleteAsync(session);
        Assert.Equal("Requeued", Entry(view, ids["Asha"]).GetProperty("status").GetString());
        Assert.Equal(["Gita", "Asha"], WaitingNames(view));
    }

    [Fact]
    public async Task Requeue_counting_survives_a_server_restart()
    {
        var doctor = await _api.CreateClinicDoctorAsync();
        var session = await doctor.SessionIdAsync();
        var ids = await doctor.WalkInsAsync(session, "R1", "R2", "R3", "R4", "R5", "R6", "R7");
        await doctor.Session.PostOkAsync($"/api/v1/clinic/sessions/{session}/call-next");
        factory.Clock.Advance(TimeSpan.FromMinutes(4));
        await doctor.ActAsync(ids["R1"], "miss");
        await doctor.CallNextAndCompleteAsync(session);
        await doctor.CallNextAndCompleteAsync(session);

        await using var restarted = factory.WithWebHostBuilder(_ => { });
        var client = restarted.CreateClient();
        client.DefaultRequestHeaders.Authorization = doctor.Session.Client.DefaultRequestHeaders.Authorization;
        var afterRestart = new ClinicDoctor(doctor.Session with { Client = client }, doctor.DoctorId, doctor.Code);

        var view = await afterRestart.ViewAsync(session);
        Assert.Equal(3, Entry(view, ids["R1"]).GetProperty("requeueAfterCompletions").GetInt32());
        for (var i = 0; i < 3; i++) view = await afterRestart.CallNextAndCompleteAsync(session);
        Assert.Equal("Requeued", Entry(view, ids["R1"]).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Missed_patient_is_requeued_at_once_when_nobody_else_is_waiting_and_second_miss_is_no_show()
    {
        var doctor = await _api.CreateClinicDoctorAsync();
        var session = await doctor.SessionIdAsync();
        var ids = await doctor.WalkInsAsync(session, "Only One", "Two");

        await doctor.Session.PostOkAsync($"/api/v1/clinic/sessions/{session}/call-next");
        factory.Clock.Advance(TimeSpan.FromMinutes(4));
        var view = await doctor.ActAsync(ids["Only One"], "miss");
        Assert.Equal("Missed", Entry(view, ids["Only One"]).GetProperty("status").GetString());

        view = await doctor.Session.PostOkAsync($"/api/v1/clinic/sessions/{session}/call-next");
        Assert.Equal("Requeued", Entry(view, ids["Only One"]).GetProperty("status").GetString());

        await doctor.ActAsync(ids["Two"], "complete");
        await doctor.Session.PostOkAsync($"/api/v1/clinic/sessions/{session}/call-next");
        factory.Clock.Advance(TimeSpan.FromMinutes(4));
        view = await doctor.ActAsync(ids["Only One"], "miss");
        Assert.Equal("NoShow", Entry(view, ids["Only One"]).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Emergency_patients_are_served_first_and_reasons_stay_private()
    {
        var doctor = await _api.CreateClinicDoctorAsync();
        var patient = await _api.CreatePatientAsync("Online Waiting");
        await patient.BookOkAsync(doctor.Code);
        var session = await doctor.SessionIdAsync();
        var ids = await doctor.WalkInsAsync(session, "Walk A", "Walk B");

        var view = await doctor.Session.PostOkAsync($"/api/v1/clinic/sessions/{session}/walk-ins",
            new { fullName = "Urgent Case", emergency = new { reason = "BreathingDifficulty", note = "Wheezing badly" } });
        Assert.Equal(["Urgent Case", "Online Waiting", "Walk A", "Walk B"], WaitingNames(view));

        view = await doctor.ActAsync(ids["Walk B"], "prioritize", new { reason = "ChildWithHighFever" });
        Assert.Equal(["Urgent Case", "Walk B", "Online Waiting", "Walk A"], WaitingNames(view));

        var withoutReason = await doctor.Session.Client.PostAsJsonAsync($"/api/v1/clinic/appointments/{ids["Walk A"]}/prioritize", new { });
        Assert.Equal(HttpStatusCode.BadRequest, withoutReason.StatusCode);

        await factory.DispatchNotificationsAsync();
        var bodies = await factory.WithDbAsync(db => db.OutboxMessages.Select(m => m.PayloadJson).ToListAsync());
        Assert.DoesNotContain(bodies, b => b.Contains("Wheezing") || b.Contains("Breathing") || b.Contains("Fever"));
        var events = await factory.WithDbAsync(db => db.AppointmentEvents.CountAsync(e => e.AppointmentId == ids["Walk B"] && e.Type == "Prioritized"));
        Assert.Equal(1, events);
    }

    [Fact]
    public async Task Turn_approaching_alert_is_sent_once()
    {
        var doctor = await _api.CreateClinicDoctorAsync();
        var session = await doctor.SessionIdAsync();
        await doctor.WalkInsAsync(session, "W1", "W2", "W3", "W4", "W5");
        var patient = await _api.CreatePatientAsync("Alert Me");
        await patient.BookOkAsync(doctor.Code);

        await doctor.CallNextAndCompleteAsync(session);
        await factory.DispatchNotificationsAsync();
        Assert.DoesNotContain(await patient.NotificationsAsync(), n => n.GetProperty("eventType").GetString() == "TurnApproaching");

        await doctor.CallNextAndCompleteAsync(session);
        await doctor.CallNextAndCompleteAsync(session);
        await factory.DispatchNotificationsAsync();

        var notifications = await patient.NotificationsAsync();
        Assert.Single(notifications, n => n.GetProperty("eventType").GetString() == "TurnApproaching");
        Assert.Single(notifications, n => n.GetProperty("eventType").GetString() == "AppointmentConfirmed");

        await doctor.Session.PostOkAsync($"/api/v1/clinic/sessions/{session}/call-next");
        await factory.DispatchNotificationsAsync();
        Assert.Single(await patient.NotificationsAsync(), n => n.GetProperty("eventType").GetString() == "TurnApproaching");
    }

    [Fact]
    public async Task Closing_a_session_cancels_waiting_patients_and_marks_missed_as_no_show()
    {
        var doctor = await _api.CreateClinicDoctorAsync();
        var session = await doctor.SessionIdAsync();
        var ids = await doctor.WalkInsAsync(session, "Close A", "Close B", "Close C");
        await doctor.Session.PostOkAsync($"/api/v1/clinic/sessions/{session}/call-next");
        factory.Clock.Advance(TimeSpan.FromMinutes(4));
        await doctor.ActAsync(ids["Close A"], "miss");
        await doctor.CallNextAndCompleteAsync(session);

        var view = await doctor.Session.PostOkAsync($"/api/v1/clinic/sessions/{session}/close");

        Assert.Equal("Closed", view.GetProperty("session").GetProperty("state").GetString());
        Assert.Equal("NoShow", Entry(view, ids["Close A"]).GetProperty("status").GetString());
        Assert.Equal("Completed", Entry(view, ids["Close B"]).GetProperty("status").GetString());
        Assert.Equal("Cancelled", Entry(view, ids["Close C"]).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Paused_queue_rejects_online_bookings_and_calls()
    {
        var doctor = await _api.CreateClinicDoctorAsync();
        var session = await doctor.SessionIdAsync();
        await doctor.WalkInsAsync(session, "Paused A");
        await doctor.Session.PostOkAsync($"/api/v1/clinic/sessions/{session}/open");
        await doctor.Session.PostOkAsync($"/api/v1/clinic/sessions/{session}/pause");

        var patient = await _api.CreatePatientAsync();
        Assert.Equal(HttpStatusCode.Conflict, (await patient.BookAsync(doctor.Code, await patient.SelfPatientIdAsync())).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await doctor.Session.Client.PostAsJsonAsync($"/api/v1/clinic/sessions/{session}/call-next", new { })).StatusCode);

        await doctor.Session.PostOkAsync($"/api/v1/clinic/sessions/{session}/resume");
        Assert.Equal(HttpStatusCode.Created, (await patient.BookAsync(doctor.Code, await patient.SelfPatientIdAsync())).StatusCode);
    }

    [Fact]
    public async Task Availability_shows_sessions_and_live_status()
    {
        var doctor = await _api.CreateClinicDoctorAsync(eveningCapacity: 1);
        await (await _api.CreatePatientAsync()).BookOkAsync(doctor.Code, sessionName: "Evening");

        var availability = await (await _api.Anonymous().GetAsync($"/api/v1/doctors/{doctor.Code}/availability")).JsonAsync();

        var days = availability.GetProperty("days").EnumerateArray().ToList();
        Assert.Equal(8, days.Count);
        var today = days[0].GetProperty("sessions").EnumerateArray().ToList();
        Assert.Equal(["Morning", "Evening"], today.Select(s => s.GetProperty("name").GetString()));
        Assert.True(today[0].GetProperty("bookable").GetBoolean());
        Assert.False(today[1].GetProperty("bookable").GetBoolean());
        Assert.Equal("Queue full", today[1].GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Patients_receive_real_time_queue_signals_and_strangers_cannot_listen()
    {
        var doctor = await _api.CreateClinicDoctorAsync();
        var patient = await _api.CreatePatientAsync("Live Listener");
        var booked = await patient.BookOkAsync(doctor.Code);
        var sessionId = booked.GetProperty("sessionId").GetGuid();

        await using var connection = Connect(patient);
        var received = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<JsonElement>("queueChanged", payload => received.TrySetResult(payload));
        await connection.StartAsync();
        await connection.InvokeAsync("JoinSession", sessionId);

        await doctor.Session.PostOkAsync($"/api/v1/clinic/sessions/{sessionId}/call-next");
        var signal = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(sessionId, signal.GetProperty("sessionId").GetGuid());
        Assert.DoesNotContain("Live Listener", signal.GetRawText());

        await using var stranger = Connect(await _api.CreatePatientAsync("Stranger"));
        await stranger.StartAsync();
        await Assert.ThrowsAsync<HubException>(() => stranger.InvokeAsync("JoinSession", sessionId));
    }

    private HubConnection Connect(Session session)
    {
        var token = session.Client.DefaultRequestHeaders.Authorization!.Parameter;
        return new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, "/hubs/queue"), o =>
            {
                o.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                o.AccessTokenProvider = () => Task.FromResult<string?>(token);
                o.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.LongPolling;
            })
            .Build();
    }
}
