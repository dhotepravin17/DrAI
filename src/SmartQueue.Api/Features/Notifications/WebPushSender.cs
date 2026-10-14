using System.Buffers.Binary;
using System.Buffers.Text;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using SmartQueue.Api.Domain;

namespace SmartQueue.Api.Features.Notifications;

public sealed class PushOptions
{
    public const string Section = "Push";

    /// <summary>VAPID public key: base64url of the uncompressed P-256 point (65 bytes).</summary>
    public string? PublicKey { get; set; }

    /// <summary>VAPID private key: base64url of the P-256 private scalar (32 bytes). Keep secret.</summary>
    public string? PrivateKey { get; set; }

    public string Subject { get; set; } = "mailto:support@smartdoctorqueue.in";

    /// <summary>Development only: create and reuse a local key file when no keys are configured.</summary>
    public bool GenerateDevelopmentKeys { get; set; } = true;
}

public sealed record VapidKeys(string PublicKey, string PrivateKey);

/// <summary>Resolves VAPID keys from configuration, or a generated key file in Development. Null disables push.</summary>
public sealed class VapidKeyProvider
{
    public VapidKeys? Keys { get; }

    public VapidKeyProvider(IOptions<PushOptions> options, IHostEnvironment env, ILogger<VapidKeyProvider> logger)
    {
        var o = options.Value;
        if (!string.IsNullOrWhiteSpace(o.PublicKey) && !string.IsNullOrWhiteSpace(o.PrivateKey))
        {
            Keys = new VapidKeys(o.PublicKey, o.PrivateKey);
            return;
        }
        if (!env.IsDevelopment() || !o.GenerateDevelopmentKeys)
        {
            logger.LogInformation("Web push is disabled: Push:PublicKey and Push:PrivateKey are not configured.");
            return;
        }

        var path = Path.Combine(env.ContentRootPath, "vapid-keys.dev.json");
        if (File.Exists(path))
        {
            Keys = JsonSerializer.Deserialize<VapidKeys>(File.ReadAllText(path));
            return;
        }
        Keys = Generate();
        File.WriteAllText(path, JsonSerializer.Serialize(Keys));
        logger.LogInformation("Generated development VAPID keys at {Path}.", path);
    }

    public static VapidKeys Generate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var p = key.ExportParameters(includePrivateParameters: true);
        return new VapidKeys(Base64Url.EncodeToString([0x04, .. p.Q.X!, .. p.Q.Y!]), Base64Url.EncodeToString(p.D!));
    }
}

public enum PushOutcome { Sent, Gone, Failed }

/// <summary>
/// Sends Web Push messages with aes128gcm payload encryption (RFC 8291) and VAPID authentication (RFC 8292),
/// using only the .NET cryptography APIs.
/// </summary>
public sealed class WebPushSender(IHttpClientFactory httpClients, VapidKeyProvider vapid, IOptions<PushOptions> options, TimeProvider clock)
{
    private const int RecordSize = 4096;

    public bool IsEnabled => vapid.Keys is not null;

    public async Task<(PushOutcome Outcome, string? Detail)> SendAsync(PushSubscription subscription, string payload, CancellationToken ct)
    {
        if (vapid.Keys is null) return (PushOutcome.Failed, "Push is not configured.");

        var body = Encrypt(Encoding.UTF8.GetBytes(payload), Base64Url.DecodeFromChars(subscription.P256dh), Base64Url.DecodeFromChars(subscription.Auth));
        var endpoint = new Uri(subscription.Endpoint);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        request.Content.Headers.ContentEncoding.Add("aes128gcm");
        request.Headers.Add("TTL", "300");
        request.Headers.Add("Urgency", "high");
        request.Headers.TryAddWithoutValidation("Authorization",
            $"vapid t={VapidToken(endpoint.GetLeftPart(UriPartial.Authority))}, k={vapid.Keys.PublicKey}");

        using var response = await httpClients.CreateClient("webpush").SendAsync(request, ct);
        return response.StatusCode switch
        {
            HttpStatusCode.Created or HttpStatusCode.OK or HttpStatusCode.Accepted => (PushOutcome.Sent, null),
            HttpStatusCode.NotFound or HttpStatusCode.Gone => (PushOutcome.Gone, "Subscription expired"),
            _ => (PushOutcome.Failed, $"Push service returned {(int)response.StatusCode}"),
        };
    }

    private string VapidToken(string audience)
    {
        var keys = vapid.Keys!;
        var publicKey = Base64Url.DecodeFromChars(keys.PublicKey);
        using var ecdsa = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = Base64Url.DecodeFromChars(keys.PrivateKey),
            Q = new ECPoint { X = publicKey[1..33], Y = publicKey[33..65] },
        });
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Audience = audience,
            Expires = clock.GetUtcNow().AddHours(12).UtcDateTime,
            Subject = new ClaimsIdentity([new Claim("sub", options.Value.Subject)]),
            SigningCredentials = new SigningCredentials(new ECDsaSecurityKey(ecdsa), SecurityAlgorithms.EcdsaSha256),
        });
    }

    /// <summary>RFC 8291 §3–4: a single aes128gcm record keyed from an ephemeral ECDH exchange and the subscription's auth secret.</summary>
    public static byte[] Encrypt(byte[] plaintext, byte[] userAgentPublicKey, byte[] authSecret)
    {
        using var serverKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var sp = serverKey.ExportParameters(false);
        byte[] serverPublic = [0x04, .. sp.Q.X!, .. sp.Q.Y!];

        using var userAgentKey = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = userAgentPublicKey[1..33], Y = userAgentPublicKey[33..65] },
        });
        var sharedSecret = serverKey.DeriveRawSecretAgreement(userAgentKey.PublicKey);

        byte[] keyInfo = [.. "WebPush: info\0"u8, .. userAgentPublicKey, .. serverPublic];
        var ikm = HKDF.DeriveKey(HashAlgorithmName.SHA256, sharedSecret, 32, authSecret, keyInfo);
        var salt = RandomNumberGenerator.GetBytes(16);
        var contentKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 16, salt, "Content-Encoding: aes128gcm\0"u8.ToArray());
        var nonce = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 12, salt, "Content-Encoding: nonce\0"u8.ToArray());

        byte[] padded = [.. plaintext, 0x02];
        var ciphertext = new byte[padded.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(contentKey, tag.Length)) aes.Encrypt(nonce, padded, ciphertext, tag);

        var header = new byte[16 + 4 + 1 + serverPublic.Length];
        salt.CopyTo(header, 0);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(16), RecordSize);
        header[20] = (byte)serverPublic.Length;
        serverPublic.CopyTo(header, 21);
        return [.. header, .. ciphertext, .. tag];
    }
}
