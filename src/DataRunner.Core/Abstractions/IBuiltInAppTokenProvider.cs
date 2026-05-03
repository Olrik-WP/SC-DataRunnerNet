namespace DataRunner.Core.Abstractions;

/// <summary>
/// Exposes the UEX app bearer token that may have been embedded in the binary
/// at build time (CI inserts <c>UEX_APP_BEARER_TOKEN</c> as an MSBuild property
/// → <c>[AssemblyMetadata("UexAppBearerToken", ...)]</c>).
///
/// Why this exists: UEX requires an app bearer token on every <c>POST /data_submit</c>
/// in addition to the user's own secret-key. Asking every end user to register
/// their own UEX application would be a brutal onboarding step — every other
/// datarunner tool on the market embeds their app token in the distributed
/// binary and rotates it on the UEX dashboard if it ever leaks.
///
/// The runtime priority for the bearer token is:
///   1. User-set token from <see cref="ISecretKeyStore.GetBearerTokenAsync"/>
///      (advanced override in Settings — useful for forks / self-builders).
///   2. Built-in token from this provider (the case for official releases).
///   3. Neither → submission fails with an actionable error.
///
/// Source builds (and CI builds without the secret) get a provider whose
/// <see cref="HasToken"/> is <c>false</c>, so the wizard falls back to asking
/// the user to register their own UEX app, exactly as it did before.
/// </summary>
public interface IBuiltInAppTokenProvider
{
    /// <summary>True when a non-empty token was embedded at build time.</summary>
    bool HasToken { get; }

    /// <summary>
    /// Returns the embedded token (trimmed) or <c>null</c> when none was
    /// embedded. Callers should treat <c>null</c> and empty as equivalent.
    /// </summary>
    string? GetEmbeddedToken();
}
