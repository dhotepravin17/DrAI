using System.Text.RegularExpressions;

namespace SmartQueue.Api.Common;

/// <summary>Indian mobile numbers, stored in E.164 form (+91XXXXXXXXXX).</summary>
public static partial class MobileNumber
{
    public static bool TryNormalize(string? input, out string normalized)
    {
        normalized = "";
        if (string.IsNullOrWhiteSpace(input)) return false;

        var digits = NonDigits().Replace(input, "");
        if (digits.Length == 12 && digits.StartsWith("91")) digits = digits[2..];
        else if (digits.Length == 11 && digits.StartsWith('0')) digits = digits[1..];

        if (!TenDigitMobile().IsMatch(digits)) return false;
        normalized = "+91" + digits;
        return true;
    }

    public static string Mask(string normalized) =>
        normalized.Length == 13 ? $"+91 {normalized[3..5]}xxxxx{normalized[10..]}" : "hidden";

    [GeneratedRegex(@"\D")]
    private static partial Regex NonDigits();

    [GeneratedRegex(@"^[6-9]\d{9}$")]
    private static partial Regex TenDigitMobile();
}
