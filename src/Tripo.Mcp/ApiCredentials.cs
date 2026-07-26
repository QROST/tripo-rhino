using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Tripo.Mcp;

public interface ITripoApiKeyProvider
{
    string? GetApiKey();
}

public interface ITripoCredentialService
{
    Tripo.Bridge.HostControlCredentialStatusReceipt GetStatus();

    Tripo.Bridge.HostControlCredentialMutationReceipt SetApiKey(
        string apiKey,
        bool persist);

    Tripo.Bridge.HostControlCredentialMutationReceipt ClearApiKey();
}

public sealed class ApiCredentialService :
    ITripoApiKeyProvider,
    ITripoCredentialService
{
    private readonly object _gate = new();
    private readonly Func<string?> _environmentProvider;
    private readonly IPlatformApiKeyStore _store;
    private readonly Tripo.Bridge.ICredentialWorkflowExecutionGate _executionGate;
    private string? _sessionKey;
    private string? _storedKey;
    private bool _storedKeyPresenceKnown;

    public ApiCredentialService()
        : this(
            () => Environment.GetEnvironmentVariable(
                TripoV3Client.ApiKeyEnvironmentVariable),
            PlatformApiKeyStore.Create(),
            new Tripo.Bridge.CredentialWorkflowExecutionGate())
    {
    }

    internal ApiCredentialService(
        Func<string?> environmentProvider,
        IPlatformApiKeyStore store,
        Tripo.Bridge.ICredentialWorkflowExecutionGate? executionGate = null)
    {
        _environmentProvider = environmentProvider ??
            throw new ArgumentNullException(nameof(environmentProvider));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _executionGate =
            executionGate ?? NoOpCredentialWorkflowExecutionGate.Instance;
    }

    public string? GetApiKey()
    {
        string? environmentKey = _environmentProvider();
        if (!string.IsNullOrWhiteSpace(environmentKey))
        {
            ValidateApiKey(environmentKey);
            return environmentKey;
        }

        lock (_gate)
        {
            if (_sessionKey is not null)
            {
                return _sessionKey;
            }

            ReloadStore();
            return _storedKey;
        }
    }

    public Tripo.Bridge.HostControlCredentialStatusReceipt GetStatus()
    {
        string? environmentKey = _environmentProvider();
        if (!string.IsNullOrWhiteSpace(environmentKey))
        {
            ValidateApiKey(environmentKey);
            lock (_gate)
            {
                return CreateStatus(
                    hasApiKey: true,
                    source: "environment",
                    includeStoredState: false);
            }
        }

        lock (_gate)
        {
            if (_sessionKey is not null)
            {
                return CreateStatus(
                    hasApiKey: true,
                    source: "session",
                    includeStoredState: false);
            }

            try
            {
                ReloadStore();
            }
            catch (TripoApiException)
            {
                _storedKey = null;
                _storedKeyPresenceKnown = false;
                return CreateStatus(hasApiKey: false, source: "none");
            }

            if (_storedKey is not null)
            {
                return CreateStatus(hasApiKey: true, source: "store");
            }

            return CreateStatus(hasApiKey: false, source: "none");
        }
    }

    public Tripo.Bridge.HostControlCredentialMutationReceipt SetApiKey(
        string apiKey,
        bool persist)
    {
        ValidateApiKey(apiKey);
        using IDisposable executionLease = _executionGate.Acquire();
        lock (_gate)
        {
            if (persist)
            {
                _store.Write(apiKey);
                _storedKey = apiKey;
                _storedKeyPresenceKnown = true;
                _sessionKey = null;
            }
            else
            {
                _sessionKey = apiKey;
                _storedKey = null;
                _storedKeyPresenceKnown = false;
            }

            return new Tripo.Bridge.HostControlCredentialMutationReceipt(
                GetStatusWhileLocked(includeStoredState: persist));
        }
    }

    public Tripo.Bridge.HostControlCredentialMutationReceipt ClearApiKey()
    {
        using IDisposable executionLease = _executionGate.Acquire();
        lock (_gate)
        {
            _sessionKey = null;
            _store.Delete();
            _storedKey = null;
            _storedKeyPresenceKnown = true;
            return new Tripo.Bridge.HostControlCredentialMutationReceipt(
                GetStatusWhileLocked(includeStoredState: true));
        }
    }

    internal static void ValidateApiKey(string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey) ||
            apiKey.Length > 2048 ||
            apiKey.Any(character =>
                char.IsWhiteSpace(character) || char.IsControl(character)))
        {
            throw new TripoApiException(
                "The Tripo API key is missing or is not a valid opaque credential.");
        }
    }

    private Tripo.Bridge.HostControlCredentialStatusReceipt GetStatusWhileLocked(
        bool includeStoredState)
    {
        string? environmentKey = _environmentProvider();
        if (!string.IsNullOrWhiteSpace(environmentKey))
        {
            ValidateApiKey(environmentKey);
            return CreateStatus(
                hasApiKey: true,
                source: "environment",
                includeStoredState);
        }

        if (_sessionKey is not null)
        {
            return CreateStatus(
                hasApiKey: true,
                source: "session",
                includeStoredState);
        }

        if (_storedKey is not null)
        {
            return CreateStatus(
                hasApiKey: true,
                source: "store",
                includeStoredState);
        }

        return CreateStatus(
            hasApiKey: false,
            source: "none",
            includeStoredState);
    }

    private Tripo.Bridge.HostControlCredentialStatusReceipt CreateStatus(
        bool hasApiKey,
        string source,
        bool includeStoredState = true) =>
        new(
            hasApiKey,
            source,
            includeStoredState && _storedKey is not null,
            includeStoredState &&
            _storedKeyPresenceKnown &&
            _storedKey is not null,
            _store.BackendName,
            _store.UsesWeakerFileFallback,
            includeStoredState && _storedKeyPresenceKnown);

    private void ReloadStore()
    {
        string? stored = _store.Read();
        if (stored is not null)
        {
            ValidateApiKey(stored);
        }

        _storedKey = stored;
        _storedKeyPresenceKnown = true;
    }
}

internal interface IPlatformApiKeyStore
{
    string BackendName { get; }

    bool UsesWeakerFileFallback { get; }

    string? Read();

    void Write(string apiKey);

    void Delete();
}

internal static class PlatformApiKeyStore
{
    public static IPlatformApiKeyStore Create()
    {
        if (OperatingSystem.IsWindows())
        {
            return new WindowsCredentialManagerApiKeyStore();
        }

        if (OperatingSystem.IsMacOS())
        {
            return new MacOsKeychainApiKeyStore();
        }

        return new PrivateFileApiKeyStore();
    }
}

internal sealed class PrivateFileApiKeyStore : IPlatformApiKeyStore
{
    private const string FileName = "tripo-v3-api-key";
    private const int MaximumStoredBytes = 8 * 1024;
    private const int MaximumStoredCharacters = 2048;

    public string BackendName => "private-file";

    public bool UsesWeakerFileFallback => true;

    public string? Read()
    {
        string path = GetPath();
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                throw new TripoApiException(
                    "The fallback credential file is a symbolic link and was refused.");
            }

            Tripo.Bridge.BridgePaths.SetPrivateFileMode(path);
            using FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.SequentialScan);
            if (stream.Length > MaximumStoredBytes)
            {
                throw new TripoApiException(
                    "The fallback credential file is oversized and was refused.");
            }

            using StreamReader reader = new(
                stream,
                new UTF8Encoding(
                    encoderShouldEmitUTF8Identifier: false,
                    throwOnInvalidBytes: true),
                detectEncodingFromByteOrderMarks: false,
                bufferSize: 4096,
                leaveOpen: false);
            char[] characters = new char[MaximumStoredCharacters + 1];
            int count = reader.ReadBlock(characters, 0, characters.Length);
            if (count > MaximumStoredCharacters || reader.Peek() >= 0)
            {
                throw new TripoApiException(
                    "The fallback credential file is oversized and was refused.");
            }

            return new string(characters, 0, count);
        }
        catch (IOException exception)
        {
            throw StoreFailure(exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw StoreFailure(exception);
        }
        catch (DecoderFallbackException exception)
        {
            throw StoreFailure(exception);
        }
    }

    public void Write(string apiKey)
    {
        string path = GetPath();
        string temporaryPath =
            path + "." +
            Convert.ToHexString(RandomNumberGenerator.GetBytes(8)) +
            ".tmp";
        try
        {
            File.WriteAllText(
                temporaryPath,
                apiKey,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            Tripo.Bridge.BridgePaths.SetPrivateFileMode(temporaryPath);
            File.Move(temporaryPath, path, true);
        }
        catch (IOException exception)
        {
            throw StoreFailure(exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw StoreFailure(exception);
        }
        finally
        {
            Tripo.Bridge.BridgePaths.TryDelete(temporaryPath);
        }
    }

    public void Delete()
    {
        string path = GetPath();
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException exception)
        {
            throw StoreFailure(exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw StoreFailure(exception);
        }
    }

    private static string GetPath()
    {
        string directory = Path.Combine(
            Tripo.Bridge.BridgePaths.GetRootDirectory(),
            "secrets");
        Tripo.Bridge.BridgePaths.EnsurePrivateDirectory(directory);
        return Path.Combine(directory, FileName);
    }

    private static TripoApiException StoreFailure(Exception exception) =>
        new(
            "The fallback credential file could not be accessed. " +
            "No weaker or alternate location was used.",
            innerException: exception);
}

internal sealed class MacOsKeychainApiKeyStore : IPlatformApiKeyStore
{
    private const string SecurityTool = "/usr/bin/security";
    private const string ServiceName = "ai.qrost.TripoMCPs.TripoV3";
    internal const int MaximumHelperOutputCharacters = 8 * 1024;
    private static readonly TimeSpan HelperTimeout = TimeSpan.FromSeconds(5);
    private static readonly string AccountName =
        string.IsNullOrWhiteSpace(Environment.UserName)
            ? "current-user"
            : Environment.UserName;

    public string BackendName => "macos-keychain";

    public bool UsesWeakerFileFallback => false;

    public string? Read()
    {
        SecurityResult result = Run(
            [
                "find-generic-password",
                "-a",
                AccountName,
                "-s",
                ServiceName,
                "-w",
            ],
            standardInput: null);
        if (result.ExitCode == 44)
        {
            return null;
        }

        EnsureSuccess(result);
        return result.StandardOutput.TrimEnd('\r', '\n');
    }

    public void Write(string apiKey)
    {
        SecurityResult result = Run(
            [
                "add-generic-password",
                "-U",
                "-a",
                AccountName,
                "-s",
                ServiceName,
                "-w",
            ],
            apiKey);
        EnsureSuccess(result);
    }

    public void Delete()
    {
        SecurityResult result = Run(
            [
                "delete-generic-password",
                "-a",
                AccountName,
                "-s",
                ServiceName,
            ],
            standardInput: null);
        if (result.ExitCode != 44)
        {
            EnsureSuccess(result);
        }
    }

    private static SecurityResult Run(
        IReadOnlyList<string> arguments,
        string? standardInput)
    {
        ProcessStartInfo startInfo = new(SecurityTool)
        {
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardInput = standardInput is not null,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using Process process = Process.Start(startInfo)
                ?? throw new InvalidOperationException(
                    "The macOS Keychain helper did not start.");
            if (standardInput is not null)
            {
                process.StandardInput.WriteLine(standardInput);
                process.StandardInput.Close();
            }

            using CancellationTokenSource deadline = new(HelperTimeout);
            Task<string> standardOutputTask = ReadBoundedAsync(
                process.StandardOutput,
                deadline.Token);
            Task<string> standardErrorTask = ReadBoundedAsync(
                process.StandardError,
                deadline.Token);
            try
            {
                Task.WhenAll(
                        process.WaitForExitAsync(deadline.Token),
                        standardOutputTask,
                        standardErrorTask)
                    .GetAwaiter()
                    .GetResult();
            }
            catch (OperationCanceledException exception)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill();
                    }
                }
                catch (InvalidOperationException)
                {
                    // The helper exited between the checks.
                }

                throw new TripoApiException(
                    "The macOS Keychain helper did not respond. " +
                    "The API key was not exposed or written to a fallback file.",
                    innerException: exception);
            }
            catch (TripoApiException)
            {
                TryKill(process);
                throw;
            }

            string standardOutput = standardOutputTask
                .GetAwaiter()
                .GetResult();
            _ = standardErrorTask.GetAwaiter().GetResult();
            return new SecurityResult(process.ExitCode, standardOutput);
        }
        catch (Exception exception)
            when (exception is Win32Exception or
                  InvalidOperationException or
                  IOException)
        {
            throw new TripoApiException(
                "The macOS Keychain helper is unavailable. " +
                "The API key was not persisted.",
                innerException: exception);
        }
    }

    internal static async Task<string> ReadBoundedAsync(
        TextReader reader,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        char[] buffer = new char[1024];
        StringBuilder output = new();
        while (true)
        {
            int count = await reader.ReadAsync(
                    buffer.AsMemory(),
                    cancellationToken)
                .ConfigureAwait(false);
            if (count == 0)
            {
                return output.ToString();
            }

            if (count > MaximumHelperOutputCharacters - output.Length)
            {
                throw new TripoApiException(
                    "The macOS Keychain helper returned oversized output. " +
                    "No output was exposed or persisted.");
            }

            output.Append(buffer, 0, count);
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill();
            }
        }
        catch (InvalidOperationException)
        {
            // The helper exited between the checks.
        }
    }

    private static void EnsureSuccess(SecurityResult result)
    {
        if (result.ExitCode != 0)
        {
            throw new TripoApiException(
                "The macOS Keychain rejected the credential operation. " +
                "The API key was not exposed or written to a fallback file.");
        }
    }

    private sealed record SecurityResult(int ExitCode, string StandardOutput);
}

internal sealed class WindowsCredentialManagerApiKeyStore :
    IPlatformApiKeyStore
{
    private const uint CredentialTypeGeneric = 1;
    private const uint CredentialPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;
    private const int MaximumCredentialBytes = 5120;
    private static readonly string TargetName =
        "TripoMCPs/TripoV3/" +
        (string.IsNullOrWhiteSpace(Environment.UserName)
            ? "current-user"
            : Environment.UserName);

    public string BackendName => "windows-credential-manager";

    public bool UsesWeakerFileFallback => false;

    public string? Read()
    {
        if (!CredRead(
                TargetName,
                CredentialTypeGeneric,
                0,
                out IntPtr credentialPointer))
        {
            int error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound)
            {
                return null;
            }

            throw NativeFailure(error);
        }

        try
        {
            NativeCredential credential =
                Marshal.PtrToStructure<NativeCredential>(credentialPointer);
            if (credential.CredentialBlobSize == 0)
            {
                return null;
            }

            if (credential.CredentialBlobSize > MaximumCredentialBytes)
            {
                throw new TripoApiException(
                    "Windows Credential Manager returned an oversized credential.");
            }

            byte[] bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(
                credential.CredentialBlob,
                bytes,
                0,
                bytes.Length);
            try
            {
                return Encoding.UTF8.GetString(bytes);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
        finally
        {
            CredFree(credentialPointer);
        }
    }

    public void Write(string apiKey)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(apiKey);
        if (bytes.Length > MaximumCredentialBytes)
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw new TripoApiException(
                "The API key is too large for Windows Credential Manager.");
        }

        IntPtr blob = Marshal.AllocCoTaskMem(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            NativeCredential credential = new()
            {
                Type = CredentialTypeGeneric,
                TargetName = TargetName,
                CredentialBlobSize = (uint)bytes.Length,
                CredentialBlob = blob,
                Persist = CredentialPersistLocalMachine,
                UserName = Environment.UserName,
            };
            if (!CredWrite(ref credential, 0))
            {
                throw NativeFailure(Marshal.GetLastWin32Error());
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            Marshal.FreeCoTaskMem(blob);
        }
    }

    public void Delete()
    {
        if (!CredDelete(TargetName, CredentialTypeGeneric, 0))
        {
            int error = Marshal.GetLastWin32Error();
            if (error != ErrorNotFound)
            {
                throw NativeFailure(error);
            }
        }
    }

    private static TripoApiException NativeFailure(int error) =>
        new(
            "Windows Credential Manager rejected the credential operation. " +
            "The API key was not written to a fallback file.",
            innerException: new Win32Exception(error));

    [DllImport(
        "advapi32.dll",
        EntryPoint = "CredReadW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(
        string target,
        uint type,
        uint flags,
        out IntPtr credential);

    [DllImport(
        "advapi32.dll",
        EntryPoint = "CredWriteW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(
        ref NativeCredential credential,
        uint flags);

    [DllImport(
        "advapi32.dll",
        EntryPoint = "CredDeleteW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(
        string target,
        uint type,
        uint flags);

    [DllImport("advapi32.dll", SetLastError = false)]
    private static extern void CredFree(IntPtr credential);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }
}
