using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;

namespace Tripo.Mcp;

public interface ITripoApiClient
{
    string ResolveEffectiveModel();

    string GetTextTaskOperationFingerprint(
        TextGenerationOptions options,
        string documentSessionId);

    string GetImageTaskOperationFingerprint(
        ImageGenerationOptions options,
        string documentSessionId) =>
        throw new NotSupportedException(
            "This API client does not support image generation.");

    string GetObjConversionOperationFingerprint(
        string taskId,
        int faceLimit,
        bool withMaterials,
        string documentSessionId);

    Task<string> CreateTextModelAsync(
        TextGenerationOptions options,
        string documentSessionId,
        ITaskCreationCheckpoint checkpoint,
        CancellationToken cancellationToken);

    Task<string> CreateImageModelAsync(
        ImageGenerationOptions options,
        string documentSessionId,
        IImageTaskCreationCheckpoint checkpoint,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException(
            "This API client does not support image generation.");

    Task<string> CreateObjConversionAsync(
        string taskId,
        int faceLimit,
        bool withMaterials,
        string documentSessionId,
        ITaskCreationCheckpoint checkpoint,
        CancellationToken cancellationToken);

    Task<TripoTaskSnapshot> GetTaskAsync(
        string taskId,
        CancellationToken cancellationToken);
}

public sealed partial class TripoV3Client : ITripoApiClient
{
    public const string ApiKeyEnvironmentVariable = "TRIPO_API_KEY";
    public const string ModelEnvironmentVariable = "TRIPO_MODEL";
    public const string DefaultModel = "v3.1-20260211";
    public static readonly Uri BaseUri = new("https://openapi.tripo3d.ai/v3/");

    private const int MaximumResponseBytes = 1024 * 1024;
    private const string ImageUploadPath = "files";
    private const string ImageGenerationPath =
        "generation/image-to-model";
    private const string UnknownCreationWarning =
        " Task creation may have succeeded remotely; the request was not retried.";
    private const string UnknownUploadWarning =
        " Image upload may have succeeded remotely; the request was not retried.";
    private static readonly string ImageUploadAbsolutePath =
        new Uri(BaseUri, ImageUploadPath).AbsolutePath;
    private static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromMinutes(2);
    private readonly HttpClient _httpClient;
    private readonly Func<string?> _apiKeyProvider;
    private readonly Func<string?> _modelEnvironmentProvider;
    private readonly TimeSpan _requestTimeout;

    public TripoV3Client(HttpClient httpClient)
        : this(
            httpClient,
            () => Environment.GetEnvironmentVariable(ApiKeyEnvironmentVariable))
    {
    }

    [ActivatorUtilitiesConstructor]
    public TripoV3Client(
        HttpClient httpClient,
        ITripoApiKeyProvider apiKeyProvider)
        : this(
            httpClient,
            (apiKeyProvider ??
             throw new ArgumentNullException(nameof(apiKeyProvider))).GetApiKey)
    {
    }

    internal TripoV3Client(
        HttpClient httpClient,
        Func<string?> apiKeyProvider,
        TimeSpan? requestTimeout = null,
        Func<string?>? modelEnvironmentProvider = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _apiKeyProvider = apiKeyProvider ?? throw new ArgumentNullException(nameof(apiKeyProvider));
        _requestTimeout = requestTimeout ?? DefaultRequestTimeout;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            _requestTimeout,
            TimeSpan.Zero,
            nameof(requestTimeout));
        _modelEnvironmentProvider = modelEnvironmentProvider
            ?? (() => Environment.GetEnvironmentVariable(ModelEnvironmentVariable));
    }

    public string ResolveEffectiveModel()
    {
        string? overrideModel = _modelEnvironmentProvider();
        if (overrideModel is null)
        {
            return DefaultModel;
        }

        if (!ModelIdRegex().IsMatch(overrideModel))
        {
            throw new TripoApiException(
                $"{ModelEnvironmentVariable} is set but is not a valid model identifier.");
        }

        return overrideModel;
    }

    public string GetTextTaskOperationFingerprint(
        TextGenerationOptions options,
        string documentSessionId)
    {
        TextGenerationRequest payload = CreateTextGenerationRequest(options);
        return ComputePaidOperationFingerprint(
            "generation/text-to-model",
            documentSessionId,
            SerializeRequest(payload),
            GetValidatedApiKey());
    }

    public string GetImageTaskOperationFingerprint(
        ImageGenerationOptions options,
        string documentSessionId)
    {
        ImageOperationIdentity identity =
            CreateImageOperationIdentity(options);
        return ComputePaidOperationFingerprint(
            "files+generation/image-to-model",
            documentSessionId,
            SerializeRequest(identity),
            GetValidatedApiKey());
    }

    public string GetObjConversionOperationFingerprint(
        string taskId,
        int faceLimit,
        bool withMaterials,
        string documentSessionId)
    {
        ConvertModelRequest payload =
            CreateObjConversionRequest(taskId, faceLimit, withMaterials);
        return ComputePaidOperationFingerprint(
            "models/convert",
            documentSessionId,
            SerializeRequest(payload),
            GetValidatedApiKey());
    }

    public Task<string> CreateTextModelAsync(
        TextGenerationOptions options,
        string documentSessionId,
        ITaskCreationCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        TextGenerationRequest payload = CreateTextGenerationRequest(options);
        byte[] payloadBytes = SerializeRequest(payload);
        string apiKey = EnsureCheckpointMatchesRequest(
            checkpoint,
            "generation/text-to-model",
            documentSessionId,
            payloadBytes);
        return CreateTaskAsync(
            "generation/text-to-model",
            payloadBytes,
            checkpoint,
            apiKey,
            cancellationToken);
    }

    public async Task<string> CreateImageModelAsync(
        ImageGenerationOptions options,
        string documentSessionId,
        IImageTaskCreationCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        ImageOperationIdentity identity =
            CreateImageOperationIdentity(options);
        byte[] identityBytes = SerializeRequest(identity);
        string apiKey = EnsureCheckpointMatchesLogicalImageRequest(
            checkpoint,
            documentSessionId,
            identityBytes);

        string fileToken = checkpoint.FileToken ??
            await UploadImageAsync(
                    options,
                    documentSessionId,
                    checkpoint,
                    apiKey,
                    cancellationToken)
                .ConfigureAwait(false);
        ImageGenerationRequest payload =
            CreateImageGenerationRequest(fileToken, options);
        byte[] payloadBytes = SerializeRequest(payload);
        string expectedGenerationFingerprint =
            ComputePaidOperationFingerprint(
                ImageGenerationPath,
                documentSessionId,
                payloadBytes,
                apiKey);
        if (!string.Equals(
                checkpoint.GenerationRequestFingerprint,
                expectedGenerationFingerprint,
                StringComparison.Ordinal))
        {
            throw new TripoApiException(
                "The durable image file token does not match the exact " +
                "generation request checkpoint.");
        }

        return await CreateImageTaskAsync(
                payloadBytes,
                checkpoint,
                apiKey,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<string> CreateObjConversionAsync(
        string taskId,
        int faceLimit,
        bool withMaterials,
        string documentSessionId,
        ITaskCreationCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        ConvertModelRequest payload =
            CreateObjConversionRequest(taskId, faceLimit, withMaterials);
        byte[] payloadBytes = SerializeRequest(payload);
        string apiKey = EnsureCheckpointMatchesRequest(
            checkpoint,
            "models/convert",
            documentSessionId,
            payloadBytes);
        return CreateTaskAsync(
            "models/convert",
            payloadBytes,
            checkpoint,
            apiKey,
            cancellationToken);
    }

    private TextGenerationRequest CreateTextGenerationRequest(
        TextGenerationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidatePrompt(options.Prompt);
        ValidateFaceLimit(options.FaceLimit);
        string effectiveModel = ResolveEffectiveModel();
        if (!string.Equals(options.Model, effectiveModel, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"The first release supports only model {effectiveModel}.",
                nameof(options));
        }

        return new TextGenerationRequest(
            options.Prompt,
            options.Model,
            options.FaceLimit,
            Texture: options.WithMaterials,
            Pbr: options.WithMaterials,
            AutoSize: true,
            Quad: false,
            ExportUv: false);
    }

    private ImageOperationIdentity CreateImageOperationIdentity(
        ImageGenerationOptions options)
    {
        ValidateImageOptions(options);
        return new ImageOperationIdentity(
            ImageUploadPath,
            ImageGenerationPath,
            options.Image.Sha256,
            options.Image.ByteLength,
            options.Image.MediaType,
            options.Model,
            options.FaceLimit,
            Texture: options.WithMaterials,
            Pbr: options.WithMaterials,
            AutoSize: true,
            Quad: false,
            ExportUv: false);
    }

    private ImageGenerationRequest CreateImageGenerationRequest(
        string fileToken,
        ImageGenerationOptions options)
    {
        ValidateFileToken(fileToken);
        ValidateImageOptions(options);
        return new ImageGenerationRequest(
            fileToken,
            options.Model,
            options.FaceLimit,
            Texture: options.WithMaterials,
            Pbr: options.WithMaterials,
            AutoSize: true,
            Quad: false,
            ExportUv: false);
    }

    private void ValidateImageOptions(ImageGenerationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Tripo.Bridge.ImageTransferStore.ValidateDescriptor(options.Image);
        ValidateFaceLimit(options.FaceLimit);
        string effectiveModel = ResolveEffectiveModel();
        if (!string.Equals(options.Model, effectiveModel, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"The first release supports only model {effectiveModel}.",
                nameof(options));
        }
    }

    private static ConvertModelRequest CreateObjConversionRequest(
        string taskId,
        int faceLimit,
        bool withMaterials)
    {
        ValidateTaskId(taskId);
        ValidateFaceLimit(faceLimit);
        return new ConvertModelRequest(
            taskId,
            "OBJ",
            Quad: false,
            faceLimit,
            Bake: withMaterials,
            WithAnimation: false);
    }

    private static byte[] SerializeRequest<TRequest>(TRequest request) =>
        JsonSerializer.SerializeToUtf8Bytes(request);

    private string EnsureCheckpointMatchesRequest(
        ITaskCreationCheckpoint checkpoint,
        string relativePath,
        string documentSessionId,
        byte[] payload)
    {
        string apiKey = GetValidatedApiKey();
        string expected = ComputePaidOperationFingerprint(
            relativePath,
            documentSessionId,
            payload,
            apiKey);
        if (!string.Equals(
                checkpoint.RequestFingerprint,
                expected,
                StringComparison.Ordinal))
        {
            throw new TripoApiException(
                "The paid-operation checkpoint does not match the API credential, " +
                "endpoint, document session, or exact request payload.");
        }

        return apiKey;
    }

    private string EnsureCheckpointMatchesLogicalImageRequest(
        IImageTaskCreationCheckpoint checkpoint,
        string documentSessionId,
        byte[] identity)
    {
        string apiKey = GetValidatedApiKey();
        string expected = ComputePaidOperationFingerprint(
            "files+generation/image-to-model",
            documentSessionId,
            identity,
            apiKey);
        if (!string.Equals(
                checkpoint.RequestFingerprint,
                expected,
                StringComparison.Ordinal))
        {
            throw new TripoApiException(
                "The image operation checkpoint does not match the API " +
                "credential, document session, staged image content, or exact " +
                "generation options.");
        }

        return apiKey;
    }

    private static string ComputePaidOperationFingerprint(
        string relativePath,
        string documentSessionId,
        byte[] payload,
        string apiKey)
    {
        if (!Guid.TryParseExact(documentSessionId, "D", out Guid documentId) ||
            !string.Equals(
                documentId.ToString("D"),
                documentSessionId,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "documentSessionId must be a canonical D-format UUID.",
                nameof(documentSessionId));
        }

        using MemoryStream fingerprintInput = new();
        using (BinaryWriter writer = new(
                   fingerprintInput,
                   System.Text.Encoding.UTF8,
                   leaveOpen: true))
        {
            writer.Write("tripo-paid-operation-request-v1");
            writer.Write(BaseUri.AbsoluteUri);
            writer.Write(relativePath);
            writer.Write(documentSessionId);
            writer.Write(payload.Length);
            writer.Write(payload);
        }

        byte[] keyBytes = System.Text.Encoding.UTF8.GetBytes(apiKey);
        try
        {
            return Convert.ToHexString(
                    HMACSHA256.HashData(keyBytes, fingerprintInput.ToArray()))
                .ToLowerInvariant();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyBytes);
        }
    }

    public async Task<TripoTaskSnapshot> GetTaskAsync(
        string taskId,
        CancellationToken cancellationToken)
    {
        ValidateTaskId(taskId);
        using HttpRequestMessage request = CreateApiRequest(
            HttpMethod.Get,
            "tasks/" + Uri.EscapeDataString(taskId));
        ApiEnvelope<TripoTaskSnapshot> envelope =
            await SendAsync<TripoTaskSnapshot>(request, cancellationToken)
                .ConfigureAwait(false);
        TripoTaskSnapshot task = envelope.Data
            ?? throw new TripoApiException(
                "Tripo returned a successful task response without task data.",
                apiCode: envelope.Code,
                requestId: envelope.RequestId);
        if (!string.Equals(task.TaskId, taskId, StringComparison.Ordinal))
        {
            throw new TripoApiException(
                "Tripo returned task data for a different task ID.",
                apiCode: envelope.Code,
                requestId: envelope.RequestId);
        }

        return task;
    }

    private async Task<string> UploadImageAsync(
        ImageGenerationOptions options,
        string documentSessionId,
        IImageTaskCreationCheckpoint checkpoint,
        string apiKey,
        CancellationToken cancellationToken)
    {
        await using Stream image =
            await Tripo.Bridge.ImageTransferStore.OpenVerifiedAsync(
                    options.Image,
                    cancellationToken)
                .ConfigureAwait(false);
        using HttpRequestMessage request = CreateApiRequest(
            HttpMethod.Post,
            ImageUploadPath,
            apiKey);
        using MultipartFormDataContent multipart = new();
        using StreamContent fileContent = new(image);
        fileContent.Headers.ContentType =
            new MediaTypeHeaderValue(options.Image.MediaType);
        string genericFileName = options.Image.MediaType switch
        {
            "image/png" => "input.png",
            "image/webp" => "input.webp",
            _ => "input.jpg",
        };
        multipart.Add(fileContent, "file", genericFileName);
        request.Content = multipart;

        bool dispatching = false;
        bool validFileTokenReceived = false;
        try
        {
            await checkpoint.BeforeImageUploadAsync(cancellationToken)
                .ConfigureAwait(false);
            dispatching = true;
            ApiEnvelope<UploadFileData> envelope =
                await SendAsync<UploadFileData>(request, cancellationToken)
                    .ConfigureAwait(false);
            string? fileToken = envelope.Data?.FileToken;
            if (!IsValidFileToken(fileToken))
            {
                throw new TripoApiException(
                    "Tripo returned a successful image upload response without " +
                    "a valid file token. The upload outcome cannot be retried " +
                    "automatically.",
                    apiCode: envelope.Code,
                    requestId: envelope.RequestId);
            }

            validFileTokenReceived = true;
            ImageGenerationRequest generationRequest =
                CreateImageGenerationRequest(fileToken!, options);
            string generationFingerprint =
                ComputePaidOperationFingerprint(
                    ImageGenerationPath,
                    documentSessionId,
                    SerializeRequest(generationRequest),
                    apiKey);
            try
            {
                await checkpoint.ImageFileTokenReceivedAsync(
                        fileToken!,
                        generationFingerprint)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                throw new TripoApiException(
                    "Tripo returned a file token, but its exact local " +
                    "generation checkpoint could not be persisted. Do not upload " +
                    "this image again; preserve this operation ID and inspect the " +
                    "operation journal.",
                    apiCode: envelope.Code,
                    requestId: envelope.RequestId,
                    innerException: exception);
            }

            Tripo.Bridge.ImageTransferStore.TryDelete(options.Image);
            return fileToken!;
        }
        catch (Exception operationException)
        {
            bool requestRejected = false;
            if (dispatching && !validFileTokenReceived)
            {
                requestRejected =
                    IsDefinitiveCredentialRejection(operationException);
                try
                {
                    if (requestRejected)
                    {
                        await checkpoint.RequestRejectedAsync(
                                "credential_rejected",
                                operationException.Message)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        await checkpoint.ImageOutcomeUnknownAsync(
                                "upload",
                                OperationFailureCode(operationException),
                                operationException.Message)
                            .ConfigureAwait(false);
                    }

                    Tripo.Bridge.ImageTransferStore.TryDelete(options.Image);
                }
                catch (Exception checkpointException)
                {
                    throw new TripoApiException(
                        requestRejected
                            ? "Tripo rejected the image-upload credential, but " +
                              "the local request-rejected checkpoint could not " +
                              "be persisted. Do not retry with a new operationId."
                            : "The Tripo image upload failed and its local " +
                              "ambiguous checkpoint could not be persisted. Do " +
                              "not retry this operationId.",
                        innerException: new AggregateException(
                            operationException,
                            checkpointException));
                }
            }

            if (requestRejected &&
                operationException is TripoApiException rejection)
            {
                throw new TripoPaidRequestRejectedException(rejection);
            }

            throw;
        }
    }

    private async Task<string> CreateImageTaskAsync(
        byte[] payload,
        IImageTaskCreationCheckpoint checkpoint,
        string apiKey,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = CreateApiRequest(
            HttpMethod.Post,
            ImageGenerationPath,
            apiKey);
        request.Content = new ByteArrayContent(payload);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(
            "application/json");
        bool dispatching = false;
        bool validTaskIdReceived = false;
        try
        {
            await checkpoint.BeforeImageGenerationAsync(cancellationToken)
                .ConfigureAwait(false);
            dispatching = true;
            ApiEnvelope<CreateTaskData> envelope =
                await SendAsync<CreateTaskData>(request, cancellationToken)
                    .ConfigureAwait(false);
            string? taskId = envelope.Data?.TaskId;
            if (!IsValidTaskId(taskId))
            {
                throw new TripoApiException(
                    "Tripo returned a successful image generation response " +
                    "without a valid task ID." +
                    UnknownCreationWarning,
                    apiCode: envelope.Code,
                    requestId: envelope.RequestId);
            }

            validTaskIdReceived = true;
            try
            {
                await checkpoint.TaskIdReceivedAsync(taskId!)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                throw new TripoApiException(
                    $"Tripo returned task ID {taskId}, but its local recovery " +
                    "checkpoint could not be persisted. Do not create a " +
                    "replacement operation; preserve this ID and inspect the " +
                    "operation journal.",
                    apiCode: envelope.Code,
                    requestId: envelope.RequestId,
                    innerException: exception);
            }

            return taskId!;
        }
        catch (Exception operationException)
        {
            bool requestRejected = false;
            if (dispatching && !validTaskIdReceived)
            {
                requestRejected =
                    IsDefinitiveCredentialRejection(operationException);
                try
                {
                    if (requestRejected)
                    {
                        await checkpoint.RequestRejectedAsync(
                                "credential_rejected",
                                operationException.Message)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        await checkpoint.ImageOutcomeUnknownAsync(
                                "generation",
                                OperationFailureCode(operationException),
                                operationException.Message)
                            .ConfigureAwait(false);
                    }
                }
                catch (Exception checkpointException)
                {
                    throw new TripoApiException(
                        requestRejected
                            ? "Tripo rejected the image-generation credential, " +
                              "but the local request-rejected checkpoint could " +
                              "not be persisted. Do not retry with a new " +
                              "operationId."
                            : "The paid Tripo image generation request failed " +
                              "and its local outcome-unknown checkpoint could " +
                              "not be persisted. Do not retry with a new " +
                              "operationId.",
                        innerException: new AggregateException(
                            operationException,
                            checkpointException));
                }
            }

            if (requestRejected &&
                operationException is TripoApiException rejection)
            {
                throw new TripoPaidRequestRejectedException(rejection);
            }

            throw;
        }
    }

    private async Task<string> CreateTaskAsync(
        string relativePath,
        byte[] payload,
        ITaskCreationCheckpoint checkpoint,
        string apiKey,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = CreateApiRequest(
            HttpMethod.Post,
            relativePath,
            apiKey);
        request.Content = new ByteArrayContent(payload);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(
            "application/json");
        bool dispatching = false;
        bool validTaskIdReceived = false;
        try
        {
            await checkpoint.BeforeSendAsync(cancellationToken).ConfigureAwait(false);
            dispatching = true;
            ApiEnvelope<CreateTaskData> envelope =
                await SendAsync<CreateTaskData>(request, cancellationToken)
                    .ConfigureAwait(false);
            string? taskId = envelope.Data?.TaskId;
            if (!IsValidTaskId(taskId))
            {
                throw new TripoApiException(
                    "Tripo returned a successful create response without a valid task ID." +
                    UnknownCreationWarning,
                    apiCode: envelope.Code,
                    requestId: envelope.RequestId);
            }

            validTaskIdReceived = true;
            try
            {
                await checkpoint.TaskIdReceivedAsync(taskId!).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                throw new TripoApiException(
                    $"Tripo returned task ID {taskId}, but its local recovery " +
                    "checkpoint could not be persisted. Do not create a replacement " +
                    "operation; preserve this ID and inspect the operation journal.",
                    apiCode: envelope.Code,
                    requestId: envelope.RequestId,
                    innerException: exception);
            }

            return taskId!;
        }
        catch (Exception operationException)
        {
            bool requestRejected = false;
            if (dispatching && !validTaskIdReceived)
            {
                requestRejected =
                    IsDefinitiveCredentialRejection(operationException);
                try
                {
                    if (requestRejected)
                    {
                        await checkpoint.RequestRejectedAsync(
                                "credential_rejected",
                                operationException.Message)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        await checkpoint.OutcomeUnknownAsync(
                                OperationFailureCode(operationException),
                                operationException.Message)
                            .ConfigureAwait(false);
                    }
                }
                catch (Exception checkpointException)
                {
                    throw new TripoApiException(
                        requestRejected
                            ? "Tripo rejected the credential, but the local " +
                              "request-rejected checkpoint could not be persisted. " +
                              "Do not retry with a new operationId."
                            : "The paid Tripo request failed and its local " +
                              "outcome-unknown checkpoint could not be persisted. " +
                              "Do not retry with a new operationId.",
                        innerException: new AggregateException(
                            operationException,
                            checkpointException));
                }
            }

            if (requestRejected &&
                operationException is TripoApiException rejection)
            {
                throw new TripoPaidRequestRejectedException(rejection);
            }

            throw;
        }
    }

    private static string OperationFailureCode(Exception exception) =>
        exception switch
        {
            OperationCanceledException => "request_cancelled",
            TripoApiException => "api_failure",
            HttpRequestException => "transport_failure",
            IOException => "transport_failure",
            _ => "post_failure",
        };

    private static bool IsDefinitiveCredentialRejection(
        Exception exception) =>
        exception is TripoApiException apiException &&
        IsDefinitiveCredentialRejection(apiException.StatusCode);

    private static bool IsDefinitiveCredentialRejection(
        HttpStatusCode? statusCode) =>
        statusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;

    private HttpRequestMessage CreateApiRequest(
        HttpMethod method,
        string relativePath,
        string? paidOperationApiKey = null)
    {
        string apiKey = paidOperationApiKey ?? GetValidatedApiKey();
        HttpRequestMessage request = new(method, relativePath);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Headers.UserAgent.ParseAdd("tripo-rhino/0.1.0");
        return request;
    }

    private string GetValidatedApiKey()
    {
        string? apiKey = _apiKeyProvider();
        ApiCredentialService.ValidateApiKey(apiKey ?? string.Empty);

        return apiKey!;
    }

    private async Task<ApiEnvelope<T>> SendAsync<T>(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource requestDeadline =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        requestDeadline.CancelAfter(_requestTimeout);
        try
        {
            using HttpResponseMessage response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    requestDeadline.Token)
                .ConfigureAwait(false);
            if (IsDefinitiveCredentialRejection(response.StatusCode))
            {
                throw new TripoApiException(
                    $"Tripo rejected the API credential with HTTP " +
                    $"{(int)response.StatusCode}.",
                    response.StatusCode,
                    retryAfter: ReadRetryAfter(response));
            }

            string unknownMutationWarning =
                UnknownMutationWarning(request);
            byte[] payload = await ReadBoundedResponseAsync(
                    response,
                    unknownMutationWarning,
                    requestDeadline.Token)
                .ConfigureAwait(false);
            ApiEnvelope<T>? envelope = null;
            try
            {
                envelope = JsonSerializer.Deserialize<ApiEnvelope<T>>(payload);
            }
            catch (JsonException)
            {
                // A typed error below avoids echoing an untrusted response body.
            }

            if (!response.IsSuccessStatusCode || envelope is null || envelope.Code != 0)
            {
                string message = RemoteText.Bound(
                    envelope?.Message,
                    512,
                    $"Tripo API returned HTTP {(int)response.StatusCode}.");
                if (request.Method == HttpMethod.Post &&
                    !IsDefinitiveCredentialRejection(response.StatusCode))
                {
                    message += unknownMutationWarning;
                }

                throw new TripoApiException(
                    message,
                    response.StatusCode,
                    envelope?.Code,
                    envelope?.RequestId,
                    ReadRetryAfter(response));
            }

            return envelope;
        }
        catch (HttpRequestException exception)
        {
            throw CreateTransportException(request, exception);
        }
        catch (IOException exception)
        {
            throw CreateTransportException(request, exception);
        }
        catch (OperationCanceledException exception)
            when (!cancellationToken.IsCancellationRequested)
        {
            string message = "The Tripo API request timed out.";
            if (request.Method == HttpMethod.Post)
            {
                message += UnknownMutationWarning(request);
            }

            throw new TripoApiException(message, innerException: exception);
        }
    }

    private static async Task<byte[]> ReadBoundedResponseAsync(
        HttpResponseMessage response,
        string unknownMutationWarning,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > MaximumResponseBytes)
        {
            throw new TripoApiException(
                "The Tripo API response exceeded the local size limit." +
                unknownMutationWarning,
                response.StatusCode);
        }

        await using Stream stream = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using MemoryStream buffer = new();
        byte[] chunk = new byte[16 * 1024];
        int total = 0;
        while (true)
        {
            int read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
            if (total > MaximumResponseBytes)
            {
                throw new TripoApiException(
                    "The Tripo API response exceeded the local size limit." +
                    unknownMutationWarning,
                    response.StatusCode);
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken)
                .ConfigureAwait(false);
        }

        return buffer.ToArray();
    }

    private static TripoApiException CreateTransportException(
        HttpRequestMessage request,
        Exception exception)
    {
        string message = "The Tripo API request could not be completed.";
        if (request.Method == HttpMethod.Post)
        {
            message += UnknownMutationWarning(request);
        }

        return new TripoApiException(message, innerException: exception);
    }

    private static string UnknownMutationWarning(HttpRequestMessage request)
    {
        if (request.Method != HttpMethod.Post)
        {
            return string.Empty;
        }

        Uri? requestUri = request.RequestUri;
        bool isImageUpload =
            requestUri is not null &&
            (requestUri.IsAbsoluteUri
                ? string.Equals(
                    requestUri.AbsolutePath,
                    ImageUploadAbsolutePath,
                    StringComparison.Ordinal)
                : string.Equals(
                    requestUri.OriginalString,
                    ImageUploadPath,
                    StringComparison.Ordinal));
        return isImageUpload
            ? UnknownUploadWarning
            : UnknownCreationWarning;
    }

    private static TimeSpan? ReadRetryAfter(HttpResponseMessage response)
    {
        RetryConditionHeaderValue? retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta)
        {
            return delta;
        }

        if (retryAfter?.Date is { } date)
        {
            TimeSpan remaining = date - DateTimeOffset.UtcNow;
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }

        return null;
    }

    internal static void ValidateTaskId(string taskId)
    {
        if (!IsValidTaskId(taskId))
        {
            throw new ArgumentException(
                "Task IDs must be a canonical lowercase UUID or start with " +
                "task_ and contain only letters, digits, underscores, or hyphens.",
                nameof(taskId));
        }
    }

    internal static bool IsValidTaskId(string? taskId) =>
        Tripo.Bridge.TripoTaskId.IsValid(taskId);

    internal static void ValidateFileToken(string fileToken)
    {
        if (!IsValidFileToken(fileToken))
        {
            throw new ArgumentException(
                "File tokens must start with file_ and contain only letters, " +
                "digits, underscores, or hyphens.",
                nameof(fileToken));
        }
    }

    internal static bool IsValidFileToken(string? fileToken) =>
        !string.IsNullOrWhiteSpace(fileToken) &&
        FileTokenRegex().IsMatch(fileToken);

    internal static void ValidatePrompt(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt) || prompt.Length > 1024)
        {
            throw new ArgumentException(
                "The prompt must contain 1 to 1024 characters.",
                nameof(prompt));
        }
    }

    internal static void ValidateFaceLimit(int faceLimit)
    {
        if (faceLimit is < 500 or > 200_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(faceLimit),
                "The first release limits faceLimit to 500 through 200000.");
        }
    }

    [GeneratedRegex(
        "^file_[A-Za-z0-9_-]{3,240}$",
        RegexOptions.CultureInvariant)]
    private static partial Regex FileTokenRegex();

    [GeneratedRegex(
        "^[A-Za-z0-9._-]{1,64}$",
        RegexOptions.CultureInvariant)]
    private static partial Regex ModelIdRegex();
}
