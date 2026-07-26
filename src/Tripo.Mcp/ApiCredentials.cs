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
    private bool _storeReadSuppressed;

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
        if (!string.IsNullOrWhiteSpace(_environmentProvider()))
        {
            throw new TripoApiException(
                $"{TripoV3Client.ApiKeyEnvironmentVariable} is set and " +
                "overrides credentials entered in the panel. Remove the " +
                "environment variable and restart the host before saving a key.");
        }

        using IDisposable executionLease = _executionGate.Acquire();
        lock (_gate)
        {
            if (persist)
            {
                _sessionKey = null;
                _storedKey = null;
                _storedKeyPresenceKnown = false;
                _storeReadSuppressed = true;
                _store.Write(apiKey);
                string? persistedKey = _store.Read();
                if (!string.Equals(
                        persistedKey,
                        apiKey,
                        StringComparison.Ordinal))
                {
                    throw new TripoApiException(
                        "The persistent credential mutation completed, but the " +
                        "store did not return the submitted API key. This " +
                        "sidecar will not use a stored credential until a later " +
                        "save is verified.");
                }

                _storedKey = persistedKey;
                _storedKeyPresenceKnown = true;
                _storeReadSuppressed = false;
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
            _storeReadSuppressed = false;
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
            throw new TripoCredentialException(
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
        if (_storeReadSuppressed)
        {
            _storedKey = null;
            _storedKeyPresenceKnown = false;
            return;
        }

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
    private const string DefaultServiceName = "ai.qrost.TripoMCPs.TripoV3";
    private readonly string _serviceName;
    private readonly string _accountName;

    public MacOsKeychainApiKeyStore()
        : this(
            DefaultServiceName,
            string.IsNullOrWhiteSpace(Environment.UserName)
                ? "current-user"
                : Environment.UserName)
    {
    }

    internal MacOsKeychainApiKeyStore(
        string serviceName,
        string accountName)
    {
        if (string.IsNullOrWhiteSpace(serviceName))
        {
            throw new ArgumentException(
                "The Keychain service name cannot be empty.",
                nameof(serviceName));
        }

        if (string.IsNullOrWhiteSpace(accountName))
        {
            throw new ArgumentException(
                "The Keychain account name cannot be empty.",
                nameof(accountName));
        }

        _serviceName = serviceName;
        _accountName = accountName;
    }

    public string BackendName => "macos-keychain";

    public bool UsesWeakerFileFallback => false;

    public string? Read() =>
        MacOsSecurityFramework.ReadGenericPassword(
            _serviceName,
            _accountName);

    public void Write(string apiKey)
    {
        try
        {
            WriteAndVerify(apiKey);
        }
        catch (MacOsKeychainStatusException exception)
            when (exception.RequiresLegacyMigration)
        {
            LegacyMacOsKeychainItem.Delete(
                _serviceName,
                _accountName);
            WriteAndVerify(apiKey);
        }
    }

    public void Delete()
    {
        try
        {
            MacOsSecurityFramework.DeleteGenericPassword(
                _serviceName,
                _accountName);
        }
        catch (MacOsKeychainStatusException exception)
            when (exception.RequiresLegacyMigration)
        {
            LegacyMacOsKeychainItem.Delete(
                _serviceName,
                _accountName);
        }
    }

    private void WriteAndVerify(string apiKey)
    {
        MacOsSecurityFramework.WriteGenericPassword(
            _serviceName,
            _accountName,
            apiKey);
        string? persisted = MacOsSecurityFramework.ReadGenericPassword(
            _serviceName,
            _accountName);
        if (!string.Equals(persisted, apiKey, StringComparison.Ordinal))
        {
            throw new TripoApiException(
                "The macOS Keychain did not return the credential that was " +
                "just written.");
        }
    }
}

internal sealed class MacOsKeychainStatusException : TripoApiException
{
    private const int AuthenticationFailed = -25293;
    private const int InteractionNotAllowed = -25308;

    public MacOsKeychainStatusException(int status)
        : base(
            "The macOS Keychain rejected the credential operation. " +
            "The API key was not exposed or written to a fallback file.",
            innerException: new InvalidOperationException(
                $"Security.framework returned OSStatus {status}."))
    {
        Status = status;
    }

    public int Status { get; }

    public bool RequiresLegacyMigration =>
        Status is AuthenticationFailed or InteractionNotAllowed;
}

internal static class LegacyMacOsKeychainItem
{
    private const string SecurityTool = "/usr/bin/security";
    private static readonly TimeSpan HelperTimeout = TimeSpan.FromSeconds(5);

    public static void Delete(string serviceName, string accountName)
    {
        ProcessStartInfo startInfo = new(SecurityTool)
        {
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        foreach (string argument in new[]
                 {
                     "delete-generic-password",
                     "-a",
                     accountName,
                     "-s",
                     serviceName,
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using Process process = Process.Start(startInfo)
                ?? throw new InvalidOperationException(
                    "The legacy macOS Keychain cleanup helper did not start.");
            using CancellationTokenSource deadline = new(HelperTimeout);
            Task<string> standardOutput = process.StandardOutput.ReadToEndAsync(
                deadline.Token);
            Task<string> standardError = process.StandardError.ReadToEndAsync(
                deadline.Token);
            try
            {
                Task.WhenAll(
                        process.WaitForExitAsync(deadline.Token),
                        standardOutput,
                        standardError)
                    .GetAwaiter()
                    .GetResult();
            }
            catch (OperationCanceledException exception)
            {
                TryKill(process);
                throw new TripoApiException(
                    "The legacy macOS Keychain item could not be replaced " +
                    "within the safety deadline. No API key was passed to the " +
                    "cleanup helper.",
                    innerException: exception);
            }

            if (process.ExitCode is not 0 and not 44)
            {
                throw new TripoApiException(
                    "The legacy macOS Keychain item could not be replaced. " +
                    "Remove the Tripo credential in Keychain Access and try again.");
            }
        }
        catch (Exception exception)
            when (exception is Win32Exception or
                  InvalidOperationException or
                  IOException)
        {
            throw new TripoApiException(
                "The legacy macOS Keychain cleanup helper is unavailable. " +
                "No API key was passed to a subprocess.",
                innerException: exception);
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
}

internal static class MacOsSecurityFramework
{
    private const string SecurityFramework =
        "/System/Library/Frameworks/Security.framework/Security";
    private const string CoreFoundationFramework =
        "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const int Success = 0;
    private const int DuplicateItem = -25299;
    private const int ItemNotFound = -25300;
    private const int MaximumCredentialBytes = 8 * 1024;
    private static readonly UTF8Encoding StrictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static string? ReadGenericPassword(
        string serviceName,
        string accountName)
    {
        DisableUserInteraction();
        byte[] serviceBytes = Encoding.UTF8.GetBytes(serviceName);
        byte[] accountBytes = Encoding.UTF8.GetBytes(accountName);
        IntPtr item = IntPtr.Zero;
        IntPtr passwordData = IntPtr.Zero;
        try
        {
            int status = SecKeychainFindGenericPasswordWithData(
                IntPtr.Zero,
                checked((uint)serviceBytes.Length),
                serviceBytes,
                checked((uint)accountBytes.Length),
                accountBytes,
                out uint passwordLength,
                out passwordData,
                out item);
            if (status == ItemNotFound)
            {
                return null;
            }

            EnsureSuccess(status);
            if (passwordLength == 0)
            {
                return string.Empty;
            }

            if (passwordLength > MaximumCredentialBytes)
            {
                throw new TripoApiException(
                    "The macOS Keychain returned an oversized credential.");
            }

            byte[] passwordBytes = new byte[passwordLength];
            try
            {
                Marshal.Copy(
                    passwordData,
                    passwordBytes,
                    0,
                    passwordBytes.Length);
                try
                {
                    return StrictUtf8.GetString(passwordBytes);
                }
                catch (DecoderFallbackException exception)
                {
                    throw new TripoApiException(
                        "The macOS Keychain credential is not valid UTF-8.",
                        innerException: exception);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(passwordBytes);
            }
        }
        finally
        {
            if (passwordData != IntPtr.Zero)
            {
                _ = SecKeychainItemFreeContent(
                    IntPtr.Zero,
                    passwordData);
            }

            Release(item);
        }
    }

    public static void WriteGenericPassword(
        string serviceName,
        string accountName,
        string password)
    {
        DisableUserInteraction();
        byte[] serviceBytes = Encoding.UTF8.GetBytes(serviceName);
        byte[] accountBytes = Encoding.UTF8.GetBytes(accountName);
        byte[] passwordBytes = StrictUtf8.GetBytes(password);
        if (passwordBytes.Length > MaximumCredentialBytes)
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
            throw new TripoApiException(
                "The API key is too large for the macOS Keychain.");
        }

        IntPtr item = IntPtr.Zero;
        try
        {
            int status = SecKeychainFindGenericPasswordWithoutData(
                IntPtr.Zero,
                checked((uint)serviceBytes.Length),
                serviceBytes,
                checked((uint)accountBytes.Length),
                accountBytes,
                IntPtr.Zero,
                IntPtr.Zero,
                out item);
            if (status == Success)
            {
                EnsureSuccess(
                    SecKeychainItemModifyAttributesAndData(
                        item,
                        IntPtr.Zero,
                        checked((uint)passwordBytes.Length),
                        passwordBytes));
                return;
            }

            if (status != ItemNotFound)
            {
                throw NativeFailure(status);
            }

            status = SecKeychainAddGenericPassword(
                IntPtr.Zero,
                checked((uint)serviceBytes.Length),
                serviceBytes,
                checked((uint)accountBytes.Length),
                accountBytes,
                checked((uint)passwordBytes.Length),
                passwordBytes,
                out item);
            if (status == DuplicateItem)
            {
                Release(item);
                item = IntPtr.Zero;
                EnsureSuccess(
                    SecKeychainFindGenericPasswordWithoutData(
                        IntPtr.Zero,
                        checked((uint)serviceBytes.Length),
                        serviceBytes,
                        checked((uint)accountBytes.Length),
                        accountBytes,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        out item));
                status = SecKeychainItemModifyAttributesAndData(
                    item,
                    IntPtr.Zero,
                    checked((uint)passwordBytes.Length),
                    passwordBytes);
            }

            EnsureSuccess(status);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
            Release(item);
        }
    }

    public static void DeleteGenericPassword(
        string serviceName,
        string accountName)
    {
        DisableUserInteraction();
        byte[] serviceBytes = Encoding.UTF8.GetBytes(serviceName);
        byte[] accountBytes = Encoding.UTF8.GetBytes(accountName);
        IntPtr item = IntPtr.Zero;
        try
        {
            int status = SecKeychainFindGenericPasswordWithoutData(
                IntPtr.Zero,
                checked((uint)serviceBytes.Length),
                serviceBytes,
                checked((uint)accountBytes.Length),
                accountBytes,
                IntPtr.Zero,
                IntPtr.Zero,
                out item);
            if (status == ItemNotFound)
            {
                return;
            }

            EnsureSuccess(status);
            EnsureSuccess(SecKeychainItemDelete(item));
        }
        finally
        {
            Release(item);
        }
    }

    private static void EnsureSuccess(int status)
    {
        if (status != Success)
        {
            throw NativeFailure(status);
        }
    }

    private static void DisableUserInteraction() =>
        EnsureSuccess(SecKeychainSetUserInteractionAllowed(false));

    private static void Release(IntPtr item)
    {
        if (item != IntPtr.Zero)
        {
            CFRelease(item);
        }
    }

    private static MacOsKeychainStatusException NativeFailure(int status) =>
        new MacOsKeychainStatusException(status);

    [DllImport(
        SecurityFramework,
        EntryPoint = "SecKeychainFindGenericPassword",
        SetLastError = false)]
    private static extern int SecKeychainFindGenericPasswordWithData(
        IntPtr keychainOrArray,
        uint serviceNameLength,
        byte[] serviceName,
        uint accountNameLength,
        byte[] accountName,
        out uint passwordLength,
        out IntPtr passwordData,
        out IntPtr item);

    [DllImport(
        SecurityFramework,
        EntryPoint = "SecKeychainFindGenericPassword",
        SetLastError = false)]
    private static extern int SecKeychainFindGenericPasswordWithoutData(
        IntPtr keychainOrArray,
        uint serviceNameLength,
        byte[] serviceName,
        uint accountNameLength,
        byte[] accountName,
        IntPtr passwordLength,
        IntPtr passwordData,
        out IntPtr item);

    [DllImport(SecurityFramework, SetLastError = false)]
    private static extern int SecKeychainAddGenericPassword(
        IntPtr keychain,
        uint serviceNameLength,
        byte[] serviceName,
        uint accountNameLength,
        byte[] accountName,
        uint passwordLength,
        byte[] passwordData,
        out IntPtr item);

    [DllImport(SecurityFramework, SetLastError = false)]
    private static extern int SecKeychainItemModifyAttributesAndData(
        IntPtr item,
        IntPtr attributes,
        uint dataLength,
        byte[] data);

    [DllImport(SecurityFramework, SetLastError = false)]
    private static extern int SecKeychainItemDelete(IntPtr item);

    [DllImport(SecurityFramework, SetLastError = false)]
    private static extern int SecKeychainItemFreeContent(
        IntPtr attributes,
        IntPtr data);

    [DllImport(SecurityFramework, SetLastError = false)]
    private static extern int SecKeychainSetUserInteractionAllowed(
        [MarshalAs(UnmanagedType.I1)] bool state);

    [DllImport(CoreFoundationFramework, SetLastError = false)]
    private static extern void CFRelease(IntPtr value);
}

internal sealed class WindowsCredentialManagerApiKeyStore :
    IPlatformApiKeyStore
{
    private const uint CredentialTypeGeneric = 1;
    private const uint CredentialPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;
    private const int MaximumCredentialBytes = 5120;
    private const int MaximumTargetNameCharacters = 32_767;
    private static readonly string DefaultTargetName =
        "TripoMCPs/TripoV3/" +
        (string.IsNullOrWhiteSpace(Environment.UserName)
            ? "current-user"
            : Environment.UserName);
    private readonly string _targetName;

    public WindowsCredentialManagerApiKeyStore()
        : this(DefaultTargetName)
    {
    }

    internal WindowsCredentialManagerApiKeyStore(string targetName)
    {
        if (string.IsNullOrWhiteSpace(targetName) ||
            targetName.Contains('\0') ||
            targetName.Length > MaximumTargetNameCharacters)
        {
            throw new ArgumentException(
                "A valid Windows Credential Manager target is required.",
                nameof(targetName));
        }

        _targetName = targetName;
    }

    public string BackendName => "windows-credential-manager";

    public bool UsesWeakerFileFallback => false;

    public string? Read()
    {
        if (!CredRead(
                _targetName,
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
                TargetName = _targetName,
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
            try
            {
                ZeroUnmanagedMemory(blob, bytes.Length);
            }
            finally
            {
                Marshal.FreeCoTaskMem(blob);
            }
        }
    }

    public void Delete()
    {
        if (!CredDelete(_targetName, CredentialTypeGeneric, 0))
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

    private static void ZeroUnmanagedMemory(
        IntPtr pointer,
        int length)
    {
        for (int index = 0; index < length; index++)
        {
            Marshal.WriteByte(pointer, index, 0);
        }
    }

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
