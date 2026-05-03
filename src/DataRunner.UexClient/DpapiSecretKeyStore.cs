using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using DataRunner.Core.Abstractions;

namespace DataRunner.UexClient;

/// <summary>
/// Stores both UEX credentials (user secret-key AND app bearer token) in two
/// separate files under %LOCALAPPDATA%\SC-DataRunnerNet\, each encrypted with
/// Windows DPAPI scoped to the CURRENT_USER. Both blobs are unreadable to other
/// users on the same machine and never leave the box.
///
/// Two distinct entropy strings are used so that one blob cannot be decrypted
/// as the other in case files are ever swapped on disk.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretKeyStore : ISecretKeyStore
{
    private static readonly byte[] SecretKeyEntropy = Encoding.UTF8.GetBytes("SC-DataRunnerNet/secret-key/v1");
    private static readonly byte[] BearerTokenEntropy = Encoding.UTF8.GetBytes("SC-DataRunnerNet/bearer-token/v1");

    private readonly string _secretKeyPath;
    private readonly string _bearerTokenPath;

    public DpapiSecretKeyStore(string? overrideStoragePath = null)
    {
        _secretKeyPath = overrideStoragePath ?? DefaultPath("secret.dat");
        _bearerTokenPath = overrideStoragePath is null
            ? DefaultPath("bearer.dat")
            : Path.Combine(Path.GetDirectoryName(overrideStoragePath)!, "bearer.dat");
        Directory.CreateDirectory(Path.GetDirectoryName(_secretKeyPath)!);
    }

    // ---- User secret-key ----
    public Task<bool> HasKeyAsync(CancellationToken ct = default) => HasFileAsync(_secretKeyPath);
    public Task<string?> GetAsync(CancellationToken ct = default) => ReadAsync(_secretKeyPath, SecretKeyEntropy, ct);
    public Task SetAsync(string secretKey, CancellationToken ct = default) => WriteAsync(_secretKeyPath, SecretKeyEntropy, secretKey, nameof(secretKey), ct);
    public Task ClearAsync(CancellationToken ct = default) => DeleteAsync(_secretKeyPath);

    // ---- App bearer token ----
    public Task<bool> HasBearerTokenAsync(CancellationToken ct = default) => HasFileAsync(_bearerTokenPath);
    public Task<string?> GetBearerTokenAsync(CancellationToken ct = default) => ReadAsync(_bearerTokenPath, BearerTokenEntropy, ct);
    public Task SetBearerTokenAsync(string bearerToken, CancellationToken ct = default) => WriteAsync(_bearerTokenPath, BearerTokenEntropy, bearerToken, nameof(bearerToken), ct);
    public Task ClearBearerTokenAsync(CancellationToken ct = default) => DeleteAsync(_bearerTokenPath);

    private static Task<bool> HasFileAsync(string path)
        => Task.FromResult(File.Exists(path) && new FileInfo(path).Length > 0);

    private static async Task<string?> ReadAsync(string path, byte[] entropy, CancellationToken ct)
    {
        if (!File.Exists(path)) return null;
        var encrypted = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        if (encrypted.Length == 0) return null;
        try
        {
            var clear = ProtectedData.Unprotect(encrypted, entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(clear);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private static async Task WriteAsync(string path, byte[] entropy, string value, string paramName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{paramName} cannot be empty", paramName);

        var clear = Encoding.UTF8.GetBytes(value.Trim());
        var encrypted = ProtectedData.Protect(clear, entropy, DataProtectionScope.CurrentUser);
        await File.WriteAllBytesAsync(path, encrypted, ct).ConfigureAwait(false);
    }

    private static Task DeleteAsync(string path)
    {
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    private static string DefaultPath(string fileName)
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SC-DataRunnerNet",
            fileName);
}
