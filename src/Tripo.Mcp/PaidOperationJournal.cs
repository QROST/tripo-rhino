using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tripo.Mcp;

public interface ITaskCreationCheckpoint
{
    string RequestFingerprint { get; }

    Task BeforeSendAsync(CancellationToken cancellationToken);

    Task TaskIdReceivedAsync(string taskId);

    Task OutcomeUnknownAsync(string code, string message);
}

public interface IImageTaskCreationCheckpoint : ITaskCreationCheckpoint
{
    string? FileToken { get; }

    string? GenerationRequestFingerprint { get; }

    Task BeforeImageUploadAsync(CancellationToken cancellationToken);

    Task ImageFileTokenReceivedAsync(
        string fileToken,
        string generationRequestFingerprint);

    Task BeforeImageGenerationAsync(CancellationToken cancellationToken);

    Task ImageOutcomeUnknownAsync(
        string stage,
        string code,
        string message);
}

public sealed record PaidOperationStatusReceipt(
    string OperationId,
    string Kind,
    string State,
    string? SourceTaskId,
    string? CreatedTaskId,
    string? FailureCode,
    string? FailureMessage,
    bool TaskIdDurable,
    bool MayHaveCreatedRemoteTask,
    bool CanResumeCreation,
    string NextAction,
    DateTimeOffset UpdatedAtUtc,
    string? FailureStage = null);

internal static class PaidOperationKinds
{
    public const string TextTaskCreation = "text_task_creation";
    public const string ImageTaskCreation = "image_task_creation";
    public const string ObjConversionCreation = "obj_conversion_creation";
}

internal static class PaidOperationStates
{
    public const string Prepared = "prepared";
    public const string Dispatching = "dispatching";
    public const string ImageUploadDispatching = "image_upload_dispatching";
    public const string ImageFileTokenPersisted =
        "image_file_token_persisted";
    public const string ImageGenerationDispatching =
        "image_generation_dispatching";
    public const string TaskIdPersisted = "task_id_persisted";
    public const string OutcomeUnknown = "outcome_unknown";
}

internal sealed record PaidOperationDescriptor(
    string OperationId,
    string Kind,
    string RequestFingerprint,
    string DocumentSessionId,
    string? SourceTaskId,
    Tripo.Bridge.StagedImageTransfer? Image)
{
    public static PaidOperationDescriptor ForTextTask(
        string operationId,
        string documentSessionId,
        string requestFingerprint)
    {
        string canonicalOperationId = CanonicalizeOperationId(operationId);
        ValidateDocumentSessionId(documentSessionId);
        ValidateRequestFingerprint(requestFingerprint);
        return new PaidOperationDescriptor(
            canonicalOperationId,
            PaidOperationKinds.TextTaskCreation,
            requestFingerprint,
            documentSessionId,
            SourceTaskId: null,
            Image: null);
    }

    public static PaidOperationDescriptor ForImageTask(
        string operationId,
        string documentSessionId,
        string requestFingerprint,
        Tripo.Bridge.StagedImageTransfer image)
    {
        string canonicalOperationId = CanonicalizeOperationId(operationId);
        ValidateDocumentSessionId(documentSessionId);
        ValidateRequestFingerprint(requestFingerprint);
        Tripo.Bridge.ImageTransferStore.ValidateDescriptor(image);
        return new PaidOperationDescriptor(
            canonicalOperationId,
            PaidOperationKinds.ImageTaskCreation,
            requestFingerprint,
            documentSessionId,
            SourceTaskId: null,
            Image: image);
    }

    public static PaidOperationDescriptor ForObjConversion(
        string operationId,
        string sourceTaskId,
        string documentSessionId,
        string requestFingerprint)
    {
        string canonicalOperationId = CanonicalizeOperationId(operationId);
        TripoV3Client.ValidateTaskId(sourceTaskId);
        ValidateDocumentSessionId(documentSessionId);
        ValidateRequestFingerprint(requestFingerprint);
        return new PaidOperationDescriptor(
            canonicalOperationId,
            PaidOperationKinds.ObjConversionCreation,
            requestFingerprint,
            documentSessionId,
            sourceTaskId,
            Image: null);
    }

    public static string CanonicalizeOperationId(string operationId)
    {
        if (!Guid.TryParseExact(operationId, "D", out Guid parsed))
        {
            throw new ArgumentException(
                "operationId must be a caller-generated UUID reused across retries.",
                nameof(operationId));
        }

        return parsed.ToString("D");
    }

    private static void ValidateRequestFingerprint(string fingerprint)
    {
        if (string.IsNullOrWhiteSpace(fingerprint) ||
            fingerprint.Length != 64 ||
            !fingerprint.All(
                character =>
                    character is >= '0' and <= '9' or
                        >= 'a' and <= 'f'))
        {
            throw new TripoWorkflowException(
                "The Tripo API client returned an invalid lowercase " +
                "paid-operation fingerprint.");
        }
    }

    private static void ValidateDocumentSessionId(string documentSessionId)
    {
        if (!Guid.TryParseExact(documentSessionId, "D", out Guid parsed) ||
            !string.Equals(
                parsed.ToString("D"),
                documentSessionId,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "documentSessionId must be a canonical D-format UUID.",
                nameof(documentSessionId));
        }
    }
}

internal interface IPaidOperationJournal
{
    Task<PaidOperationLease> AcquireAsync(
        PaidOperationDescriptor descriptor,
        CancellationToken cancellationToken,
        bool requireExistingOperation = false);

    Task<PaidOperationStatusReceipt> GetStatusAsync(
        string operationId,
        CancellationToken cancellationToken);
}

internal abstract class PaidOperationLease :
    IImageTaskCreationCheckpoint,
    IAsyncDisposable
{
    public abstract string RequestFingerprint { get; }

    public abstract PaidOperationStatusReceipt Status { get; }

    public abstract string? FileToken { get; }

    public abstract string? GenerationRequestFingerprint { get; }

    public abstract Task BeforeSendAsync(CancellationToken cancellationToken);

    public abstract Task TaskIdReceivedAsync(string taskId);

    public abstract Task OutcomeUnknownAsync(string code, string message);

    public abstract Task BeforeImageUploadAsync(
        CancellationToken cancellationToken);

    public abstract Task ImageFileTokenReceivedAsync(
        string fileToken,
        string generationRequestFingerprint);

    public abstract Task BeforeImageGenerationAsync(
        CancellationToken cancellationToken);

    public abstract Task ImageOutcomeUnknownAsync(
        string stage,
        string code,
        string message);

    public abstract ValueTask DisposeAsync();
}

internal sealed class PaidOperationJournal : IPaidOperationJournal
{
    private const int SchemaVersion = 1;
    private const int MaximumJournalBytes = 1024 * 1024;
    private const int MaximumRecordBytes = 64 * 1024;
    private const string OperationsDirectoryName = "operations";
    private const UnixFileMode PrivateDirectoryMode =
        UnixFileMode.UserRead |
        UnixFileMode.UserWrite |
        UnixFileMode.UserExecute;
    private const UnixFileMode PrivateFileMode =
        UnixFileMode.UserRead |
        UnixFileMode.UserWrite;
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly string _operationsDirectory;
    private readonly TimeProvider _timeProvider;

    public PaidOperationJournal()
        : this(
            Path.Combine(
                Tripo.Bridge.BridgePaths.GetRootDirectory(),
                OperationsDirectoryName),
            TimeProvider.System)
    {
    }

    internal PaidOperationJournal(
        string operationsDirectory,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationsDirectory);
        if (!Path.IsPathFullyQualified(operationsDirectory))
        {
            throw new ArgumentException(
                "The operation journal directory must be absolute.",
                nameof(operationsDirectory));
        }

        _operationsDirectory = Path.GetFullPath(operationsDirectory);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<PaidOperationLease> AcquireAsync(
        PaidOperationDescriptor descriptor,
        CancellationToken cancellationToken,
        bool requireExistingOperation = false)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureOperationsDirectory();

        string journalPath = GetJournalPath(descriptor.OperationId);
        string lockPath = GetLockPath(descriptor.OperationId);
        FileStream lockStream;
        try
        {
            lockStream = OpenPrivateFile(
                lockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.WriteThrough);
            Tripo.Bridge.BridgePaths.SetPrivateFileMode(lockPath);
        }
        catch (IOException exception)
        {
            throw new TripoWorkflowException(
                $"Paid operation {descriptor.OperationId} is already in progress. " +
                "Retry later with the same operationId.",
                exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new TripoWorkflowException(
                $"The local recovery lock for paid operation {descriptor.OperationId} " +
                "could not be opened.",
                exception);
        }

        try
        {
            if (File.Exists(journalPath))
            {
                Tripo.Bridge.BridgePaths.SetPrivateFileMode(journalPath);
            }

            JournalReadResult read = await ReadJournalAsync(
                    journalPath,
                    cancellationToken)
                .ConfigureAwait(false);
            if (read.HasIncompleteTail)
            {
                TruncateIncompleteTail(journalPath, read.ValidLength);
            }

            PaidOperationJournalEntry entry;
            bool needsNewlineBeforeAppend = read.NeedsNewlineBeforeAppend;
            if (read.LastEntry is null)
            {
                if (requireExistingOperation)
                {
                    throw new TripoWorkflowException(
                        $"Paid operation {descriptor.OperationId} has no local " +
                        "journal. A recovered UI retry cannot create a replacement " +
                        "operation; restore the original local data or reconcile it " +
                        "manually.");
                }

                DateTimeOffset now = _timeProvider.GetUtcNow();
                entry = CreateEntry(
                    revision: 1,
                    descriptor,
                    PaidOperationStates.Prepared,
                    createdTaskId: null,
                    now,
                    now,
                    fileToken: null,
                    generationRequestFingerprint: null,
                    failureStage: null,
                    failureCode: null,
                    failureMessage: null);
                await AppendAsync(
                        journalPath,
                        entry,
                        prefixNewline: read.NeedsNewlineBeforeAppend,
                        cancellationToken)
                    .ConfigureAwait(false);
                needsNewlineBeforeAppend = false;
            }
            else
            {
                entry = read.LastEntry;
                ValidateIdentity(entry, descriptor);
                if (entry.State is PaidOperationStates.Dispatching or
                    PaidOperationStates.ImageUploadDispatching or
                    PaidOperationStates.ImageGenerationDispatching)
                {
                    DateTimeOffset now = _timeProvider.GetUtcNow();
                    string? failureStage = entry.State switch
                    {
                        PaidOperationStates.ImageUploadDispatching => "upload",
                        PaidOperationStates.ImageGenerationDispatching =>
                            "generation",
                        _ => null,
                    };
                    entry = CreateEntry(
                        entry.Revision + 1,
                        descriptor,
                        PaidOperationStates.OutcomeUnknown,
                        entry.CreatedTaskId,
                        entry.CreatedAtUtc,
                        now,
                        entry.FileToken,
                        entry.GenerationRequestFingerprint,
                        failureStage,
                        "interrupted_dispatch",
                        failureStage == "upload"
                            ? "The prior process ended while an image upload may " +
                              "have been in flight."
                            : "The prior process ended while a paid request may " +
                              "have been in flight.");
                    await AppendAsync(
                            journalPath,
                            entry,
                            prefixNewline: read.NeedsNewlineBeforeAppend,
                            cancellationToken)
                        .ConfigureAwait(false);
                    needsNewlineBeforeAppend = false;
                }
            }

            return new FilePaidOperationLease(
                lockStream,
                journalPath,
                descriptor,
                entry,
                _timeProvider,
                needsNewlineBeforeAppend);
        }
        catch
        {
            await lockStream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<PaidOperationStatusReceipt> GetStatusAsync(
        string operationId,
        CancellationToken cancellationToken)
    {
        string canonicalOperationId =
            PaidOperationDescriptor.CanonicalizeOperationId(operationId);
        string journalPath = GetJournalPath(canonicalOperationId);
        string lockPath = GetLockPath(canonicalOperationId);
        FileStream? statusLock = null;
        bool operationInProgress = false;
        if (File.Exists(lockPath))
        {
            try
            {
                statusLock = new FileStream(
                    lockPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.SequentialScan);
            }
            catch (IOException)
            {
                operationInProgress = true;
            }
            catch (UnauthorizedAccessException exception)
            {
                throw new TripoWorkflowException(
                    $"The local recovery lock for paid operation " +
                    $"{canonicalOperationId} could not be inspected.",
                    exception);
            }
        }

        try
        {
            JournalReadResult read = await ReadJournalAsync(
                    journalPath,
                    cancellationToken)
                .ConfigureAwait(false);
            PaidOperationJournalEntry entry = read.LastEntry
                ?? throw new TripoWorkflowException(
                    $"No local paid operation was found for {canonicalOperationId}.");
            return ToReceipt(entry, operationInProgress);
        }
        finally
        {
            if (statusLock is not null)
            {
                await statusLock.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private string GetJournalPath(string operationId) =>
        Path.Combine(_operationsDirectory, operationId + ".jsonl");

    private string GetLockPath(string operationId) =>
        Path.Combine(_operationsDirectory, operationId + ".lock");

    private static async Task<JournalReadResult> ReadJournalAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return JournalReadResult.Empty;
        }

        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > MaximumJournalBytes)
        {
            throw new TripoWorkflowException(
                "The local paid-operation journal exceeded its size limit.");
        }

        byte[] bytes = new byte[checked((int)stream.Length)];
        int total = 0;
        while (total < bytes.Length)
        {
            int read = await stream.ReadAsync(
                    bytes.AsMemory(total),
                    cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        if (total != bytes.Length)
        {
            throw new TripoWorkflowException(
                "The local paid-operation journal changed while it was being read.");
        }

        return ParseJournal(bytes);
    }

    private static JournalReadResult ParseJournal(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return JournalReadResult.Empty;
        }

        PaidOperationJournalEntry? lastEntry = null;
        long expectedRevision = 1;
        int recordStart = 0;
        int validLength = 0;
        bool hasIncompleteTail = false;
        bool needsNewlineBeforeAppend = false;
        while (recordStart < bytes.Length)
        {
            int newlineOffset = Array.IndexOf(bytes, (byte)'\n', recordStart);
            bool hasNewline = newlineOffset >= 0;
            int recordEnd = hasNewline ? newlineOffset : bytes.Length;
            int recordLength = recordEnd - recordStart;
            if (recordLength == 0)
            {
                throw new TripoWorkflowException(
                    "The local paid-operation journal contains an empty record.");
            }

            if (recordLength > MaximumRecordBytes)
            {
                if (!hasNewline)
                {
                    hasIncompleteTail = true;
                    break;
                }

                throw new TripoWorkflowException(
                    "The local paid-operation journal contains an oversized record.");
            }

            PaidOperationJournalEntry? entry = TryParseEntry(
                bytes.AsSpan(recordStart, recordLength));
            if (entry is null)
            {
                if (!hasNewline)
                {
                    hasIncompleteTail = true;
                    break;
                }

                throw new TripoWorkflowException(
                    "The local paid-operation journal is corrupt.");
            }

            ValidateEntry(entry, expectedRevision, lastEntry);
            lastEntry = entry;
            expectedRevision++;
            validLength = hasNewline ? recordEnd + 1 : recordEnd;
            needsNewlineBeforeAppend = !hasNewline;
            recordStart = hasNewline ? recordEnd + 1 : bytes.Length;
        }

        return new JournalReadResult(
            lastEntry,
            validLength,
            hasIncompleteTail,
            needsNewlineBeforeAppend);
    }

    private static PaidOperationJournalEntry? TryParseEntry(
        ReadOnlySpan<byte> bytes)
    {
        try
        {
            PaidOperationJournalEntry? entry =
                JsonSerializer.Deserialize<PaidOperationJournalEntry>(
                    bytes,
                    JsonOptions);
            if (entry is null)
            {
                return null;
            }

            string expectedChecksum = ComputeRecordChecksum(
                entry with { RecordChecksum = string.Empty });
            return string.Equals(
                    entry.RecordChecksum,
                    expectedChecksum,
                    StringComparison.Ordinal)
                ? entry
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void ValidateEntry(
        PaidOperationJournalEntry entry,
        long expectedRevision,
        PaidOperationJournalEntry? prior)
    {
        if (entry.SchemaVersion != SchemaVersion)
        {
            throw new TripoWorkflowException(
                "The local paid-operation journal uses an unsupported schema.");
        }

        bool documentSessionValid =
            Guid.TryParseExact(
                entry.DocumentSessionId,
                "D",
                out Guid documentSessionId) &&
            string.Equals(
                documentSessionId.ToString("D"),
                entry.DocumentSessionId,
                StringComparison.Ordinal);
        bool fingerprintValid = IsValidFingerprint(entry.RequestFingerprint);
        bool imageIdentityValid =
            entry.ImageSha256 is not null &&
            IsValidFingerprint(entry.ImageSha256) &&
            entry.ImageByteLength is > 0 and
                <= Tripo.Bridge.BridgeConstants.MaximumImageTransferBytes &&
            entry.ImageMediaType is "image/png" or "image/jpeg";
        bool noImageIdentity =
            entry.ImageSha256 is null &&
            entry.ImageByteLength is null &&
            entry.ImageMediaType is null;
        bool kindAndTaskIdsValid =
            entry.Kind switch
            {
                PaidOperationKinds.TextTaskCreation =>
                    entry.SourceTaskId is null && noImageIdentity,
                PaidOperationKinds.ImageTaskCreation =>
                    entry.SourceTaskId is null && imageIdentityValid,
                PaidOperationKinds.ObjConversionCreation =>
                    TripoV3Client.IsValidTaskId(entry.SourceTaskId) &&
                    noImageIdentity,
                _ => false,
            };
        bool statePayloadValid = IsValidStatePayload(entry);
        if (entry.Revision != expectedRevision ||
            !Guid.TryParseExact(entry.OperationId, "D", out Guid operationId) ||
            !string.Equals(
                operationId.ToString("D"),
                entry.OperationId,
                StringComparison.Ordinal) ||
            !fingerprintValid ||
            !documentSessionValid ||
            entry.CreatedTaskId?.Length > 256 ||
            entry.SourceTaskId?.Length > 256 ||
            entry.FileToken?.Length > 256 ||
            entry.FailureCode?.Length > 64 ||
            entry.FailureMessage?.Length > 512 ||
            entry.FailureStage?.Length > 32 ||
            !kindAndTaskIdsValid ||
            !statePayloadValid ||
            entry.CreatedAtUtc == default ||
            entry.UpdatedAtUtc == default)
        {
            throw new TripoWorkflowException(
                "The local paid-operation journal contains an invalid record.");
        }

        if (prior is null)
        {
            if (entry.Revision != 1 ||
                entry.State != PaidOperationStates.Prepared ||
                entry.CreatedTaskId is not null)
            {
                throw new TripoWorkflowException(
                    "The local paid-operation journal has an invalid initial state.");
            }

            return;
        }

        if (!string.Equals(prior.OperationId, entry.OperationId, StringComparison.Ordinal) ||
            !string.Equals(prior.Kind, entry.Kind, StringComparison.Ordinal) ||
            !string.Equals(
                prior.RequestFingerprint,
                entry.RequestFingerprint,
                StringComparison.Ordinal) ||
            !string.Equals(
                prior.DocumentSessionId,
                entry.DocumentSessionId,
                StringComparison.Ordinal) ||
            !string.Equals(prior.SourceTaskId, entry.SourceTaskId, StringComparison.Ordinal) ||
            !string.Equals(
                prior.ImageSha256,
                entry.ImageSha256,
                StringComparison.Ordinal) ||
            prior.ImageByteLength != entry.ImageByteLength ||
            !string.Equals(
                prior.ImageMediaType,
                entry.ImageMediaType,
                StringComparison.Ordinal) ||
            prior.CreatedAtUtc != entry.CreatedAtUtc ||
            (prior.CreatedTaskId is not null &&
             !string.Equals(
                 prior.CreatedTaskId,
                 entry.CreatedTaskId,
                 StringComparison.Ordinal)) ||
            (prior.FileToken is not null &&
             !string.Equals(
                 prior.FileToken,
                 entry.FileToken,
                 StringComparison.Ordinal)) ||
            (prior.GenerationRequestFingerprint is not null &&
             !string.Equals(
                 prior.GenerationRequestFingerprint,
                 entry.GenerationRequestFingerprint,
                 StringComparison.Ordinal)) ||
            !IsAllowedTransition(prior.State, entry.State))
        {
            throw new TripoWorkflowException(
                "The local paid-operation journal contains an invalid state transition.");
        }
    }

    private static bool IsAllowedTransition(string from, string to) =>
        (from, to) switch
        {
            (PaidOperationStates.Prepared, PaidOperationStates.Dispatching) => true,
            (PaidOperationStates.Dispatching, PaidOperationStates.TaskIdPersisted) => true,
            (PaidOperationStates.Dispatching, PaidOperationStates.OutcomeUnknown) => true,
            (
                PaidOperationStates.Prepared,
                PaidOperationStates.ImageUploadDispatching) => true,
            (
                PaidOperationStates.ImageUploadDispatching,
                PaidOperationStates.ImageFileTokenPersisted) => true,
            (
                PaidOperationStates.ImageUploadDispatching,
                PaidOperationStates.OutcomeUnknown) => true,
            (
                PaidOperationStates.ImageFileTokenPersisted,
                PaidOperationStates.ImageGenerationDispatching) => true,
            (
                PaidOperationStates.ImageGenerationDispatching,
                PaidOperationStates.TaskIdPersisted) => true,
            (
                PaidOperationStates.ImageGenerationDispatching,
                PaidOperationStates.OutcomeUnknown) => true,
            _ => false,
        };

    private static bool IsValidStatePayload(
        PaidOperationJournalEntry entry)
    {
        bool noFailure =
            entry.FailureStage is null &&
            entry.FailureCode is null &&
            entry.FailureMessage is null;
        bool noImageProgress =
            entry.FileToken is null &&
            entry.GenerationRequestFingerprint is null;
        bool imageProgress =
            TripoV3Client.IsValidFileToken(entry.FileToken) &&
            IsValidFingerprint(entry.GenerationRequestFingerprint);
        bool isImage =
            entry.Kind == PaidOperationKinds.ImageTaskCreation;
        return entry.State switch
        {
            PaidOperationStates.Prepared =>
                entry.CreatedTaskId is null &&
                noFailure &&
                noImageProgress,
            PaidOperationStates.Dispatching =>
                !isImage &&
                entry.CreatedTaskId is null &&
                noFailure &&
                noImageProgress,
            PaidOperationStates.ImageUploadDispatching =>
                isImage &&
                entry.CreatedTaskId is null &&
                noFailure &&
                noImageProgress,
            PaidOperationStates.ImageFileTokenPersisted or
                PaidOperationStates.ImageGenerationDispatching =>
                isImage &&
                entry.CreatedTaskId is null &&
                noFailure &&
                imageProgress,
            PaidOperationStates.TaskIdPersisted =>
                TripoV3Client.IsValidTaskId(entry.CreatedTaskId) &&
                noFailure &&
                (isImage ? imageProgress : noImageProgress),
            PaidOperationStates.OutcomeUnknown =>
                entry.CreatedTaskId is null &&
                !string.IsNullOrWhiteSpace(entry.FailureCode) &&
                !string.IsNullOrWhiteSpace(entry.FailureMessage) &&
                (isImage
                    ? entry.FailureStage switch
                    {
                        "upload" => noImageProgress,
                        "generation" => imageProgress,
                        _ => false,
                    }
                    : entry.FailureStage is null && noImageProgress),
            _ => false,
        };
    }

    private static bool IsValidFingerprint(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length == 64 &&
        value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void ValidateIdentity(
        PaidOperationJournalEntry entry,
        PaidOperationDescriptor descriptor)
    {
        if (!string.Equals(entry.OperationId, descriptor.OperationId, StringComparison.Ordinal) ||
            !string.Equals(entry.Kind, descriptor.Kind, StringComparison.Ordinal) ||
            !string.Equals(
                entry.RequestFingerprint,
                descriptor.RequestFingerprint,
                StringComparison.Ordinal) ||
            !string.Equals(
                entry.DocumentSessionId,
                descriptor.DocumentSessionId,
                StringComparison.Ordinal) ||
            !string.Equals(
                entry.SourceTaskId,
                descriptor.SourceTaskId,
                StringComparison.Ordinal) ||
            !ImageIdentityMatches(entry, descriptor.Image))
        {
            throw new TripoWorkflowException(
                $"Paid operation {descriptor.OperationId} was already used with " +
                "different parameters or a different operation kind.");
        }
    }

    private static PaidOperationJournalEntry CreateEntry(
        long revision,
        PaidOperationDescriptor descriptor,
        string state,
        string? createdTaskId,
        DateTimeOffset createdAtUtc,
        DateTimeOffset updatedAtUtc,
        string? fileToken,
        string? generationRequestFingerprint,
        string? failureStage,
        string? failureCode,
        string? failureMessage)
    {
        PaidOperationJournalEntry entry = new(
            SchemaVersion,
            revision,
            descriptor.OperationId,
            descriptor.Kind,
            descriptor.RequestFingerprint,
            descriptor.DocumentSessionId,
            state,
            descriptor.SourceTaskId,
            createdTaskId,
            descriptor.Image?.Sha256,
            descriptor.Image?.ByteLength,
            descriptor.Image?.MediaType,
            fileToken,
            generationRequestFingerprint,
            createdAtUtc,
            updatedAtUtc,
            failureStage,
            failureCode,
            failureMessage,
            RecordChecksum: string.Empty);
        return entry with
        {
            RecordChecksum = ComputeRecordChecksum(entry),
        };
    }

    private static bool ImageIdentityMatches(
        PaidOperationJournalEntry entry,
        Tripo.Bridge.StagedImageTransfer? image) =>
        image is null
            ? entry.ImageSha256 is null &&
              entry.ImageByteLength is null &&
              entry.ImageMediaType is null
            : string.Equals(
                  entry.ImageSha256,
                  image.Sha256,
                  StringComparison.Ordinal) &&
              entry.ImageByteLength == image.ByteLength &&
              string.Equals(
                  entry.ImageMediaType,
                  image.MediaType,
                  StringComparison.Ordinal);

    private static string ComputeRecordChecksum(PaidOperationJournalEntry entry) =>
        Convert.ToHexString(
                SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(entry, JsonOptions)))
            .ToLowerInvariant();

    private static async Task AppendAsync(
        string path,
        PaidOperationJournalEntry entry,
        bool prefixNewline,
        CancellationToken cancellationToken)
    {
        byte[] record = JsonSerializer.SerializeToUtf8Bytes(entry, JsonOptions);
        if (record.Length > MaximumRecordBytes)
        {
            throw new TripoWorkflowException(
                "The local paid-operation journal record exceeded its size limit.");
        }

        int prefixLength = prefixNewline ? 1 : 0;
        byte[] framed = new byte[prefixLength + record.Length + 1];
        if (prefixNewline)
        {
            framed[0] = (byte)'\n';
        }

        record.CopyTo(framed.AsSpan(prefixLength));
        framed[^1] = (byte)'\n';
        await using FileStream stream = OpenPrivateFile(
            path,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read,
            16 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        Tripo.Bridge.BridgePaths.SetPrivateFileMode(path);
        await stream.WriteAsync(framed, cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private static void TruncateIncompleteTail(string path, int validLength)
    {
        using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Write,
            FileShare.Read);
        stream.SetLength(validLength);
        stream.Flush(flushToDisk: true);
    }

    private void EnsureOperationsDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            Tripo.Bridge.BridgePaths.EnsurePrivateDirectory(_operationsDirectory);
            return;
        }

        Directory.CreateDirectory(_operationsDirectory, PrivateDirectoryMode);
        File.SetUnixFileMode(_operationsDirectory, PrivateDirectoryMode);
    }

    private static FileStream OpenPrivateFile(
        string path,
        FileMode mode,
        FileAccess access,
        FileShare share,
        int bufferSize,
        FileOptions options)
    {
        FileStreamOptions streamOptions = new()
        {
            Mode = mode,
            Access = access,
            Share = share,
            BufferSize = bufferSize,
            Options = options,
        };
        if (!OperatingSystem.IsWindows())
        {
            streamOptions.UnixCreateMode = PrivateFileMode;
        }

        return new FileStream(path, streamOptions);
    }

    private static PaidOperationStatusReceipt ToReceipt(
        PaidOperationJournalEntry entry,
        bool operationInProgress = false)
    {
        bool taskIdDurable =
            entry.State == PaidOperationStates.TaskIdPersisted &&
            entry.CreatedTaskId is not null;
        bool mayHaveCreatedRemoteTask =
            entry.State is PaidOperationStates.Dispatching or
                PaidOperationStates.ImageGenerationDispatching ||
            entry.State == PaidOperationStates.OutcomeUnknown &&
            entry.FailureStage != "upload" ||
            taskIdDurable;
        bool canResumeCreation =
            !operationInProgress &&
            entry.State is PaidOperationStates.Prepared or
                PaidOperationStates.ImageFileTokenPersisted;
        string nextAction = operationInProgress
            ? "The original process still owns this operation. Wait and query " +
              "tripo_operation_status again; do not start a replacement operation."
            : entry.State switch
        {
            PaidOperationStates.Prepared =>
                "Retry the same creation tool with the same operationId and identical parameters.",
            PaidOperationStates.ImageFileTokenPersisted =>
                "Retry the same image creation action with the same operationId " +
                "and identical parameters. The durable file token will be reused.",
            PaidOperationStates.TaskIdPersisted =>
                $"Query {entry.CreatedTaskId} with tripo_task_status.",
            _ =>
                "Do not resend the paid request. Preserve this journal and inspect " +
                "the provider task or billing history manually.",
        };
        return new PaidOperationStatusReceipt(
            entry.OperationId,
            entry.Kind,
            operationInProgress &&
            entry.State == PaidOperationStates.Prepared
                ? "operation_in_progress"
                : entry.State,
            entry.SourceTaskId,
            entry.CreatedTaskId,
            entry.FailureCode,
            entry.FailureMessage,
            taskIdDurable,
            mayHaveCreatedRemoteTask || operationInProgress,
            canResumeCreation,
            nextAction,
            entry.UpdatedAtUtc,
            entry.FailureStage);
    }

    private sealed class FilePaidOperationLease : PaidOperationLease
    {
        private readonly FileStream _lockStream;
        private readonly string _journalPath;
        private readonly PaidOperationDescriptor _descriptor;
        private readonly TimeProvider _timeProvider;
        private PaidOperationJournalEntry _entry;
        private bool _needsNewlineBeforeAppend;
        private bool _disposed;

        public FilePaidOperationLease(
            FileStream lockStream,
            string journalPath,
            PaidOperationDescriptor descriptor,
            PaidOperationJournalEntry entry,
            TimeProvider timeProvider,
            bool needsNewlineBeforeAppend)
        {
            _lockStream = lockStream;
            _journalPath = journalPath;
            _descriptor = descriptor;
            _entry = entry;
            _timeProvider = timeProvider;
            _needsNewlineBeforeAppend = needsNewlineBeforeAppend;
        }

        public override PaidOperationStatusReceipt Status => ToReceipt(_entry);

        public override string RequestFingerprint =>
            _descriptor.RequestFingerprint;

        public override string? FileToken => _entry.FileToken;

        public override string? GenerationRequestFingerprint =>
            _entry.GenerationRequestFingerprint;

        public override async Task BeforeSendAsync(
            CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            if (_descriptor.Kind == PaidOperationKinds.ImageTaskCreation)
            {
                throw new TripoWorkflowException(
                    "Image operations must checkpoint upload and generation " +
                    "dispatches separately.");
            }

            if (_entry.State != PaidOperationStates.Prepared)
            {
                throw new TripoWorkflowException(
                    $"Paid operation {_entry.OperationId} cannot dispatch from " +
                    $"state {_entry.State}.");
            }

            PaidOperationJournalEntry next = CreateEntry(
                _entry.Revision + 1,
                _descriptor,
                PaidOperationStates.Dispatching,
                createdTaskId: null,
                _entry.CreatedAtUtc,
                _timeProvider.GetUtcNow(),
                fileToken: null,
                generationRequestFingerprint: null,
                failureStage: null,
                failureCode: null,
                failureMessage: null);
            await AppendAsync(
                    _journalPath,
                    next,
                    prefixNewline: _needsNewlineBeforeAppend,
                    cancellationToken)
                .ConfigureAwait(false);
            _entry = next;
            _needsNewlineBeforeAppend = false;
        }

        public override async Task TaskIdReceivedAsync(string taskId)
        {
            ThrowIfDisposed();
            TripoV3Client.ValidateTaskId(taskId);
            if (_entry.State is not PaidOperationStates.Dispatching and
                not PaidOperationStates.ImageGenerationDispatching)
            {
                throw new TripoWorkflowException(
                    $"Paid operation {_entry.OperationId} cannot persist a task ID " +
                    $"from state {_entry.State}.");
            }

            PaidOperationJournalEntry next = CreateEntry(
                _entry.Revision + 1,
                _descriptor,
                PaidOperationStates.TaskIdPersisted,
                taskId,
                _entry.CreatedAtUtc,
                _timeProvider.GetUtcNow(),
                _entry.FileToken,
                _entry.GenerationRequestFingerprint,
                failureStage: null,
                failureCode: null,
                failureMessage: null);
            await AppendAsync(
                    _journalPath,
                    next,
                    prefixNewline: _needsNewlineBeforeAppend,
                    CancellationToken.None)
                .ConfigureAwait(false);
            _entry = next;
            _needsNewlineBeforeAppend = false;
        }

        public override async Task OutcomeUnknownAsync(
            string code,
            string message)
        {
            ThrowIfDisposed();
            if (_descriptor.Kind == PaidOperationKinds.ImageTaskCreation)
            {
                throw new TripoWorkflowException(
                    "Image operations must record the ambiguous upload or " +
                    "generation stage explicitly.");
            }

            if (_entry.State != PaidOperationStates.Dispatching)
            {
                return;
            }

            PaidOperationJournalEntry next = CreateEntry(
                _entry.Revision + 1,
                _descriptor,
                PaidOperationStates.OutcomeUnknown,
                createdTaskId: null,
                _entry.CreatedAtUtc,
                _timeProvider.GetUtcNow(),
                fileToken: null,
                generationRequestFingerprint: null,
                failureStage: null,
                RemoteText.Bound(code, 64, "post_failed"),
                RemoteText.Bound(message, 512, "The paid request outcome is unknown."));
            await AppendAsync(
                    _journalPath,
                    next,
                    prefixNewline: _needsNewlineBeforeAppend,
                    CancellationToken.None)
                .ConfigureAwait(false);
            _entry = next;
            _needsNewlineBeforeAppend = false;
        }

        public override async Task BeforeImageUploadAsync(
            CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            EnsureImageKind();
            if (_entry.State != PaidOperationStates.Prepared)
            {
                throw new TripoWorkflowException(
                    $"Paid image operation {_entry.OperationId} cannot upload " +
                    $"from state {_entry.State}.");
            }

            PaidOperationJournalEntry next = CreateEntry(
                _entry.Revision + 1,
                _descriptor,
                PaidOperationStates.ImageUploadDispatching,
                createdTaskId: null,
                _entry.CreatedAtUtc,
                _timeProvider.GetUtcNow(),
                fileToken: null,
                generationRequestFingerprint: null,
                failureStage: null,
                failureCode: null,
                failureMessage: null);
            await AppendTransitionAsync(next, cancellationToken)
                .ConfigureAwait(false);
        }

        public override async Task ImageFileTokenReceivedAsync(
            string fileToken,
            string generationRequestFingerprint)
        {
            ThrowIfDisposed();
            EnsureImageKind();
            TripoV3Client.ValidateFileToken(fileToken);
            if (!IsValidFingerprint(generationRequestFingerprint))
            {
                throw new TripoWorkflowException(
                    "The image generation request fingerprint was invalid.");
            }

            if (_entry.State != PaidOperationStates.ImageUploadDispatching)
            {
                throw new TripoWorkflowException(
                    $"Paid image operation {_entry.OperationId} cannot persist a " +
                    $"file token from state {_entry.State}.");
            }

            PaidOperationJournalEntry next = CreateEntry(
                _entry.Revision + 1,
                _descriptor,
                PaidOperationStates.ImageFileTokenPersisted,
                createdTaskId: null,
                _entry.CreatedAtUtc,
                _timeProvider.GetUtcNow(),
                fileToken,
                generationRequestFingerprint,
                failureStage: null,
                failureCode: null,
                failureMessage: null);
            await AppendTransitionAsync(next, CancellationToken.None)
                .ConfigureAwait(false);
        }

        public override async Task BeforeImageGenerationAsync(
            CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            EnsureImageKind();
            if (_entry.State != PaidOperationStates.ImageFileTokenPersisted)
            {
                throw new TripoWorkflowException(
                    $"Paid image operation {_entry.OperationId} cannot create a " +
                    $"generation task from state {_entry.State}.");
            }

            PaidOperationJournalEntry next = CreateEntry(
                _entry.Revision + 1,
                _descriptor,
                PaidOperationStates.ImageGenerationDispatching,
                createdTaskId: null,
                _entry.CreatedAtUtc,
                _timeProvider.GetUtcNow(),
                _entry.FileToken,
                _entry.GenerationRequestFingerprint,
                failureStage: null,
                failureCode: null,
                failureMessage: null);
            await AppendTransitionAsync(next, cancellationToken)
                .ConfigureAwait(false);
        }

        public override async Task ImageOutcomeUnknownAsync(
            string stage,
            string code,
            string message)
        {
            ThrowIfDisposed();
            EnsureImageKind();
            string expectedState = stage switch
            {
                "upload" => PaidOperationStates.ImageUploadDispatching,
                "generation" => PaidOperationStates.ImageGenerationDispatching,
                _ => throw new ArgumentException(
                    "stage must be upload or generation.",
                    nameof(stage)),
            };
            if (_entry.State != expectedState)
            {
                return;
            }

            PaidOperationJournalEntry next = CreateEntry(
                _entry.Revision + 1,
                _descriptor,
                PaidOperationStates.OutcomeUnknown,
                createdTaskId: null,
                _entry.CreatedAtUtc,
                _timeProvider.GetUtcNow(),
                _entry.FileToken,
                _entry.GenerationRequestFingerprint,
                stage,
                RemoteText.Bound(code, 64, "post_failed"),
                RemoteText.Bound(
                    message,
                    512,
                    "The image operation outcome is unknown."));
            await AppendTransitionAsync(next, CancellationToken.None)
                .ConfigureAwait(false);
        }

        private async Task AppendTransitionAsync(
            PaidOperationJournalEntry next,
            CancellationToken cancellationToken)
        {
            await AppendAsync(
                    _journalPath,
                    next,
                    prefixNewline: _needsNewlineBeforeAppend,
                    cancellationToken)
                .ConfigureAwait(false);
            _entry = next;
            _needsNewlineBeforeAppend = false;
        }

        private void EnsureImageKind()
        {
            if (_descriptor.Kind != PaidOperationKinds.ImageTaskCreation)
            {
                throw new TripoWorkflowException(
                    $"Paid operation {_entry.OperationId} is not an image task.");
            }
        }

        public override async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            await _lockStream.DisposeAsync().ConfigureAwait(false);
        }

        private void ThrowIfDisposed()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }
    }

    private sealed record PaidOperationJournalEntry(
        int SchemaVersion,
        long Revision,
        string OperationId,
        string Kind,
        string RequestFingerprint,
        string DocumentSessionId,
        string State,
        string? SourceTaskId,
        string? CreatedTaskId,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? ImageSha256,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        long? ImageByteLength,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? ImageMediaType,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? FileToken,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? GenerationRequestFingerprint,
        DateTimeOffset CreatedAtUtc,
        DateTimeOffset UpdatedAtUtc,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? FailureStage,
        string? FailureCode,
        string? FailureMessage,
        string RecordChecksum);

    private sealed record JournalReadResult(
        PaidOperationJournalEntry? LastEntry,
        int ValidLength,
        bool HasIncompleteTail,
        bool NeedsNewlineBeforeAppend)
    {
        public static JournalReadResult Empty { get; } =
            new(null, 0, false, false);
    }
}
