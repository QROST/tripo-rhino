using Xunit;

namespace Tripo.HostUi.Tests;

public sealed class DirectGlbGenerationPollingPolicyTests
{
    private const long SessionGeneration = 7;
    private const string DocumentSessionId =
        "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
    private const string GenerationOperationId =
        "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb";
    private const string TaskId = "task_source123";

    [Fact]
    public void StoppedIntentSuppressesEveryOrdinaryPollingReconciliation()
    {
        Tripo.HostUi.TripoPanelState running = RunningState();
        Tripo.HostUi.DirectGlbAutoImportIntent intent = Intent();

        Assert.Equal(
            TaskId,
            Tripo.HostUi.DirectGlbGenerationPollingPolicy.GetPendingTaskId(
                running,
                Tripo.HostUi.TripoPanelRecoveryLoadResult.Empty,
                intent));
        Assert.True(
            intent.TryStopWaiting(SessionGeneration, running));

        Assert.Null(
            Tripo.HostUi.DirectGlbGenerationPollingPolicy.GetPendingTaskId(
                running,
                Tripo.HostUi.TripoPanelRecoveryLoadResult.Empty,
                intent));
        Assert.Null(
            Tripo.HostUi.DirectGlbGenerationPollingPolicy.GetPendingTaskId(
                running with
                {
                    LastError = null,
                    LastErrorCode = null,
                },
                Tripo.HostUi.TripoPanelRecoveryLoadResult.Empty,
                intent));
        Assert.Null(
            Tripo.HostUi.DirectGlbGenerationPollingPolicy.GetPendingTaskId(
                running with
                {
                    GenerationStatus = TaskStatus("success"),
                },
                Tripo.HostUi.TripoPanelRecoveryLoadResult.Empty,
                intent));
    }

    [Fact]
    public void ExplicitResumeRestoresPendingTaskWithoutChangingItsIdentity()
    {
        Tripo.HostUi.TripoPanelState running = RunningState();
        Tripo.HostUi.DirectGlbAutoImportIntent intent = Intent();
        Assert.True(
            intent.TryStopWaiting(SessionGeneration, running));

        Assert.True(
            intent.TryResumeWaiting(SessionGeneration, running));
        Assert.Equal(
            TaskId,
            Tripo.HostUi.DirectGlbGenerationPollingPolicy.GetPendingTaskId(
                running,
                Tripo.HostUi.TripoPanelRecoveryLoadResult.Empty,
                intent));
        Assert.Equal(TaskId, intent.TaskId);
    }

    [Fact]
    public void ManualWorkflowWithoutIntentKeepsNormalPollingPolicy()
    {
        Assert.Equal(
            TaskId,
            Tripo.HostUi.DirectGlbGenerationPollingPolicy.GetPendingTaskId(
                RunningState(),
                Tripo.HostUi.TripoPanelRecoveryLoadResult.Empty,
                intent: null));
    }

    private static Tripo.HostUi.DirectGlbAutoImportIntent Intent() =>
        new(
            SessionGeneration,
            GenerationOperationId,
            DocumentSessionId,
            "Pavilion Study");

    private static Tripo.HostUi.TripoPanelState RunningState()
    {
        Tripo.HostUi.PreparedTextGeneration prepared = new(
            "a pavilion",
            20_000,
            true,
            DocumentSessionId,
            GenerationOperationId);
        return Tripo.HostUi.TripoPanelState.Initial with
        {
            Connected = true,
            Context = new Tripo.Bridge.HostContextReceipt(
                "rhino",
                "8-test",
                123,
                DocumentSessionId,
                "Test.3dm",
                "Meters",
                [Tripo.Bridge.BridgeConstants.ContextMethod]),
            PreparedGeneration = prepared,
            GenerationDispatchAttempted = true,
            GenerationReceipt =
                new Tripo.Bridge.HostControlTextTaskCreationReceipt(
                    GenerationOperationId,
                    TaskId,
                    "v3"),
            GenerationStatus = TaskStatus("running"),
        };
    }

    private static Tripo.Bridge.HostControlTaskStatusReceipt TaskStatus(
        string status) =>
        new(
            TaskId,
            "text_to_model",
            status,
            status == "success" ? 100 : 50,
            null,
            null,
            null,
            null,
            null);
}
