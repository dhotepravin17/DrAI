using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace SmartQueue.Api.Features.Auth;

/// <summary>Time-based one-time passwords (RFC 6238, SHA-1, 6 digits, 30-second steps) for Super Admin sign-in.</summary>
public static class Totp
{
    public const int StepSeconds = 30;

    public static string NewSecret() => Base32.Encode(RandomNumberGenerator.GetBytes(20));

    public static long CurrentStep(DateTimeOffset now) => now.ToUnixTimeSeconds() / StepSeconds;

    public static string Compute(string base32Secret, long step)
    {
        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, step);
        var hash = HMACSHA1.HashData(Base32.Decode(base32Secret), counter);
        var offset = hash[^1] & 0x0F;
        var binary = BinaryPrimitives.ReadInt32BigEndian(hash.AsSpan(offset)) & 0x7FFFFFFF;
        return (binary % 1_000_000).ToString("D6");
    }

    /// <summary>Accepts the current step ±1 for clock drift, and never a step at or before the last one used (replay protection).</summary>
    public static long? Verify(string base32Secret, string? code, DateTimeOffset now, long? lastUsedStep)
    {
        code = code?.Trim().Replace(" ", "");
        if (code is not { Length: 6 } || !code.All(char.IsAsciiDigit)) return null;
        var current = CurrentStep(now);
        for (var step = current - 1; step <= current + 1; step++)
        {
            if (lastUsedStep >= step) continue;
            if (CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Compute(base32Secret, step)), Encoding.ASCII.GetBytes(code)))
                return step;
        }
        return null;
    }

    public static string OtpAuthUri(string base32Secret, string accountLabel) =>
        $"otpauth://totp/{Uri.EscapeDataString("Smart Doctor:" + accountLabel)}?secret={base32Secret}&issuer=Smart%20Doctor&algorithm=SHA1&digits=6&period=30";
}

public static class Base32
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string Encode(ReadOnlySpan<byte> data)
    {
        var sb = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                sb.Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }
        if (bits > 0) sb.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        return sb.ToString();
    }

    public static byte[] Decode(string text)
    {
        var clean = text.Trim().TrimEnd('=').Replace(" ", "").ToUpperInvariant();
        var output = new List<byte>(clean.Length * 5 / 8);
        int buffer = 0, bits = 0;
        foreach (var c in clean)
        {
            var value = Alphabet.IndexOf(c);
            if (value < 0) throw new FormatException("Invalid base32 character.");
            buffer = (buffer << 5) | value;
            bits += 5;
            if (bits >= 8)
            {
                output.Add((byte)((buffer >> (bits - 8)) & 0xFF));
                bits -= 8;
            }
        }
        return [.. output];
    }
}
