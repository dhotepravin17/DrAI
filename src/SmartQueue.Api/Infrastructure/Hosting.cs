using System.Threading.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SmartQueue.Api.Common;
using SmartQueue.Api.Domain;

namespace SmartQueue.Api.Infrastructure;

public static class AuthPolicies
{
    public const string SuperAdmin = "SuperAdmin";
}

public sealed class RateLimitOptions
{
    public const string Section = "RateLimits";
    public int OtpPerMinutePerIp { get; set; } = 10;
    public int SearchPerMinutePerIp { get; set; } = 60;
}

public static class RateLimitPolicies
{
    public const string Otp = "otp";
    public const string Search = "search";

    public static IServiceCollection AddAppRateLimiting(this IServiceCollection services)
    {
        return services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.AddPolicy(Otp, ctx => PerIp(ctx, Otp, opts => opts.OtpPerMinutePerIp));
            o.AddPolicy(Search, ctx => PerIp(ctx, Search, opts => opts.SearchPerMinutePerIp));
        });
    }

    private static RateLimitPartition<string> PerIp(HttpContext ctx, string policy, Func<RateLimitOptions, int> limit)
    {
        var permits = limit(ctx.RequestServices.GetRequiredService<IOptions<RateLimitOptions>>().Value);
        return RateLimitPartition.GetFixedWindowLimiter($"{policy}:{ctx.ClientIp() ?? "unknown"}", _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permits,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        });
    }
}

public sealed class BootstrapOptions
{
    public const string Section = "Bootstrap";
    public bool ApplyMigrationsOnStartup { get; set; }
    public List<SuperAdminSeed> SuperAdmins { get; set; } = [];

    public sealed class SuperAdminSeed
    {
        public string MobileNumber { get; set; } = "";
        public string FullName { get; set; } = "Platform Admin";
    }
}

/// <summary>Applies migrations (when enabled) and creates configured Super Admin accounts if they do not exist.</summary>
public sealed class BootstrapSeeder(AppDbContext db, IOptions<BootstrapOptions> options, TimeProvider clock, ILogger<BootstrapSeeder> logger)
{
    public async Task RunAsync(CancellationToken ct = default)
    {
        if (options.Value.ApplyMigrationsOnStartup)
            await db.Database.MigrateAsync(ct);

        foreach (var seed in options.Value.SuperAdmins)
        {
            if (!MobileNumber.TryNormalize(seed.MobileNumber, out var mobile))
            {
                logger.LogWarning("Skipping Super Admin seed with an invalid mobile number.");
                continue;
            }

            var exists = await db.UserRoleAssignments.AnyAsync(r =>
                r.Role == RoleName.SuperAdmin && r.RevokedAt == null &&
                db.Users.Any(u => u.Id == r.UserId && u.MobileNumber == mobile), ct);
            if (exists) continue;

            var now = clock.GetUtcNow();
            var user = new User { MobileNumber = mobile, FullName = seed.FullName, CreatedAt = now };
            db.Users.Add(user);
            db.UserRoleAssignments.Add(new UserRoleAssignment { UserId = user.Id, Role = RoleName.SuperAdmin, GrantedAt = now });
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Created Super Admin account for {Mobile}.", MobileNumber.Mask(mobile));
        }
    }
}
