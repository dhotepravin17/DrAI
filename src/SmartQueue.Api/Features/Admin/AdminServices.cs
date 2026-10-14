using Microsoft.EntityFrameworkCore;
using SmartQueue.Api.Common;
using SmartQueue.Api.Domain;
using SmartQueue.Api.Infrastructure;

namespace SmartQueue.Api.Features.Admin;

/// <summary>Adds audit rows to the current unit of work; they are saved with the change they describe.</summary>
public sealed class AuditLogger(AppDbContext db, IHttpContextAccessor http, TimeProvider clock)
{
    public void Record(string action, string entityType, string entityId, string? purpose = null, string? details = null,
        Guid? actorUserId = null)
    {
        var context = http.HttpContext;
        var sub = context?.User.FindFirst("sub")?.Value;
        db.AdminAuditLogs.Add(new AdminAuditLog
        {
            ActorUserId = actorUserId ?? (Guid.TryParse(sub, out var id) ? id : null),
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            Purpose = purpose,
            Details = details is { Length: > 2000 } ? details[..2000] : details,
            IpAddress = context?.ClientIp(),
            OccurredAt = clock.GetUtcNow(),
        });
    }
}

/// <summary>FR-02: issues Doctor IDs from a per-city counter. Must run inside the caller's transaction.</summary>
public sealed class DoctorCodeGenerator(AppDbContext db, ILogger<DoctorCodeGenerator> logger)
{
    public async Task<string> NextAsync(string cityCode, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Doctor codes must be generated inside a transaction.");

        // The UPDATE takes a row lock that is held until commit, so concurrent verifications in one city are serialized
        // and a rolled-back verification also rolls back its number.
        var values = await db.Database
            .SqlQuery<int>($"UPDATE [DoctorCodeSequences] SET [LastValue] = [LastValue] + 1 OUTPUT inserted.[LastValue] AS [Value] WHERE [CityCode] = {cityCode}")
            .ToListAsync(ct);

        if (values.Count == 0)
            throw ApiException.Conflict($"City code {cityCode} has no Doctor ID sequence.");

        var next = values[0];
        if (next > DoctorCodeFormat.MaxSequence)
            throw ApiException.Conflict($"Doctor ID capacity for {cityCode} is exhausted.", "Decide the expansion strategy (PRD OQ-07).");
        if (next >= DoctorCodeFormat.CapacityWarningThreshold)
            logger.LogWarning("Doctor ID sequence for {CityCode} has reached {Value} of {Max}.", cityCode, next, DoctorCodeFormat.MaxSequence);

        return DoctorCodeFormat.Format(cityCode, next);
    }
}
