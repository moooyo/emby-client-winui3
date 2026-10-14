using System.Runtime.Versioning;
using System.Security;
using Microsoft.Win32;

namespace EmbyClient.App.Services;

public sealed record StartupRegistrationStatus(bool IsSupported, bool IsEnabled, string? Error = null);

/// <summary>Manages the current installation's optional per-user startup registration.</summary>
public sealed class StartupRegistrationService
{
    private const string UnavailableError = "Startup registration is unavailable for this launch.";
    private const string ReadError = "Could not read startup registration. Try again.";
    private const string ChangeError = "Could not change startup registration. Try again.";
    private const string ConflictError = "Startup registration belongs to a different installation.";
    private static readonly SemaphoreSlim RegistrationGate = new(1, 1);
    private readonly IStartupRegistrationBackend _backend;
    private readonly Lazy<string?> _ownedCommand;

    public StartupRegistrationService()
    {
        _backend = new WindowsStartupRegistrationBackend();
        // Resolve the launch path only when queried; construction performs no platform I/O.
        _ownedCommand = new(static () => CreateOwnedCommand(Environment.ProcessPath));
    }

    internal StartupRegistrationService(string? executablePath, IStartupRegistrationBackend backend)
    {
        ArgumentNullException.ThrowIfNull(backend);
        _backend = backend;
        _ownedCommand = new(() => CreateOwnedCommand(executablePath));
    }

    public StartupRegistrationStatus GetStatus()
    {
        RegistrationGate.Wait();
        try
        {
            var command = GetOwnedCommand();
            return command is null ? new(false, false, UnavailableError) : ReadStatus(command);
        }
        finally
        {
            RegistrationGate.Release();
        }
    }

    public async Task<StartupRegistrationStatus> SetEnabledAsync(
        bool enabled, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await RegistrationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var command = GetOwnedCommand();
            if (command is null) return new(false, false, UnavailableError);
            var before = ReadStatus(command);
            cancellationToken.ThrowIfCancellationRequested();
            if (before.Error is not null || before.IsEnabled == enabled) return before;

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!_backend.TrySetEnabled(command, enabled, cancellationToken))
                {
                    return new(true, false, ConflictError);
                }

                // Complete readback after a mutation even if cancellation arrives during the write.
                var after = ReadStatus(command);
                return after.Error is not null || after.IsEnabled == enabled
                    ? after : after with { Error = ChangeError };
            }
            catch (Exception exception) when (IsBackendFailure(exception))
            {
                // A backend can fail after writing; report the state that can still be verified.
                return ReadStatus(command) with { Error = ChangeError };
            }
        }
        finally
        {
            RegistrationGate.Release();
        }
    }

    private string? GetOwnedCommand() => _backend.IsSupported ? _ownedCommand.Value : null;

    private static string? CreateOwnedCommand(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)
            || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            || path.Any(character => character == '"' || char.IsControl(character))) return null;
        return $"\"{path}\"";
    }

    private StartupRegistrationStatus ReadStatus(string command)
    {
        try
        {
            var value = _backend.ReadValue();
            if (!value.Exists) return new(true, false);
            return IsOwnedCommand(value.Command, command)
                ? new(true, true) : new(true, false, ConflictError);
        }
        catch (Exception exception) when (IsBackendFailure(exception))
        {
            return new(true, false, ReadError);
        }
    }

    private static bool IsOwnedCommand(string? actual, string expected) =>
        string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);

    private static bool IsBackendFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or SecurityException or PlatformNotSupportedException;

    private sealed class WindowsStartupRegistrationBackend : IStartupRegistrationBackend
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "Lumen.EmbyClient";
        private static readonly object RegistryGate = new();

        public bool IsSupported => OperatingSystem.IsWindows();

        public StartupRegistrationValue ReadValue()
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            lock (RegistryGate)
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
                return ReadValue(key);
            }
        }

        public bool TrySetEnabled(string command, bool enabled, CancellationToken cancellationToken)
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            lock (RegistryGate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var existingKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
                if (existingKey is not null) return TryChangeValue(existingKey, command, enabled, cancellationToken);
                if (!enabled) return true;

                cancellationToken.ThrowIfCancellationRequested();
                using var createdKey = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
                    ?? throw new IOException("Could not open the per-user startup registration key.");
                return TryChangeValue(createdKey, command, enabled, cancellationToken);
            }
        }

        [SupportedOSPlatform("windows")]
        private static bool TryChangeValue(
            RegistryKey key, string command, bool enabled, CancellationToken cancellationToken)
        {
            var current = ReadValue(key);
            if (current.Exists && !IsOwnedCommand(current.Command, command)) return false;
            if (current.Exists == enabled) return true;

            cancellationToken.ThrowIfCancellationRequested();
            if (enabled) key.SetValue(ValueName, command, RegistryValueKind.String);
            else key.DeleteValue(ValueName, throwOnMissingValue: false);
            return true;
        }

        [SupportedOSPlatform("windows")]
        private static StartupRegistrationValue ReadValue(RegistryKey? key)
        {
            if (key is null) return default;
            if (!key.GetValueNames().Contains(ValueName, StringComparer.OrdinalIgnoreCase)) return default;
            // Non-string and expanding values are foreign registrations even when their text looks similar.
            if (key.GetValueKind(ValueName) != RegistryValueKind.String) return new(true);
            return new(true, key.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string);
        }
    }
}

/// <summary>A present value without a command represents a foreign registry value type.</summary>
internal readonly record struct StartupRegistrationValue(bool Exists, string? Command = null);

internal interface IStartupRegistrationBackend
{
    bool IsSupported { get; }
    StartupRegistrationValue ReadValue();
    /// <summary>Returns false for a foreign value and checks cancellation before any conditional mutation.</summary>
    bool TrySetEnabled(string command, bool enabled, CancellationToken cancellationToken);
}
