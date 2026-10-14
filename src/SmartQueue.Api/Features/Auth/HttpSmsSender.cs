using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SmartQueue.Api.Common;

namespace SmartQueue.Api.Features.Auth;

/// <summary>
/// SMS gateway settings (PRD §6.2). Indian SMS requires TRAI DLT registration: the sender ID, the principal entity ID
/// and one template ID per message type must be registered before production use.
/// </summary>
public sealed class SmsOptions
{
    public const string Section = "Sms";

    /// <summary>"Http" sends through <see cref="Endpoint"/>; anything else uses the logging sender.</summary>
    public string? Provider { get; set; }

    public string? Endpoint { get; set; }
    public string? ApiKey { get; set; }
    public string? SenderId { get; set; }
    public string? DltEntityId { get; set; }
    public string? OtpTemplateId { get; set; }
    public string? NotificationTemplateId { get; set; }
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);

    public bool UsesHttp => string.Equals(Provider, "Http", StringComparison.OrdinalIgnoreCase);
}

public sealed class SmsDeliveryException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Sends SMS through an HTTP gateway as one JSON POST per message:
/// <c>{ to, senderId, dltEntityId, dltTemplateId, type, message }</c> with a bearer API key.
/// Most Indian gateways accept this shape directly or through a thin adapter. A JSON response with
/// <c>id</c>, <c>messageId</c> or <c>requestId</c> is stored as the provider reference.
/// </summary>
public sealed class HttpSmsSender(HttpClient http, IOptions<SmsOptions> options, IOptions<OtpOptions> otp, ILogger<HttpSmsSender> logger)
    : ISmsSender
{
    public const string ClientName = "sms";

    public bool IsConfigured => true;

    public async Task SendOtpAsync(string mobileNumber, string code, CancellationToken ct) =>
        await SendAsync(mobileNumber, SmsTexts.Otp(code, otp.Value.WebOtpDomain), "otp", options.Value.OtpTemplateId, ct);

    public Task<string?> SendTextAsync(string mobileNumber, string text, CancellationToken ct) =>
        SendAsync(mobileNumber, text, "notification", options.Value.NotificationTemplateId, ct);

    private async Task<string?> SendAsync(string mobileNumber, string text, string type, string? templateId, CancellationToken ct)
    {
        var o = options.Value;
        using var request = new HttpRequestMessage(HttpMethod.Post, o.Endpoint)
        {
            Content = JsonContent.Create(new
            {
                to = mobileNumber,
                senderId = o.SenderId,
                dltEntityId = o.DltEntityId,
                dltTemplateId = templateId,
                type,
                message = text,
            }),
        };
        request.Headers.Authorization = new("Bearer", o.ApiKey);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning(e, "SMS gateway unreachable for {Mobile}.", MobileNumber.Mask(mobileNumber));
            throw new SmsDeliveryException("SMS gateway unreachable.", e);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("SMS gateway returned {Status} for {Mobile}.", (int)response.StatusCode, MobileNumber.Mask(mobileNumber));
                throw new SmsDeliveryException($"SMS gateway returned {(int)response.StatusCode}: {Truncate(body, 300)}");
            }
            return ReadReference(body);
        }
    }

    private static string? ReadReference(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            foreach (var name in (string[])["id", "messageId", "requestId"])
                if (doc.RootElement.TryGetProperty(name, out var value))
                    return Truncate(value.ToString(), 200);
        }
        catch (JsonException)
        {
        }
        return null;
    }

    private static string Truncate(string value, int max) => value.Length > max ? value[..max] : value;

    /// <summary>Fails start-up when the HTTP provider is selected without the settings DLT delivery needs.</summary>
    public static bool IsValid(SmsOptions o) =>
        !o.UsesHttp || (Uri.TryCreate(o.Endpoint, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
                        !string.IsNullOrWhiteSpace(o.ApiKey) && !string.IsNullOrWhiteSpace(o.SenderId) &&
                        !string.IsNullOrWhiteSpace(o.OtpTemplateId));
}
