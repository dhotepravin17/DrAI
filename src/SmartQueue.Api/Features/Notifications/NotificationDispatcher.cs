using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SmartQueue.Api.Domain;
using SmartQueue.Api.Features.Analytics;
using SmartQueue.Api.Features.Auth;
using SmartQueue.Api.Infrastructure;

namespace SmartQueue.Api.Features.Notifications;

public sealed class NotificationOptions
{
    public const string Section = "Notifications";
    public bool WorkerEnabled { get; set; } = true;
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);
    public int MaxAttempts { get; set; } = 3;
}

/// <summary>
/// Delivers outbox messages: an in-app notification per recipient, Web Push to their subscribed devices, and SMS
/// when the patient has no account. Every attempt is logged for failure monitoring (PRD §19.4, §27.3).
/// </summary>
public sealed class NotificationDispatcher(
    AppDbContext db, TimeProvider clock, WebPushSender push, ISmsSender sms, ProductAnalytics analytics, IOptions<NotificationOptions> options,
    ILogger<NotificationDispatcher> logger)
{
    public async Task<int> ProcessPendingAsync(CancellationToken ct, int batchSize = 50)
    {
        var now = clock.GetUtcNow();
        var messages = await db.OutboxMessages
            .Where(m => m.Status == OutboxStatus.Pending && m.NextAttemptAt <= now)
            .OrderBy(m => m.Id).Take(batchSize).ToListAsync(ct);

        foreach (var message in messages)
        {
            try
            {
                await DeliverAsync(message, now, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                message.Attempts++;
                message.LastError = e.Message.Length > 1000 ? e.Message[..1000] : e.Message;
                if (message.Attempts >= options.Value.MaxAttempts || message.ExpiresAt < now)
                    message.Status = OutboxStatus.Failed;
                else
                    message.NextAttemptAt = now.AddSeconds(30 * Math.Pow(2, message.Attempts - 1));
                logger.LogWarning(e, "Notification {Id} ({Type}) failed on attempt {Attempt}.", message.Id, message.EventType, message.Attempts);
            }
            await db.SaveChangesAsync(ct);
        }
        return messages.Count;
    }

    private async Task DeliverAsync(OutboxMessage message, DateTimeOffset now, CancellationToken ct)
    {
        var payload = NotificationOutbox.Deserialize(message.PayloadJson) ?? throw new InvalidOperationException("Unreadable notification payload.");
        if (message.ExpiresAt < now)
        {
            Log(message, payload, null, NotificationChannel.InApp, DeliveryStatus.Failed, "Expired before delivery", now);
            message.Status = OutboxStatus.Failed;
            message.LastError = "Expired before delivery";
            return;
        }

        var existing = await db.Notifications.Where(n => n.OutboxMessageId == message.Id).Select(n => n.UserId).ToListAsync(ct);
        foreach (var userId in payload.UserIds.Except(existing))
        {
            db.Notifications.Add(new Notification
            {
                UserId = userId,
                EventType = message.EventType,
                Title = payload.Title,
                Body = payload.Body,
                AppointmentId = payload.AppointmentId,
                OutboxMessageId = message.Id,
                CreatedAt = now,
            });
            Log(message, payload, userId, NotificationChannel.InApp, DeliveryStatus.Sent, null, now);
        }

        if (push.IsEnabled && payload.UserIds.Count > 0 && message.Attempts == 0)
        {
            var subscriptions = await db.PushSubscriptions.Where(s => payload.UserIds.Contains(s.UserId)).ToListAsync(ct);
            var body = JsonSerializer.Serialize(new { title = payload.Title, body = payload.Body, url = payload.Url, tag = message.DedupKey });
            foreach (var subscription in subscriptions)
            {
                PushOutcome outcome;
                string? detail;
                try
                {
                    (outcome, detail) = await push.SendAsync(subscription, body, ct);
                }
                catch (HttpRequestException e)
                {
                    (outcome, detail) = (PushOutcome.Failed, e.Message);
                }
                if (outcome == PushOutcome.Gone) db.PushSubscriptions.Remove(subscription);
                Log(message, payload, subscription.UserId, NotificationChannel.WebPush,
                    outcome == PushOutcome.Sent ? DeliveryStatus.Sent : DeliveryStatus.Failed, detail, now);
            }
        }

        if (payload.SmsMobile is { } mobile)
        {
            if (!sms.IsConfigured)
            {
                Log(message, payload, null, NotificationChannel.Sms, DeliveryStatus.Skipped, "No SMS provider configured", now);
            }
            else
            {
                string? reference;
                try
                {
                    reference = await sms.SendTextAsync(mobile, $"{payload.Title}: {payload.Body}", ct);
                }
                catch (SmsDeliveryException e)
                {
                    // Logged per attempt; the outbox retries the whole message (in-app rows are not duplicated).
                    Log(message, payload, null, NotificationChannel.Sms, DeliveryStatus.Failed, e.Message, now);
                    throw;
                }
                Log(message, payload, null, NotificationChannel.Sms, DeliveryStatus.Sent, null, now, reference);
            }
        }

        message.Status = OutboxStatus.Processed;
        message.ProcessedAt = now;
    }

    private void Log(
        OutboxMessage m, NotificationPayload p, Guid? userId, NotificationChannel channel, DeliveryStatus status, string? error,
        DateTimeOffset now, string? providerReference = null)
    {
        if (status != DeliveryStatus.Skipped)
            analytics.Track(status == DeliveryStatus.Sent ? ProductEventNames.NotificationSent : ProductEventNames.NotificationFailed,
                new { eventType = m.EventType, channel = channel.ToString(), attempt = m.Attempts + 1 }, channel.ToString());
        db.NotificationLogs.Add(new NotificationLog
        {
            OutboxMessageId = m.Id,
            EventType = m.EventType,
            UserId = userId,
            Channel = channel,
            Status = status,
            Error = error is { Length: > 1000 } ? error[..1000] : error,
            ProviderReference = providerReference,
            AppointmentId = p.AppointmentId,
            CreatedAt = now,
        });
    }
}

public sealed class NotificationWorker(IServiceScopeFactory scopes, IOptions<NotificationOptions> options, ILogger<NotificationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.WorkerEnabled) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                int processed;
                do
                {
                    using var scope = scopes.CreateScope();
                    processed = await scope.ServiceProvider.GetRequiredService<NotificationDispatcher>().ProcessPendingAsync(stoppingToken);
                } while (processed > 0 && !stoppingToken.IsCancellationRequested);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogError(e, "Notification dispatch loop failed; retrying.");
            }
            await Task.Delay(options.Value.PollInterval, stoppingToken).ContinueWith(_ => { }, CancellationToken.None);
        }
    }
}
