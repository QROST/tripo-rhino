using Xunit;

namespace Tripo.HostUi.Tests;

public sealed class DirectGlbAutoImportIntentTests
{
    private const long SessionGeneration = 7;
    private const string GenerationOperationId =
        "11111111-1111-4111-8111-111111111111";
    private const string DocumentSessionId =
        "22222222-2222-4222-8222-222222222222";
    private const string TaskId =
        "33333333-3333-4333-8333-333333333333";
    private const string OtherTaskId =
        "44444444-4444-4444-8444-444444444444";

    [Fact]
    public void ConstructorFreezesCanonicalIdentityAndNormalizedName()
    {
        Tripo.HostUi.DirectGlbAutoImportIntent intent = new(
            SessionGeneration,
            GenerationOperationId,
            DocumentSessionId,
            "  Pavilion Study  ");

        Assert.Equal(SessionGeneration, intent.SessionGeneration);
        Assert.Equal(GenerationOperationId, intent.GenerationOperationId);
        Assert.Equal(DocumentSessionId, intent.DocumentSessionId);
        Assert.Equal("Pavilion Study", intent.ObjectName);
        Assert.Equal(
            Tripo.HostUi.DirectGlbAutoImportPhase.Waiting,
            intent.Phase);
        Assert.Null(intent.TaskId);
    }

    [Theory]
    [InlineData(0, GenerationOperationId, DocumentSessionId, "Model")]
    [InlineData(-1, GenerationOperationId, DocumentSessionId, "Model")]
    [InlineData(SessionGeneration, "not-a-uuid", DocumentSessionId, "Model")]
    [InlineData(SessionGeneration, GenerationOperationId, "NOT-A-UUID", "Model")]
    [InlineData(SessionGeneration, GenerationOperationId, DocumentSessionId, "")]
    public void ConstructorRejectsUnsafeIdentity(
        long sessionGeneration,
        string operationId,
        string documentSessionId,
        string objectName)
    {
        Assert.ThrowsAny<ArgumentException>(
            () => new Tripo.HostUi.DirectGlbAutoImportIntent(
                sessionGeneration,
                operationId,
                documentSessionId,
                objectName));
    }

    [Fact]
    public void MissingEvidenceDoesNotConsumeLaterDurableReceipt()
    {
        Tripo.HostUi.DirectGlbAutoImportIntent intent = CreateIntent();

        Assert.False(
            intent.TryBindDurableTask(
                SessionGeneration,
                PanelState()));
        Assert.Null(intent.TaskId);
        Assert.Equal(
            Tripo.HostUi.DirectGlbAutoImportPhase.Waiting,
            intent.Phase);

        Assert.True(
            intent.TryBindDurableTask(
                SessionGeneration,
                PanelState(receipt: Receipt())));
        Assert.Equal(TaskId, intent.TaskId);
        Assert.Equal(
            Tripo.HostUi.DirectGlbAutoImportPhase.Waiting,
            intent.Phase);
    }

    [Fact]
    public void MatchingDurableOperationStatusCanBindWithoutReceipt()
    {
        Tripo.HostUi.DirectGlbAutoImportIntent intent = CreateIntent();

        Assert.True(
            intent.TryBindDurableTask(
                SessionGeneration,
                PanelState(
                    operationStatus: DurableOperationStatus())));
        Assert.Equal(TaskId, intent.TaskId);
    }

    [Fact]
    public void NonDurableOperationStatusWaitsForDurableEvidence()
    {
        Tripo.HostUi.DirectGlbAutoImportIntent intent = CreateIntent();
        Tripo.HostUi.TripoPanelState state = PanelState(
            taskStatus: TaskStatus("success"),
            operationStatus: NonDurableOperationStatus());

        Assert.False(intent.TryBindDurableTask(SessionGeneration, state));
        Assert.Equal(
            Tripo.HostUi.DirectGlbAutoImportDecision.NoAction,
            intent.ObserveState(SessionGeneration, state));
        Assert.Null(intent.TaskId);
        Assert.Equal(
            Tripo.HostUi.DirectGlbAutoImportPhase.Waiting,
            intent.Phase);

        Tripo.HostUi.TripoPanelState durable = state with
        {
            GenerationOperationStatus = DurableOperationStatus(),
        };
        Assert.Equal(
            Tripo.HostUi.DirectGlbAutoImportDecision.BeginImport,
            intent.ObserveState(SessionGeneration, durable));
    }

    [Fact]
    public void DurableTaskCanBindOnlyOnce()
    {
        Tripo.HostUi.DirectGlbAutoImportIntent intent = CreateIntent();

        Assert.True(BindTask(intent));
        Assert.True(BindTask(intent));
        Assert.False(
            intent.TryBindDurableTask(
                SessionGeneration,
                PanelState(receipt: Receipt(OtherTaskId))));
        Assert.Equal(TaskId, intent.TaskId);
        Assert.Equal(
            Tripo.HostUi.DirectGlbAutoImportPhase.Finished,
            intent.Phase);
    }

    [Theory]
    [InlineData("queued")]
    [InlineData("RUNNING")]
    public void PendingStatusKeepsTheIntentWaiting(string status)
    {
        Tripo.HostUi.DirectGlbAutoImportIntent intent = CreateBoundIntent();

        Tripo.HostUi.DirectGlbAutoImportDecision decision =
            Observe(intent, status);

        Assert.Equal(
            Tripo.HostUi.DirectGlbAutoImportDecision.Waiting,
            decision);
        Assert.Equal(
            Tripo.HostUi.DirectGlbAutoImportPhase.Waiting,
            intent.Phase);
    }

    [Fact]
    public void DurableTaskWithoutTaskStatusStaysWaiting()
    {
        Tripo.HostUi.DirectGlbAutoImportIntent intent = CreateIntent();

        Assert.Equal(
            Tripo.HostUi.DirectGlbAutoImportDecision.Waiting,
            intent.ObserveState(
                SessionGeneration,
                PanelState(receipt: Receipt())));
        Assert.Equal(TaskId, intent.TaskId);
        Assert.Equal(
            Tripo.HostUi.DirectGlbAutoImportPhase.Waiting,
            intent.Phase);
    }

    [Fact]
    public void SuccessAuthorizesExactlyOneImport()
    {
        Tripo.HostUi.DirectGlbAutoImportIntent intent = CreateBoundIntent();
        Tripo.HostUi.TripoPanelState state = PanelState(
            taskStatus: TaskStatus("success"),
            receipt: Receipt());

        Tripo.HostUi.DirectGlbAutoImportDecision first =
            intent.ObserveState(SessionGeneration, state);
        Tripo.HostUi.DirectGlbAutoImportDecision second =
            intent.ObserveState(SessionGeneration, state);

        Assert.Equal(
            Tripo.HostUi.DirectGlbAutoImportDecision.BeginImport,
            first);
        Assert.Equal(
            Tripo.HostUi.DirectGlbAutoImportDecision.NoAction,
            second);
        Assert.Equal(
            Tripo.HostUi.DirectGlbAutoImportPhase.Importing,
            intent.Phase);
        Assert.True(
            intent.TryFinishImport(SessionGeneration, state));
        Assert.False(
            intent.TryFinishImport(SessionGeneration, state));
        Assert.Equal(
            Tripo.HostUi.DirectGlbAutoImportPhase.Finished,
            intent.Phase);
    }

    [Fact]
    public void BusySuccessCannotConsumeImportAuthorization()
    {
        Tripo.HostUi.DirectGlbAutoImportIntent intent = CreateBoundIntent();
        Tripo.HostUi.TripoPanelState success = PanelState(
            taskStatus: TaskStatus("success"),
            receipt: Receipt());

        Assert.Equal(
            Tripo.HostUi.DirectGlbAutoImportDecision.NoAction,
            intent.ObserveState(
                SessionGeneration,
                success with { Busy = true }));
        Assert.Equal(
            Tripo.HostUi.DirectGlbAutoImportPhase.Waiting,
            intent.Phase);
        Assert.Equal(
            Tripo.HostUi.DirectGlbAutoImportDecision.BeginImport,
            intent.ObserveState(SessionGeneration, success));
    }

    [Fact]
    public void CredentialRefreshFailureHoldsSuccessUntilKeyRecovery()
    {
        Tripo.HostUi.DirectGlbAutoImportIntent intent = CreateBoundIntent();
        Tripo.HostUi.TripoPanelState credentialFailure = PanelState(
            taskStatus: TaskStatus("success"),
            receipt: Receipt()) with
        {
            LastError = "The provider rejected the credential.",
            LastErrorCode =
                Tripo.Bridge.HostControlConstants.CredentialInvalidError,
        };

        Assert.Equal(
            Tripo.HostUi.DirectGlbAutoImportDecision.Waiting,
            intent.ObserveState(SessionGeneration, credentialFailure));
        Assert.Equal(
            Tripo.HostUi.DirectGlbAutoImportPhase.Waiting,
            intent.Phase);
        Assert.Equal(
            Tripo.HostUi.DirectGlbAutoImportDecision.BeginImport,
            intent.ObserveState(
                SessionGeneration,
                credentialFailure with
                {
                    LastError = null,
                    LastErrorCode = null,
                }));
    }

    [Fact]
    public void ImportCanBeDeferredBeforeAnyImportMutation()
    {
        Tripo.HostUi.DirectGlbAutoImportIntent intent = CreateBoundIntent();
        Tripo.HostUi.TripoPanelState success = PanelState(
            taskStatus: TaskStatus("success"),
            receipt: Receipt());

        Assert.Equal(
            Tripo.HostUi.DirectGlbAutoImportDecision.BeginImport,
            intent.ObserveState(SessionGeneration, success));
        Assert.True(
            intent.TryDeferImport(
                SessionGeneration,
                success with { Busy = true }));
        Assert.Equal(
            Tripo.HostUi.DirectGlbAutoImportPhase.Waiting,
            intent.Phase);
        Assert.Equal(
            Tripo.HostUi.DirectGlbAutoImportDecision.BeginImport,
            intent.ObserveState(SessionGeneration, success));
    }

    [Fact]
    public void ImportCannotBeDeferredAfterImportPreparation()
    {
        Tripo.HostUi.DirectGlbAutoImportIntent intent = CreateBoundIntent();
        Tripo.HostUi.TripoPanelState success = PanelState(
            taskStatus: TaskStatus("success"),
            receipt: Receipt());
        Assert.Equal(
            Tripo.HostUi.DirectGlbAutoImportDecision.BeginImport,
            intent.ObserveState(SessionGeneration, success));

        Assert.False(
            intent.TryDeferImport(
                SessionGeneration,
                success with
                {
                    PreparedImport =
                        new Tripo.HostUi.PreparedObjImport(
                            TaskId,
                            "Pavilion Study",
                            DocumentSessionId,
                            "77777777-7777-4777-8777-777777777777",
                            "glb_instance",
                            ApplyMaterials: true,
                            ArtifactFormat: "glb"),
                }));
        Assert.Equal(
            Tripo.HostUi.DirectGlbAutoImportPhase.Importing,
            intent.Phase);
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("cancelled")]
    [InlineData("banned")]
    [InlineData("expired")]
    [InlineData("provider_added_terminal_state")]
    public void TerminalStatusEndsWithoutImport(string status)
    {
        Tripo.HostUi.DirectGlbAutoImportIntent intent = CreateBoundIntent();

        Tripo.HostUi.DirectGlbAutoImportDecision decision =
            Observe(intent, status);

        Assert.Equal(
            Tripo.HostUi.DirectGlbAutoImportDecision.TerminalWithoutImport,
            decision);
        Assert.Equal(
            Tripo.HostUi.DirectGlbAutoImportPhase.Finished,
            intent.Phase);
    }

    [Fact]
    public void StopSuppressesSuccessUntilExplicitResume()
    {
        Tripo.HostUi.DirectGlbAutoImportIntent intent = CreateBoundIntent();
        Tripo.HostUi.TripoPanelState running = PanelState(
            taskStatus: TaskStatus("running"),
            receipt: Receipt());
        Tripo.HostUi.TripoPanelState success = running with
        {
            GenerationStatus = TaskStatus("success"),
        };

        Assert.True(
            intent.TryStopWaiting(SessionGeneration, running));
        Assert.Equal(
            Tripo.HostUi.DirectGlbAutoImportDecision.Stopped,
            intent.ObserveState(SessionGeneration, success));
        Assert.Equal(
            Tripo.HostUi.DirectGlbAutoImportPhase.Stopped,
            intent.Phase);
        Assert.True(
            intent.TryResumeWaiting(SessionGeneration, success));
        Assert.Equal(
            Tripo.HostUi.DirectGlbAutoImportDecision.BeginImport,
            intent.ObserveState(SessionGeneration, success));
    }

    [Fact]
    public async Task StopAndSuccessRaceHasOnlyOneWinner()
    {
        for (int iteration = 0; iteration < 100; iteration++)
        {
            Tripo.HostUi.DirectGlbAutoImportIntent intent = CreateBoundIntent();
            using Barrier barrier = new(2);
            bool stopped = false;
            Tripo.HostUi.DirectGlbAutoImportDecision decision =
                Tripo.HostUi.DirectGlbAutoImportDecision.NoAction;
            Tripo.HostUi.TripoPanelState running = PanelState(
                taskStatus: TaskStatus("running"),
                receipt: Receipt());
            Tripo.HostUi.TripoPanelState success = running with
            {
                GenerationStatus = TaskStatus("success"),
            };

            Task stop = Task.Run(() =>
            {
                barrier.SignalAndWait();
                stopped =
                    intent.TryStopWaiting(SessionGeneration, running);
            });
            Task observeSuccess = Task.Run(() =>
            {
                barrier.SignalAndWait();
                decision =
                    intent.ObserveState(SessionGeneration, success);
            });

            await Task.WhenAll(stop, observeSuccess);

            if (stopped)
            {
                Assert.Equal(
                    Tripo.HostUi.DirectGlbAutoImportDecision.Stopped,
                    decision);
                Assert.Equal(
                    Tripo.HostUi.DirectGlbAutoImportPhase.Stopped,
                    intent.Phase);
            }
            else
            {
                Assert.Equal(
                    Tripo.HostUi.DirectGlbAutoImportDecision.BeginImport,
                    decision);
                Assert.Equal(
                    Tripo.HostUi.DirectGlbAutoImportPhase.Importing,
                    intent.Phase);
            }
        }
    }

    [Fact]
    public void StateIdentityDriftCannotStopResumeOrImport()
    {
        Tripo.HostUi.DirectGlbAutoImportIntent intent = CreateBoundIntent();
        Tripo.HostUi.TripoPanelState valid = PanelState(
            taskStatus: TaskStatus("running"),
            receipt: Receipt());
        Tripo.HostUi.TripoPanelState otherDocument = valid with
        {
            Context = valid.Context! with
            {
                DocumentSessionId =
                    "55555555-5555-4555-8555-555555555555",
            },
        };
        Tripo.HostUi.TripoPanelState otherOperation = valid with
        {
            PreparedGeneration = valid.PreparedGeneration! with
            {
                OperationId =
                    "66666666-6666-4666-8666-666666666666",
            },
        };

        Assert.Equal(
            Tripo.HostUi.DirectGlbAutoImportDecision.NoAction,
            intent.ObserveState(SessionGeneration + 1, valid));
        Assert.False(
            intent.TryStopWaiting(SessionGeneration, otherDocument));
        Assert.False(
            intent.TryResumeWaiting(SessionGeneration, otherOperation));
        Assert.Equal(
            Tripo.HostUi.DirectGlbAutoImportPhase.Waiting,
            intent.Phase);
    }

    [Theory]
    [InlineData(OtherTaskId, "text_to_model")]
    [InlineData(TaskId, "image_to_model")]
    [InlineData(TaskId, "")]
    public void TaskIdentityOrTypeMismatchRefusesAutoImport(
        string taskId,
        string type)
    {
        Tripo.HostUi.DirectGlbAutoImportIntent intent = CreateBoundIntent();

        Tripo.HostUi.DirectGlbAutoImportDecision decision =
            intent.ObserveState(
                SessionGeneration,
                PanelState(
                    taskStatus:
                        TaskStatus("success", taskId, type),
                    receipt: Receipt()));

        Assert.Equal(
            Tripo.HostUi.DirectGlbAutoImportDecision.Refused,
            decision);
        Assert.Equal(
            Tripo.HostUi.DirectGlbAutoImportPhase.Finished,
            intent.Phase);
    }

    [Fact]
    public void NullTaskStatusFailsClosedWithoutThrowing()
    {
        Tripo.HostUi.DirectGlbAutoImportIntent intent = CreateBoundIntent();
        Tripo.Bridge.HostControlTaskStatusReceipt status =
            TaskStatus("success") with { Status = null! };

        Tripo.HostUi.DirectGlbAutoImportDecision decision =
            intent.ObserveState(
                SessionGeneration,
                PanelState(
                    taskStatus: status,
                    receipt: Receipt()));

        Assert.Equal(
            Tripo.HostUi.DirectGlbAutoImportDecision.Refused,
            decision);
        Assert.Equal(
            Tripo.HostUi.DirectGlbAutoImportPhase.Finished,
            intent.Phase);
    }

    [Fact]
    public void TaskStatusBeforeDurableEvidenceCannotAuthorizeImport()
    {
        Tripo.HostUi.DirectGlbAutoImportIntent intent = CreateIntent();
        Tripo.HostUi.TripoPanelState statusOnly = PanelState(
            taskStatus: TaskStatus("success"));

        Assert.Equal(
            Tripo.HostUi.DirectGlbAutoImportDecision.NoAction,
            intent.ObserveState(SessionGeneration, statusOnly));
        Assert.Equal(
            Tripo.HostUi.DirectGlbAutoImportPhase.Waiting,
            intent.Phase);
        Assert.Null(intent.TaskId);

        Assert.Equal(
            Tripo.HostUi.DirectGlbAutoImportDecision.BeginImport,
            intent.ObserveState(
                SessionGeneration,
                statusOnly with
                {
                    GenerationReceipt = Receipt(),
                }));
    }

    [Theory]
    [InlineData("77777777-7777-4777-8777-777777777777", "text_task_creation")]
    [InlineData(GenerationOperationId, "obj_task_creation")]
    public void MismatchedDurableOperationEvidenceIsRefused(
        string operationId,
        string kind)
    {
        Tripo.HostUi.DirectGlbAutoImportIntent intent = CreateIntent();

        Assert.Equal(
            Tripo.HostUi.DirectGlbAutoImportDecision.Refused,
            intent.ObserveState(
                SessionGeneration,
                PanelState(
                    operationStatus:
                        DurableOperationStatus(
                            operationId: operationId,
                            kind: kind))));
        Assert.Equal(
            Tripo.HostUi.DirectGlbAutoImportPhase.Finished,
            intent.Phase);
    }

    [Fact]
    public void MismatchedReceiptOperationIsRefused()
    {
        Tripo.HostUi.DirectGlbAutoImportIntent intent = CreateIntent();

        Assert.Equal(
            Tripo.HostUi.DirectGlbAutoImportDecision.Refused,
            intent.ObserveState(
                SessionGeneration,
                PanelState(
                    receipt: Receipt(
                        operationId:
                            "77777777-7777-4777-8777-777777777777"))));
        Assert.Equal(
            Tripo.HostUi.DirectGlbAutoImportPhase.Finished,
            intent.Phase);
    }

    [Fact]
    public void ReceiptAndOperationEvidenceMustAgreeOnTaskId()
    {
        Tripo.HostUi.DirectGlbAutoImportIntent intent = CreateIntent();

        Assert.Equal(
            Tripo.HostUi.DirectGlbAutoImportDecision.Refused,
            intent.ObserveState(
                SessionGeneration,
                PanelState(
                    receipt: Receipt(),
                    operationStatus:
                        DurableOperationStatus(taskId: OtherTaskId))));
        Assert.Equal(
            Tripo.HostUi.DirectGlbAutoImportPhase.Finished,
            intent.Phase);
    }

    [Fact]
    public void DurableOperationEvidenceRejectsSourceTaskIdentity()
    {
        Tripo.HostUi.DirectGlbAutoImportIntent intent = CreateIntent();

        Assert.Equal(
            Tripo.HostUi.DirectGlbAutoImportDecision.Refused,
            intent.ObserveState(
                SessionGeneration,
                PanelState(
                    operationStatus:
                        DurableOperationStatus() with
                        {
                            SourceTaskId = OtherTaskId,
                        })));
        Assert.Equal(
            Tripo.HostUi.DirectGlbAutoImportPhase.Finished,
            intent.Phase);
    }

    private static Tripo.HostUi.DirectGlbAutoImportIntent CreateIntent() =>
        new(
            SessionGeneration,
            GenerationOperationId,
            DocumentSessionId,
            "Pavilion Study");

    private static Tripo.HostUi.DirectGlbAutoImportIntent
        CreateBoundIntent()
    {
        Tripo.HostUi.DirectGlbAutoImportIntent intent = CreateIntent();
        Assert.True(BindTask(intent));
        return intent;
    }

    private static bool BindTask(
        Tripo.HostUi.DirectGlbAutoImportIntent intent) =>
        intent.TryBindDurableTask(
            SessionGeneration,
            PanelState(receipt: Receipt()));

    private static Tripo.HostUi.DirectGlbAutoImportDecision Observe(
        Tripo.HostUi.DirectGlbAutoImportIntent intent,
        string status) =>
        intent.ObserveState(
            SessionGeneration,
            PanelState(
                taskStatus: TaskStatus(status),
                receipt: Receipt()));

    private static Tripo.HostUi.TripoPanelState PanelState(
        Tripo.Bridge.HostControlTaskStatusReceipt? taskStatus = null,
        Tripo.Bridge.HostControlTextTaskCreationReceipt? receipt = null,
        Tripo.Bridge.HostControlOperationStatusReceipt? operationStatus = null)
    {
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
                []),
            PreparedGeneration = new Tripo.HostUi.PreparedTextGeneration(
                "Pavilion study",
                20_000,
                true,
                DocumentSessionId,
                GenerationOperationId),
            GenerationDispatchAttempted = true,
            GenerationReceipt = receipt,
            GenerationOperationStatus = operationStatus,
            GenerationStatus = taskStatus,
        };
    }

    private static Tripo.Bridge.HostControlTextTaskCreationReceipt Receipt(
        string taskId = TaskId,
        string operationId = GenerationOperationId) =>
        new(operationId, taskId, "v2.5-20250123");

    private static Tripo.Bridge.HostControlOperationStatusReceipt
        DurableOperationStatus(
            string taskId = TaskId,
            string operationId = GenerationOperationId,
            string kind = "text_task_creation") =>
        new(
            operationId,
            kind,
            "task_id_persisted",
            null,
            taskId,
            null,
            null,
            TaskIdDurable: true,
            MayHaveCreatedRemoteTask: true,
            CanResumeCreation: true,
            NextAction: "Query the durable task ID.",
            UpdatedAtUtc: DateTimeOffset.UnixEpoch);

    private static Tripo.Bridge.HostControlOperationStatusReceipt
        NonDurableOperationStatus() =>
        new(
            GenerationOperationId,
            "text_task_creation",
            "dispatching",
            null,
            null,
            null,
            null,
            TaskIdDurable: false,
            MayHaveCreatedRemoteTask: true,
            CanResumeCreation: false,
            NextAction: "Wait for durable task evidence.",
            UpdatedAtUtc: DateTimeOffset.UnixEpoch);

    private static Tripo.Bridge.HostControlTaskStatusReceipt TaskStatus(
        string status,
        string taskId = TaskId,
        string type = "text_to_model") =>
        new(
            taskId,
            type,
            status,
            status == "success" ? 100 : 42,
            null,
            null,
            null,
            null,
            null);
}
