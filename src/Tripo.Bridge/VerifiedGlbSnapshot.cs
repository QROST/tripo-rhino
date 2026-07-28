using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;

namespace Tripo.Bridge;

public sealed class VerifiedGlbSnapshot : IDisposable
{
    private const int MaximumCleanupEntries = 256;
    private const int MaximumCleanupDeletions = 16;
    private static readonly TimeSpan StaleSnapshotAge = TimeSpan.FromHours(24);

    private readonly string _directory;
    private readonly StagedBundleEntry _entry;
    private readonly FileStream _lease;
    private bool _disposed;

    private VerifiedGlbSnapshot(
        string directory,
        string glbPath,
        StagedBundleEntry entry,
        FileStream lease)
    {
        _directory = directory;
        GlbPath = glbPath;
        _entry = entry;
        _lease = lease;
    }

    public string GlbPath { get; }

    public static async Task<VerifiedGlbSnapshot> CreateAsync(
        PreparedGlbArtifact prepared,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        byte[] content = prepared.VerifiedContent.ToArray();
        VerifyContent(content, prepared.Entry);

        string root = BridgePaths.GetRootDirectory();
        BridgePaths.EnsurePrivateNonReparseDirectory(root);
        string snapshots = Path.Combine(root, "host-import-snapshots");
        BridgePaths.EnsurePrivateNonReparseDirectory(snapshots);
        TryCleanupStaleSnapshots(snapshots);
        string directory = Path.Combine(
            snapshots,
            Environment.ProcessId.ToString(
                CultureInfo.InvariantCulture) +
            "-" +
            Convert.ToHexString(RandomNumberGenerator.GetBytes(16))
                .ToLowerInvariant());
        string glbPath = Path.Combine(directory, "model.glb");
        FileStream? lease = null;
        try
        {
            BridgePaths.EnsurePrivateNonReparseDirectory(directory);
            await using (FileStream writer = new(
                             glbPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             64 * 1024,
                             FileOptions.Asynchronous |
                             FileOptions.SequentialScan |
                             FileOptions.WriteThrough))
            {
                await writer.WriteAsync(content, cancellationToken)
                    .ConfigureAwait(false);
                await writer.FlushAsync(cancellationToken)
                    .ConfigureAwait(false);
                writer.Flush(flushToDisk: true);
            }

            BridgePaths.SetPrivateFileMode(glbPath);
            lease = new FileStream(
                glbPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.SequentialScan);
            VerifiedGlbSnapshot snapshot = new(
                directory,
                glbPath,
                prepared.Entry,
                lease);
            lease = null;
            snapshot.Verify();
            return snapshot;
        }
        catch
        {
            lease?.Dispose();
            TryDelete(glbPath, directory);
            throw;
        }
    }

    public void Verify()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        DirectoryInfo directory = new(_directory);
        FileInfo file = new(GlbPath);
        directory.Refresh();
        file.Refresh();
        if (!directory.Exists ||
            directory.LinkTarget is not null ||
            (directory.Attributes & FileAttributes.ReparsePoint) != 0 ||
            !file.Exists ||
            file.LinkTarget is not null ||
            (file.Attributes & FileAttributes.ReparsePoint) != 0 ||
            file.Length != _entry.ByteLength)
        {
            throw new BridgeCallException(
                "artifact_hash_mismatch",
                "The verified GLB snapshot path or length changed.");
        }

        try
        {
            using FileStream stream = new(
                GlbPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.SequentialScan);
            string actualHash = Convert.ToHexString(
                    SHA256.HashData(stream))
                .ToLowerInvariant();
            if (!string.Equals(
                    actualHash,
                    _entry.Sha256,
                    StringComparison.Ordinal))
            {
                throw new BridgeCallException(
                    "artifact_hash_mismatch",
                    "The verified GLB snapshot content changed.");
            }
        }
        catch (BridgeCallException)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException)
        {
            throw new BridgeCallException(
                "artifact_hash_mismatch",
                "The verified GLB snapshot could not be revalidated.",
                exception);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lease.Dispose();
        TryDelete(GlbPath, _directory);
    }

    private static void VerifyContent(
        byte[] content,
        StagedBundleEntry entry)
    {
        if (content.LongLength != entry.ByteLength)
        {
            throw new BridgeCallException(
                "artifact_hash_mismatch",
                "The verified GLB bytes no longer match their declared length.");
        }

        string actualHash = Convert.ToHexString(SHA256.HashData(content))
            .ToLowerInvariant();
        if (!string.Equals(
                actualHash,
                entry.Sha256,
                StringComparison.Ordinal))
        {
            throw new BridgeCallException(
                "artifact_hash_mismatch",
                "The verified GLB bytes no longer match their declared hash.");
        }

        _ = GlbContainerValidator.Validate(content);
    }

    private static void TryCleanupStaleSnapshots(string snapshots)
    {
        try
        {
            DateTime staleBefore = DateTime.UtcNow - StaleSnapshotAge;
            List<string> candidates = new(MaximumCleanupEntries);
            foreach (string candidate in
                     Directory.EnumerateFileSystemEntries(snapshots))
            {
                candidates.Add(candidate);
                if (candidates.Count >= MaximumCleanupEntries)
                {
                    break;
                }
            }

            int mutated = 0;
            foreach (string candidate in candidates)
            {
                if (mutated >= MaximumCleanupDeletions)
                {
                    break;
                }

                if (TryDeleteStaleCleanupTombstone(
                        candidate,
                        staleBefore) ||
                    TryDeleteStaleSnapshot(
                        snapshots,
                        candidate,
                        staleBefore,
                        out bool snapshotMutated))
                {
                    mutated++;
                }
                else if (snapshotMutated)
                {
                    mutated++;
                }
            }
        }
        catch (IOException)
        {
            // Crash-recovery cleanup must not block a fresh import.
        }
        catch (UnauthorizedAccessException)
        {
            // Crash-recovery cleanup must not block a fresh import.
        }
        catch (System.Security.SecurityException)
        {
            // Crash-recovery cleanup must not block a fresh import.
        }
        catch (NotSupportedException)
        {
            // Link metadata may be unavailable on an unsupported filesystem.
        }
    }

    private static bool TryDeleteStaleSnapshot(
        string snapshots,
        string candidate,
        DateTime staleBefore,
        out bool mutated)
    {
        mutated = false;
        try
        {
            string name = Path.GetFileName(candidate);
            if (!TryGetSnapshotDirectoryProcessId(
                    name,
                    out int processId) ||
                !IsProcessDefinitelyNotAlive(processId) ||
                !TryInspectCleanupDirectory(
                    candidate,
                    staleBefore,
                    out _) ||
                !IsProcessDefinitelyNotAlive(processId))
            {
                return false;
            }

            string quarantine = Path.Combine(
                snapshots,
                Environment.ProcessId.ToString(
                    CultureInfo.InvariantCulture) +
                "-cleanup-" +
                Convert.ToHexString(RandomNumberGenerator.GetBytes(16))
                    .ToLowerInvariant());
            Directory.Move(candidate, quarantine);
            mutated = true;
            bool moved = true;
            try
            {
                if (!TryInspectCleanupDirectory(
                        quarantine,
                        staleBefore,
                        out string? modelPath) ||
                    !IsProcessDefinitelyNotAlive(processId))
                {
                    return false;
                }

                if (modelPath is not null)
                {
                    string tombstone = Path.Combine(
                        snapshots,
                        Environment.ProcessId.ToString(
                            CultureInfo.InvariantCulture) +
                        "-cleanup-file-" +
                        Convert.ToHexString(
                                RandomNumberGenerator.GetBytes(16))
                            .ToLowerInvariant() +
                        ".glb");
                    File.Move(modelPath, tombstone);
                    bool tombstoneMoved = true;
                    bool quarantineDeleted = false;
                    try
                    {
                        Directory.Delete(quarantine);
                        moved = false;
                        quarantineDeleted = true;
                        File.Delete(tombstone);
                        tombstoneMoved = false;
                        return true;
                    }
                    finally
                    {
                        if (tombstoneMoved &&
                            !quarantineDeleted)
                        {
                            TryRestoreCleanupFile(
                                tombstone,
                                quarantine,
                                candidate,
                                ref moved);
                        }
                    }
                }

                Directory.Delete(quarantine);
                moved = false;
                return true;
            }
            finally
            {
                if (moved)
                {
                    TryRestoreCleanupCandidate(
                        quarantine,
                        candidate);
                }
            }
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (System.Security.SecurityException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private static bool TryDeleteStaleCleanupTombstone(
        string candidate,
        DateTime staleBefore)
    {
        try
        {
            string name = Path.GetFileName(candidate);
            if (!TryGetCleanupTombstoneProcessId(
                    name,
                    out int processId) ||
                !IsProcessDefinitelyNotAlive(processId))
            {
                return false;
            }

            FileInfo file = new(candidate);
            file.Refresh();
            if (!file.Exists ||
                file.LinkTarget is not null ||
                (file.Attributes &
                 (FileAttributes.Directory |
                  FileAttributes.Device |
                  FileAttributes.ReparsePoint)) != 0 ||
                file.LastWriteTimeUtc > staleBefore ||
                !IsProcessDefinitelyNotAlive(processId))
            {
                return false;
            }

            File.Delete(candidate);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (System.Security.SecurityException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    private static bool TryInspectCleanupDirectory(
        string directory,
        DateTime staleBefore,
        out string? modelPath)
    {
        modelPath = null;
        DirectoryInfo directoryInfo = new(directory);
        directoryInfo.Refresh();
        if (!directoryInfo.Exists ||
            directoryInfo.LinkTarget is not null ||
            (directoryInfo.Attributes & FileAttributes.ReparsePoint) != 0 ||
            directoryInfo.LastWriteTimeUtc > staleBefore)
        {
            return false;
        }

        int entries = 0;
        foreach (string entry in
                 Directory.EnumerateFileSystemEntries(directory))
        {
            entries++;
            if (entries > 1 ||
                !string.Equals(
                    Path.GetFileName(entry),
                    "model.glb",
                    StringComparison.Ordinal))
            {
                return false;
            }

            FileInfo fileInfo = new(entry);
            fileInfo.Refresh();
            if (!fileInfo.Exists ||
                fileInfo.LinkTarget is not null ||
                (fileInfo.Attributes &
                 (FileAttributes.Directory |
                  FileAttributes.ReparsePoint |
                  FileAttributes.Device)) != 0 ||
                fileInfo.LastWriteTimeUtc > staleBefore)
            {
                return false;
            }

            _ = fileInfo.Length;
            modelPath = entry;
        }

        return true;
    }

    private static bool TryGetSnapshotDirectoryProcessId(
        string name,
        out int processId)
    {
        int separator = name.IndexOf('-');
        if (separator <= 0 ||
            (separator > 1 && name[0] == '0') ||
            !int.TryParse(
                name.AsSpan(0, separator),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out processId) ||
            processId <= 0)
        {
            processId = 0;
            return false;
        }

        ReadOnlySpan<char> suffix = name.AsSpan(separator + 1);
        const string cleanupPrefix = "cleanup-";
        if (suffix.StartsWith(
                cleanupPrefix,
                StringComparison.Ordinal))
        {
            suffix = suffix[cleanupPrefix.Length..];
        }

        return IsLowerHexToken(suffix);
    }

    private static bool TryGetCleanupTombstoneProcessId(
        string name,
        out int processId)
    {
        processId = 0;
        const string marker = "-cleanup-file-";
        int separator = name.IndexOf(marker, StringComparison.Ordinal);
        if (separator <= 0 ||
            (separator > 1 && name[0] == '0') ||
            !name.EndsWith(".glb", StringComparison.Ordinal) ||
            int.TryParse(
                name.AsSpan(0, separator),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out processId) is false ||
            processId <= 0)
        {
            processId = 0;
            return false;
        }

        ReadOnlySpan<char> token = name.AsSpan(
            separator + marker.Length,
            name.Length -
            separator -
            marker.Length -
            ".glb".Length);
        return IsLowerHexToken(token);
    }

    private static bool IsLowerHexToken(ReadOnlySpan<char> token)
    {
        if (token.Length != 32)
        {
            return false;
        }

        foreach (char character in token)
        {
            if (character is not (>= '0' and <= '9') and
                not (>= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsProcessDefinitelyNotAlive(int processId)
    {
        if (processId == Environment.ProcessId)
        {
            return false;
        }

        try
        {
            using Process process = Process.GetProcessById(processId);
            return process.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    private static void TryRestoreCleanupCandidate(
        string quarantine,
        string candidate)
    {
        try
        {
            if (Directory.Exists(quarantine) &&
                !Directory.Exists(candidate) &&
                !File.Exists(candidate))
            {
                Directory.Move(quarantine, candidate);
            }
        }
        catch (IOException)
        {
            // A raced cleanup remains quarantined rather than traversed.
        }
        catch (UnauthorizedAccessException)
        {
            // A raced cleanup remains quarantined rather than traversed.
        }
        catch (System.Security.SecurityException)
        {
            // A raced cleanup remains quarantined rather than traversed.
        }
    }

    private static void TryRestoreCleanupFile(
        string tombstone,
        string quarantine,
        string candidate,
        ref bool quarantineMoved)
    {
        try
        {
            if (!File.Exists(tombstone))
            {
                return;
            }

            DirectoryInfo quarantineInfo = new(quarantine);
            quarantineInfo.Refresh();
            if (!quarantineInfo.Exists ||
                quarantineInfo.LinkTarget is not null ||
                (quarantineInfo.Attributes &
                 FileAttributes.ReparsePoint) != 0)
            {
                return;
            }

            string modelPath = Path.Combine(quarantine, "model.glb");
            if (!File.Exists(modelPath) &&
                !Directory.Exists(modelPath))
            {
                File.Move(tombstone, modelPath);
            }

            if (Directory.Exists(quarantine) &&
                !Directory.Exists(candidate) &&
                !File.Exists(candidate))
            {
                Directory.Move(quarantine, candidate);
                quarantineMoved = false;
            }
        }
        catch (IOException)
        {
            // The owned tombstone retains the bytes for a later bounded pass.
        }
        catch (UnauthorizedAccessException)
        {
            // The owned tombstone retains the bytes for a later bounded pass.
        }
        catch (System.Security.SecurityException)
        {
            // The owned tombstone retains the bytes for a later bounded pass.
        }
    }

    private static void TryDelete(string path, string directory)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            if (Directory.Exists(directory))
            {
                Directory.Delete(directory);
            }
        }
        catch (IOException)
        {
            // Snapshot cleanup is best effort after the read lease closes.
        }
        catch (UnauthorizedAccessException)
        {
            // Snapshot cleanup is best effort after the read lease closes.
        }
    }
}
