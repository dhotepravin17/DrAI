using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SmartQueue.Api.Domain;
using SmartQueue.Api.Features.Analytics;
using SmartQueue.Api.Features.Auth;
using SmartQueue.Api.Infrastructure;
using SmartQueue.Api.Tests.Infrastructure;
using static SmartQueue.Api.Tests.Infrastructure.QueueTestExtensions;

namespace SmartQueue.Api.Tests;

/// <summary>Plans (PRD §39), product analytics (§38), data retention (§32.4) and the SMS gateway (§6.2).</summary>
[Collection(ApiCollection.Name)]
public class ReadinessTests(ApiFactory factory)
{
    private readonly TestApi _api = new(factory);

    private async Task SetPlanAsync(Guid doctorId, string plan)
    {
        var admin = await _api.LoginSuperAdminAsync();
        await admin.PostOkAsync($"/api/v1/admin/doctors/{doctorId}/plan", new { plan, note = "Test upgrade" });
    }

    [Fact]
    public async Task Queue_activity_records_product_events_without_personal_data()
    {
        var doctor = await _api.CreateClinicDoctorAsync();
        var booked = await (await _api.CreatePatientAsync("Meera Joshi")).BookOkAsync(doctor.Code);
        var appointmentId = booked.GetProperty("appointmentId").GetGuid();
        var session = booked.GetProperty("sessionId").GetGuid();
        await doctor.Session.PostOkAsync($"/api/v1/clinic/sessions/{session}/call-next");
        await doctor.ActAsync(appointmentId, "complete");

        var events = await factory.WithDbAsync(db => db.ProductEvents.AsNoTracking()
            .Where(e => e.PropertiesJson.Contains(appointmentId.ToString())).ToListAsync());
        var names = events.Select(e => e.Name).ToHashSet();
        Assert.Contains(ProductEventNames.AppointmentBooked, names);
        Assert.Contains(ProductEventNames.PatientCalled, names);
        Assert.Contains(ProductEventNames.ConsultationCompleted, names);
        Assert.Equal(nameof(BookingSource.Online), events.Single(e => e.Name == ProductEventNames.AppointmentBooked).Dimension);
        Assert.Contains("\"forSelf\":true", events.Single(e => e.Name == ProductEventNames.AppointmentBooked).PropertiesJson);
        Assert.All(events, e => Assert.DoesNotContain("Meera", e.PropertiesJson));

        var analytics = await (await (await _api.LoginSuperAdminAsync()).Client.GetAsync("/api/v1/admin/analytics")).JsonAsync();
        Assert.True(analytics.GetProperty("metrics").GetProperty("bookings").GetInt32() >= 1);
        Assert.Contains(analytics.GetProperty("events").EnumerateArray(),
            e => e.GetProperty("name").GetString() == ProductEventNames.ConsultationCompleted);
    }

    [Fact]
    public async Task Free_plan_allows_one_staff_account_and_pro_allows_more()
    {
        var doctor = await _api.CreateClinicDoctorAsync();
        await doctor.Session.PostOkAsync("/api/v1/clinic/staff", new { mobileNumber = TestApi.NextMobile(), fullName = "First Desk" });

        var second = await doctor.Session.Client.PostAsJsonAsync("/api/v1/clinic/staff", new { mobileNumber = TestApi.NextMobile(), fullName = "Second Desk" });
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        await SetPlanAsync(doctor.DoctorId, "Pro");
        await doctor.Session.PostOkAsync("/api/v1/clinic/staff", new { mobileNumber = TestApi.NextMobile(), fullName = "Second Desk" });

        var audited = await factory.WithDbAsync(db => db.AdminAuditLogs.AnyAsync(a =>
            a.Action == "ChangeDoctorPlan" && a.EntityId == doctor.DoctorId.ToString()));
        Assert.True(audited);
    }

    [Fact]
    public async Task Clinic_reports_require_a_pro_plan()
    {
        var doctor = await _api.CreateClinicDoctorAsync();
        var session = await doctor.SessionIdAsync();
        await doctor.WalkInsAsync(session, "Report Walk-in");
        await doctor.CallNextAndCompleteAsync(session);

        Assert.Equal(HttpStatusCode.Forbidden, (await doctor.Session.Client.GetAsync("/api/v1/clinic/reports")).StatusCode);

        await SetPlanAsync(doctor.DoctorId, "Pro");
        var report = await (await doctor.Session.Client.GetAsync($"/api/v1/clinic/reports?from={Today:yyyy-MM-dd}&to={Today:yyyy-MM-dd}")).JsonAsync();
        Assert.Equal(1, report.GetProperty("appointments").GetInt32());
        Assert.Equal(1, report.GetProperty("completed").GetInt32());
        Assert.Equal(1, report.GetProperty("bySource").GetProperty("WalkIn").GetInt32());
    }

    [Fact]
    public async Task Free_plan_history_covers_the_last_90_days()
    {
        var doctor = await _api.CreateClinicDoctorAsync();
        var booked = await (await _api.CreatePatientAsync("Old Visit")).BookOkAsync(doctor.Code);
        var id = booked.GetProperty("appointmentId").GetGuid();
        await factory.WithDbAsync(db => db.Appointments.Where(a => a.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.Date, Today.AddDays(-120))));

        async Task<int> TotalAsync() =>
            (await (await doctor.Session.Client.GetAsync("/api/v1/clinic/appointments?patientName=Old%20Visit")).JsonAsync()).GetProperty("total").GetInt32();

        Assert.Equal(0, await TotalAsync());
        await SetPlanAsync(doctor.DoctorId, "Pro");
        Assert.Equal(1, await TotalAsync());
    }

    [Fact]
    public async Task Retention_removes_expired_records_but_keeps_pending_notifications()
    {
        var old = factory.Clock.GetUtcNow().AddDays(-200);
        var marker = $"retention-{Guid.NewGuid():N}";
        await factory.WithDbAsync(async db =>
        {
            db.OtpChallenges.Add(new OtpChallenge { MobileNumber = "+919999999999", CodeHash = marker[..32], CreatedAt = old, ExpiresAt = old });
            db.NotificationLogs.Add(new NotificationLog { EventType = marker[..40], Channel = NotificationChannel.InApp, Status = DeliveryStatus.Sent, CreatedAt = old });
            db.OutboxMessages.Add(new OutboxMessage { EventType = "Test", DedupKey = marker + ":done", PayloadJson = "{}", Status = OutboxStatus.Processed, CreatedAt = old, NextAttemptAt = old });
            db.OutboxMessages.Add(new OutboxMessage { EventType = "Test", DedupKey = marker + ":pending", PayloadJson = "{}", Status = OutboxStatus.Pending, CreatedAt = old, NextAttemptAt = old.AddYears(10) });
            return await db.SaveChangesAsync();
        });

        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<RetentionService>().RunAsync(CancellationToken.None);

        await factory.WithDbAsync(async db =>
        {
            Assert.False(await db.OtpChallenges.AnyAsync(c => c.CodeHash == marker.Substring(0, 32)));
            Assert.False(await db.NotificationLogs.AnyAsync(l => l.EventType == marker.Substring(0, 40)));
            Assert.False(await db.OutboxMessages.AnyAsync(m => m.DedupKey == marker + ":done"));
            Assert.True(await db.OutboxMessages.AnyAsync(m => m.DedupKey == marker + ":pending"));
            return 0;
        });
    }

    [Fact]
    public async Task Http_sms_sender_posts_dlt_fields_and_returns_the_provider_reference()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{"messageId":"MSG-42"}""");
        var sender = CreateSender(handler);

        var reference = await sender.SendTextAsync("+919812345678", "Token T-004 is next", CancellationToken.None);

        Assert.Equal("MSG-42", reference);
        Assert.Equal("Bearer", handler.Request!.Headers.Authorization!.Scheme);
        var body = JsonDocument.Parse(handler.Body!).RootElement;
        Assert.Equal("+919812345678", body.GetProperty("to").GetString());
        Assert.Equal("SMARTQ", body.GetProperty("senderId").GetString());
        Assert.Equal("1201000000000000001", body.GetProperty("dltEntityId").GetString());
        Assert.Equal("TPL-NOTIFY", body.GetProperty("dltTemplateId").GetString());
    }

    [Fact]
    public async Task Http_sms_sender_reports_gateway_errors()
    {
        var sender = CreateSender(new StubHandler(HttpStatusCode.BadGateway, "upstream down"));
        await Assert.ThrowsAsync<SmsDeliveryException>(() => sender.SendOtpAsync("+919812345678", "123456", CancellationToken.None));
    }

    [Fact]
    public void Http_sms_provider_requires_complete_settings()
    {
        Assert.True(HttpSmsSender.IsValid(new SmsOptions()));
        Assert.False(HttpSmsSender.IsValid(new SmsOptions { Provider = "Http", Endpoint = "https://sms.example.in/send" }));
        Assert.False(HttpSmsSender.IsValid(Options("http://sms.example.in/send")));
        Assert.True(HttpSmsSender.IsValid(Options("https://sms.example.in/send")));
    }

    private static SmsOptions Options(string endpoint) => new()
    {
        Provider = "Http", Endpoint = endpoint, ApiKey = "key", SenderId = "SMARTQ", DltEntityId = "1201000000000000001",
        OtpTemplateId = "TPL-OTP", NotificationTemplateId = "TPL-NOTIFY",
    };

    private static HttpSmsSender CreateSender(StubHandler handler) => new(
        new HttpClient(handler), Microsoft.Extensions.Options.Options.Create(Options("https://sms.example.in/send")),
        Microsoft.Extensions.Options.Options.Create(new OtpOptions()), NullLogger<HttpSmsSender>.Instance);

    private sealed class StubHandler(HttpStatusCode status, string response) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            Body = await request.Content!.ReadAsStringAsync(ct);
            return new HttpResponseMessage(status) { Content = new StringContent(response) };
        }
    }
}
