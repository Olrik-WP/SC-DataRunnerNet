namespace DataRunner.Core.Abstractions;

/// <summary>
/// Persists UEX credentials securely on the local machine.
/// Implementation should encrypt at rest (Windows DPAPI / DPAPI-NG / Credential Manager).
///
/// UEX requires TWO independent secrets for POST /data_submit:
///   - the user's "secret-key"  → identifies the datarunner (from Account page)
///   - an "app bearer token"    → identifies the application   (from /api/apps)
/// Both are sent on every POST: `secret-key: ...` AND `Authorization: Bearer ...`.
/// </summary>
public interface ISecretKeyStore
{
    // ---- User secret-key (per-user, from uexcorp.space → Account → Secret Key) ----
    Task<string?> GetAsync(CancellationToken ct = default);
    Task SetAsync(string secretKey, CancellationToken ct = default);
    Task ClearAsync(CancellationToken ct = default);
    Task<bool> HasKeyAsync(CancellationToken ct = default);

    // ---- App bearer token (per-application, from uexcorp.space/api/apps) ----
    Task<string?> GetBearerTokenAsync(CancellationToken ct = default);
    Task SetBearerTokenAsync(string bearerToken, CancellationToken ct = default);
    Task ClearBearerTokenAsync(CancellationToken ct = default);
    Task<bool> HasBearerTokenAsync(CancellationToken ct = default);
}
