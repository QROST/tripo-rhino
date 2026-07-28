using System.Globalization;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Tripo.Bridge;

public sealed record HostImportJournalIdentity(
    string Host,
    string DocumentSessionId,
    string OperationId,
    string RequestFingerprint,
    string ArtifactId,
    string EntrySha256,
    long EntryByteLength);

public sealed record HostImportCommitReceipt(
    string CreatedId,
    int VertexCount,
    int TriangleCount,
    int MaterialCount,
    int TextureCount,
    int DefinitionMemberCount,
    string DefinitionMemberDigest,
    string PbrContentDigest);

public sealed record HostImportJournalStatus(
    string State,
    HostImportCommitReceipt? Commit);

public sealed partial class HostImportJournal : IDisposable
{
    public const string PreparedState = "prepared";
    public const string OutcomeUnknownState = "outcome_unknown";
    public const string AbortedBeforeImportState = "aborted_before_import";
    public const string CommittedState = "committed";

    private const int SchemaVersion = 2;
    private const int MaximumJournalBytes = 64 * 1024;
    private const int MaximumRecords = 32;
    private static readonly ConcurrentDictionary<string, byte> ActiveLeases =
        new(StringComparer.Ordinal);
    private readonly HostImportJournalIdentity _identity;
    private readonly string _leaseKey;
    private readonly FileStream _stream;
    private readonly Mutex _mutex;
    private int _recordCount;
    private readonly bool _appendAllowed;
    private bool _disposed;

    private HostImportJournal(
        HostImportJournalIdentity identity,
        string leaseKey,
        FileStream stream,
        Mutex mutex,
        HostImportJournalStatus? current,
        int recordCount,
        bool appendAllowed)
    {
        _identity = identity;
        _leaseKey = leaseKey;
        _stream = stream;
        _mutex = mutex;
        _recordCount = recordCount;
        _appendAllowed = appendAllowed;
        Current = current;
    }

    public HostImportJournalStatus? Current { get; private set; }

    public static HostImportJournal Open(HostImportJournalIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ValidateIdentity(identity);
        string root = BridgePaths.GetRootDirectory();
        BridgePaths.EnsurePrivateNonReparseDirectory(root);
        string imports = Path.Combine(root, "host-imports");
        BridgePaths.EnsurePrivateNonReparseDirectory(imports);
        string hostDirectory = Path.Combine(imports, identity.Host);
        BridgePaths.EnsurePrivateNonReparseDirectory(hostDirectory);
        string path = Path.Combine(
            hostDirectory,
            identity.OperationId + ".jsonl");

        FileStream stream;
        bool createdNew;
        try
        {
            stream = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.Read,
                4096,
                FileOptions.WriteThrough);
            createdNew = true;
        }
        catch (IOException) when (File.Exists(path))
        {
            createdNew = false;
            EnsureSafeJournalFile(path);
            try
            {
                stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.ReadWrite,
                    FileShare.Read,
                    4096,
                    FileOptions.WriteThrough);
            }
            catch (IOException exception)
            {
                throw new BridgeCallException(
                    BridgeConstants.MutationStateUncertainError,
                    "The host import journal is already in use or unavailable.",
                    exception);
            }
        }

        Mutex? mutex = null;
        bool mutexHeld = false;
        string leaseKey = Path.GetFullPath(path);
        bool processLeaseHeld = ActiveLeases.TryAdd(leaseKey, 0);
        if (!processLeaseHeld)
        {
            stream.Dispose();
            throw new BridgeCallException(
                BridgeConstants.MutationStateUncertainError,
                "The host import journal is already in use or unavailable.");
        }

        try
        {
            try
            {
                string mutexName = "TripoMCP-HostImport-" +
                    Convert.ToHexString(
                        SHA256.HashData(Encoding.UTF8.GetBytes(
                            leaseKey)))
                    .ToLowerInvariant();
                mutex = new Mutex(initiallyOwned: false, mutexName);
                try
                {
                    mutexHeld = mutex.WaitOne(0);
                }
                catch (AbandonedMutexException)
                {
                    mutexHeld = true;
                }
            }
            catch (Exception exception)
                when (exception is IOException or UnauthorizedAccessException)
            {
                throw new BridgeCallException(
                    BridgeConstants.MutationStateUncertainError,
                    "The host import journal is already in use or unavailable.",
                    exception);
            }

            if (!mutexHeld)
            {
                throw new BridgeCallException(
                    BridgeConstants.MutationStateUncertainError,
                    "The host import journal is already in use or unavailable.");
            }

            BridgePaths.SetPrivateFileMode(path);
            EnsureSafeJournalFile(path);
            HostImportJournalStatus? current =
                ReadCurrent(
                    stream,
                    identity,
                    createdNew,
                    out int recordCount,
                    out bool appendAllowed);
            stream.Position = stream.Length;
            return new HostImportJournal(
                identity,
                leaseKey,
                stream,
                mutex,
                current,
                recordCount,
                appendAllowed);
        }
        catch
        {
            if (mutexHeld)
            {
                mutex!.ReleaseMutex();
            }

            mutex?.Dispose();
            ActiveLeases.TryRemove(leaseKey, out _);
            stream.Dispose();
            throw;
        }
    }

    public void RecordPrepared()
    {
        ThrowIfDisposed();
        if (Current is
            {
                State: PreparedState or OutcomeUnknownState or CommittedState,
            })
        {
            throw new BridgeCallException(
                BridgeConstants.MutationStateUncertainError,
                "The host import journal does not authorize another native import.");
        }

        Append(PreparedState, commit: null);
    }

    public void RecordOutcomeUnknown()
    {
        RequireCurrent(PreparedState);
        Append(OutcomeUnknownState, commit: null);
    }

    public void RecordAbortedBeforeImport()
    {
        RequireCurrent(PreparedState);
        Append(AbortedBeforeImportState, commit: null);
    }

    public void RecordCommitted(HostImportCommitReceipt commit)
    {
        ArgumentNullException.ThrowIfNull(commit);
        ValidateCommit(commit);
        RequireCurrent(PreparedState);
        Append(CommittedState, commit);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _stream.Dispose();
            if (_recordCount == 0)
            {
                TryDeleteUnusedJournal(_leaseKey);
            }
        }
        finally
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            finally
            {
                _mutex.Dispose();
                ActiveLeases.TryRemove(_leaseKey, out _);
            }
        }
    }

    private void Append(
        string state,
        HostImportCommitReceipt? commit)
    {
        JournalRecord unsigned = new(
            SchemaVersion,
            _identity,
            state,
            commit,
            DateTimeOffset.UtcNow,
            Checksum: string.Empty);
        JournalRecord record = unsigned with
        {
            Checksum = ComputeChecksum(unsigned),
        };
        string json = JsonSerializer.Serialize(record, BridgeJson.Options);
        byte[] line = Encoding.UTF8.GetBytes(json + "\n");
        if (!_appendAllowed ||
            _recordCount >= MaximumRecords ||
            _stream.Length + line.Length > MaximumJournalBytes)
        {
            throw new BridgeCallException(
                BridgeConstants.MutationStateUncertainError,
                "The host import journal exceeded its bounded size.");
        }

        try
        {
            _stream.Position = _stream.Length;
            _stream.Write(line);
            _stream.Flush(flushToDisk: true);
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException)
        {
            throw new BridgeCallException(
                BridgeConstants.MutationStateUncertainError,
                "The host import journal could not durably record its state.",
                exception);
        }

        _recordCount++;
        Current = new HostImportJournalStatus(state, commit);
    }

    private void RequireCurrent(string state)
    {
        ThrowIfDisposed();
        if (!string.Equals(Current?.State, state, StringComparison.Ordinal))
        {
            throw new BridgeCallException(
                BridgeConstants.MutationStateUncertainError,
                "The host import journal state transition was not authorized.");
        }
    }

    private static HostImportJournalStatus? ReadCurrent(
        FileStream stream,
        HostImportJournalIdentity expectedIdentity,
        bool allowEmpty,
        out int recordCount,
        out bool appendAllowed)
    {
        recordCount = 0;
        appendAllowed = true;
        if (stream.Length == 0)
        {
            return allowEmpty
                ? null
                : throw CorruptJournal();
        }

        if (stream.Length > MaximumJournalBytes)
        {
            throw CorruptJournal();
        }

        stream.Position = 0;
        byte[] bytes = new byte[checked((int)stream.Length)];
        int offset = 0;
        while (offset < bytes.Length)
        {
            int read = stream.Read(bytes, offset, bytes.Length - offset);
            if (read == 0)
            {
                throw CorruptJournal();
            }

            offset += read;
        }

        string text;
        try
        {
            text = new UTF8Encoding(
                    encoderShouldEmitUTF8Identifier: false,
                    throwOnInvalidBytes: true)
                .GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            throw CorruptJournal();
        }

        string[] lines = text.Split('\n');
        appendAllowed = text.EndsWith('\n');
        int completeLineCount = lines.Length - 1;
        if (completeLineCount <= 0 ||
            completeLineCount > MaximumRecords)
        {
            throw CorruptJournal();
        }

        HostImportJournalStatus? current = null;
        for (int index = 0; index < completeLineCount; index++)
        {
            string line = lines[index];
            if (string.IsNullOrEmpty(line))
            {
                throw CorruptJournal();
            }

            JournalRecord record;
            try
            {
                record = JsonSerializer.Deserialize<JournalRecord>(
                        line,
                        BridgeJson.Options)
                    ?? throw new JsonException();
            }
            catch (JsonException)
            {
                throw CorruptJournal();
            }

            if (!string.Equals(
                    JsonSerializer.Serialize(record, BridgeJson.Options),
                    line,
                    StringComparison.Ordinal) ||
                record.SchemaVersion != SchemaVersion ||
                !string.Equals(
                    record.Checksum,
                    ComputeChecksum(record with { Checksum = string.Empty }),
                    StringComparison.Ordinal))
            {
                throw CorruptJournal();
            }

            ValidateRecordIdentity(record.Identity, expectedIdentity);
            ValidateTransition(current, record.State, record.Commit);
            current = new HostImportJournalStatus(
                record.State,
                record.Commit);
        }

        if (!appendAllowed &&
            string.Equals(
                current?.State,
                CommittedState,
                StringComparison.Ordinal))
        {
            throw CorruptJournal();
        }

        recordCount = completeLineCount;
        return current;
    }

    private static void ValidateTransition(
        HostImportJournalStatus? current,
        string state,
        HostImportCommitReceipt? commit)
    {
        bool valid =
            current is null &&
            string.Equals(state, PreparedState, StringComparison.Ordinal) ||
            current?.State == AbortedBeforeImportState &&
            state == PreparedState ||
            current?.State == PreparedState &&
            (state == OutcomeUnknownState ||
             state == AbortedBeforeImportState ||
             state == CommittedState);
        if (!valid ||
            state == CommittedState != (commit is not null))
        {
            throw CorruptJournal();
        }

        if (commit is not null)
        {
            ValidateCommit(commit);
        }
    }

    private static void ValidateRecordIdentity(
        HostImportJournalIdentity actual,
        HostImportJournalIdentity expected)
    {
        if (!string.Equals(actual.Host, expected.Host, StringComparison.Ordinal) ||
            !string.Equals(
                actual.OperationId,
                expected.OperationId,
                StringComparison.Ordinal) ||
            !string.Equals(
                actual.RequestFingerprint,
                expected.RequestFingerprint,
                StringComparison.Ordinal) ||
            !string.Equals(
                actual.ArtifactId,
                expected.ArtifactId,
                StringComparison.Ordinal) ||
            !string.Equals(
                actual.EntrySha256,
                expected.EntrySha256,
                StringComparison.Ordinal) ||
            actual.EntryByteLength != expected.EntryByteLength)
        {
            throw new BridgeCallException(
                "idempotency_conflict",
                "The host import journal belongs to a different request identity.");
        }
    }

    private static string ComputeChecksum(JournalRecord record)
    {
        byte[] canonical = JsonSerializer.SerializeToUtf8Bytes(
            record,
            BridgeJson.Options);
        return Convert.ToHexString(SHA256.HashData(canonical))
            .ToLowerInvariant();
    }

    private static void ValidateIdentity(HostImportJournalIdentity identity)
    {
        string normalizedHost = BridgePaths.NormalizeHost(identity.Host);
        if (!string.Equals(
                normalizedHost,
                identity.Host,
                StringComparison.Ordinal) ||
            !IsCanonicalGuid(identity.DocumentSessionId) ||
            !IsCanonicalGuid(identity.OperationId) ||
            !HashRegex().IsMatch(identity.RequestFingerprint) ||
            !HashRegex().IsMatch(identity.ArtifactId) ||
            !HashRegex().IsMatch(identity.EntrySha256) ||
            identity.EntryByteLength <= 0 ||
            identity.EntryByteLength >
                BridgeConstants.MaximumGlbArtifactBytes)
        {
            throw new BridgeCallException(
                "invalid_request",
                "The host import journal identity was invalid.");
        }
    }

    private static void ValidateCommit(HostImportCommitReceipt commit)
    {
        if (!IsCanonicalGuid(commit.CreatedId) ||
            commit.VertexCount < 0 ||
            commit.VertexCount > BridgeConstants.MaximumVertices ||
            commit.TriangleCount < 0 ||
            commit.TriangleCount > BridgeConstants.MaximumTriangles ||
            commit.MaterialCount < 0 ||
            commit.MaterialCount > 256 ||
            commit.TextureCount < 0 ||
            commit.TextureCount > 512 ||
            commit.DefinitionMemberCount <= 0 ||
            commit.DefinitionMemberCount > 4_096 ||
            commit.DefinitionMemberDigest is null ||
            !HashRegex().IsMatch(commit.DefinitionMemberDigest) ||
            commit.PbrContentDigest is null ||
            !HashRegex().IsMatch(commit.PbrContentDigest))
        {
            throw new BridgeCallException(
                "invalid_request",
                "The host import commit receipt was invalid.");
        }
    }

    private static void EnsureSafeJournalFile(string path)
    {
        FileInfo info = new(path);
        info.Refresh();
        if (!info.Exists ||
            info.LinkTarget is not null ||
            (info.Attributes & FileAttributes.ReparsePoint) != 0 ||
            info.Length > MaximumJournalBytes)
        {
            throw new BridgeCallException(
                BridgeConstants.MutationStateUncertainError,
                "The host import journal path or size was unsafe.");
        }
    }

    private static void TryDeleteUnusedJournal(string path)
    {
        try
        {
            FileInfo info = new(path);
            info.Refresh();
            if (info.Exists &&
                info.Length == 0 &&
                info.LinkTarget is null &&
                (info.Attributes & FileAttributes.ReparsePoint) == 0)
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // A failed cleanup leaves an empty fail-closed journal.
        }
        catch (UnauthorizedAccessException)
        {
            // A failed cleanup leaves an empty fail-closed journal.
        }
    }

    private static bool IsCanonicalGuid(string value) =>
        Guid.TryParseExact(value, "D", out Guid parsed) &&
        string.Equals(
            parsed.ToString("D"),
            value,
            StringComparison.Ordinal);

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private static BridgeCallException CorruptJournal() =>
        new(
            BridgeConstants.MutationStateUncertainError,
            "The host import journal was incomplete or corrupt; manual review is required.");

    private sealed record JournalRecord(
        int SchemaVersion,
        HostImportJournalIdentity Identity,
        string State,
        HostImportCommitReceipt? Commit,
        DateTimeOffset UpdatedAtUtc,
        string Checksum);

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex HashRegex();
}
