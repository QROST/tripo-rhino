using System.Net;
using System.Text.Json;
using Xunit;

namespace Tripo.Mcp.Tests;

public sealed class TripoV3ClientTests
{
    private const string DocumentSessionId =
        "11111111-1111-1111-1111-111111111111";

    [Fact]
    public async Task CreateTextModelAsyncUsesV3GeometryOnlyPayload()
    {
        string? requestPath = null;
        string? authorization = null;
        JsonDocument? payload = null;
        DelegateHttpMessageHandler handler = new(async (request, _) =>
        {
            requestPath = request.RequestUri?.AbsolutePath;
            authorization = request.Headers.Authorization?.ToString();
            payload = JsonDocument.Parse(await request.Content!.ReadAsStreamAsync());
            return DelegateHttpMessageHandler.Json(
                """{"code":0,"data":{"task_id":"task_source123"}}""");
        });
        Tripo.Mcp.TripoV3Client client = CreateClient(handler);
        Tripo.Mcp.TextGenerationOptions options =
            new("a test chair", 10_000);
        RecordingCheckpoint checkpoint = CreateCheckpoint(client, options);

        string taskId = await client.CreateTextModelAsync(
            options,
            DocumentSessionId,
            checkpoint,
            CancellationToken.None);

        Assert.Equal("task_source123", taskId);
        Assert.Equal(1, checkpoint.BeforeSendCalls);
        Assert.Equal("task_source123", checkpoint.TaskId);
        Assert.Equal(0, checkpoint.OutcomeUnknownCalls);
        Assert.Equal("/v3/generation/text-to-model", requestPath);
        Assert.Equal("Bearer opaque_test_key", authorization);
        JsonElement root = payload!.RootElement;
        Assert.False(root.GetProperty("texture").GetBoolean());
        Assert.False(root.GetProperty("pbr").GetBoolean());
        Assert.True(root.GetProperty("auto_size").GetBoolean());
        Assert.False(root.GetProperty("export_uv").GetBoolean());
        payload.Dispose();
    }

    [Fact]
    public async Task CreatePostIsNeverRetried()
    {
        DelegateHttpMessageHandler handler = new((_, _) =>
            Task.FromResult(
                DelegateHttpMessageHandler.Json(
                    """{"code":500,"message":"busy"}""",
                    HttpStatusCode.ServiceUnavailable)));
        Tripo.Mcp.TripoV3Client client = CreateClient(handler);
        Tripo.Mcp.TextGenerationOptions options =
            new("a test chair", 10_000);
        RecordingCheckpoint checkpoint = CreateCheckpoint(client, options);

        await Assert.ThrowsAsync<Tripo.Mcp.TripoApiException>(
            () => client.CreateTextModelAsync(
                options,
                DocumentSessionId,
                checkpoint,
                CancellationToken.None));

        Assert.Equal(1, handler.CallCount);
        Assert.Equal(1, checkpoint.OutcomeUnknownCalls);
        Assert.Equal(0, checkpoint.RequestRejectedCalls);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task CredentialRejectionIsNotMarkedOutcomeUnknown(
        HttpStatusCode statusCode)
    {
        DelegateHttpMessageHandler handler = new((_, _) =>
            Task.FromResult(
                DelegateHttpMessageHandler.Json(
                    """{"code":1001,"message":"credential rejected"}""",
                    statusCode)));
        Tripo.Mcp.TripoV3Client client = CreateClient(handler);
        Tripo.Mcp.TextGenerationOptions options =
            new("a test chair", 10_000);
        RecordingCheckpoint checkpoint = CreateCheckpoint(client, options);

        Tripo.Mcp.TripoPaidRequestRejectedException exception =
            await Assert.ThrowsAsync<
                Tripo.Mcp.TripoPaidRequestRejectedException>(
                () => client.CreateTextModelAsync(
                    options,
                    DocumentSessionId,
                    checkpoint,
                    CancellationToken.None));

        Assert.Equal(statusCode, exception.StatusCode);
        Assert.DoesNotContain(
            "may have succeeded remotely",
            exception.Message,
            StringComparison.Ordinal);
        Assert.Equal(1, handler.CallCount);
        Assert.Equal(1, checkpoint.RequestRejectedCalls);
        Assert.Equal(0, checkpoint.OutcomeUnknownCalls);
    }

    [Fact]
    public async Task RejectionCheckpointFailureStaysFailClosed()
    {
        DelegateHttpMessageHandler handler = new((_, _) =>
            Task.FromResult(
                DelegateHttpMessageHandler.Json(
                    """{"code":1001,"message":"credential rejected"}""",
                    HttpStatusCode.Unauthorized)));
        Tripo.Mcp.TripoV3Client client = CreateClient(handler);
        Tripo.Mcp.TextGenerationOptions options =
            new("a test chair", 10_000);
        RecordingCheckpoint checkpoint = CreateCheckpoint(client, options);
        checkpoint.RequestRejectedException =
            new IOException("checkpoint unavailable");

        Tripo.Mcp.TripoApiException exception =
            await Assert.ThrowsAsync<Tripo.Mcp.TripoApiException>(
                () => client.CreateTextModelAsync(
                    options,
                    DocumentSessionId,
                    checkpoint,
                    CancellationToken.None));

        Assert.IsNotType<
            Tripo.Mcp.TripoPaidRequestRejectedException>(exception);
        Assert.Contains(
            "checkpoint could not be persisted",
            exception.Message);
        Assert.Equal(1, checkpoint.RequestRejectedCalls);
        Assert.Equal(0, checkpoint.OutcomeUnknownCalls);
    }

    [Fact]
    public async Task ObjConversionUsesTheFingerprintMatchedV3Payload()
    {
        string? requestPath = null;
        JsonDocument? payload = null;
        DelegateHttpMessageHandler handler = new(async (request, _) =>
        {
            requestPath = request.RequestUri?.AbsolutePath;
            payload = JsonDocument.Parse(await request.Content!.ReadAsStreamAsync());
            return DelegateHttpMessageHandler.Json(
                """{"code":0,"data":{"task_id":"task_conversion123"}}""");
        });
        Tripo.Mcp.TripoV3Client client = CreateClient(handler);
        string fingerprint = client.GetObjConversionOperationFingerprint(
            "task_source123",
            12_000,
            withMaterials: false,
            DocumentSessionId);
        RecordingCheckpoint checkpoint = new(fingerprint);

        string taskId = await client.CreateObjConversionAsync(
            "task_source123",
            12_000,
            withMaterials: false,
            DocumentSessionId,
            checkpoint,
            CancellationToken.None);

        Assert.Equal("task_conversion123", taskId);
        Assert.Equal("/v3/models/convert", requestPath);
        JsonElement root = payload!.RootElement;
        Assert.Equal("task_source123", root.GetProperty("input").GetString());
        Assert.Equal("OBJ", root.GetProperty("format").GetString());
        Assert.Equal(12_000, root.GetProperty("face_limit").GetInt32());
        Assert.False(root.GetProperty("quad").GetBoolean());
        Assert.False(root.GetProperty("bake").GetBoolean());
        Assert.False(root.GetProperty("with_animation").GetBoolean());
        payload.Dispose();
    }

    [Fact]
    public async Task CreateTextModelAsyncWithMaterialsRequestsTextureAndPbr()
    {
        JsonDocument? payload = null;
        DelegateHttpMessageHandler handler = new(async (request, _) =>
        {
            payload = JsonDocument.Parse(await request.Content!.ReadAsStreamAsync());
            return DelegateHttpMessageHandler.Json(
                """{"code":0,"data":{"task_id":"task_source123"}}""");
        });
        Tripo.Mcp.TripoV3Client client = CreateClient(handler);
        Tripo.Mcp.TextGenerationOptions options = new(
            "a test chair",
            10_000,
            Tripo.Mcp.TripoV3Client.DefaultModel,
            WithMaterials: true);
        RecordingCheckpoint checkpoint = new(
            client.GetTextTaskOperationFingerprint(options, DocumentSessionId));

        await client.CreateTextModelAsync(
            options,
            DocumentSessionId,
            checkpoint,
            CancellationToken.None);

        JsonElement root = payload!.RootElement;
        Assert.True(root.GetProperty("texture").GetBoolean());
        Assert.True(root.GetProperty("pbr").GetBoolean());
        Assert.True(root.GetProperty("auto_size").GetBoolean());
        payload.Dispose();
    }

    [Fact]
    public async Task CreateImageModelAsyncUploadsOpaquePngThenCreatesTask()
    {
        using TemporaryDataRoot root = new();
        string sourcePath = Path.Combine(root.Path, "private-source.png");
        Tripo.Mcp.ImageGenerationOptions options =
            await CreateImageOptionsAsync(sourcePath);
        List<string> paths = [];
        string? uploadPartName = null;
        string? uploadFileName = null;
        string? uploadMediaType = null;
        byte[]? uploadBytes = null;
        JsonDocument? generationPayload = null;
        DelegateHttpMessageHandler handler = new(async (request, call) =>
        {
            paths.Add(request.RequestUri!.AbsolutePath);
            if (call == 1)
            {
                MultipartFormDataContent multipart =
                    Assert.IsType<MultipartFormDataContent>(request.Content);
                HttpContent file = Assert.Single(multipart);
                uploadPartName =
                    file.Headers.ContentDisposition?.Name?.Trim('"');
                uploadFileName =
                    file.Headers.ContentDisposition?.FileName?.Trim('"');
                uploadMediaType = file.Headers.ContentType?.MediaType;
                uploadBytes = await file.ReadAsByteArrayAsync();
                return DelegateHttpMessageHandler.Json(
                    """{"code":0,"data":{"file_token":"file_token123"}}""");
            }

            generationPayload = JsonDocument.Parse(
                await request.Content!.ReadAsStreamAsync());
            return DelegateHttpMessageHandler.Json(
                """{"code":0,"data":{"task_id":"task_image123"}}""");
        });
        Tripo.Mcp.TripoV3Client client = CreateClient(handler);
        RecordingImageCheckpoint checkpoint = CreateImageCheckpoint(
            client,
            options);

        string taskId = await client.CreateImageModelAsync(
            options,
            DocumentSessionId,
            checkpoint,
            CancellationToken.None);

        Assert.Equal("task_image123", taskId);
        Assert.Equal(
            ["/v3/files", "/v3/generation/image-to-model"],
            paths);
        Assert.Equal("file", uploadPartName);
        Assert.Equal("input.png", uploadFileName);
        Assert.Equal("image/png", uploadMediaType);
        Assert.Equal(TestPngBytes, uploadBytes);
        Assert.DoesNotContain(
            Path.GetFileName(sourcePath),
            uploadFileName,
            StringComparison.Ordinal);
        JsonElement rootElement = generationPayload!.RootElement;
        Assert.Equal(
            "file_token123",
            rootElement.GetProperty("input").GetString());
        Assert.False(rootElement.GetProperty("texture").GetBoolean());
        Assert.False(rootElement.GetProperty("pbr").GetBoolean());
        Assert.True(rootElement.GetProperty("auto_size").GetBoolean());
        Assert.Equal(1, checkpoint.BeforeImageUploadCalls);
        Assert.Equal(1, checkpoint.BeforeImageGenerationCalls);
        Assert.Equal("task_image123", checkpoint.TaskId);
        Assert.Equal(0, checkpoint.ImageOutcomeUnknownCalls);
        Tripo.Bridge.BridgeCallException missing =
            await Assert.ThrowsAsync<Tripo.Bridge.BridgeCallException>(
                () => Tripo.Bridge.ImageTransferStore.OpenVerifiedAsync(
                    options.Image,
                    CancellationToken.None));
        Assert.Equal("image_transfer_missing", missing.Code);
        generationPayload.Dispose();
    }

    [Fact]
    public async Task ImageLogicalFingerprintMismatchFailsBeforeCheckpointOrNetwork()
    {
        using TemporaryDataRoot root = new();
        Tripo.Mcp.ImageGenerationOptions options =
            await CreateImageOptionsAsync(
                Path.Combine(root.Path, "fingerprint.png"));
        DelegateHttpMessageHandler handler = new((_, _) =>
            throw new InvalidOperationException(
                "No network call was expected."));
        Tripo.Mcp.TripoV3Client client = CreateClient(handler);
        RecordingImageCheckpoint checkpoint = new(new string('f', 64));

        await Assert.ThrowsAsync<Tripo.Mcp.TripoApiException>(
            () => client.CreateImageModelAsync(
                options,
                DocumentSessionId,
                checkpoint,
                CancellationToken.None));

        Assert.Equal(0, handler.CallCount);
        Assert.Equal(0, checkpoint.BeforeImageUploadCalls);
        Assert.Equal(0, checkpoint.BeforeImageGenerationCalls);
    }

    [Fact]
    public async Task DurableImageGenerationFingerprintMismatchFailsBeforeNetwork()
    {
        using TemporaryDataRoot root = new();
        Tripo.Mcp.ImageGenerationOptions options =
            await CreateImageOptionsAsync(
                Path.Combine(root.Path, "generation-fingerprint.png"));
        DelegateHttpMessageHandler handler = new((_, _) =>
            throw new InvalidOperationException(
                "No network call was expected."));
        Tripo.Mcp.TripoV3Client client = CreateClient(handler);
        RecordingImageCheckpoint checkpoint = CreateImageCheckpoint(
            client,
            options);
        checkpoint.SeedDurableFileToken(
            "file_resume123",
            new string('f', 64));

        await Assert.ThrowsAsync<Tripo.Mcp.TripoApiException>(
            () => client.CreateImageModelAsync(
                options,
                DocumentSessionId,
                checkpoint,
                CancellationToken.None));

        Assert.Equal(0, handler.CallCount);
        Assert.Equal(0, checkpoint.BeforeImageUploadCalls);
        Assert.Equal(0, checkpoint.BeforeImageGenerationCalls);
    }

    [Fact]
    public async Task DurableImageFileTokenResumeSkipsUpload()
    {
        using TemporaryDataRoot root = new();
        Tripo.Mcp.ImageGenerationOptions options =
            await CreateImageOptionsAsync(
                Path.Combine(root.Path, "resume.png"));
        DelegateHttpMessageHandler firstHandler = new((request, _) =>
        {
            Assert.Equal("/v3/files", request.RequestUri?.AbsolutePath);
            return Task.FromResult(
                DelegateHttpMessageHandler.Json(
                    """{"code":0,"data":{"file_token":"file_resume123"}}"""));
        });
        Tripo.Mcp.TripoV3Client firstClient = CreateClient(firstHandler);
        RecordingImageCheckpoint checkpoint = CreateImageCheckpoint(
            firstClient,
            options);
        checkpoint.BeforeImageGenerationException =
            new IOException("stop before generation");

        await Assert.ThrowsAsync<IOException>(
            () => firstClient.CreateImageModelAsync(
                options,
                DocumentSessionId,
                checkpoint,
                CancellationToken.None));

        Assert.Equal("file_resume123", checkpoint.FileToken);
        Assert.NotNull(checkpoint.GenerationRequestFingerprint);
        Assert.Equal(1, firstHandler.CallCount);
        checkpoint.BeforeImageGenerationException = null;
        string? resumedPath = null;
        DelegateHttpMessageHandler resumedHandler = new((request, _) =>
        {
            resumedPath = request.RequestUri?.AbsolutePath;
            return Task.FromResult(
                DelegateHttpMessageHandler.Json(
                    """{"code":0,"data":{"task_id":"task_image123"}}"""));
        });
        Tripo.Mcp.TripoV3Client resumedClient = CreateClient(resumedHandler);

        string taskId = await resumedClient.CreateImageModelAsync(
            options,
            DocumentSessionId,
            checkpoint,
            CancellationToken.None);

        Assert.Equal("task_image123", taskId);
        Assert.Equal("/v3/generation/image-to-model", resumedPath);
        Assert.Equal(1, resumedHandler.CallCount);
        Assert.Equal(1, checkpoint.BeforeImageUploadCalls);
        Assert.Equal(2, checkpoint.BeforeImageGenerationCalls);
    }

    [Fact]
    public async Task ImageFileTokenCheckpointFailureIsNotReclassifiedAsUploadFailure()
    {
        using TemporaryDataRoot root = new();
        Tripo.Mcp.ImageGenerationOptions options =
            await CreateImageOptionsAsync(
                Path.Combine(root.Path, "checkpoint.png"));
        DelegateHttpMessageHandler handler = new((_, _) =>
            Task.FromResult(
                DelegateHttpMessageHandler.Json(
                    """{"code":0,"data":{"file_token":"file_known123"}}""")));
        Tripo.Mcp.TripoV3Client client = CreateClient(handler);
        RecordingImageCheckpoint checkpoint = CreateImageCheckpoint(
            client,
            options);
        checkpoint.FileTokenException = new IOException("disk failure");

        Tripo.Mcp.TripoApiException exception =
            await Assert.ThrowsAsync<Tripo.Mcp.TripoApiException>(
                () => client.CreateImageModelAsync(
                    options,
                    DocumentSessionId,
                    checkpoint,
                    CancellationToken.None));

        Assert.DoesNotContain(
            "file_known123",
            exception.Message,
            StringComparison.Ordinal);
        Assert.Equal(1, handler.CallCount);
        Assert.Equal(0, checkpoint.ImageOutcomeUnknownCalls);
        await using Stream preserved =
            await Tripo.Bridge.ImageTransferStore.OpenVerifiedAsync(
                options.Image,
                CancellationToken.None);
        Assert.Equal(TestPngBytes.Length, preserved.Length);
    }

    [Fact]
    public async Task ImageUploadTransportFailureUsesUploadWarningAndDeletesDurablyAmbiguousSnapshot()
    {
        using TemporaryDataRoot root = new();
        Tripo.Mcp.ImageGenerationOptions options =
            await CreateImageOptionsAsync(
                Path.Combine(root.Path, "upload-failure.png"));
        DelegateHttpMessageHandler handler = new((_, _) =>
            Task.FromException<HttpResponseMessage>(
                new HttpRequestException("connection reset")));
        Tripo.Mcp.TripoV3Client client = CreateClient(handler);
        RecordingImageCheckpoint checkpoint = CreateImageCheckpoint(
            client,
            options);

        Tripo.Mcp.TripoApiException exception =
            await Assert.ThrowsAsync<Tripo.Mcp.TripoApiException>(
                () => client.CreateImageModelAsync(
                    options,
                    DocumentSessionId,
                    checkpoint,
                    CancellationToken.None));

        Assert.Contains(
            "Image upload may have succeeded remotely",
            exception.Message,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Task creation may have succeeded remotely",
            exception.Message,
            StringComparison.Ordinal);
        Assert.Equal(1, handler.CallCount);
        Assert.Equal(1, checkpoint.BeforeImageUploadCalls);
        Assert.Equal(0, checkpoint.BeforeImageGenerationCalls);
        Assert.Equal(1, checkpoint.ImageOutcomeUnknownCalls);
        Assert.Equal("upload", checkpoint.LastUnknownStage);
        Tripo.Bridge.BridgeCallException missing =
            await Assert.ThrowsAsync<Tripo.Bridge.BridgeCallException>(
                () => Tripo.Bridge.ImageTransferStore.OpenVerifiedAsync(
                    options.Image,
                    CancellationToken.None));
        Assert.Equal("image_transfer_missing", missing.Code);
    }

    [Fact]
    public async Task ImageGenerationTransportFailureIsStageSpecificAndNotRetried()
    {
        using TemporaryDataRoot root = new();
        Tripo.Mcp.ImageGenerationOptions options =
            await CreateImageOptionsAsync(
                Path.Combine(root.Path, "generation-failure.png"));
        DelegateHttpMessageHandler handler = new((_, call) =>
            call == 1
                ? Task.FromResult(
                    DelegateHttpMessageHandler.Json(
                        """{"code":0,"data":{"file_token":"file_known123"}}"""))
                : Task.FromException<HttpResponseMessage>(
                    new HttpRequestException("connection reset")));
        Tripo.Mcp.TripoV3Client client = CreateClient(handler);
        RecordingImageCheckpoint checkpoint = CreateImageCheckpoint(
            client,
            options);

        Tripo.Mcp.TripoApiException exception =
            await Assert.ThrowsAsync<Tripo.Mcp.TripoApiException>(
                () => client.CreateImageModelAsync(
                    options,
                    DocumentSessionId,
                    checkpoint,
                    CancellationToken.None));

        Assert.Contains("may have succeeded remotely", exception.Message);
        Assert.Equal(2, handler.CallCount);
        Assert.Equal(1, checkpoint.ImageOutcomeUnknownCalls);
        Assert.Equal("generation", checkpoint.LastUnknownStage);
    }

    [Fact]
    public async Task ObjConversionWithMaterialsRequestsBake()
    {
        JsonDocument? payload = null;
        DelegateHttpMessageHandler handler = new(async (request, _) =>
        {
            payload = JsonDocument.Parse(await request.Content!.ReadAsStreamAsync());
            return DelegateHttpMessageHandler.Json(
                """{"code":0,"data":{"task_id":"task_conversion123"}}""");
        });
        Tripo.Mcp.TripoV3Client client = CreateClient(handler);
        RecordingCheckpoint checkpoint = new(
            client.GetObjConversionOperationFingerprint(
                "task_source123",
                12_000,
                withMaterials: true,
                DocumentSessionId));

        await client.CreateObjConversionAsync(
            "task_source123",
            12_000,
            withMaterials: true,
            DocumentSessionId,
            checkpoint,
            CancellationToken.None);

        Assert.True(payload!.RootElement.GetProperty("bake").GetBoolean());
        payload.Dispose();
    }

    [Fact]
    public void PaidFingerprintFlipsWhenMaterialFlagsFlip()
    {
        DelegateHttpMessageHandler handler = new((_, _) =>
            throw new InvalidOperationException("No network call was expected."));
        Tripo.Mcp.TripoV3Client client = CreateClient(handler);

        string textWithout = client.GetTextTaskOperationFingerprint(
            new Tripo.Mcp.TextGenerationOptions("a chair", 10_000),
            DocumentSessionId);
        string textWith = client.GetTextTaskOperationFingerprint(
            new Tripo.Mcp.TextGenerationOptions(
                "a chair",
                10_000,
                Tripo.Mcp.TripoV3Client.DefaultModel,
                WithMaterials: true),
            DocumentSessionId);
        string conversionWithout = client.GetObjConversionOperationFingerprint(
            "task_source123",
            10_000,
            withMaterials: false,
            DocumentSessionId);
        string conversionWith = client.GetObjConversionOperationFingerprint(
            "task_source123",
            10_000,
            withMaterials: true,
            DocumentSessionId);

        Assert.NotEqual(textWithout, textWith);
        Assert.NotEqual(conversionWithout, conversionWith);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task CreatePostTimeoutIsNotRetriedAndReportsUnknownRemoteState()
    {
        DelegateHttpMessageHandler handler = new((_, _) =>
            Task.FromException<HttpResponseMessage>(new TaskCanceledException()));
        Tripo.Mcp.TripoV3Client client = CreateClient(handler);
        Tripo.Mcp.TextGenerationOptions options =
            new("a test chair", 10_000);
        RecordingCheckpoint checkpoint = CreateCheckpoint(client, options);

        Tripo.Mcp.TripoApiException exception =
            await Assert.ThrowsAsync<Tripo.Mcp.TripoApiException>(
                () => client.CreateTextModelAsync(
                    options,
                    DocumentSessionId,
                    checkpoint,
                    CancellationToken.None));

        Assert.Contains("may have succeeded remotely", exception.Message);
        Assert.Equal(1, handler.CallCount);
        Assert.Equal(1, checkpoint.OutcomeUnknownCalls);
    }

    [Fact]
    public async Task CreatePostBodyTimeoutIsBoundedAndReportsUnknownRemoteState()
    {
        DelegateHttpMessageHandler handler = new((_, _) =>
            Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(new HangingReadStream()),
                }));
        Tripo.Mcp.TripoV3Client client = CreateClient(
            handler,
            requestTimeout: TimeSpan.FromMilliseconds(50));
        Tripo.Mcp.TextGenerationOptions options =
            new("a test chair", 10_000);
        RecordingCheckpoint checkpoint = CreateCheckpoint(client, options);

        Tripo.Mcp.TripoApiException exception =
            await Assert.ThrowsAsync<Tripo.Mcp.TripoApiException>(
                () => client.CreateTextModelAsync(
                    options,
                    DocumentSessionId,
                    checkpoint,
                    CancellationToken.None));

        Assert.Contains("timed out", exception.Message);
        Assert.Contains("may have succeeded remotely", exception.Message);
        Assert.Equal(1, handler.CallCount);
        Assert.Equal(1, checkpoint.OutcomeUnknownCalls);
    }

    [Fact]
    public async Task CreatePostTransportFailureReportsUnknownRemoteState()
    {
        DelegateHttpMessageHandler handler = new((_, _) =>
            Task.FromException<HttpResponseMessage>(
                new HttpRequestException("connection reset")));
        Tripo.Mcp.TripoV3Client client = CreateClient(handler);
        Tripo.Mcp.TextGenerationOptions options =
            new("a test chair", 10_000);
        RecordingCheckpoint checkpoint = CreateCheckpoint(client, options);

        Tripo.Mcp.TripoApiException exception =
            await Assert.ThrowsAsync<Tripo.Mcp.TripoApiException>(
                () => client.CreateTextModelAsync(
                    options,
                    DocumentSessionId,
                    checkpoint,
                    CancellationToken.None));

        Assert.Contains("may have succeeded remotely", exception.Message);
        Assert.Equal(1, handler.CallCount);
        Assert.Equal(1, checkpoint.OutcomeUnknownCalls);
    }

    [Fact]
    public async Task ShortOpaqueKeyIsSentWithoutModification()
    {
        string? authorization = null;
        DelegateHttpMessageHandler handler = new((request, _) =>
        {
            authorization = request.Headers.Authorization?.ToString();
            return Task.FromResult(
                DelegateHttpMessageHandler.Json(
                    """{"code":0,"data":{"task_id":"task_source123"}}"""));
        });
        Tripo.Mcp.TripoV3Client client = CreateClient(handler, () => "x");
        Tripo.Mcp.TextGenerationOptions options =
            new("a test chair", 10_000);
        RecordingCheckpoint checkpoint = CreateCheckpoint(client, options);

        await client.CreateTextModelAsync(
            options,
            DocumentSessionId,
            checkpoint,
            CancellationToken.None);

        Assert.Equal("Bearer x", authorization);
    }

    [Fact]
    public async Task CheckpointFailureBeforeDispatchPerformsNoNetworkCall()
    {
        DelegateHttpMessageHandler handler = new((_, _) =>
            Task.FromResult(
                DelegateHttpMessageHandler.Json(
                    """{"code":0,"data":{"task_id":"task_source123"}}""")));
        Tripo.Mcp.TripoV3Client client = CreateClient(handler);
        Tripo.Mcp.TextGenerationOptions options =
            new("a test chair", 10_000);
        RecordingCheckpoint checkpoint = CreateCheckpoint(client, options);
        checkpoint.BeforeSendException = new IOException("disk failure");

        await Assert.ThrowsAsync<IOException>(
            () => client.CreateTextModelAsync(
                options,
                DocumentSessionId,
                checkpoint,
                CancellationToken.None));

        Assert.Equal(0, handler.CallCount);
        Assert.Equal(0, checkpoint.OutcomeUnknownCalls);
    }

    [Fact]
    public async Task CancellationAfterDispatchMarksTheOutcomeUnknown()
    {
        DelegateHttpMessageHandler handler = new((_, _) =>
            Task.FromResult(
                DelegateHttpMessageHandler.Json(
                    """{"code":0,"data":{"task_id":"task_source123"}}""")));
        Tripo.Mcp.TripoV3Client client = CreateClient(handler);
        Tripo.Mcp.TextGenerationOptions options =
            new("a test chair", 10_000);
        using CancellationTokenSource cancellation = new();
        RecordingCheckpoint checkpoint = CreateCheckpoint(client, options);
        checkpoint.AfterBeforeSend = cancellation.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.CreateTextModelAsync(
                options,
                DocumentSessionId,
                checkpoint,
                cancellation.Token));

        Assert.Equal(1, checkpoint.OutcomeUnknownCalls);
    }

    [Fact]
    public async Task TaskIdCheckpointFailureKeepsTheKnownIdInTheError()
    {
        DelegateHttpMessageHandler handler = new((_, _) =>
            Task.FromResult(
                DelegateHttpMessageHandler.Json(
                    """{"code":0,"data":{"task_id":"task_source123"}}""")));
        Tripo.Mcp.TripoV3Client client = CreateClient(handler);
        Tripo.Mcp.TextGenerationOptions options =
            new("a test chair", 10_000);
        RecordingCheckpoint checkpoint = CreateCheckpoint(client, options);
        checkpoint.TaskIdException = new IOException("disk failure");

        Tripo.Mcp.TripoApiException exception =
            await Assert.ThrowsAsync<Tripo.Mcp.TripoApiException>(
                () => client.CreateTextModelAsync(
                    options,
                    DocumentSessionId,
                    checkpoint,
                    CancellationToken.None));

        Assert.Contains("task_source123", exception.Message);
        Assert.Equal(1, handler.CallCount);
        Assert.Equal(0, checkpoint.OutcomeUnknownCalls);
    }

    [Fact]
    public async Task FingerprintMismatchFailsBeforeCheckpointOrNetwork()
    {
        DelegateHttpMessageHandler handler = new((_, _) =>
            Task.FromResult(
                DelegateHttpMessageHandler.Json(
                    """{"code":0,"data":{"task_id":"task_source123"}}""")));
        Tripo.Mcp.TripoV3Client client = CreateClient(handler);
        Tripo.Mcp.TextGenerationOptions options =
            new("a test chair", 10_000);
        RecordingCheckpoint checkpoint = new(new string('f', 64));

        await Assert.ThrowsAsync<Tripo.Mcp.TripoApiException>(
            () => client.CreateTextModelAsync(
                options,
                DocumentSessionId,
                checkpoint,
                CancellationToken.None));

        Assert.Equal(0, checkpoint.BeforeSendCalls);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public void PaidOperationFingerprintBindsCredentialAndExactPayload()
    {
        DelegateHttpMessageHandler handler = new((_, _) =>
            throw new InvalidOperationException("No network call was expected."));
        Tripo.Mcp.TripoV3Client firstClient =
            CreateClient(handler, () => "first_key");
        Tripo.Mcp.TripoV3Client secondClient =
            CreateClient(handler, () => "second_key");

        string first = firstClient.GetTextTaskOperationFingerprint(
            new Tripo.Mcp.TextGenerationOptions("a chair", 10_000),
            DocumentSessionId);
        string changedKey = secondClient.GetTextTaskOperationFingerprint(
            new Tripo.Mcp.TextGenerationOptions("a chair", 10_000),
            DocumentSessionId);
        string changedPrompt = firstClient.GetTextTaskOperationFingerprint(
            new Tripo.Mcp.TextGenerationOptions("a table", 10_000),
            DocumentSessionId);

        Assert.NotEqual(first, changedKey);
        Assert.NotEqual(first, changedPrompt);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task CredentialChangeAfterPrepareFailsBeforeDispatch()
    {
        string key = "first_key";
        DelegateHttpMessageHandler handler = new((_, _) =>
            Task.FromResult(
                DelegateHttpMessageHandler.Json(
                    """{"code":0,"data":{"task_id":"task_source123"}}""")));
        Tripo.Mcp.TripoV3Client client = CreateClient(handler, () => key);
        Tripo.Mcp.TextGenerationOptions options =
            new("a chair", 10_000);
        RecordingCheckpoint checkpoint = CreateCheckpoint(client, options);
        key = "second_key";

        await Assert.ThrowsAsync<Tripo.Mcp.TripoApiException>(
            () => client.CreateTextModelAsync(
                options,
                DocumentSessionId,
                checkpoint,
                CancellationToken.None));

        Assert.Equal(0, checkpoint.BeforeSendCalls);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task InvalidOpaqueKeyIsRejectedBeforeNetworkAccess()
    {
        DelegateHttpMessageHandler handler = new((_, _) =>
            Task.FromResult(DelegateHttpMessageHandler.Json("""{"code":0}""")));
        Tripo.Mcp.TripoV3Client client = CreateClient(
            handler,
            () => "opaque key");

        await Assert.ThrowsAsync<Tripo.Mcp.TripoCredentialException>(
            () => client.GetTaskAsync(
                "task_expected123",
                CancellationToken.None));

        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task RemoteErrorTextIsBoundedAndControlCharactersAreSanitized()
    {
        string remoteMessage = "bad\u202e\n" + new string('x', 600);
        string responseJson = JsonSerializer.Serialize(
            new
            {
                code = 500,
                message = remoteMessage,
                request_id = "request\n123",
            });
        DelegateHttpMessageHandler handler = new((_, _) =>
            Task.FromResult(
                DelegateHttpMessageHandler.Json(
                    responseJson,
                    HttpStatusCode.ServiceUnavailable)));
        Tripo.Mcp.TripoV3Client client = CreateClient(handler);

        Tripo.Mcp.TripoApiException exception =
            await Assert.ThrowsAsync<Tripo.Mcp.TripoApiException>(
                () => client.GetTaskAsync(
                    "task_expected123",
                    CancellationToken.None));

        Assert.True(exception.Message.Length <= 512);
        Assert.All(
            exception.Message,
            character => Assert.False(char.IsControl(character)));
        Assert.DoesNotContain('\u202e', exception.Message);
        Assert.Equal("request 123", exception.RequestId);
    }

    [Fact]
    public void RemoteTextTruncationDoesNotSplitASurrogatePair()
    {
        string bounded = Tripo.Mcp.RemoteText.Bound("1234😀rest", 5);

        Assert.Equal("1234", bounded);
    }

    [Fact]
    public async Task GetCancellationFromTransportIsClassifiedAsRetryableReadFailure()
    {
        DelegateHttpMessageHandler handler = new((_, _) =>
            Task.FromException<HttpResponseMessage>(
                new OperationCanceledException()));
        Tripo.Mcp.TripoV3Client client = CreateClient(handler);

        Tripo.Mcp.TripoApiException exception =
            await Assert.ThrowsAsync<Tripo.Mcp.TripoApiException>(
                () => client.GetTaskAsync(
                    "task_expected123",
                    CancellationToken.None));

        Assert.True(exception.IsRetryableReadFailure);
    }

    [Fact]
    public async Task GetTaskAsyncRejectsMismatchedTaskId()
    {
        DelegateHttpMessageHandler handler = new((_, _) =>
            Task.FromResult(
                DelegateHttpMessageHandler.Json(
                    """
                    {
                      "code": 0,
                      "data": {
                        "task_id": "task_other123",
                        "type": "text_to_model",
                        "status": "success",
                        "progress": 100
                      }
                    }
                    """)));
        Tripo.Mcp.TripoV3Client client = CreateClient(handler);

        await Assert.ThrowsAsync<Tripo.Mcp.TripoApiException>(
            () => client.GetTaskAsync("task_expected123", CancellationToken.None));
    }

    [Fact]
    public async Task ModelEnvironmentVariableOverridesTheAcceptedModel()
    {
        JsonDocument? payload = null;
        DelegateHttpMessageHandler handler = new(async (request, _) =>
        {
            payload = JsonDocument.Parse(await request.Content!.ReadAsStreamAsync());
            return DelegateHttpMessageHandler.Json(
                """{"code":0,"data":{"task_id":"task_source123"}}""");
        });
        Tripo.Mcp.TripoV3Client client = CreateClient(
            handler,
            modelEnvironmentProvider: () => "tripo-v3.1");
        Tripo.Mcp.TextGenerationOptions options =
            new("a test chair", 10_000, "tripo-v3.1");
        RecordingCheckpoint checkpoint = new(
            client.GetTextTaskOperationFingerprint(options, DocumentSessionId));

        string taskId = await client.CreateTextModelAsync(
            options,
            DocumentSessionId,
            checkpoint,
            CancellationToken.None);

        Assert.Equal("task_source123", taskId);
        Assert.Equal(
            "tripo-v3.1",
            payload!.RootElement.GetProperty("model").GetString());
        payload.Dispose();
    }

    [Fact]
    public void InvalidModelEnvironmentVariableIsRejectedBeforeNetworkAccess()
    {
        DelegateHttpMessageHandler handler = new((_, _) =>
            throw new InvalidOperationException("No network call was expected."));
        Tripo.Mcp.TripoV3Client client = CreateClient(
            handler,
            modelEnvironmentProvider: () => "bad model!");
        Tripo.Mcp.TextGenerationOptions options =
            new("a test chair", 10_000);

        Assert.Throws<Tripo.Mcp.TripoApiException>(
            () => client.GetTextTaskOperationFingerprint(options, DocumentSessionId));

        Assert.Equal(0, handler.CallCount);
    }

    private static Tripo.Mcp.TripoV3Client CreateClient(
        HttpMessageHandler handler,
        Func<string?>? apiKeyProvider = null,
        TimeSpan? requestTimeout = null,
        Func<string?>? modelEnvironmentProvider = null)
    {
        HttpClient httpClient = new(handler)
        {
            BaseAddress = Tripo.Mcp.TripoV3Client.BaseUri,
        };
        return new Tripo.Mcp.TripoV3Client(
            httpClient,
            apiKeyProvider ?? (() => "opaque_test_key"),
            requestTimeout,
            modelEnvironmentProvider ?? (() => null));
    }

    private static RecordingCheckpoint CreateCheckpoint(
        Tripo.Mcp.TripoV3Client client,
        Tripo.Mcp.TextGenerationOptions options) =>
        new(client.GetTextTaskOperationFingerprint(options, DocumentSessionId));

    private static readonly byte[] TestPngBytes =
    [
        0x89,
        (byte)'P',
        (byte)'N',
        (byte)'G',
        0x0d,
        0x0a,
        0x1a,
        0x0a,
        0x00,
        0x01,
    ];

    private static async Task<Tripo.Mcp.ImageGenerationOptions>
        CreateImageOptionsAsync(string sourcePath)
    {
        await File.WriteAllBytesAsync(sourcePath, TestPngBytes);
        Tripo.Bridge.StagedImageTransfer transfer =
            await Tripo.Bridge.ImageTransferStore.StageAsync(
                sourcePath,
                CancellationToken.None);
        return new Tripo.Mcp.ImageGenerationOptions(
            transfer,
            10_000);
    }

    private static RecordingImageCheckpoint CreateImageCheckpoint(
        Tripo.Mcp.TripoV3Client client,
        Tripo.Mcp.ImageGenerationOptions options) =>
        new(
            client.GetImageTaskOperationFingerprint(
                options,
                DocumentSessionId));

    private sealed class RecordingCheckpoint :
        Tripo.Mcp.ITaskCreationCheckpoint
    {
        public RecordingCheckpoint(string requestFingerprint)
        {
            RequestFingerprint = requestFingerprint;
        }

        public string RequestFingerprint { get; }

        public int BeforeSendCalls { get; private set; }

        public int OutcomeUnknownCalls { get; private set; }

        public int RequestRejectedCalls { get; private set; }

        public string? TaskId { get; private set; }

        public Exception? BeforeSendException { get; set; }

        public Exception? TaskIdException { get; set; }

        public Exception? RequestRejectedException { get; set; }

        public Action? AfterBeforeSend { get; set; }

        public Task BeforeSendAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (BeforeSendException is not null)
            {
                throw BeforeSendException;
            }

            BeforeSendCalls++;
            AfterBeforeSend?.Invoke();
            return Task.CompletedTask;
        }

        public Task TaskIdReceivedAsync(string taskId)
        {
            if (TaskIdException is not null)
            {
                throw TaskIdException;
            }

            TaskId = taskId;
            return Task.CompletedTask;
        }

        public Task OutcomeUnknownAsync(string code, string message)
        {
            OutcomeUnknownCalls++;
            return Task.CompletedTask;
        }

        public Task RequestRejectedAsync(string code, string message)
        {
            RequestRejectedCalls++;
            if (RequestRejectedException is not null)
            {
                throw RequestRejectedException;
            }

            return Task.CompletedTask;
        }
    }

    private sealed class RecordingImageCheckpoint :
        Tripo.Mcp.IImageTaskCreationCheckpoint
    {
        public RecordingImageCheckpoint(string requestFingerprint)
        {
            RequestFingerprint = requestFingerprint;
        }

        public string RequestFingerprint { get; }

        public string? FileToken { get; private set; }

        public string? GenerationRequestFingerprint { get; private set; }

        public string? TaskId { get; private set; }

        public int BeforeImageUploadCalls { get; private set; }

        public int BeforeImageGenerationCalls { get; private set; }

        public int ImageOutcomeUnknownCalls { get; private set; }

        public string? LastUnknownStage { get; private set; }

        public Exception? BeforeImageGenerationException { get; set; }

        public Exception? FileTokenException { get; set; }

        public void SeedDurableFileToken(
            string fileToken,
            string generationRequestFingerprint)
        {
            FileToken = fileToken;
            GenerationRequestFingerprint = generationRequestFingerprint;
        }

        public Task BeforeSendAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                "Image operations must not use the generic dispatch checkpoint.");

        public Task OutcomeUnknownAsync(string code, string message) =>
            throw new InvalidOperationException(
                "Image operations must record the failed stage.");

        public Task RequestRejectedAsync(string code, string message) =>
            throw new InvalidOperationException(
                "Image operations must record the rejected stage.");

        public Task BeforeImageUploadAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BeforeImageUploadCalls++;
            return Task.CompletedTask;
        }

        public Task ImageFileTokenReceivedAsync(
            string fileToken,
            string generationRequestFingerprint)
        {
            if (FileTokenException is not null)
            {
                throw FileTokenException;
            }

            FileToken = fileToken;
            GenerationRequestFingerprint = generationRequestFingerprint;
            return Task.CompletedTask;
        }

        public Task BeforeImageGenerationAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BeforeImageGenerationCalls++;
            if (BeforeImageGenerationException is not null)
            {
                throw BeforeImageGenerationException;
            }

            return Task.CompletedTask;
        }

        public Task ImageOutcomeUnknownAsync(
            string stage,
            string code,
            string message)
        {
            ImageOutcomeUnknownCalls++;
            LastUnknownStage = stage;
            return Task.CompletedTask;
        }

        public Task TaskIdReceivedAsync(string taskId)
        {
            TaskId = taskId;
            return Task.CompletedTask;
        }
    }
}
