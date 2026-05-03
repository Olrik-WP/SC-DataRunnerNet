using System.Reflection;
using DataRunner.Core.Abstractions;

namespace DataRunner.UexClient;

/// <summary>
/// Reads the UEX app bearer token from <c>[AssemblyMetadata("UexAppBearerToken", ...)]</c>
/// attributes baked into one of the application assemblies at build time.
///
/// Build-time wiring:
///   - <c>Directory.Build.props</c> conditionally emits the metadata attribute
///     when the MSBuild property <c>$(UexAppBearerToken)</c> is non-empty.
///   - The CI workflow (<c>.github/workflows/release.yml</c>) sets that
///     property from the GitHub Actions secret <c>UEX_APP_BEARER_TOKEN</c>.
///   - Local <c>dotnet build</c> with no env var → no attribute → no token →
///     <see cref="HasToken"/> is <c>false</c>, exactly the expected behavior
///     for a self-built dev binary.
///
/// We scan ALL loaded assemblies (not just the entry assembly) so a single
/// attribute on the WPF host or on the UexClient assembly is enough.
/// </summary>
public sealed class AssemblyMetadataBuiltInAppTokenProvider : IBuiltInAppTokenProvider
{
    private const string MetadataKey = "UexAppBearerToken";
    private readonly Lazy<string?> _token;

    public AssemblyMetadataBuiltInAppTokenProvider()
    {
        _token = new Lazy<string?>(LookupToken, isThreadSafe: true);
    }

    public bool HasToken => !string.IsNullOrWhiteSpace(_token.Value);

    public string? GetEmbeddedToken()
    {
        var v = _token.Value;
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }

    private static string? LookupToken()
    {
        // Walk every loaded assembly; the attribute could legitimately live on
        // any of them (Directory.Build.props applies to all projects in the
        // solution). Stop at the first non-empty match.
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            string? value;
            try
            {
                value = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                    .FirstOrDefault(a => string.Equals(a.Key, MetadataKey, StringComparison.Ordinal))
                    ?.Value
                    ?.Trim();
            }
            catch
            {
                // Some dynamically-loaded assemblies throw on attribute reads.
                // Just skip them — the token won't be in there anyway.
                continue;
            }

            if (!string.IsNullOrWhiteSpace(value)) return value;
        }
        return null;
    }
}
