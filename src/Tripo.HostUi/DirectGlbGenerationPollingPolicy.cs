namespace Tripo.HostUi;

internal static class DirectGlbGenerationPollingPolicy
{
    internal static string? GetPendingTaskId(
        TripoPanelState state,
        TripoPanelRecoveryLoadResult recovery,
        DirectGlbAutoImportIntent? intent)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(recovery);

        if (intent?.Phase == DirectGlbAutoImportPhase.Stopped)
        {
            return null;
        }

        return GenerationStatusPoller.GetPendingTaskId(state, recovery);
    }
}
