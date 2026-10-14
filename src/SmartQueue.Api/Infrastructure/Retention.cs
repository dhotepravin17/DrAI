using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SmartQueue.Api.Domain;

namespace SmartQueue.Api.Infrastructure;

/// <summary>
/// Retention periods from PRD §32.4. They are proposals pending legal review (OQ-03), so they are configuration.
/// Appointment anonymization after 3 years is not automated yet; it waits on the same decision.
/// </summary>
public sealed class RetentionOptions
{
    public const string Section = "Retention";
    public bool WorkerEnabled { get; set; } = true;
    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(6);
    public TimeSpan OtpAndIdempotency { get; set; } = TimeSpan.FromHours(24);
    public TimeSpan NotificationLogs { get; set; } = TimeSpan.FromDays(180);
    public TimeSpan AuditLogs { get; set; } = TimeSpan.FromDays(3 * 365);
    public TimeSpan ProductEvents { get; set; } = TimeSpan.FromDays(3 * 365);
}

public sealed record RetentionResult(int OtpChallenges, int IdempotencyRecords, int Notifications, int NotificationLogs, int OutboxMessages,
    int AuditLogs, int ProductEvents)
{
    public int Total => OtpChallenges + IdempotencyRecords + Notifications + NotificationLogs + OutboxMessages + AuditLogs + ProductEvents;
}

public sealed class RetentionService(AppDbContext db, IOptions<RetentionOptions> options, TimeProvider clock)
{
    public async Task<RetentionResult> RunAsync(CancellationToken ct)
    {
        var o = options.Value;
        var now = clock.GetUtcNow();
        var shortCutoff = now - o.OtpAndIdempotency;
        var notificationCutoff = now - o.NotificationLogs;
        var auditCutoff = now - o.AuditLogs;
        var eventCutoff = now - o.ProductEvents;

        var otp = await db.OtpChallenges.Where(c => c.CreatedAt < shortCutoff).ExecuteDeleteAsync(ct);
        var idempotency = await db.IdempotencyRecords.Where(r => r.CreatedAt < shortCutoff).ExecuteDeleteAsync(ct);
        var notifications = await db.Notifications.Where(n => n.CreatedAt < notificationCutoff).ExecuteDeleteAsync(ct);
        var logs = await db.NotificationLogs.Where(l => l.CreatedAt < notificationCutoff).ExecuteDeleteAsync(ct);
        // Pending messages are never deleted, however old, so a stuck backlog stays visible on the admin dashboard.
        var outbox = await db.OutboxMessages
            .Where(m => m.Status != OutboxStatus.Pending && m.CreatedAt < notificationCutoff)
            .ExecuteDeleteAsync(ct);
        var audit = await db.AdminAuditLogs.Where(a => a.OccurredAt < auditCutoff).ExecuteDeleteAsync(ct);
        var events = await db.ProductEvents.Where(e => e.OccurredAt < eventCutoff).ExecuteDeleteAsync(ct);
        return new RetentionResult(otp, idempotency, notifications, logs, outbox, audit, events);
    }
}

public sealed class RetentionWorker(IServiceScopeFactory scopes, IOptions<RetentionOptions> options, ILogger<RetentionWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.WorkerEnabled) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var result = await scope.ServiceProvider.GetRequiredService<RetentionService>().RunAsync(stoppingToken);
                if (result.Total > 0) logger.LogInformation("Retention cleanup removed {Result}.", result);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogError(e, "Retention cleanup failed; retrying at the next interval.");
            }
            await Task.Delay(options.Value.Interval, stoppingToken).ContinueWith(_ => { }, CancellationToken.None);
        }
    }
}
