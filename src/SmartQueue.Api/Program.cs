using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SmartQueue.Api.Common;
using SmartQueue.Api.Domain;
using SmartQueue.Api.Features.Admin;
using SmartQueue.Api.Features.Analytics;
using SmartQueue.Api.Features.Plans;
using SmartQueue.Api.Features.Auth;
using SmartQueue.Api.Features.Booking;
using SmartQueue.Api.Features.Doctors;
using SmartQueue.Api.Features.Family;
using SmartQueue.Api.Features.History;
using SmartQueue.Api.Features.Staff;
using Microsoft.AspNetCore.DataProtection;
using SmartQueue.Api.Features.Notifications;
using SmartQueue.Api.Features.Queue;
using SmartQueue.Api.Features.Scheduling;
using SmartQueue.Api.Features.Me;
using SmartQueue.Api.Features.Reference;
using SmartQueue.Api.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<JwtOptions>().BindConfiguration(JwtOptions.Section)
    .Validate(o => o.SigningKey.Length >= 32, "Jwt:SigningKey must be at least 32 characters.")
    .ValidateOnStart();
builder.Services.AddOptions<OtpOptions>().BindConfiguration(OtpOptions.Section);
builder.Services.AddOptions<AdminAuthOptions>().BindConfiguration(AdminAuthOptions.Section);
builder.Services.AddOptions<RateLimitOptions>().BindConfiguration(RateLimitOptions.Section);
builder.Services.AddOptions<BootstrapOptions>().BindConfiguration(BootstrapOptions.Section);

builder.Services.AddDbContext<AppDbContext>((sp, o) =>
    o.UseSqlServer(sp.GetRequiredService<IConfiguration>().GetConnectionString("Default")));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<TokenService>();
builder.Services.AddOptions<SmsOptions>().BindConfiguration(SmsOptions.Section)
    .Validate(HttpSmsSender.IsValid, "Sms:Provider Http needs an https Endpoint, ApiKey, SenderId and OtpTemplateId.")
    .ValidateOnStart();
builder.Services.AddHttpClient(HttpSmsSender.ClientName, (sp, c) => c.Timeout = sp.GetRequiredService<IOptions<SmsOptions>>().Value.Timeout);
builder.Services.AddSingleton<ISmsSender>(sp => sp.GetRequiredService<IOptions<SmsOptions>>().Value.UsesHttp
    ? ActivatorUtilities.CreateInstance<HttpSmsSender>(sp, sp.GetRequiredService<IHttpClientFactory>().CreateClient(HttpSmsSender.ClientName))
    : ActivatorUtilities.CreateInstance<LoggingSmsSender>(sp));
builder.Services.AddScoped<OtpService>();
builder.Services.AddScoped<AuditLogger>();
builder.Services.AddScoped<ProductAnalytics>();
builder.Services.AddOptions<PlanOptions>().BindConfiguration(PlanOptions.Section);
builder.Services.AddScoped<PlanService>();
builder.Services.AddOptions<RetentionOptions>().BindConfiguration(RetentionOptions.Section);
builder.Services.AddScoped<RetentionService>();
builder.Services.AddHostedService<RetentionWorker>();
builder.Services.AddScoped<DoctorCodeGenerator>();
builder.Services.AddScoped<BootstrapSeeder>();
builder.Services.AddScoped<NotificationOutbox>();
builder.Services.AddScoped<SessionPlanner>();
builder.Services.AddScoped<ScheduleService>();
builder.Services.AddScoped<QueueEngine>();
builder.Services.AddScoped<QueueStatusService>();
builder.Services.AddScoped<ClinicAccess>();
builder.Services.AddScoped<PatientAccess>();
builder.Services.AddSingleton<IQueueEvents, SignalRQueueEvents>();
builder.Services.AddSignalR();
builder.Services.AddOptions<PushOptions>().BindConfiguration(PushOptions.Section);
builder.Services.AddOptions<NotificationOptions>().BindConfiguration(NotificationOptions.Section);
builder.Services.AddSingleton<VapidKeyProvider>();
builder.Services.AddSingleton<WebPushSender>();
builder.Services.AddScoped<NotificationDispatcher>();
builder.Services.AddHostedService<NotificationWorker>();
builder.Services.AddHttpClient("webpush", c => c.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddDataProtection().SetApplicationName("SmartDoctorQueue");
builder.Services.AddHttpContextAccessor();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<IOptions<JwtOptions>>((bearer, jwt) =>
    {
        bearer.MapInboundClaims = false;
        bearer.TokenValidationParameters = TokenService.CreateValidationParameters(jwt.Value, TokenService.AccessAudience);
        bearer.Events = new JwtBearerEvents
        {
            // WebSockets cannot send headers, so the SignalR client passes the token in the query string.
            OnMessageReceived = context =>
            {
                var token = context.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(token) && context.HttpContext.Request.Path.StartsWithSegments("/hubs"))
                    context.Token = token;
                return Task.CompletedTask;
            },
        };
    });
builder.Services.AddAuthorizationBuilder()
    .AddPolicy(AuthPolicies.SuperAdmin, p => p.RequireRole(nameof(RoleName.SuperAdmin)));

builder.Services.AddAppRateLimiting();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddOpenApi();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

var api = app.MapGroup("/api/v1");
api.MapAuthEndpoints();
api.MapMeEndpoints();
api.MapReferenceEndpoints();
api.MapDoctorRegistrationEndpoints();
api.MapDirectoryEndpoints();
api.MapAdminEndpoints();
api.MapBookingEndpoints();
api.MapClinicQueueEndpoints();
api.MapScheduleEndpoints();
api.MapAnnouncementEndpoints();
api.MapFamilyEndpoints();
api.MapStaffEndpoints();
api.MapHistoryEndpoints();
api.MapNotificationEndpoints();
api.MapAdminOversightEndpoints();
api.MapAnalyticsEndpoints();
app.MapHub<QueueHub>("/hubs/queue");
app.MapGet("/health", () => Results.Ok(new { status = "ok" })).ExcludeFromDescription();

using (var scope = app.Services.CreateScope())
{
    await scope.ServiceProvider.GetRequiredService<BootstrapSeeder>().RunAsync();
}

app.Run();

public partial class Program;
