using System.Runtime.Versioning;
using DataRunner.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace DataRunner.UexClient;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the UEX client + secret store + payload validator + duplicate checker +
    /// SQLite history + in-memory catalog provider. Call this from your composition root.
    /// Windows-only because the secret-key store relies on DPAPI.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static IServiceCollection AddUexClient(this IServiceCollection services)
    {
        services.AddSingleton<ISecretKeyStore>(_ => new DpapiSecretKeyStore());
        services.AddSingleton<IAppPreferences>(_ => new JsonAppPreferences());
        services.AddSingleton<ISubmissionHistory>(_ => new SqliteSubmissionHistory());

        // Built-in app bearer token: official CI releases bake it in via
        // Directory.Build.props; local dev builds get a provider whose
        // HasToken=false and the wizard then asks for a manual token.
        services.AddSingleton<IBuiltInAppTokenProvider, AssemblyMetadataBuiltInAppTokenProvider>();

        services.AddHttpClient<IUexApiClient, UexApiClient>();

        services.AddSingleton<ICatalogProvider, CatalogProvider>();
        services.AddSingleton<IPayloadValidator, PayloadValidator>();
        services.AddSingleton<IDuplicateChecker, DuplicateChecker>();
        services.AddSingleton<IStaleTargetProvider, StaleTargetProvider>();

        return services;
    }
}
