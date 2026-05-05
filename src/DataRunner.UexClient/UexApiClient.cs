using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DataRunner.Core.Abstractions;
using DataRunner.Core.Models;
using Microsoft.Extensions.Logging;

namespace DataRunner.UexClient;

/// <summary>
/// HTTP client for UEX 2.0. Uses bearer-style `secret-key` header for POST endpoints
/// and unauthenticated GETs for catalog data.
///
/// Endpoints used (all on api.uexcorp.space — the .uk hostname does NOT exist):
///   GET  https://api.uexcorp.space/2.0/commodities/
///   GET  https://api.uexcorp.space/2.0/terminals/?type=commodity
///   GET  https://api.uexcorp.space/2.0/commodities_raw_prices/?id_terminal={id}
///   GET  https://api.uexcorp.space/2.0/commodities_prices_all
///   POST https://api.uexcorp.space/2.0/data_submit
///
/// Notes from UEX docs:
///  - Daily quota: 172800 requests (~120/min). We don't enforce client-side beyond simple guards.
///  - data_submit limit: max 1000 reports per 30 minutes.
///  - data_submit rejects same (item, location) within 5 minutes (server-side).
///  - Successful response: { "status": "ok", "data": ... }
///  - data_submit auth: required `secret-key` header (the user secret-key from My Apps).
/// </summary>
public sealed class UexApiClient : IUexApiClient
{
    private const string BaseUrl = "https://api.uexcorp.space/2.0/";

    private static readonly JsonSerializerOptions WireJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    private static readonly JsonSerializerOptions ReadJson = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;
    private readonly ISecretKeyStore _secretStore;
    private readonly IBuiltInAppTokenProvider _builtInToken;
    private readonly ILogger<UexApiClient> _logger;

    public UexApiClient(
        HttpClient http,
        ISecretKeyStore secretStore,
        IBuiltInAppTokenProvider builtInToken,
        ILogger<UexApiClient> logger)
    {
        _http = http;
        _secretStore = secretStore;
        _builtInToken = builtInToken;
        _logger = logger;

        if (_http.Timeout == TimeSpan.FromSeconds(100)) // default
        {
            _http.Timeout = TimeSpan.FromSeconds(30);
        }

        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "SC-DataRunnerNet/0.1 (+https://github.com/) dotnet/9");
    }

    public async Task<IReadOnlyList<UexCommodity>> GetCommoditiesAsync(CancellationToken ct = default)
    {
        var url = $"{BaseUrl}commodities/";
        var env = await _http.GetFromJsonAsync<UexEnvelope<UexCommodity>>(url, ReadJson, ct);
        EnsureOk(env, url);
        return env!.Data;
    }

    public async Task<IReadOnlyList<UexTerminal>> GetCommodityTerminalsAsync(CancellationToken ct = default)
    {
        var url = $"{BaseUrl}terminals/?type=commodity";
        var env = await _http.GetFromJsonAsync<UexEnvelope<UexTerminal>>(url, ReadJson, ct);
        EnsureOk(env, url);
        return env!.Data;
    }

    public async Task<IReadOnlyList<UexCommodityRawPrice>> GetCommodityRawPricesAsync(int idTerminal, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}commodities_raw_prices/?id_terminal={idTerminal}";
        var env = await _http.GetFromJsonAsync<UexEnvelope<UexCommodityRawPrice>>(url, ReadJson, ct);
        EnsureOk(env, url);
        return env!.Data;
    }

    public async Task<IReadOnlyList<UexCommodityPriceAll>> GetAllCommodityPricesAsync(CancellationToken ct = default)
    {
        // Heavy-ish endpoint (~1 MB). Callers (StaleTargetProvider) MUST cache aggressively.
        var url = $"{BaseUrl}commodities_prices_all";
        _logger.LogInformation("GET {Url} (large payload, cache for 6h+)", url);
        var env = await _http.GetFromJsonAsync<UexEnvelope<UexCommodityPriceAll>>(url, ReadJson, ct);
        EnsureOk(env, url);
        return env!.Data;
    }

    public async Task<UexGameVersions> GetGameVersionsAsync(CancellationToken ct = default)
    {
        // /game_versions wraps a SINGLE object, not a list. The shared
        // UexEnvelope<T> is built around `data: T[]` so we use a dedicated
        // envelope shape here.
        var url = $"{BaseUrl}game_versions";
        var env = await _http.GetFromJsonAsync<GameVersionsEnvelope>(url, ReadJson, ct);
        if (env is null)
            throw new InvalidOperationException($"UEX returned an empty body for {url}.");
        if (!string.Equals(env.Status, "ok", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"UEX returned status='{env.Status}' for {url}: {env.Message}");
        return env.Data ?? new UexGameVersions();
    }

    private sealed class GameVersionsEnvelope
    {
        public string? Status { get; set; }
        public string? Message { get; set; }
        public UexGameVersions? Data { get; set; }
    }

    public async Task<UexSubmitResult> SubmitDataAsync(UexDataSubmitPayload payload, CancellationToken ct = default)
    {
        // UEX requires BOTH credentials on every /data_submit POST:
        //   - secret-key      : identifies the user (datarunner)
        //   - Bearer <token>  : identifies the application
        // Source: UEX staff confirmation on Discord, plus the API doc page.
        //
        // Bearer-token priority:
        //   1. Custom token explicitly set by the user via Settings (override).
        //   2. Token embedded in the binary at build time (the case for
        //      official CI releases — see Directory.Build.props).
        //   3. Neither → actionable error.
        var secretKey = (await _secretStore.GetAsync(ct).ConfigureAwait(false))?.Trim();
        var customBearer = (await _secretStore.GetBearerTokenAsync(ct).ConfigureAwait(false))?.Trim();
        var bearer = !string.IsNullOrWhiteSpace(customBearer)
            ? customBearer
            : _builtInToken.GetEmbeddedToken();

        if (string.IsNullOrWhiteSpace(secretKey))
        {
            throw new InvalidOperationException(
                "UEX secret-key is not configured. Set it in Settings before submitting.");
        }
        if (string.IsNullOrWhiteSpace(bearer))
        {
            throw new InvalidOperationException(
                "UEX app bearer token is not available. The official release " +
                "ships with one embedded; if you self-built without setting " +
                "$(UexAppBearerToken), either rebuild with the property set " +
                "or paste your own token in Settings -> Advanced.");
        }

        // Strip the local _meta block before serialising. The wire format must
        // contain ONLY UEX-recognised fields.
        var wireBody = SerialiseWirePayload(payload);

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}data_submit");
        req.Headers.TryAddWithoutValidation("secret-key", secretKey);
        req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {bearer}");
        req.Content = new StringContent(wireBody, Encoding.UTF8, "application/json");

        _logger.LogInformation("POST {Url} (terminal={Terminal}, rows={Rows}, prod={Prod})",
            req.RequestUri, payload.IdTerminal, payload.Prices.Count, payload.IsProduction);

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var bodyText = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        string? status = null, message = null;
        try
        {
            using var doc = JsonDocument.Parse(bodyText);
            if (doc.RootElement.TryGetProperty("status", out var s)) status = s.GetString();
            if (doc.RootElement.TryGetProperty("message", out var m)) message = m.GetString();
        }
        catch
        {
            // not JSON; keep message null
        }

        var ok = resp.IsSuccessStatusCode && string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase);
        return new UexSubmitResult(
            Ok: ok,
            HttpStatusCode: (int)resp.StatusCode,
            Status: status,
            Message: message,
            RawResponseBody: bodyText,
            SerialisedRequestBody: wireBody);
    }

    /// <summary>
    /// Serialises the payload using ONLY wire-format fields. Strips the `_meta` extension.
    /// Exposed as a public utility so the UI can show an exact preview of what will be sent.
    /// </summary>
    public static string SerialiseWirePayload(UexDataSubmitPayload payload)
    {
        var clone = new UexDataSubmitPayload
        {
            IdTerminal = payload.IdTerminal,
            Type = payload.Type,
            IsProduction = payload.IsProduction,
            Prices = payload.Prices,
            ContainerSizes = payload.ContainerSizes,
            GameVersion = payload.GameVersion,
            FactionAffinity = payload.FactionAffinity,
            Details = payload.Details,
            Screenshot = payload.Screenshot,
            Meta = null!,
        };
        var json = JsonSerializer.Serialize(clone, new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = true,
        });
        return json;
    }

    private static void EnsureOk<T>(UexEnvelope<T>? env, string url)
    {
        if (env is null)
            throw new InvalidOperationException($"UEX returned an empty body for {url}.");
        if (!string.Equals(env.Status, "ok", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"UEX returned status='{env.Status}' for {url}: {env.Message}");
    }
}
