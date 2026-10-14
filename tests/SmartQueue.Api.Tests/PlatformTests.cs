using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using SmartQueue.Api.Domain;
using SmartQueue.Api.Features.Auth;
using SmartQueue.Api.Tests.Infrastructure;

namespace SmartQueue.Api.Tests;

[Collection(ApiCollection.Name)]
public class PlatformTests(ApiFactory factory)
{
    private readonly TestApi _api = new(factory);

    [Fact]
    public async Task Super_admin_sign_in_requires_an_authenticator_code_and_rejects_replays()
    {
        var first = await _api.AdminChallengeAsync(ApiFactory.SecondAdminMobile);
        var secret = first.GetProperty("enrollment").GetProperty("secret").GetString()!;
        Assert.StartsWith("otpauth://totp/", first.GetProperty("enrollment").GetProperty("otpAuthUri").GetString());

        var wrong = await _api.Anonymous().PostAsJsonAsync("/api/v1/auth/mfa", new { mfaTicket = first.GetProperty("mfaTicket").GetString(), code = "000000" });
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);

        var code = Totp.Compute(secret, Totp.CurrentStep(factory.Clock.GetUtcNow()));
        var ok = await _api.Anonymous().PostAsJsonAsync("/api/v1/auth/mfa", new { mfaTicket = first.GetProperty("mfaTicket").GetString(), code });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Contains("SuperAdmin", (await ok.JsonAsync()).GetProperty("roles").EnumerateArray().Select(r => r.GetString()));

        var second = await _api.AdminChallengeAsync(ApiFactory.SecondAdminMobile);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, second.GetProperty("enrollment").ValueKind);
        var replay = await _api.Anonymous().PostAsJsonAsync("/api/v1/auth/mfa", new { mfaTicket = second.GetProperty("mfaTicket").GetString(), code });
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);

        factory.Clock.Advance(TimeSpan.FromSeconds(31));
        var next = Totp.Compute(secret, Totp.CurrentStep(factory.Clock.GetUtcNow()));
        var again = await _api.Anonymous().PostAsJsonAsync("/api/v1/auth/mfa", new { mfaTicket = second.GetProperty("mfaTicket").GetString(), code = next });
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
    }

    [Fact]
    public async Task Patient_history_access_requires_a_purpose_and_is_audited()
    {
        var admin = await _api.LoginSuperAdminAsync();
        var doctor = await _api.CreateClinicDoctorAsync();
        var patient = await _api.CreatePatientAsync("Audited Person");
        await patient.BookOkAsync(doctor.Code);
        var patientId = await patient.SelfPatientIdAsync();

        var search = await (await admin.Client.GetAsync($"/api/v1/admin/patients?q={patient.Mobile}")).JsonAsync();
        Assert.Contains(search.GetProperty("items").EnumerateArray(), p => p.GetProperty("patientId").GetGuid() == patientId);

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.Client.GetAsync($"/api/v1/admin/patients/{patientId}/history")).StatusCode);
        var history = await (await admin.Client.GetAsync($"/api/v1/admin/patients/{patientId}/history?purpose=Support%20ticket%20about%20booking&ticket=SUP-42")).JsonAsync();
        Assert.Single(history.GetProperty("appointments").EnumerateArray());

        var log = await factory.WithDbAsync(db => db.AdminAuditLogs.SingleAsync(a => a.Action == "ViewPatientHistory" && a.EntityId == patientId.ToString()));
        Assert.Equal("Support ticket about booking", log.Purpose);
        Assert.Equal("SUP-42", log.Details);
        Assert.Equal(admin.UserId, log.ActorUserId);

        var doctorHistory = await admin.Client.GetAsync($"/api/v1/admin/doctors/{doctor.DoctorId}/appointments?purpose=Quality%20review");
        Assert.Equal(HttpStatusCode.OK, doctorHistory.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await patient.Client.GetAsync($"/api/v1/admin/patients/{patientId}/history?purpose=curious")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await doctor.Session.Client.GetAsync("/api/v1/admin/queues")).StatusCode);
    }

    [Fact]
    public async Task Admin_sees_active_queues_and_overview()
    {
        var admin = await _api.LoginSuperAdminAsync();
        var doctor = await _api.CreateClinicDoctorAsync();
        var session = await doctor.SessionIdAsync();
        await doctor.WalkInsAsync(session, "Overview One");
        await doctor.Session.PostOkAsync($"/api/v1/clinic/sessions/{session}/call-next");

        var queues = await (await admin.Client.GetAsync("/api/v1/admin/queues")).JsonAsync();
        var row = queues.EnumerateArray().Single(q => q.GetProperty("sessionId").GetGuid() == session);
        Assert.Equal("Open", row.GetProperty("state").GetString());
        Assert.Equal("T-001", row.GetProperty("nowServing").GetString());

        var overview = await (await admin.Client.GetAsync("/api/v1/admin/overview")).JsonAsync();
        Assert.True(overview.GetProperty("openQueuesToday").GetInt32() >= 1);
    }

    [Fact]
    public async Task Notifications_are_delivered_once_and_can_be_marked_read()
    {
        var doctor = await _api.CreateClinicDoctorAsync();
        var patient = await _api.CreatePatientAsync("Notify Me");
        await patient.BookOkAsync(doctor.Code);

        await factory.DispatchNotificationsAsync();
        await factory.DispatchNotificationsAsync();
        var list = await (await patient.Client.GetAsync("/api/v1/notifications")).JsonAsync();
        var item = Assert.Single(list.GetProperty("items").EnumerateArray());
        Assert.Equal(1, list.GetProperty("unread").GetInt32());
        Assert.Contains("T-001", item.GetProperty("body").GetString());

        Assert.Equal(HttpStatusCode.NoContent, (await patient.Client.PostAsJsonAsync($"/api/v1/notifications/{item.GetProperty("id").GetInt64()}/read", new { })).StatusCode);
        Assert.Equal(0, (await (await patient.Client.GetAsync("/api/v1/notifications")).JsonAsync()).GetProperty("unread").GetInt32());

        var other = await _api.CreatePatientAsync("Other Reader");
        Assert.Equal(HttpStatusCode.NotFound, (await other.Client.PostAsJsonAsync($"/api/v1/notifications/{item.GetProperty("id").GetInt64()}/read", new { })).StatusCode);
    }

    [Fact]
    public async Task Walk_ins_without_an_account_get_sms_which_is_logged_as_skipped_without_a_provider()
    {
        var doctor = await _api.CreateClinicDoctorAsync();
        var session = await doctor.SessionIdAsync();
        var mobile = TestApi.NextMobile();
        var view = await doctor.Session.PostOkAsync($"/api/v1/clinic/sessions/{session}/walk-ins", new { fullName = "Phone Only", mobileNumber = mobile });
        var id = view.GetProperty("waiting")[0].GetProperty("appointmentId").GetGuid();

        await factory.DispatchNotificationsAsync();

        var logs = await factory.WithDbAsync(db => db.NotificationLogs.Where(l => l.AppointmentId == id).ToListAsync());
        var sms = Assert.Single(logs);
        Assert.Equal(NotificationChannel.Sms, sms.Channel);
        Assert.Equal(DeliveryStatus.Skipped, sms.Status);
    }

    [Fact]
    public async Task Late_turn_alerts_expire_instead_of_arriving_after_the_fact()
    {
        var doctor = await _api.CreateClinicDoctorAsync();
        var patient = await _api.CreatePatientAsync("Late Alert");
        var booked = await patient.BookOkAsync(doctor.Code);
        await factory.DispatchNotificationsAsync();

        await doctor.Session.PostOkAsync($"/api/v1/clinic/sessions/{booked.GetProperty("sessionId").GetGuid()}/call-next");
        factory.Clock.Advance(TimeSpan.FromMinutes(6));
        await factory.DispatchNotificationsAsync();

        Assert.DoesNotContain(await patient.NotificationsAsync(), n => n.GetProperty("eventType").GetString() == "PatientCalled");
        var failed = await factory.WithDbAsync(db => db.OutboxMessages.SingleAsync(m => m.DedupKey.StartsWith("called:" + booked.GetProperty("appointmentId").GetGuid())));
        Assert.Equal(OutboxStatus.Failed, failed.Status);

        var admin = await _api.LoginSuperAdminAsync();
        var failures = await (await admin.Client.GetAsync("/api/v1/admin/notification-failures")).JsonAsync();
        Assert.Contains(failures.GetProperty("items").EnumerateArray(), f => f.GetProperty("eventType").GetString() == "PatientCalled");
    }

    [Fact]
    public async Task Push_subscriptions_must_use_https()
    {
        var patient = await _api.CreatePatientAsync();
        var bad = await patient.Client.PostAsJsonAsync("/api/v1/notifications/push-subscriptions",
            new { endpoint = "http://push.example.com/abc", keys = new { p256dh = "x", auth = "y" } });
        var good = await patient.Client.PostAsJsonAsync("/api/v1/notifications/push-subscriptions",
            new { endpoint = "https://push.example.com/abc", keys = new { p256dh = "BKey", auth = "secret" } });

        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, good.StatusCode);
    }

    [Fact]
    public async Task Verified_doctor_is_notified()
    {
        var admin = await _api.LoginSuperAdminAsync();
        var draft = await _api.CreateSubmittedDoctorAsync("NGP", "Dr. Notified");
        var code = await TestApi.VerifyAsync(admin, draft.DoctorId);
        await factory.DispatchNotificationsAsync();

        var note = Assert.Single(await draft.Session.NotificationsAsync());
        Assert.Contains(code, note.GetProperty("body").GetString());
    }
}
