using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using SmartQueue.Api.Domain;
using SmartQueue.Api.Features.Auth;
using SmartQueue.Api.Tests.Infrastructure;
using static SmartQueue.Api.Tests.Infrastructure.QueueTestExtensions;

namespace SmartQueue.Api.Tests;

/// <summary>Regression tests for code-review findings on scheduling, walk-ins and Super Admin sign-in.</summary>
[Collection(ApiCollection.Name)]
public class ReviewFixTests(ApiFactory factory)
{
    private readonly TestApi _api = new(factory);

    private static DateTimeOffset IstMidnight(DateOnly date) => new(date.ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(5.5));

    [Fact]
    public async Task Closing_the_clinic_while_a_patient_is_being_seen_changes_nothing()
    {
        var doctor = await _api.CreateClinicDoctorAsync();
        var session = await doctor.SessionIdAsync();
        var ids = await doctor.WalkInsAsync(session, "Being Seen", "Still Waiting");
        await doctor.Session.PostOkAsync($"/api/v1/clinic/sessions/{session}/call-next");

        var announcement = await doctor.Session.Client.PostAsJsonAsync("/api/v1/clinic/announcements", new
        {
            type = "ClinicClosure", message = "Closing early", startsAt = factory.Clock.GetUtcNow(), endsAt = factory.Clock.GetUtcNow().AddHours(2),
        });
        Assert.Equal(HttpStatusCode.Conflict, announcement.StatusCode);

        var exception = await doctor.Session.Client.PostAsJsonAsync("/api/v1/clinic/schedule/exceptions", new
        {
            kind = "Closed", startDate = Today.ToString("yyyy-MM-dd"), reason = "Closing early",
        });
        Assert.Equal(HttpStatusCode.Conflict, exception.StatusCode);

        await factory.WithDbAsync(async db =>
        {
            Assert.False(await db.Announcements.AnyAsync(a => a.DoctorId == doctor.DoctorId));
            Assert.False(await db.ScheduleExceptions.AnyAsync(e => e.DoctorId == doctor.DoctorId));
            Assert.Equal(SessionState.Open, (await db.QueueSessions.SingleAsync(s => s.Id == session)).State);
            var waiting = ids["Still Waiting"];
            Assert.Equal(AppointmentStatus.CheckedIn, (await db.Appointments.SingleAsync(a => a.Id == waiting)).Status);
            return 0;
        });
    }

    [Fact]
    public async Task Removing_a_delay_notice_keeps_the_other_delays()
    {
        var doctor = await _api.CreateClinicDoctorAsync();
        var patient = await _api.CreatePatientAsync("Delay Keeper");
        var tomorrow = Today.AddDays(1);
        await patient.BookOkAsync(doctor.Code);
        await patient.BookOkAsync(doctor.Code, date: tomorrow, sessionName: "Evening");
        var now = factory.Clock.GetUtcNow();

        async Task<Guid> DelayAsync(DateTimeOffset start, DateTimeOffset end, int minutes) =>
            (await doctor.Session.PostOkAsync("/api/v1/clinic/announcements", new
            {
                type = "DelayedOpening", message = $"Running {minutes} min late", startsAt = start, endsAt = end, delayMinutes = minutes,
            })).GetProperty("id").GetGuid();

        var morning30 = await DelayAsync(now, now.AddHours(1), 30);
        var morning45 = await DelayAsync(now, now.AddHours(1), 45);
        await DelayAsync(IstMidnight(tomorrow).AddHours(16), IstMidnight(tomorrow).AddHours(22), 60);

        Task<int> DelayOf(DateOnly date, string name) => factory.WithDbAsync(db => db.QueueSessions
            .Where(s => s.DoctorId == doctor.DoctorId && s.Date == date && s.Name == name).Select(s => s.DelayMinutes).SingleAsync());

        Assert.Equal(45, await DelayOf(Today, "Morning"));
        Assert.Equal(60, await DelayOf(tomorrow, "Evening"));

        Assert.Equal(HttpStatusCode.NoContent, (await doctor.Session.Client.DeleteAsync($"/api/v1/clinic/announcements/{morning45}")).StatusCode);
        Assert.Equal(30, await DelayOf(Today, "Morning"));
        Assert.Equal(60, await DelayOf(tomorrow, "Evening"));

        Assert.Equal(HttpStatusCode.NoContent, (await doctor.Session.Client.DeleteAsync($"/api/v1/clinic/announcements/{morning30}")).StatusCode);
        Assert.Equal(0, await DelayOf(Today, "Morning"));
        Assert.Equal(60, await DelayOf(tomorrow, "Evening"));
    }

    [Fact]
    public async Task Rejected_walk_ins_do_not_create_patient_records()
    {
        var doctor = await _api.CreateClinicDoctorAsync();
        var session = await doctor.SessionIdAsync();
        var name = $"Orphan Check {Guid.NewGuid():N}"[..30];

        var longNote = await doctor.Session.Client.PostAsJsonAsync($"/api/v1/clinic/sessions/{session}/walk-ins", new
        {
            fullName = name, emergency = new { reason = "Injury", note = new string('x', 250) },
        });
        Assert.Equal(HttpStatusCode.BadRequest, longNote.StatusCode);

        var noReason = await doctor.Session.Client.PostAsJsonAsync($"/api/v1/clinic/sessions/{session}/walk-ins", new
        {
            fullName = name, emergency = new { note = "Fell down" },
        });
        Assert.Equal(HttpStatusCode.BadRequest, noReason.StatusCode);

        await doctor.Session.PostOkAsync($"/api/v1/clinic/sessions/{session}/close");
        var closed = await doctor.Session.Client.PostAsJsonAsync($"/api/v1/clinic/sessions/{session}/walk-ins", new { fullName = name });
        Assert.Equal(HttpStatusCode.Conflict, closed.StatusCode);

        Assert.Equal(0, await factory.WithDbAsync(db => db.Patients.CountAsync(p => p.FullName == name)));
    }

    [Fact]
    public async Task Removing_one_of_two_overlapping_closures_keeps_the_date_closed()
    {
        var doctor = await _api.CreateClinicDoctorAsync();
        var patient = await _api.CreatePatientAsync("Overlap Patient");
        var tomorrow = Today.AddDays(1);
        await patient.BookOkAsync(doctor.Code, date: tomorrow);

        await doctor.Session.PostOkAsync("/api/v1/clinic/schedule/exceptions", new
        {
            kind = "Closed", startDate = tomorrow.ToString("yyyy-MM-dd"), reason = "Holiday",
        });
        var leave = await doctor.Session.PostOkAsync("/api/v1/clinic/announcements", new
        {
            type = "Leave", message = "On leave", startsAt = IstMidnight(tomorrow), endsAt = IstMidnight(tomorrow).AddDays(2).AddHours(-1),
        });

        var holidayId = await factory.WithDbAsync(db => db.ScheduleExceptions
            .Where(e => e.DoctorId == doctor.DoctorId && e.AnnouncementId == null).Select(e => e.Id).SingleAsync());
        Assert.Equal(HttpStatusCode.OK, (await doctor.Session.Client.DeleteAsync($"/api/v1/clinic/schedule/exceptions/{holidayId}")).StatusCode);

        Task<SessionState> StateOf() => factory.WithDbAsync(db => db.QueueSessions
            .Where(s => s.DoctorId == doctor.DoctorId && s.Date == tomorrow && s.Name == "Morning").Select(s => s.State).SingleAsync());

        // The leave notice still covers tomorrow, so the session stays closed to bookings.
        Assert.Equal(SessionState.Cancelled, await StateOf());
        var other = await _api.CreatePatientAsync("Overlap Booker");
        Assert.False((await other.BookAsync(doctor.Code, await other.SelfPatientIdAsync(), tomorrow)).IsSuccessStatusCode);

        Assert.Equal(HttpStatusCode.NoContent,
            (await doctor.Session.Client.DeleteAsync($"/api/v1/clinic/announcements/{leave.GetProperty("id").GetGuid()}")).StatusCode);
        Assert.Equal(SessionState.Scheduled, await StateOf());
    }

    [Fact]
    public async Task Super_admin_authenticator_locks_after_repeated_wrong_codes()
    {
        var mobile = TestApi.NextMobile();
        await factory.WithDbAsync(async db =>
        {
            var user = new User { MobileNumber = "+91" + mobile, FullName = "Lockout Admin", CreatedAt = factory.Clock.GetUtcNow() };
            db.Users.Add(user);
            db.UserRoleAssignments.Add(new UserRoleAssignment { UserId = user.Id, Role = RoleName.SuperAdmin, GrantedAt = user.CreatedAt });
            return await db.SaveChangesAsync();
        });

        var challenge = await _api.AdminChallengeAsync(mobile);
        var ticket = challenge.GetProperty("mfaTicket").GetString();
        var secret = challenge.GetProperty("enrollment").GetProperty("secret").GetString()!;
        var code = Totp.Compute(secret, Totp.CurrentStep(factory.Clock.GetUtcNow()));
        var wrong = code == "000000" ? "111111" : "000000";

        Task<HttpResponseMessage> TryAsync(string attempt) =>
            _api.Anonymous().PostAsJsonAsync("/api/v1/auth/mfa", new { mfaTicket = ticket, code = attempt });

        for (var i = 1; i < AuthEndpoints.MaxTotpFailures; i++)
            Assert.Equal(HttpStatusCode.BadRequest, (await TryAsync(wrong)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await TryAsync(wrong)).StatusCode);

        // Even the right code is refused while locked.
        Assert.Equal(HttpStatusCode.TooManyRequests, (await TryAsync(code)).StatusCode);
        Assert.True(await factory.WithDbAsync(db => db.AdminAuditLogs.AnyAsync(a => a.Action == "AdminMfaLocked" && a.Details!.Contains("5"))));

        // Once the lock expires the right code works and the counter is reset.
        await factory.WithDbAsync(db => db.Users.Where(u => u.MobileNumber == "+91" + mobile)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.TotpLockedUntil, factory.Clock.GetUtcNow().AddMinutes(-1))));
        Assert.Equal(HttpStatusCode.OK, (await TryAsync(code)).StatusCode);
        Assert.Equal(0, await factory.WithDbAsync(db => db.Users.Where(u => u.MobileNumber == "+91" + mobile)
            .Select(u => u.TotpFailedAttempts).SingleAsync()));
    }
}
