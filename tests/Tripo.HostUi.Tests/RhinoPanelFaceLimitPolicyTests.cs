using Xunit;

namespace Tripo.HostUi.Tests;

public sealed class RhinoPanelFaceLimitPolicyTests
{
    [Theory]
    [InlineData(int.MinValue, 500)]
    [InlineData(499, 500)]
    [InlineData(500, 500)]
    [InlineData(12_345, 12_345)]
    [InlineData(200_000, 200_000)]
    [InlineData(200_001, 200_000)]
    [InlineData(int.MaxValue, 200_000)]
    public void ClampSnapsIntegerValuesToTheSupportedEnvelope(
        int value,
        int expected)
    {
        Assert.Equal(
            expected,
            Tripo.HostUi.RhinoPanelFaceLimitPolicy.Clamp(value));
    }

    [Theory]
    [InlineData(double.NegativeInfinity, 500)]
    [InlineData(-1.7976931348623157E+308, 500)]
    [InlineData(499.999, 500)]
    [InlineData(500.4, 500)]
    [InlineData(500.5, 501)]
    [InlineData(18_234.49, 18_234)]
    [InlineData(18_234.5, 18_235)]
    [InlineData(199_999.6, 200_000)]
    [InlineData(200_000.001, 200_000)]
    [InlineData(1.7976931348623157E+308, 200_000)]
    [InlineData(double.PositiveInfinity, 200_000)]
    public void TrySnapInteractiveClampsBeforeSafeWholeNumberRounding(
        double value,
        int expected)
    {
        bool valid = Tripo.HostUi.RhinoPanelFaceLimitPolicy
            .TrySnapInteractive(value, 20_000, out int snapped);

        Assert.True(valid);
        Assert.Equal(expected, snapped);
    }

    [Theory]
    [InlineData(48_000, 48_000)]
    [InlineData(int.MinValue, 500)]
    [InlineData(int.MaxValue, 200_000)]
    public void TrySnapInteractiveRejectsNaNAndRestoresALegalFallback(
        int fallback,
        int expected)
    {
        bool valid = Tripo.HostUi.RhinoPanelFaceLimitPolicy
            .TrySnapInteractive(double.NaN, fallback, out int snapped);

        Assert.False(valid);
        Assert.Equal(expected, snapped);
    }
}
