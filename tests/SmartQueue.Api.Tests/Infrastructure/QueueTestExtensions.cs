using System.Net.Http.Json;
using System.Text.Json;

namespace SmartQueue.Api.Tests.Infrastructure;

public sealed record ClinicDoctor(Session Session, Guid DoctorId, string Code);

public static class QueueTestExtensions
{
    /// <summary>The fake clock's date (Wednesday) in IST.</summary>
    public static readonly DateOnly Today = new(2026, 10, 14);

    private static int _doctorCounter;

    /// <summary>A verified doctor with Morning (08:00–14:00) and Evening (17:00–21:00) sessions every day.</summary>
    public static async Task<ClinicDoctor> CreateClinicDoctorAsync(this TestApi api, int morningCapacity = 40, int eveningCapacity = 40)
    {
        var admin = await api.LoginSuperAdminAsync();
        var draft = await api.CreateSubmittedDoctorAsync("NGP", $"Dr. Queue Tester {Interlocked.Increment(ref _doctorCounter)}");
        var code = await TestApi.VerifyAsync(admin, draft.DoctorId);

        var sessions = Enum.GetValues<DayOfWeek>().SelectMany(day => new object[]
        {
            new { dayOfWeek = day.ToString(), name = "Morning", startTime = "08:00", endTime = "14:00", capacity = morningCapacity },
            new { dayOfWeek = day.ToString(), name = "Evening", startTime = "17:00", endTime = "21:00", capacity = eveningCapacity },
        }).ToList();
        (await draft.Session.Client.PutAsJsonAsync("/api/v1/clinic/schedule/sessions", sessions)).EnsureSuccessStatusCode();
        return new ClinicDoctor(draft.Session, draft.DoctorId, code);
    }

    public static async Task<Guid> SelfPatientIdAsync(this Session session) =>
        (await (await session.Client.GetAsync("/api/v1/me")).JsonAsync()).GetProperty("patient").GetProperty("patientId").GetGuid();

    public static Task<HttpResponseMessage> BookAsync(
        this Session session, string doctorCode, Guid patientId, DateOnly? date = null, string sessionName = "Morning", string? key = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/appointments")
        {
            Content = JsonContent.Create(new { doctorCode, date = (date ?? Today).ToString("yyyy-MM-dd"), sessionName, patientId }),
        };
        request.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString());
        return session.Client.SendAsync(request);
    }

    public static async Task<JsonElement> BookOkAsync(this Session session, string doctorCode, Guid? patientId = null, DateOnly? date = null, string sessionName = "Morning")
    {
        var response = await session.BookAsync(doctorCode, patientId ?? await session.SelfPatientIdAsync(), date, sessionName);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return await response.JsonAsync();
    }

    public static async Task<Guid> SessionIdAsync(this ClinicDoctor doctor, string name = "Morning", DateOnly? date = null)
    {
        var sessions = await (await doctor.Session.Client.GetAsync($"/api/v1/clinic/sessions?date={(date ?? Today):yyyy-MM-dd}")).JsonAsync();
        return sessions.EnumerateArray().Single(s => s.GetProperty("name").GetString() == name).GetProperty("sessionId").GetGuid();
    }

    public static async Task<JsonElement> ViewAsync(this ClinicDoctor doctor, Guid sessionId) =>
        await (await doctor.Session.Client.GetAsync($"/api/v1/clinic/sessions/{sessionId}")).JsonAsync();

    public static async Task<JsonElement> PostOkAsync(this Session session, string path, object? body = null)
    {
        var response = await session.Client.PostAsJsonAsync(path, body ?? new { });
        Assert.True(response.IsSuccessStatusCode, $"{path}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return response.Content.Headers.ContentLength == 0 ? default : await response.JsonAsync();
    }

    /// <summary>Adds walk-ins in order and returns their appointment ids keyed by name.</summary>
    public static async Task<Dictionary<string, Guid>> WalkInsAsync(this ClinicDoctor doctor, Guid sessionId, params string[] names)
    {
        var ids = new Dictionary<string, Guid>();
        foreach (var name in names)
        {
            var view = await doctor.Session.PostOkAsync($"/api/v1/clinic/sessions/{sessionId}/walk-ins", new { fullName = name });
            ids[name] = AllEntries(view).Single(e => e.GetProperty("patientName").GetString() == name).GetProperty("appointmentId").GetGuid();
        }
        return ids;
    }

    public static IEnumerable<JsonElement> AllEntries(JsonElement view)
    {
        if (view.GetProperty("nowServing").ValueKind == JsonValueKind.Object) yield return view.GetProperty("nowServing");
        foreach (var list in new[] { "waiting", "missed", "done" })
            foreach (var e in view.GetProperty(list).EnumerateArray())
                yield return e;
    }

    public static JsonElement Entry(JsonElement view, Guid appointmentId) =>
        AllEntries(view).Single(e => e.GetProperty("appointmentId").GetGuid() == appointmentId);

    public static List<string> WaitingNames(JsonElement view) =>
        view.GetProperty("waiting").EnumerateArray().Select(e => e.GetProperty("patientName").GetString()!).ToList();

    public static Task<JsonElement> ActAsync(this ClinicDoctor doctor, Guid appointmentId, string action, object? body = null) =>
        doctor.Session.PostOkAsync($"/api/v1/clinic/appointments/{appointmentId}/{action}", body);

    public static async Task<JsonElement> CallNextAndCompleteAsync(this ClinicDoctor doctor, Guid sessionId)
    {
        var view = await doctor.Session.PostOkAsync($"/api/v1/clinic/sessions/{sessionId}/call-next");
        return await doctor.ActAsync(view.GetProperty("nowServing").GetProperty("appointmentId").GetGuid(), "complete");
    }

    public static async Task<List<JsonElement>> NotificationsAsync(this Session session)
    {
        var list = await (await session.Client.GetAsync("/api/v1/notifications")).JsonAsync();
        return list.GetProperty("items").EnumerateArray().ToList();
    }
}
