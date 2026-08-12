namespace Tripo.HostUi;

internal static class RhinoPanelFaceLimitPolicy
{
    internal const int Minimum = 500;
    internal const int Maximum = 200_000;
    internal const int Default = 20_000;

    internal static int Clamp(int value) =>
        Math.Clamp(value, Minimum, Maximum);

    internal static bool TrySnapInteractive(
        double value,
        int fallback,
        out int snapped)
    {
        if (double.IsNaN(value))
        {
            snapped = Clamp(fallback);
            return false;
        }

        // Clamp while the value is still a double. This handles infinities and
        // prevents an overflowing conversion for pasted/scientific input.
        if (value <= Minimum)
        {
            snapped = Minimum;
            return true;
        }

        if (value >= Maximum)
        {
            snapped = Maximum;
            return true;
        }

        snapped = checked((int)Math.Round(
            value,
            digits: 0,
            MidpointRounding.AwayFromZero));
        return true;
    }
}
