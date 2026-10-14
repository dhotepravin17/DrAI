using SmartQueue.Api.Common;
using SmartQueue.Api.Domain;
using static SmartQueue.Api.Domain.DoctorRegistrationStatus;

namespace SmartQueue.Api.Tests;

public class DomainRulesTests
{
    [Theory]
    [InlineData("9876543210", "+919876543210")]
    [InlineData("+91 98765 43210", "+919876543210")]
    [InlineData("09876543210", "+919876543210")]
    [InlineData("919876543210", "+919876543210")]
    [InlineData("98765-43210", "+919876543210")]
    public void Normalizes_valid_indian_mobile_numbers(string input, string expected)
    {
        Assert.True(MobileNumber.TryNormalize(input, out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("5876543210")]
    [InlineData("98765432101")]
    [InlineData("abcdefghij")]
    public void Rejects_invalid_mobile_numbers(string input) =>
        Assert.False(MobileNumber.TryNormalize(input, out _));

    [Fact]
    public void Masks_mobile_numbers() =>
        Assert.Equal("+91 98xxxxx210", MobileNumber.Mask("+919876543210"));

    [Theory]
    [InlineData(Draft, PendingVerification)]
    [InlineData(PendingVerification, Verified)]
    [InlineData(PendingVerification, Rejected)]
    [InlineData(Rejected, PendingVerification)]
    [InlineData(Verified, Suspended)]
    [InlineData(Suspended, Verified)]
    [InlineData(Verified, Deactivated)]
    [InlineData(Suspended, Deactivated)]
    public void Allows_documented_transitions(DoctorRegistrationStatus from, DoctorRegistrationStatus to) =>
        Assert.True(DoctorRegistrationStateMachine.CanTransition(from, to));

    [Theory]
    [InlineData(Draft, Verified)]
    [InlineData(Rejected, Verified)]
    [InlineData(Deactivated, Verified)]
    [InlineData(Deactivated, PendingVerification)]
    [InlineData(PendingVerification, Suspended)]
    [InlineData(Verified, PendingVerification)]
    public void Blocks_other_transitions(DoctorRegistrationStatus from, DoctorRegistrationStatus to) =>
        Assert.False(DoctorRegistrationStateMachine.CanTransition(from, to));

    [Theory]
    [InlineData("NGP", 1, "NGP-Dr-0001")]
    [InlineData("MUM", 42, "MUM-Dr-0042")]
    [InlineData("CDP", 9999, "CDP-Dr-9999")]
    public void Formats_doctor_codes(string city, int sequence, string expected) =>
        Assert.Equal(expected, DoctorCodeFormat.Format(city, sequence));

    [Theory]
    [InlineData("NGP-Dr-0001", true)]
    [InlineData("ngp-dr-0001", true)]
    [InlineData("NGP-0001", false)]
    [InlineData("NGPX-Dr-0001", false)]
    public void Recognizes_doctor_codes(string value, bool expected) =>
        Assert.Equal(expected, DoctorCodeFormat.LooksLikeDoctorCode(value));
}
