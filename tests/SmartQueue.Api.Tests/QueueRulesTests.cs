using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using SmartQueue.Api.Domain;
using SmartQueue.Api.Features.Auth;
using SmartQueue.Api.Features.Notifications;
using static SmartQueue.Api.Domain.AppointmentStatus;

namespace SmartQueue.Api.Tests;

public class QueueRulesTests
{
    private static Appointment Make(int token, AppointmentStatus status = Booked, long? prioritySequence = null) => new()
    {
        Reference = $"APT-{token}",
        SessionName = "Morning",
        TokenNumber = token,
        QueueSequence = token,
        Status = status,
        Priority = prioritySequence is null ? QueuePriority.Normal : QueuePriority.Emergency,
        PrioritySequence = prioritySequence,
    };

    [Fact]
    public void Orders_emergencies_first_in_the_order_they_were_flagged_then_by_sequence()
    {
        var list = new[]
        {
            Make(1), Make(2, CheckedIn), Make(3, prioritySequence: 20), Make(4, Requeued), Make(5, prioritySequence: 10),
            Make(6, Missed), Make(7, Called), Make(8, Cancelled),
        };

        var order = QueueOrdering.Waiting(list).Select(a => a.TokenNumber);

        Assert.Equal([5, 3, 1, 2, 4], order);
    }

    [Fact]
    public void Patients_ahead_counts_only_eligible_waiting_patients()
    {
        var waiting = QueueOrdering.Waiting([Make(1, Called), Make(2), Make(3, Missed), Make(4)]);
        Assert.Equal(1, QueueOrdering.PatientsAhead(waiting, waiting[1].Id));
        Assert.Null(QueueOrdering.PatientsAhead(waiting, Guid.NewGuid()));
    }

    [Theory]
    [InlineData(Booked, Called, true)]
    [InlineData(Called, Missed, true)]
    [InlineData(Missed, Requeued, true)]
    [InlineData(Requeued, Called, true)]
    [InlineData(Completed, Cancelled, false)]
    [InlineData(NoShow, Requeued, false)]
    [InlineData(Booked, Completed, false)]
    [InlineData(InConsultation, Cancelled, false)]
    public void Appointment_transitions_follow_the_state_model(AppointmentStatus from, AppointmentStatus to, bool allowed) =>
        Assert.Equal(allowed, AppointmentStateMachine.CanTransition(from, to));

    [Fact]
    public void Wait_estimate_uses_configured_average_until_there_is_enough_history()
    {
        Assert.Equal(8, WaitEstimator.AverageConsultMinutes([5, 6, 7], 8));
        Assert.Equal(6, WaitEstimator.AverageConsultMinutes(Enumerable.Repeat(6.0, 9).Append(30).Append(2).ToList(), 8));
    }

    [Fact]
    public void Wait_estimate_is_a_rounded_range_that_includes_time_until_the_session_starts()
    {
        var now = new DateTimeOffset(2026, 10, 14, 8, 0, 0, TimeSpan.Zero);
        var started = WaitEstimator.Estimate(4, consultationInProgress: false, 8, now, now.AddHours(-1), 0);
        Assert.Equal(new WaitEstimate(25, 40), started);

        var later = WaitEstimator.Estimate(0, false, 8, now, now.AddMinutes(60), delayMinutes: 30);
        Assert.True(later.MinMinutes >= 75 && later.MaxMinutes <= 110);
        Assert.Equal(0, later.MinMinutes % 5);
    }

    [Fact]
    public void Totp_matches_the_rfc_6238_test_vector()
    {
        var secret = Base32.Encode(Encoding.ASCII.GetBytes("12345678901234567890"));
        Assert.Equal("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ", secret);
        Assert.Equal("287082", Totp.Compute(secret, 59 / 30));
        Assert.Equal("081804", Totp.Compute(secret, 1111111109 / 30));
    }

    [Fact]
    public void Totp_accepts_adjacent_steps_and_rejects_replays()
    {
        var secret = Totp.NewSecret();
        var now = DateTimeOffset.UtcNow;
        var step = Totp.CurrentStep(now);
        Assert.Equal(step - 1, Totp.Verify(secret, Totp.Compute(secret, step - 1), now, null));
        Assert.Null(Totp.Verify(secret, Totp.Compute(secret, step), now, lastUsedStep: step));
        Assert.Null(Totp.Verify(secret, Totp.Compute(secret, step + 2), now, null));
        Assert.Null(Totp.Verify(secret, "12ab56", now, null));
    }

    [Fact]
    public void Web_push_payload_decrypts_with_the_browsers_keys()
    {
        using var browserKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var bp = browserKey.ExportParameters(false);
        byte[] browserPublic = [0x04, .. bp.Q.X!, .. bp.Q.Y!];
        var authSecret = RandomNumberGenerator.GetBytes(16);
        var message = "{\"title\":\"It's your turn\"}";

        var body = WebPushSender.Encrypt(Encoding.UTF8.GetBytes(message), browserPublic, authSecret);

        var salt = body[..16];
        Assert.Equal(4096u, BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(16)));
        var serverPublic = body[21..(21 + body[20])];
        using var serverKey = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = serverPublic[1..33], Y = serverPublic[33..65] },
        });
        var shared = browserKey.DeriveRawSecretAgreement(serverKey.PublicKey);
        var ikm = HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, authSecret, [.. "WebPush: info\0"u8, .. browserPublic, .. serverPublic]);
        var cek = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 16, salt, "Content-Encoding: aes128gcm\0"u8.ToArray());
        var nonce = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 12, salt, "Content-Encoding: nonce\0"u8.ToArray());

        var record = body[(21 + serverPublic.Length)..];
        var plaintext = new byte[record.Length - 16];
        using (var aes = new AesGcm(cek, 16)) aes.Decrypt(nonce, record[..^16], record[^16..], plaintext);

        Assert.Equal(0x02, plaintext[^1]);
        Assert.Equal(message, Encoding.UTF8.GetString(plaintext[..^1]));
    }

    [Fact]
    public void Generated_vapid_keys_have_the_expected_shape()
    {
        var keys = VapidKeyProvider.Generate();
        Assert.Equal(65, System.Buffers.Text.Base64Url.DecodeFromChars(keys.PublicKey).Length);
        Assert.Equal(32, System.Buffers.Text.Base64Url.DecodeFromChars(keys.PrivateKey).Length);
    }
}
