using System.Collections.Concurrent;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using SmartQueue.Api.Features.Auth;
using SmartQueue.Api.Features.Notifications;
using SmartQueue.Api.Infrastructure;

namespace SmartQueue.Api.Tests.Infrastructure;

/// <summary>Hosts the API against a throwaway LocalDB database that is deleted when the test run ends.</summary>
public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string SuperAdminMobile = "9000000009";
    public const string SecondAdminMobile = "9000000008";

    /// <summary>Wednesday 14 October 2026, 10:00 IST. Business logic runs on this clock; tokens use the real clock.</summary>
    public static readonly DateTimeOffset Start = new(2026, 10, 14, 4, 30, 0, TimeSpan.Zero);

    private readonly string _connectionString =
        $"Server=(localdb)\\SmartQueue;Database=SmartQueue_Test_{Guid.NewGuid():N};Trusted_Connection=True;TrustServerCertificate=True";

    public CapturingSmsSender Sms { get; } = new();
    public FakeTimeProvider Clock { get; } = new(Start);

    internal Task<Session>? AdminSession { get; set; }
    internal ConcurrentDictionary<string, string> TotpSecrets { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Default", _connectionString);
        builder.UseSetting("Jwt:SigningKey", "test-signing-key-0123456789abcdef0123456789");
        builder.UseSetting("Bootstrap:ApplyMigrationsOnStartup", "true");
        builder.UseSetting("Bootstrap:SuperAdmins:0:MobileNumber", SuperAdminMobile);
        builder.UseSetting("Bootstrap:SuperAdmins:0:FullName", "Test Admin");
        builder.UseSetting("Bootstrap:SuperAdmins:1:MobileNumber", SecondAdminMobile);
        builder.UseSetting("Bootstrap:SuperAdmins:1:FullName", "Second Admin");
        builder.UseSetting("RateLimits:OtpPerMinutePerIp", "100000");
        builder.UseSetting("RateLimits:SearchPerMinutePerIp", "100000");
        builder.UseSetting("Notifications:WorkerEnabled", "false");
        builder.UseSetting("Retention:WorkerEnabled", "false");
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<ISmsSender>(Sms);
            services.AddSingleton<TimeProvider>(Clock);
        });
    }

    public Task InitializeAsync()
    {
        _ = Server; // starts the host, which applies migrations and seeds the Super Admins
        return Task.CompletedTask;
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        using (var scope = Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureDeletedAsync();
        }
        await DisposeAsync();
    }

    public async Task<T> WithDbAsync<T>(Func<AppDbContext, Task<T>> action)
    {
        using var scope = Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    /// <summary>Runs the notification dispatcher, which the background worker does every few seconds in production.</summary>
    public async Task DispatchNotificationsAsync()
    {
        using var scope = Services.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<NotificationDispatcher>();
        while (await dispatcher.ProcessPendingAsync(CancellationToken.None) > 0) { }
    }
}

public sealed class CapturingSmsSender : ISmsSender
{
    private readonly ConcurrentDictionary<string, string> _lastCodes = new();

    public bool IsConfigured => false;

    public Task SendOtpAsync(string mobileNumber, string code, CancellationToken ct)
    {
        _lastCodes[mobileNumber] = code;
        return Task.CompletedTask;
    }

    public Task<string?> SendTextAsync(string mobileNumber, string text, CancellationToken ct) => Task.FromResult<string?>(null);

    public string LastCodeFor(string tenDigitMobile) => _lastCodes["+91" + tenDigitMobile];
}

[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<ApiFactory>
{
    public const string Name = "api";
}
