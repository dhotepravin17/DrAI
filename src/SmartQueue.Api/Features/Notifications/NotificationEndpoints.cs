using Microsoft.EntityFrameworkCore;
using SmartQueue.Api.Common;
using SmartQueue.Api.Infrastructure;

namespace SmartQueue.Api.Features.Notifications;

public static class NotificationEndpoints
{
    public const int PageSize = 30;

    public sealed record NotificationDto(long Id, string EventType, string Title, string Body, Guid? AppointmentId, DateTimeOffset CreatedAt, bool IsRead);
    public sealed record NotificationList(IReadOnlyList<NotificationDto> Items, int Unread, int Page, int Total);
    public sealed record SubscriptionKeys(string? P256dh, string? Auth);
    public sealed record SubscriptionRequest(string? Endpoint, SubscriptionKeys? Keys);

    public static RouteGroupBuilder MapNotificationEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/notifications").WithTags("Notifications").RequireAuthorization();
        group.MapGet("/", List);
        group.MapPost("/{id:long}/read", MarkRead);
        group.MapPost("/read-all", MarkAllRead);
        group.MapGet("/push-key", (VapidKeyProvider vapid) => Results.Ok(new { publicKey = vapid.Keys?.PublicKey }));
        group.MapPost("/push-subscriptions", Subscribe);
        group.MapDelete("/push-subscriptions", Unsubscribe);
        return api;
    }

    private static async Task<IResult> List(int? page, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var userId = http.User.GetUserId();
        var pageNumber = Math.Max(1, page ?? 1);
        var query = db.Notifications.AsNoTracking().Where(n => n.UserId == userId);
        var total = await query.CountAsync(ct);
        var unread = await query.CountAsync(n => n.ReadAt == null, ct);
        var items = await query.OrderByDescending(n => n.Id).Skip((pageNumber - 1) * PageSize).Take(PageSize)
            .Select(n => new NotificationDto(n.Id, n.EventType, n.Title, n.Body, n.AppointmentId, n.CreatedAt, n.ReadAt != null))
            .ToListAsync(ct);
        return Results.Ok(new NotificationList(items, unread, pageNumber, total));
    }

    private static async Task<IResult> MarkRead(long id, HttpContext http, AppDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var userId = http.User.GetUserId();
        var updated = await db.Notifications.Where(n => n.Id == id && n.UserId == userId && n.ReadAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAt, clock.GetUtcNow()), ct);
        return updated == 0 ? Results.NotFound() : Results.NoContent();
    }

    private static async Task<IResult> MarkAllRead(HttpContext http, AppDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var userId = http.User.GetUserId();
        await db.Notifications.Where(n => n.UserId == userId && n.ReadAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAt, clock.GetUtcNow()), ct);
        return Results.NoContent();
    }

    private static async Task<IResult> Subscribe(SubscriptionRequest r, HttpContext http, AppDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var v = new Validator();
        if (!Uri.TryCreate(r.Endpoint, UriKind.Absolute, out var endpoint) || endpoint.Scheme != Uri.UriSchemeHttps || r.Endpoint!.Length > 1000)
            v.Add("endpoint", "A valid HTTPS push endpoint is required.");
        if (string.IsNullOrWhiteSpace(r.Keys?.P256dh) || r.Keys.P256dh.Length > 200) v.Add("keys.p256dh", "The subscription key is missing.");
        if (string.IsNullOrWhiteSpace(r.Keys?.Auth) || r.Keys.Auth.Length > 100) v.Add("keys.auth", "The subscription secret is missing.");
        if (!v.IsValid) return v.ToResult();

        var userId = http.User.GetUserId();
        var existing = await db.PushSubscriptions.FirstOrDefaultAsync(s => s.Endpoint == r.Endpoint, ct);
        if (existing is null)
        {
            db.PushSubscriptions.Add(new Domain.PushSubscription
            {
                UserId = userId, Endpoint = r.Endpoint!, P256dh = r.Keys!.P256dh!, Auth = r.Keys.Auth!, CreatedAt = clock.GetUtcNow(),
            });
        }
        else
        {
            existing.UserId = userId;
            existing.P256dh = r.Keys!.P256dh!;
            existing.Auth = r.Keys.Auth!;
        }
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> Unsubscribe(string? endpoint, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var userId = http.User.GetUserId();
        await db.PushSubscriptions.Where(s => s.UserId == userId && s.Endpoint == endpoint).ExecuteDeleteAsync(ct);
        return Results.NoContent();
    }
}
