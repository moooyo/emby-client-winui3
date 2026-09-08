using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Windows.Security.Cryptography;
using Windows.Security.Cryptography.DataProtection;

namespace EmbyClient.App.Services;

public sealed class AppSettings
{
    [JsonRequired]
    public string DeviceId { get; set; } = Guid.NewGuid().ToString("N");
    public string? LastAccountKey { get; set; }
    public string Theme { get; set; } = "System";
    [JsonRequired]
    public List<SavedAccount> Accounts { get; set; } = [];
}

public sealed class SavedAccount
{
    public string Key { get; set; } = string.Empty;
    public string ApiRoot { get; set; } = string.Empty;
    public string ServerId { get; set; } = string.Empty;
    public string ServerName { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string ProtectedToken { get; set; } = string.Empty;
}

public enum AccountStoreError
{
    SettingsReadFailed,
    SettingsCorrupt,
    SettingsWriteFailed,
    TokenProtectionFailed,
    TokenUnprotectionFailed
}

public sealed class AccountStoreException(AccountStoreError error, string message) : Exception(message)
{
    public AccountStoreError Error { get; } = error;
}

/// <summary>Persists account metadata and Windows-user-protected credentials without owning login state.</summary>
public sealed class AccountStore
{
    private const string TokenPrefix = "dpapi-user-v1:";
    private const int MaximumSettingsBytes = 4 * 1024 * 1024;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public AccountStore(string? settingsPath = null)
    {
        SettingsPath = Path.GetFullPath(settingsPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EmbyClient.Windows", "settings.json"));
    }

    public string SettingsPath { get; }

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(SettingsPath))
            {
                var settings = new AppSettings();
                if (await WriteCoreAsync(settings, cancellationToken, overwrite: false).ConfigureAwait(false))
                {
                    return settings;
                }
            }

            try
            {
                await using var stream = new FileStream(SettingsPath, FileMode.Open, FileAccess.Read,
                    FileShare.Read, 16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                if (stream.Length > MaximumSettingsBytes)
                {
                    throw CorruptSettings();
                }

                var settings = await JsonSerializer.DeserializeAsync(stream,
                    SettingsJsonContext.Default.AppSettings, cancellationToken).ConfigureAwait(false);
                if (settings is null) throw CorruptSettings();
                ValidateSettings(settings);
                return settings;
            }
            catch (JsonException)
            {
                throw CorruptSettings();
            }
            catch (IOException)
            {
                throw new AccountStoreException(AccountStoreError.SettingsReadFailed,
                    "Saved account settings could not be read. Check access to the application data folder.");
            }
            catch (UnauthorizedAccessException)
            {
                throw new AccountStoreException(AccountStoreError.SettingsReadFailed,
                    "Access to saved account settings was denied.");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ValidateSettings(settings);
            await WriteCoreAsync(settings, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string> ProtectTokenAsync(string token, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var provider = new DataProtectionProvider("LOCAL=user");
            var input = CryptographicBuffer.ConvertStringToBinary(token, BinaryStringEncoding.Utf8);
            var protectedBuffer = await provider.ProtectAsync(input).AsTask(cancellationToken).ConfigureAwait(false);
            return TokenPrefix + CryptographicBuffer.EncodeToBase64String(protectedBuffer);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            throw new AccountStoreException(AccountStoreError.TokenProtectionFailed,
                "Windows could not protect the access token for the current user. The account has not been remembered.");
        }
    }

    public async Task<string> UnprotectTokenAsync(string protectedToken,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(protectedToken) || !HasProtectedTokenFormat(protectedToken))
        {
            throw UnprotectFailure();
        }

        try
        {
            var provider = new DataProtectionProvider();
            var input = CryptographicBuffer.DecodeFromBase64String(protectedToken[TokenPrefix.Length..]);
            var clearBuffer = await provider.UnprotectAsync(input).AsTask(cancellationToken).ConfigureAwait(false);
            var token = CryptographicBuffer.ConvertBinaryToString(BinaryStringEncoding.Utf8, clearBuffer);
            if (string.IsNullOrWhiteSpace(token)) throw UnprotectFailure();
            return token;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            throw UnprotectFailure();
        }
    }

    public static string CreateAccountKey(string serverId, string userId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        var identity = FormattableString.Invariant($"emby-account-v1:{serverId.Length}:{serverId}{userId.Length}:{userId}");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }

    private async Task<bool> WriteCoreAsync(AppSettings settings, CancellationToken cancellationToken,
        bool overwrite = true)
    {
        var folder = Path.GetDirectoryName(SettingsPath)!;
        var temporaryPath = Path.Combine(folder, $".{Path.GetFileName(SettingsPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(settings, SettingsJsonContext.Default.AppSettings);
            if (bytes.Length > MaximumSettingsBytes)
            {
                throw new AccountStoreException(AccountStoreError.SettingsWriteFailed,
                    "Saved account settings exceed the application size limit.");
            }

            Directory.CreateDirectory(folder);
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 16 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                File.Move(temporaryPath, SettingsPath, overwrite);
                return true;
            }
            catch (IOException) when (!overwrite && File.Exists(SettingsPath))
            {
                // Another process created the initial settings. Load its persisted device identity.
                return false;
            }
            catch (UnauthorizedAccessException) when (!overwrite && File.Exists(SettingsPath))
            {
                // An initial-settings winner may already have a read handle open on Windows.
                return false;
            }
        }
        catch (IOException)
        {
            throw new AccountStoreException(AccountStoreError.SettingsWriteFailed,
                "Saved account settings could not be written. The existing settings have been retained.");
        }
        catch (UnauthorizedAccessException)
        {
            throw new AccountStoreException(AccountStoreError.SettingsWriteFailed,
                "Access to save account settings was denied. The existing settings have been retained.");
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (IOException)
            {
                // The destination is unaffected if temporary-file cleanup is unavailable.
            }
            catch (UnauthorizedAccessException)
            {
                // Temporary files contain protected tokens only, never clear credentials.
            }
        }
    }

    private static void ValidateSettings(AppSettings settings)
    {
        if (!Guid.TryParseExact(settings.DeviceId, "N", out var deviceId) || deviceId == Guid.Empty
            || settings.Accounts is null || settings.Theme is not ("System" or "Light" or "Dark"))
        {
            throw CorruptSettings();
        }

        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var account in settings.Accounts)
        {
            if (account is null || string.IsNullOrWhiteSpace(account.ServerId) || string.IsNullOrWhiteSpace(account.UserId)
                || !string.Equals(account.Key, CreateAccountKey(account.ServerId, account.UserId), StringComparison.Ordinal)
                || !keys.Add(account.Key) || !IsSafeApiRoot(account.ApiRoot)
                || account.ProtectedToken is null
                || (account.ProtectedToken.Length > 0 && !HasProtectedTokenFormat(account.ProtectedToken)))
            {
                throw CorruptSettings();
            }
        }

        if (settings.LastAccountKey is not null && !keys.Contains(settings.LastAccountKey))
        {
            throw CorruptSettings();
        }
    }

    private static bool IsSafeApiRoot(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
        && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0;

    private static bool HasProtectedTokenFormat(string value)
    {
        if (!value.StartsWith(TokenPrefix, StringComparison.Ordinal)) return false;
        var base64 = value.AsSpan(TokenPrefix.Length);
        if (base64.Length == 0 || base64.Length > MaximumSettingsBytes) return false;
        var scratch = new byte[(base64.Length / 4 + 1) * 3];
        return Convert.TryFromBase64Chars(base64, scratch, out var written) && written > 0;
    }

    private static AccountStoreException CorruptSettings() => new(AccountStoreError.SettingsCorrupt,
        "Saved account settings are damaged or incompatible. Restore or reset them before remembering another account.");

    private static AccountStoreException UnprotectFailure() => new(AccountStoreError.TokenUnprotectionFailed,
        "Windows could not unlock the saved account for the current user. Sign in again to replace the saved credentials.");
}
