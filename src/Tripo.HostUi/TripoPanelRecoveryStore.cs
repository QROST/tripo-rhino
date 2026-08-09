using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tripo.HostUi;

public sealed record TripoPanelPaidRecoveryHint(
    string OperationId,
    bool DispatchAttempted,
    string? TaskId,
    string? JournalState,
    bool TaskIdDurable,
    bool CanResumeCreation);

public sealed record TripoPanelImportRecoveryHint(
    string OperationId,
    string ConversionTaskId,
    string Name,
    string ImportMode,
    bool ApplyMaterials,
    bool DispatchAttempted,
    bool ReceiptKnown);

public sealed record TripoPanelRecoveryHint(
    int SchemaVersion,
    string RecoveryId,
    string Host,
    int OwnerProcessId,
    DateTimeOffset OwnerProcessStartedAtUtc,
    string DocumentSessionId,
    DateTimeOffset UpdatedAtUtc,
    TripoPanelPaidRecoveryHint? Generation,
    TripoPanelPaidRecoveryHint? Conversion,
    TripoPanelImportRecoveryHint? Import);

public sealed record LoadedTripoPanelRecoveryHint(
    string FileName,
    TripoPanelRecoveryHint Hint);

public sealed record TripoPanelRecoveryIssue(
    string FileName,
    string Code,
    string Message);

public sealed record TripoPanelRecoveryLoadResult(
    IReadOnlyList<LoadedTripoPanelRecoveryHint> Hints,
    IReadOnlyList<TripoPanelRecoveryIssue> Issues)
{
    public static TripoPanelRecoveryLoadResult Empty { get; } =
        new([], []);

    public bool HasBlock => Hints.Count > 0 || Issues.Count > 0;

    public string PresentationToken
    {
        get
        {
            StringBuilder material = new();
            foreach (LoadedTripoPanelRecoveryHint loaded in Hints)
            {
                AppendTokenPart(material, "hint");
                AppendTokenPart(material, loaded.FileName);
                AppendTokenPart(
                    material,
                    JsonSerializer.Serialize(
                        loaded.Hint,
                        Tripo.Bridge.BridgeJson.Options));
            }

            foreach (TripoPanelRecoveryIssue issue in Issues)
            {
                AppendTokenPart(material, "issue");
                AppendTokenPart(material, issue.FileName);
                AppendTokenPart(material, issue.Code);
                AppendTokenPart(material, issue.Message);
            }

            return Convert.ToHexString(
                SHA256.HashData(
                    Encoding.UTF8.GetBytes(material.ToString())));
        }
    }

    private static void AppendTokenPart(
        StringBuilder material,
        string value)
    {
        material
            .Append(value.Length.ToString(CultureInfo.InvariantCulture))
            .Append(':')
            .Append(value);
    }
}

public sealed class TripoPanelRecoveryStore : IDisposable
{
    public const int CurrentSchemaVersion = 2;
    public const int MaximumRecoveryFileBytes = 16 * 1024;
    public const int MaximumRecoveryFiles = 128;

    private static readonly UTF8Encoding StrictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly JsonSerializerOptions RecoveryJsonOptions =
        new(Tripo.Bridge.BridgeJson.Options)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        };
    private static readonly ConcurrentDictionary<
        string,
        WeakReference<TripoPanelRecoveryStore>> ActiveOwners =
        new(StringComparer.Ordinal);
    private static readonly string[] CredentialRecoveryHosts =
    [
        "rhino",
        "revit",
    ];

    private static readonly HashSet<string> RootProperties =
    [
        "schemaVersion",
        "recoveryId",
        "host",
        "ownerProcessId",
        "ownerProcessStartedAtUtc",
        "documentSessionId",
        "updatedAtUtc",
        "generation",
        "conversion",
        "import",
    ];

    private static readonly HashSet<string> PaidProperties =
    [
        "operationId",
        "dispatchAttempted",
        "taskId",
        "journalState",
        "taskIdDurable",
        "canResumeCreation",
    ];

    private static readonly HashSet<string> ImportProperties =
    [
        "operationId",
        "conversionTaskId",
        "name",
        "importMode",
        "applyMaterials",
        "dispatchAttempted",
        "receiptKnown",
    ];

    private readonly string _host;
    private readonly string _rootDirectory;
    private readonly string _recoveryRoot;
    private readonly string _directory;
    private readonly string _recoveryId;
    private readonly int _ownerProcessId;
    private readonly DateTimeOffset _ownerProcessStartedAtUtc;
    private readonly object _ownedPathsGate = new();
    private readonly HashSet<string> _ownedPaths =
        new(StringComparer.Ordinal);
    private bool _disposed;

    internal string RootDirectory => _rootDirectory;

    public TripoPanelRecoveryStore(
        string host,
        string? rootDirectory = null)
    {
        _host = Tripo.Bridge.BridgePaths.NormalizeHost(host);
        string root = Path.GetFullPath(
            rootDirectory ??
            Tripo.Bridge.BridgePaths.GetRootDirectory());
        _rootDirectory = root;
        string recoveryRoot = Path.Combine(root, "ui-recovery");
        EnsurePrivateNonReparseDirectory(recoveryRoot);
        _recoveryRoot = recoveryRoot;
        _directory = Path.Combine(recoveryRoot, _host);
        EnsurePrivateNonReparseDirectory(_directory);
        _recoveryId = Guid.NewGuid().ToString("D");
        _ownerProcessId = Environment.ProcessId;
        using Process process = Process.GetCurrentProcess();
        _ownerProcessStartedAtUtc =
            new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
    }

    public void Save(TripoPanelState state)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(state);
        if (state.Context is null)
        {
            return;
        }

        string contextHost =
            Tripo.Bridge.BridgePaths.NormalizeHost(state.Context.Host);
        if (!string.Equals(contextHost, _host, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The panel recovery store does not match the active host.");
        }

        string documentSessionId =
            CanonicalizeUuid(
                state.Context.DocumentSessionId,
                nameof(state.Context.DocumentSessionId));
        string path = GetHintPath(_recoveryId);
        TripoPanelRecoveryHint? hint = BuildHint(state, documentSessionId);
        if (hint is null)
        {
            DeleteOwnedRegularFile(path);
            return;
        }

        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(
            hint,
            RecoveryJsonOptions);
        if (payload.Length > MaximumRecoveryFileBytes)
        {
            throw new InvalidOperationException(
                "The panel recovery hint exceeded its size limit.");
        }

        EnsurePrivateNonReparseDirectory(_directory);
        using FileStream recoveryLock = AcquireRecoveryLock();
        if (IsFileLinkOrReparsePoint(path))
        {
            throw new InvalidOperationException(
                "The panel recovery path is a symbolic link or reparse point.");
        }

        if (File.Exists(path) && !OwnsPath(path))
        {
            throw new InvalidOperationException(
                "The panel recovery path is already owned by another panel session.");
        }

        string temporaryPath =
            path + "." +
            Convert.ToHexString(RandomNumberGenerator.GetBytes(8)) +
            ".tmp";
        try
        {
            File.WriteAllBytes(temporaryPath, payload);
            Tripo.Bridge.BridgePaths.SetPrivateFileMode(temporaryPath);
            File.Move(temporaryPath, path, overwrite: true);
            RegisterOwnedPath(path);
        }
        finally
        {
            Tripo.Bridge.BridgePaths.TryDelete(temporaryPath);
        }
    }

    public TripoPanelRecoveryLoadResult LoadStale() =>
        Load(includeActiveOwners: false);

    public TripoPanelRecoveryLoadResult LoadCredentialMutationBlocks(
        bool excludeCurrentStoreHint = false)
    {
        List<LoadedTripoPanelRecoveryHint> hints = [];
        List<TripoPanelRecoveryIssue> issues = [];
        try
        {
            ValidateCredentialWorkflowLeaseFile();
        }
        catch (Exception exception)
            when (exception is IOException or
                  UnauthorizedAccessException or
                  InvalidDataException)
        {
            issues.Add(
                new TripoPanelRecoveryIssue(
                    "(credential-workflow-lock)",
                    "credential_workflow_lock_invalid",
                    BoundMessage(exception.Message)));
        }

        foreach (string host in CredentialRecoveryHosts)
        {
            TripoPanelRecoveryLoadResult result;
            if (string.Equals(host, _host, StringComparison.Ordinal))
            {
                result = Load(includeActiveOwners: true);
            }
            else
            {
                using TripoPanelRecoveryStore sibling =
                    new(host, _rootDirectory);
                result = sibling.Load(includeActiveOwners: true);
            }

            hints.AddRange(
                result.Hints
                    .Where(HasCredentialSensitiveOperation)
                    .Where(loaded =>
                        !excludeCurrentStoreHint ||
                        !IsCurrentOwnedHint(loaded))
                    .Select(loaded => loaded with
                    {
                        FileName = host + "/" + loaded.FileName,
                    }));
            issues.AddRange(
                result.Issues.Select(issue => issue with
                {
                    FileName = host + "/" + issue.FileName,
                }));
        }

        return new TripoPanelRecoveryLoadResult(hints, issues);
    }

    public IDisposable AcquireCredentialWorkflowLease()
    {
        ThrowIfDisposed();
        EnsurePrivateNonReparseDirectory(_recoveryRoot);
        string lockPath = GetCredentialWorkflowLeasePath();
        ValidatePrivateRegularLockFile(
            lockPath,
            "The credential/workflow lock");

        FileStream stream = new(
            lockPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 1,
            FileOptions.WriteThrough);
        try
        {
            Tripo.Bridge.BridgePaths.SetPrivateFileMode(lockPath);
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private TripoPanelRecoveryLoadResult Load(bool includeActiveOwners)
    {
        string[] entries;
        string[] files;
        try
        {
            EnsurePrivateNonReparseDirectory(_directory);
            ValidateRecoveryLockFile();
            entries = Directory
                .EnumerateFileSystemEntries(_directory)
                .Take(MaximumRecoveryFiles + 3)
                .ToArray();
            files = entries
                .Where(path => string.Equals(
                    Path.GetExtension(path),
                    ".json",
                    StringComparison.Ordinal))
                .ToArray();
        }
        catch (IOException exception)
        {
            return DirectoryIssue("recovery_directory_unreadable", exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            return DirectoryIssue("recovery_directory_unreadable", exception);
        }
        catch (InvalidDataException exception)
        {
            return DirectoryIssue("recovery_directory_invalid", exception);
        }

        Array.Sort(files, StringComparer.Ordinal);
        List<LoadedTripoPanelRecoveryHint> hints = [];
        List<TripoPanelRecoveryIssue> issues = [];
        if (entries.Length > MaximumRecoveryFiles + 2 ||
            files.Length > MaximumRecoveryFiles)
        {
            issues.Add(
                new TripoPanelRecoveryIssue(
                    "(directory)",
                    "too_many_recovery_files",
                    "The panel recovery directory contains too many entries."));
        }

        foreach (string path in files.Take(MaximumRecoveryFiles))
        {
            string fileName = Path.GetFileName(path);
            if (!includeActiveOwners && IsOwnedByActiveStore(path))
            {
                continue;
            }

            try
            {
                TripoPanelRecoveryHint hint = ReadAndValidate(path);
                string expectedName = hint.RecoveryId + ".json";
                if (!string.Equals(
                        fileName,
                        expectedName,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "The recovery file name does not match its recovery ID.");
                }

                ForeignOwnerStatus foreignOwner =
                    GetForeignOwnerStatus(hint);
                if (foreignOwner is not ForeignOwnerStatus.NotLiveOwner)
                {
                    bool knownLive =
                        foreignOwner == ForeignOwnerStatus.LiveOwner;
                    issues.Add(
                        new TripoPanelRecoveryIssue(
                            fileName,
                            knownLive
                                ? "recovery_owner_process_alive"
                                : "recovery_owner_process_unknown",
                            knownLive
                                ? "The owner host process is still alive. " +
                                  "Reconcile this hint in that process or close " +
                                  "it before recovering elsewhere."
                                : "The owner host process could not be verified " +
                                  "as exited. Reconcile this hint in that process " +
                                  "or verify that it has closed before recovering " +
                                  "elsewhere."));
                    continue;
                }

                hints.Add(new LoadedTripoPanelRecoveryHint(fileName, hint));
            }
            catch (Exception exception)
                when (exception is IOException or
                      UnauthorizedAccessException or
                      InvalidDataException or
                      JsonException or
                      DecoderFallbackException)
            {
                issues.Add(
                    new TripoPanelRecoveryIssue(
                        fileName,
                        ClassifyReadFailure(exception),
                        BoundMessage(exception.Message)));
            }
        }

        return new TripoPanelRecoveryLoadResult(hints, issues);
    }

    public void Archive(LoadedTripoPanelRecoveryHint loaded)
    {
        ArgumentNullException.ThrowIfNull(loaded);
        ValidateHint(loaded.Hint);
        string expectedName = loaded.Hint.RecoveryId + ".json";
        if (!string.Equals(
                loaded.FileName,
                expectedName,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The recovery file name does not match its recovery ID.");
        }

        string source = GetHintPath(loaded.Hint.RecoveryId);
        EnsurePrivateNonReparseDirectory(_directory);
        using FileStream recoveryLock = AcquireRecoveryLock();
        if (!File.Exists(source))
        {
            throw new FileNotFoundException(
                "The panel recovery hint no longer exists.",
                loaded.FileName);
        }

        if (IsReparsePoint(source))
        {
            throw new InvalidDataException(
                "The panel recovery hint is a symbolic link or reparse point.");
        }

        TripoPanelRecoveryHint current = ReadAndValidate(source);
        if (!Equals(current, loaded.Hint))
        {
            throw new InvalidDataException(
                "The panel recovery hint changed after it was inspected.");
        }

        string archiveDirectory = Path.Combine(_directory, "archive");
        EnsurePrivateNonReparseDirectory(archiveDirectory);
        string destination = Path.Combine(
            archiveDirectory,
            loaded.Hint.RecoveryId + "." +
            DateTimeOffset.UtcNow.ToString(
                "yyyyMMddTHHmmssfffZ",
                CultureInfo.InvariantCulture) + "." +
            Convert.ToHexString(RandomNumberGenerator.GetBytes(4)) +
            ".json");
        File.Move(source, destination);
        UnregisterOwnedPath(source);
        Tripo.Bridge.BridgePaths.SetPrivateFileMode(destination);
    }

    public void Dispose()
    {
        string[] paths;
        lock (_ownedPathsGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            paths = _ownedPaths.ToArray();
            _ownedPaths.Clear();
        }

        foreach (string path in paths)
        {
            if (ActiveOwners.TryGetValue(path, out WeakReference<
                    TripoPanelRecoveryStore>? owner) &&
                owner.TryGetTarget(out TripoPanelRecoveryStore? target) &&
                ReferenceEquals(target, this))
            {
                ActiveOwners.TryRemove(
                    new KeyValuePair<
                        string,
                        WeakReference<TripoPanelRecoveryStore>>(
                        path,
                        owner));
            }
        }
    }

    private TripoPanelRecoveryHint? BuildHint(
        TripoPanelState state,
        string documentSessionId)
    {
        TripoPanelPaidRecoveryHint? generation =
            BuildPaidHint(
                state.PreparedGenerationOperationId,
                state.GenerationDispatchAttempted,
                state.GenerationTaskId,
                state.GenerationReceiptOperationId is not null,
                state.GenerationOperationStatus);
        TripoPanelPaidRecoveryHint? conversion =
            BuildPaidHint(
                state.PreparedConversion?.OperationId,
                state.ConversionDispatchAttempted,
                state.ConversionReceipt?.ConversionTaskId ??
                state.ConversionOperationStatus?.CreatedTaskId,
                state.ConversionReceipt is not null,
                state.ConversionOperationStatus);
        TripoPanelImportRecoveryHint? import =
            state.ImportDispatchAttempted &&
            state.PreparedImport is not null
                ? new TripoPanelImportRecoveryHint(
                    CanonicalizeUuid(
                        state.PreparedImport.OperationId,
                        nameof(state.PreparedImport.OperationId)),
                    ValidateTaskId(
                        state.PreparedImport.ConversionTaskId,
                        nameof(state.PreparedImport.ConversionTaskId)),
                    ValidateName(state.PreparedImport.Name),
                    ValidateImportMode(state.PreparedImport.ImportMode),
                    state.PreparedImport.ApplyMaterials,
                    DispatchAttempted: true,
                    ReceiptKnown: state.ImportReceipt is not null)
                : null;
        if (state.ImportDispatchAttempted &&
            state.PreparedImport is null)
        {
            throw new InvalidOperationException(
                "The import dispatch has no prepared recovery identity.");
        }

        if (generation is null && conversion is null && import is null)
        {
            return null;
        }

        return new TripoPanelRecoveryHint(
            CurrentSchemaVersion,
            _recoveryId,
            _host,
            _ownerProcessId,
            _ownerProcessStartedAtUtc,
            documentSessionId,
            DateTimeOffset.UtcNow,
            generation,
            conversion,
            import);
    }

    private static TripoPanelPaidRecoveryHint? BuildPaidHint(
        string? operationId,
        bool dispatchAttempted,
        string? taskId,
        bool receiptKnown,
        Tripo.Bridge.HostControlOperationStatusReceipt? status)
    {
        if (TripoPanelState.IsDefinitiveRequestRejection(status))
        {
            return null;
        }

        if (!dispatchAttempted || operationId is null)
        {
            if (dispatchAttempted)
            {
                throw new InvalidOperationException(
                    "The paid dispatch has no prepared recovery identity.");
            }

            return null;
        }

        return new TripoPanelPaidRecoveryHint(
            CanonicalizeUuid(operationId, nameof(operationId)),
            DispatchAttempted: true,
            taskId is null ? null : ValidateTaskId(taskId, nameof(taskId)),
            status?.State is null
                ? null
                : ValidateBoundedText(
                    status.State,
                    64,
                    nameof(status.State)),
            receiptKnown || status?.TaskIdDurable == true,
            status?.CanResumeCreation == true);
    }

    private TripoPanelRecoveryHint ReadAndValidate(string path)
    {
        if (IsReparsePoint(path))
        {
            throw new InvalidDataException(
                "The recovery hint is a symbolic link or reparse point.");
        }

        string json;
        using (FileStream stream = new(
                   path,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.Read,
                   bufferSize: 4096,
                   FileOptions.SequentialScan))
        {
            if (stream.Length <= 0 ||
                stream.Length > MaximumRecoveryFileBytes)
            {
                throw new InvalidDataException(
                    "The recovery hint is empty or exceeds its size limit.");
            }

            if (!OperatingSystem.IsWindows())
            {
                UnixFileMode mode = File.GetUnixFileMode(path);
                UnixFileMode nonOwnerBits =
                    UnixFileMode.GroupRead |
                    UnixFileMode.GroupWrite |
                    UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead |
                    UnixFileMode.OtherWrite |
                    UnixFileMode.OtherExecute;
                if ((mode & nonOwnerBits) != 0)
                {
                    throw new InvalidDataException(
                        "The recovery hint is accessible outside its owner.");
                }
            }

            using StreamReader reader = new(
                stream,
                StrictUtf8,
                detectEncodingFromByteOrderMarks: false,
                bufferSize: 4096,
                leaveOpen: false);
            json = reader.ReadToEnd();
        }

        using JsonDocument document = JsonDocument.Parse(json);
        EnsureObjectProperties(
            document.RootElement,
            RootProperties,
            "recovery hint");
        EnsureOptionalObjectProperties(
            document.RootElement,
            "generation",
            PaidProperties);
        EnsureOptionalObjectProperties(
            document.RootElement,
            "conversion",
            PaidProperties);
        EnsureOptionalObjectProperties(
            document.RootElement,
            "import",
            ImportProperties);
        TripoPanelRecoveryHint hint =
            JsonSerializer.Deserialize<TripoPanelRecoveryHint>(
                json,
                RecoveryJsonOptions)
            ?? throw new JsonException("The recovery hint is null.");
        ValidateHint(hint);
        return hint;
    }

    private void ValidateHint(TripoPanelRecoveryHint hint)
    {
        if (hint.SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                "The recovery hint uses an unsupported schema version.");
        }

        if (!string.Equals(hint.Host, _host, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The recovery hint belongs to a different host.");
        }

        _ = CanonicalizeUuid(hint.RecoveryId, nameof(hint.RecoveryId));
        if (hint.OwnerProcessId <= 0)
        {
            throw new InvalidDataException(
                "The recovery hint has an invalid owner process ID.");
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (hint.OwnerProcessStartedAtUtc < DateTimeOffset.UnixEpoch ||
            hint.OwnerProcessStartedAtUtc > hint.UpdatedAtUtc ||
            hint.UpdatedAtUtc > now.AddMinutes(5))
        {
            throw new InvalidDataException(
                "The recovery hint contains invalid timestamps.");
        }

        _ = CanonicalizeUuid(
            hint.DocumentSessionId,
            nameof(hint.DocumentSessionId));
        if (hint.Generation is null &&
            hint.Conversion is null &&
            hint.Import is null)
        {
            throw new InvalidDataException(
                "The recovery hint contains no workflow operation.");
        }

        ValidatePaidHint(hint.Generation);
        ValidatePaidHint(hint.Conversion);
        if (hint.Import is not null)
        {
            _ = CanonicalizeUuid(
                hint.Import.OperationId,
                nameof(hint.Import.OperationId));
            _ = ValidateTaskId(
                hint.Import.ConversionTaskId,
                nameof(hint.Import.ConversionTaskId));
            _ = ValidateName(hint.Import.Name);
            _ = ValidateImportMode(hint.Import.ImportMode);
            if (!hint.Import.DispatchAttempted)
            {
                throw new InvalidDataException(
                    "The import recovery hint was not dispatched.");
            }
        }
    }

    private static void ValidatePaidHint(
        TripoPanelPaidRecoveryHint? hint)
    {
        if (hint is null)
        {
            return;
        }

        _ = CanonicalizeUuid(hint.OperationId, nameof(hint.OperationId));
        if (!hint.DispatchAttempted)
        {
            throw new InvalidDataException(
                "The paid recovery hint was not dispatched.");
        }

        if (hint.TaskId is not null)
        {
            _ = ValidateTaskId(hint.TaskId, nameof(hint.TaskId));
        }

        if (hint.JournalState is not null)
        {
            _ = ValidateBoundedText(
                hint.JournalState,
                64,
                nameof(hint.JournalState));
        }

        if (hint.TaskIdDurable && hint.TaskId is null)
        {
            throw new InvalidDataException(
                "The recovery hint marks a task durable without a task ID.");
        }
    }

    private static void EnsureOptionalObjectProperties(
        JsonElement root,
        string propertyName,
        HashSet<string> allowed)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement value) ||
            value.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        EnsureObjectProperties(value, allowed, propertyName);
    }

    private static void EnsureObjectProperties(
        JsonElement value,
        HashSet<string> allowed,
        string description)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException(
                $"The {description} must be a JSON object.");
        }

        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (!seen.Add(property.Name))
            {
                throw new JsonException(
                    $"The {description} contains a duplicate property.");
            }

            if (!allowed.Contains(property.Name))
            {
                throw new JsonException(
                    $"The {description} contains an unknown property.");
            }
        }

        if (!seen.SetEquals(allowed))
        {
            throw new JsonException(
                $"The {description} is missing a required property.");
        }
    }

    private static ForeignOwnerStatus GetForeignOwnerStatus(
        TripoPanelRecoveryHint hint)
    {
        if (hint.OwnerProcessId == Environment.ProcessId)
        {
            return ForeignOwnerStatus.NotLiveOwner;
        }

        try
        {
            using Process process =
                Process.GetProcessById(hint.OwnerProcessId);
            if (process.HasExited)
            {
                return ForeignOwnerStatus.NotLiveOwner;
            }

            DateTimeOffset startedAt =
                new(
                    process.StartTime.ToUniversalTime(),
                    TimeSpan.Zero);
            return Math.Abs(
                       (startedAt - hint.OwnerProcessStartedAtUtc)
                       .TotalSeconds) < 1
                ? ForeignOwnerStatus.LiveOwner
                : ForeignOwnerStatus.NotLiveOwner;
        }
        catch (ArgumentException)
        {
            return ForeignOwnerStatus.NotLiveOwner;
        }
        catch (InvalidOperationException)
        {
            return ForeignOwnerStatus.Unknown;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return ForeignOwnerStatus.Unknown;
        }
    }

    private enum ForeignOwnerStatus
    {
        NotLiveOwner,
        LiveOwner,
        Unknown,
    }

    private void RegisterOwnedPath(string path)
    {
        lock (_ownedPathsGate)
        {
            ThrowIfDisposed();
            _ownedPaths.Add(path);
            ActiveOwners[path] =
                new WeakReference<TripoPanelRecoveryStore>(this);
        }
    }

    private void UnregisterOwnedPath(string path)
    {
        lock (_ownedPathsGate)
        {
            _ownedPaths.Remove(path);
        }

        if (ActiveOwners.TryGetValue(path, out WeakReference<
                TripoPanelRecoveryStore>? owner) &&
            owner.TryGetTarget(out TripoPanelRecoveryStore? target) &&
            ReferenceEquals(target, this))
        {
            ActiveOwners.TryRemove(
                new KeyValuePair<
                    string,
                    WeakReference<TripoPanelRecoveryStore>>(
                    path,
                    owner));
        }
    }

    private static bool IsOwnedByActiveStore(string path)
    {
        if (!ActiveOwners.TryGetValue(
                path,
                out WeakReference<TripoPanelRecoveryStore>? owner))
        {
            return false;
        }

        if (owner.TryGetTarget(out TripoPanelRecoveryStore? target) &&
            !target._disposed)
        {
            return true;
        }

        ActiveOwners.TryRemove(
            new KeyValuePair<
                string,
                WeakReference<TripoPanelRecoveryStore>>(
                path,
                owner));
        return false;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private string GetHintPath(string recoveryId) =>
        Path.Combine(
            _directory,
            CanonicalizeUuid(
                recoveryId,
                nameof(recoveryId)) + ".json");

    private static string CanonicalizeUuid(
        string value,
        string parameterName)
    {
        if (!Guid.TryParseExact(value, "D", out Guid parsed) ||
            !string.Equals(
                parsed.ToString("D"),
                value,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"{parameterName} must be a canonical lowercase D-format UUID.");
        }

        return value;
    }

    private static string ValidateTaskId(
        string value,
        string parameterName)
    {
        if (!Tripo.Bridge.TripoTaskId.IsValid(value))
        {
            throw new InvalidDataException(
                $"{parameterName} is not a valid Tripo task ID.");
        }

        return value;
    }

    private static string ValidateName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128)
        {
            throw new InvalidDataException(
                "The recovery hint contains an invalid import name.");
        }

        return value;
    }

    private static string ValidateImportMode(string value)
    {
        if (value is not "native" and
            not "mesh" and
            not "instance" and
            not "family" and
            not "glb_instance")
        {
            throw new InvalidDataException(
                "The recovery hint contains an invalid import mode.");
        }

        return value;
    }

    private static string ValidateBoundedText(
        string value,
        int maximumLength,
        string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > maximumLength ||
            value.Any(char.IsControl))
        {
            throw new InvalidDataException(
                $"{parameterName} contains invalid text.");
        }

        return value;
    }

    private static bool IsReparsePoint(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private void DeleteOwnedRegularFile(string path)
    {
        if (!OwnsPath(path))
        {
            return;
        }

        EnsurePrivateNonReparseDirectory(_directory);
        using FileStream recoveryLock = AcquireRecoveryLock();
        if (IsFileLinkOrReparsePoint(path))
        {
            throw new InvalidDataException(
                "The panel recovery path is a symbolic link or reparse point.");
        }

        if (File.Exists(path))
        {
            File.Delete(path);
        }

        UnregisterOwnedPath(path);
    }

    private FileStream AcquireRecoveryLock()
    {
        string lockPath = Path.Combine(_directory, ".recovery.lock");
        if (IsFileLinkOrReparsePoint(lockPath))
        {
            throw new InvalidDataException(
                "The panel recovery lock is a symbolic link or reparse point.");
        }

        FileStream stream = new(
            lockPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 1,
            FileOptions.WriteThrough);
        try
        {
            Tripo.Bridge.BridgePaths.SetPrivateFileMode(lockPath);
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private void ValidateRecoveryLockFile()
    {
        string lockPath = Path.Combine(_directory, ".recovery.lock");
        ValidatePrivateRegularLockFile(
            lockPath,
            "The panel recovery lock");
    }

    private void ValidateCredentialWorkflowLeaseFile()
    {
        EnsurePrivateNonReparseDirectory(_recoveryRoot);
        ValidatePrivateRegularLockFile(
            GetCredentialWorkflowLeasePath(),
            "The credential/workflow lock");
    }

    private string GetCredentialWorkflowLeasePath() =>
        Path.Combine(_recoveryRoot, ".credential-workflow.lock");

    private static void ValidatePrivateRegularLockFile(
        string lockPath,
        string description)
    {
        if (Directory.Exists(lockPath) ||
            IsFileLinkOrReparsePoint(lockPath))
        {
            throw new InvalidDataException(
                $"{description} is not a private regular file.");
        }

        if (!File.Exists(lockPath))
        {
            return;
        }

        FileInfo info = new(lockPath);
        if (info.Length > 64)
        {
            throw new InvalidDataException(
                $"{description} exceeded its size limit.");
        }

        if (!OperatingSystem.IsWindows())
        {
            UnixFileMode mode = File.GetUnixFileMode(lockPath);
            UnixFileMode nonOwnerBits =
                UnixFileMode.GroupRead |
                UnixFileMode.GroupWrite |
                UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead |
                UnixFileMode.OtherWrite |
                UnixFileMode.OtherExecute;
            if ((mode & nonOwnerBits) != 0)
            {
                throw new InvalidDataException(
                    $"{description} is accessible outside its owner.");
            }
        }
    }

    private static bool IsFileLinkOrReparsePoint(string path)
    {
        FileInfo info = new(path);
        if (info.LinkTarget is not null)
        {
            return true;
        }

        return File.Exists(path) && IsReparsePoint(path);
    }

    private static bool HasCredentialSensitiveOperation(
        LoadedTripoPanelRecoveryHint loaded) =>
        loaded.Hint.Generation is not null ||
        loaded.Hint.Conversion is not null ||
        loaded.Hint.Import is { ReceiptKnown: false };

    private bool IsCurrentOwnedHint(
        LoadedTripoPanelRecoveryHint loaded)
    {
        TripoPanelRecoveryHint hint = loaded.Hint;
        if (!string.Equals(
                hint.RecoveryId,
                _recoveryId,
                StringComparison.Ordinal) ||
            !string.Equals(
                hint.Host,
                _host,
                StringComparison.Ordinal) ||
            hint.OwnerProcessId != _ownerProcessId ||
            hint.OwnerProcessStartedAtUtc != _ownerProcessStartedAtUtc)
        {
            return false;
        }

        return OwnsPath(GetHintPath(hint.RecoveryId));
    }

    private static void EnsurePrivateNonReparseDirectory(string path)
    {
        if (Directory.Exists(path) && IsReparsePoint(path))
        {
            throw new InvalidDataException(
                "The panel recovery directory is a symbolic link or reparse point.");
        }

        Tripo.Bridge.BridgePaths.EnsurePrivateDirectory(path);
        if (IsReparsePoint(path))
        {
            throw new InvalidDataException(
                "The panel recovery directory is a symbolic link or reparse point.");
        }
    }

    private bool OwnsPath(string path)
    {
        lock (_ownedPathsGate)
        {
            return _ownedPaths.Contains(path);
        }
    }

    private static TripoPanelRecoveryLoadResult DirectoryIssue(
        string code,
        Exception exception) =>
        new(
            [],
            [
                new TripoPanelRecoveryIssue(
                    "(directory)",
                    code,
                    BoundMessage(exception.Message)),
            ]);

    private static string ClassifyReadFailure(Exception exception) =>
        exception switch
        {
            UnauthorizedAccessException => "recovery_file_unreadable",
            DecoderFallbackException => "recovery_file_invalid_utf8",
            JsonException => "recovery_file_invalid_json",
            InvalidDataException => "recovery_file_invalid",
            _ => "recovery_file_unreadable",
        };

    private static string BoundMessage(string? message)
    {
        const string fallback = "The panel recovery hint could not be read.";
        if (string.IsNullOrWhiteSpace(message))
        {
            return fallback;
        }

        string trimmed = message.Trim();
        return trimmed.Length <= 256 ? trimmed : trimmed[..256];
    }
}
