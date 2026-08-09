namespace Tripo.HostUi;

internal static class TripoPanelImageSnapshotPolicy
{
    internal static bool CanDeleteUnadmittedSnapshot(
        Tripo.Bridge.StagedImageTransfer staged,
        TripoPanelState state)
    {
        ArgumentNullException.ThrowIfNull(staged);
        ArgumentNullException.ThrowIfNull(state);
        bool ownsPreparedImage =
            Equals(state.PreparedImageGeneration?.Image, staged);
        bool mayHaveBeenAdmitted =
            state.GenerationDispatchAttempted ||
            state.ImageGenerationReceipt is not null ||
            state.GenerationOperationStatus is not null ||
            state.GenerationStatus is not null;
        return !ownsPreparedImage || !mayHaveBeenAdmitted;
    }
}
