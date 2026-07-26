namespace Tripo.Bridge;

public interface ICredentialWorkflowExecutionGate
{
    IDisposable Acquire();
}

public sealed class CredentialWorkflowExecutionGate :
    ICredentialWorkflowExecutionGate
{
    private const int MaximumLockFileBytes = 64;

    private readonly string _directory;
    private readonly string _lockPath;

    public CredentialWorkflowExecutionGate(string? rootDirectory = null)
    {
        string root = Path.GetFullPath(
            rootDirectory ?? BridgePaths.GetRootDirectory());
        _directory = Path.Combine(root, "operations");
        _lockPath = Path.Combine(
            _directory,
            ".credential-workflow-execution.lock");
    }

    public IDisposable Acquire()
    {
        try
        {
            EnsurePrivateNonReparseDirectory(_directory);
            ValidatePrivateRegularLockFile(_lockPath);
            FileStream stream = new(
                _lockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.WriteThrough);
            try
            {
                BridgePaths.SetPrivateFileMode(_lockPath);
                return stream;
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }
        catch (Exception exception)
            when (exception is IOException or
                  UnauthorizedAccessException)
        {
            throw new BridgeCallException(
                "credential_workflow_unavailable",
                "Another credential mutation or paid workflow is active, or " +
                "its private local execution lock is invalid. Retry only after " +
                "the current operation is reconciled.",
                exception);
        }
    }

    private static void EnsurePrivateNonReparseDirectory(string path)
    {
        if (Directory.Exists(path) && IsReparsePoint(path))
        {
            throw new InvalidDataException(
                "The credential/workflow directory is a symbolic link or " +
                "reparse point.");
        }

        BridgePaths.EnsurePrivateDirectory(path);
        if (IsReparsePoint(path))
        {
            throw new InvalidDataException(
                "The credential/workflow directory is a symbolic link or " +
                "reparse point.");
        }
    }

    private static void ValidatePrivateRegularLockFile(string path)
    {
        if (Directory.Exists(path) || IsFileLinkOrReparsePoint(path))
        {
            throw new InvalidDataException(
                "The credential/workflow execution lock is not a private " +
                "regular file.");
        }

        if (!File.Exists(path))
        {
            return;
        }

        FileInfo info = new(path);
        if (info.Length > MaximumLockFileBytes)
        {
            throw new InvalidDataException(
                "The credential/workflow execution lock exceeded its size " +
                "limit.");
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
                    "The credential/workflow execution lock is accessible " +
                    "outside its owner.");
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

    private static bool IsReparsePoint(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
}
