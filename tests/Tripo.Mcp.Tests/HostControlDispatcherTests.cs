using System.Text.Json;
using Xunit;

namespace Tripo.Mcp.Tests;

public sealed class HostControlDispatcherTests
{
    [Fact]
    public async Task CredentialSetResponseNeverContainsTheApiKey()
    {
        const string secret = "secret-that-must-not-be-returned";
        FakeCredentialService credentials = new();
        FakeWorkflow workflow = new();
        Tripo.Mcp.HostControlDispatcher dispatcher = CreateDispatcher(
            credentials,
            workflow);
        Tripo.Bridge.HostControlSetApiKeyRequest request = new(
            secret,
            persist: true);

        object result = await dispatcher.DispatchAsync(
            Tripo.Bridge.HostControlConstants.CredentialSetMethod,
            Tripo.Bridge.BridgeJson.ToElement(request),
            CancellationToken.None);
        string serialized = JsonSerializer.Serialize(
            result,
            Tripo.Bridge.BridgeJson.Options);

        Assert.Equal(secret, credentials.LastSetKey);
        Assert.True(credentials.LastPersist);
        Assert.DoesNotContain(secret, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TextWorkflowMethodMapsTheExactPaidArguments()
    {
        FakeWorkflow workflow = new();
        Tripo.Mcp.HostControlDispatcher dispatcher = CreateDispatcher(
            new FakeCredentialService(),
            workflow);
        string sessionId = Guid.NewGuid().ToString("D");
        string operationId = Guid.NewGuid().ToString("D");
        Tripo.Bridge.HostControlCreateTextTaskRequest request = new(
            "a timber pavilion",
            25_000,
            WithMaterials: true,
            sessionId,
            operationId,
            ConfirmExternalCost: true,
            RequireExistingOperation: true);

        object result = await dispatcher.DispatchAsync(
            Tripo.Bridge.HostControlConstants.CreateTextTaskMethod,
            Tripo.Bridge.BridgeJson.ToElement(request),
            CancellationToken.None);

        Tripo.Bridge.HostControlTextTaskCreationReceipt receipt = Assert.IsType<
            Tripo.Bridge.HostControlTextTaskCreationReceipt>(result);
        Assert.Equal(operationId, receipt.OperationId);
        Assert.Equal(request, workflow.LastTextRequest);
        Assert.Equal(1, workflow.CreateTextCalls);
    }

    [Fact]
    public async Task ImageWorkflowMethodMapsOpaqueIdentityAndRetryRequirement()
    {
        FakeWorkflow workflow = new();
        Tripo.Mcp.HostControlDispatcher dispatcher = CreateDispatcher(
            new FakeCredentialService(),
            workflow);
        Tripo.Bridge.HostControlCreateImageTaskRequest request = new(
            new Tripo.Bridge.StagedImageTransfer(
                "11111111-1111-1111-1111-111111111111",
                new string('a', 64),
                10,
                "image/png"),
            12_000,
            WithMaterials: true,
            Guid.NewGuid().ToString("D"),
            Guid.NewGuid().ToString("D"),
            ConfirmExternalCost: true,
            RequireExistingOperation: true);

        object result = await dispatcher.DispatchAsync(
            Tripo.Bridge.HostControlConstants.CreateImageTaskMethod,
            Tripo.Bridge.BridgeJson.ToElement(request),
            CancellationToken.None);

        Tripo.Bridge.HostControlImageTaskCreationReceipt receipt =
            Assert.IsType<Tripo.Bridge.HostControlImageTaskCreationReceipt>(
                result);
        Assert.Equal(request.OperationId, receipt.OperationId);
        Assert.Equal(request.Image.Sha256, receipt.ImageSha256);
        Assert.Equal(request, workflow.LastImageRequest);
    }

    [Fact]
    public async Task StageObjMethodMapsStageOnlyReceipt()
    {
        FakeWorkflow workflow = new();
        Tripo.Mcp.HostControlDispatcher dispatcher = CreateDispatcher(
            new FakeCredentialService(),
            workflow);
        Tripo.Bridge.HostControlStageObjTaskRequest request = new(
            "task_conversion123",
            Guid.NewGuid().ToString("D"),
            IncludeMaterials: true);

        object result = await dispatcher.DispatchAsync(
            Tripo.Bridge.HostControlConstants.StageObjTaskMethod,
            Tripo.Bridge.BridgeJson.ToElement(request),
            CancellationToken.None);

        Tripo.Bridge.HostControlObjTaskStageReceipt receipt =
            Assert.IsType<Tripo.Bridge.HostControlObjTaskStageReceipt>(
                result);
        Assert.Equal(request, workflow.LastStageRequest);
        Assert.Equal("task_conversion123", receipt.ConversionTaskId);
        Assert.True(receipt.Mesh.ApplyMaterials);
    }

    [Fact]
    public async Task InvalidCredentialPayloadReturnsTypedErrorWithoutPayload()
    {
        const string secret = "secret-never-in-error";
        Tripo.Mcp.HostControlDispatcher dispatcher = CreateDispatcher(
            new FakeCredentialService(),
            new FakeWorkflow());
        JsonElement invalidPayload = Tripo.Bridge.BridgeJson.ToElement(
            new
            {
                apiKey = secret,
                persist = "not-a-boolean",
            });

        Tripo.Bridge.HostControlCallException exception =
            await Assert.ThrowsAsync<Tripo.Bridge.HostControlCallException>(
                () => dispatcher.DispatchAsync(
                    Tripo.Bridge.HostControlConstants.CredentialSetMethod,
                    invalidPayload,
                    CancellationToken.None));

        Assert.Equal("invalid_request", exception.Code);
        Assert.DoesNotContain(
            secret,
            exception.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void HostControlCommandLineRequiresAnExactPositivePid()
    {
        Assert.False(
            Tripo.Mcp.SidecarCommandLine.TryParseHostControl(
                Array.Empty<string>(),
                out _));
        Assert.Throws<ArgumentException>(
            () => Tripo.Mcp.SidecarCommandLine.TryParseHostControl(
                ["--host-control"],
                out _));
        Assert.Throws<ArgumentException>(
            () => Tripo.Mcp.SidecarCommandLine.TryParseHostControl(
                ["--host-control", "--host-pid", "0"],
                out _));

        bool parsed = Tripo.Mcp.SidecarCommandLine.TryParseHostControl(
            [
                "--host-control",
                "--host-pid",
                Environment.ProcessId.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
            ],
            out Tripo.Mcp.HostControlCommandLineOptions? options);

        Assert.True(parsed);
        Assert.Equal(Environment.ProcessId, options?.HostProcessId);
    }

    private static Tripo.Mcp.HostControlDispatcher CreateDispatcher(
        Tripo.Mcp.ITripoCredentialService credentials,
        Tripo.Mcp.ITripoWorkflow workflow) =>
        new(
            "rhino",
            Environment.ProcessId,
            Tripo.Bridge.HostControlConstants.WorkflowCapabilities,
            credentials,
            workflow,
            () => { });

    private sealed class FakeCredentialService :
        Tripo.Mcp.ITripoCredentialService
    {
        public string? LastSetKey { get; private set; }

        public bool LastPersist { get; private set; }

        public Tripo.Bridge.HostControlCredentialStatusReceipt GetStatus() =>
            Status();

        public Tripo.Bridge.HostControlCredentialMutationReceipt SetApiKey(
            string apiKey,
            bool persist)
        {
            LastSetKey = apiKey;
            LastPersist = persist;
            return new Tripo.Bridge.HostControlCredentialMutationReceipt(
                Status());
        }

        public Tripo.Bridge.HostControlCredentialMutationReceipt ClearApiKey() =>
            new(Status());

        private static Tripo.Bridge.HostControlCredentialStatusReceipt Status() =>
            new(
                HasApiKey: true,
                Source: "store",
                StoredKeyPresent: true,
                CanClearStoredKey: true,
                PersistenceBackend: "fake",
                UsesWeakerFileFallback: false);
    }

    private sealed class FakeWorkflow : Tripo.Mcp.ITripoWorkflow
    {
        public int CreateTextCalls { get; private set; }

        public Tripo.Bridge.HostControlCreateTextTaskRequest? LastTextRequest
        {
            get;
            private set;
        }

        public Tripo.Bridge.HostControlCreateImageTaskRequest? LastImageRequest
        {
            get;
            private set;
        }

        public Tripo.Bridge.HostControlStageObjTaskRequest? LastStageRequest
        {
            get;
            private set;
        }

        public Task<Tripo.Bridge.HostContextReceipt> GetHostContextAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult(
                new Tripo.Bridge.HostContextReceipt(
                    "rhino",
                    "8-test",
                    Environment.ProcessId,
                    Guid.NewGuid().ToString("D"),
                    "Test",
                    "Meters",
                    []));

        public Task<Tripo.Mcp.TaskStatusReceipt> GetTaskStatusAsync(
            string taskId,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                new Tripo.Mcp.TaskStatusReceipt(
                    taskId,
                    "text_to_model",
                    "success",
                    100,
                    null,
                    null,
                    null,
                    null,
                    null));

        public Task<Tripo.Mcp.PaidOperationStatusReceipt>
            GetPaidOperationStatusAsync(
                string operationId,
                CancellationToken cancellationToken) =>
            Task.FromResult(
                new Tripo.Mcp.PaidOperationStatusReceipt(
                    operationId,
                    "text_task_creation",
                    "task_id_persisted",
                    null,
                    "task_source123",
                    null,
                    null,
                    TaskIdDurable: true,
                    MayHaveCreatedRemoteTask: true,
                    CanResumeCreation: false,
                    "Reuse the durable task ID.",
                    DateTimeOffset.UtcNow));

        public Task<Tripo.Mcp.TextTaskCreationReceipt> CreateTextTaskAsync(
            string prompt,
            int faceLimit,
            bool withMaterials,
            string documentSessionId,
            string operationId,
            bool confirmExternalCost,
            CancellationToken cancellationToken,
            bool requireExistingOperation = false)
        {
            CreateTextCalls++;
            LastTextRequest = new Tripo.Bridge.HostControlCreateTextTaskRequest(
                prompt,
                faceLimit,
                withMaterials,
                documentSessionId,
                operationId,
                confirmExternalCost,
                requireExistingOperation);
            return Task.FromResult(
                new Tripo.Mcp.TextTaskCreationReceipt(
                    operationId,
                    "task_source123",
                    Tripo.Mcp.TripoV3Client.DefaultModel));
        }

        public Task<Tripo.Mcp.ObjConversionCreationReceipt>
            CreateObjConversionAsync(
                string sourceTaskId,
                int faceLimit,
                bool withMaterials,
                string documentSessionId,
                string operationId,
                bool confirmExternalCost,
                CancellationToken cancellationToken,
                bool requireExistingOperation = false) =>
            Task.FromResult(
                new Tripo.Mcp.ObjConversionCreationReceipt(
                    operationId,
                    sourceTaskId,
                    "task_conversion123",
                    "OBJ"));

        public Task<Tripo.Mcp.ImageTaskCreationReceipt> CreateImageTaskAsync(
            Tripo.Bridge.StagedImageTransfer image,
            int faceLimit,
            bool withMaterials,
            string documentSessionId,
            string operationId,
            bool confirmExternalCost,
            CancellationToken cancellationToken,
            bool requireExistingOperation = false)
        {
            LastImageRequest =
                new Tripo.Bridge.HostControlCreateImageTaskRequest(
                    image,
                    faceLimit,
                    withMaterials,
                    documentSessionId,
                    operationId,
                    confirmExternalCost,
                    requireExistingOperation);
            return Task.FromResult(
                new Tripo.Mcp.ImageTaskCreationReceipt(
                    operationId,
                    "task_image123",
                    Tripo.Mcp.TripoV3Client.DefaultModel,
                    image.Sha256));
        }

        public Task<Tripo.Mcp.ObjTaskStageReceipt> StageObjTaskAsync(
            string conversionTaskId,
            string documentSessionId,
            bool includeMaterials,
            CancellationToken cancellationToken)
        {
            LastStageRequest =
                new Tripo.Bridge.HostControlStageObjTaskRequest(
                    conversionTaskId,
                    documentSessionId,
                    includeMaterials);
            return Task.FromResult(
                new Tripo.Mcp.ObjTaskStageReceipt(
                    conversionTaskId,
                    1.25m,
                    new Tripo.Bridge.StagedMeshLoadRequest(
                        "bundle_123",
                        "model.obj",
                        includeMaterials ? "model.mtl" : null,
                        [],
                        "meter",
                        "Y",
                        "right",
                        includeMaterials)));
        }

        public Task<Tripo.Mcp.ObjTaskImportReceipt> ImportObjTaskAsync(
            string conversionTaskId,
            string name,
            string documentSessionId,
            string operationId,
            string importMode,
            bool applyMaterials,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                new Tripo.Mcp.ObjTaskImportReceipt(
                    operationId,
                    conversionTaskId,
                    null,
                    new Tripo.Bridge.HostImportReceipt(
                        "rhino",
                        documentSessionId,
                        operationId,
                        Guid.NewGuid().ToString("D"),
                        3,
                        1,
                        0,
                        "committed",
                        "mesh",
                        0,
                        0,
                        null)));
    }
}
