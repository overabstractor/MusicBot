using System.Text.Json;
using Microsoft.Extensions.Options;

namespace MusicBot.Services.Platforms;

/// <summary>
/// Fetches the product's shared Euler Stream signing key from the Cloudflare relay
/// (GET {Relay:Url}/signing-key/tiktok, authenticated with X-Relay-Key).
///
/// This is the "eulerstream-shared" tier of <c>PlatformConnectionManager.ResolveSigningConfig</c>:
/// used when the user has no key of their own, so distributed installs get the authenticated
/// (higher-quota) signing tier instead of the anonymous, rate-limited one.
///
/// The key is cached and this never throws — on any failure it returns the last known value
/// (even if stale) or null, letting the caller fall back to the anonymous tier gracefully.
/// </summary>
public class SigningKeyService
{
    private readonly RelaySettings _relay;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<SigningKeyService> _logger;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(6);
    private string? _cachedKey;
    private DateTime _fetchedAtUtc = DateTime.MinValue;

    public SigningKeyService(
        IOptions<RelaySettings> relay,
        IHttpClientFactory httpFactory,
        ILogger<SigningKeyService> logger)
    {
        _relay       = relay.Value;
        _httpFactory = httpFactory;
        _logger      = logger;
    }

    /// <summary>
    /// Returns the shared Euler Stream signing key, or null if the relay is unconfigured,
    /// unreachable, or has no key set. Cached for <see cref="CacheTtl"/>; never throws.
    /// </summary>
    public async Task<string?> GetSharedSigningKeyAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_relay.Url) || string.IsNullOrWhiteSpace(_relay.ApiKey))
            return null;

        // Fast path: fresh cache (a previously-fetched empty key is cached as null, so we
        // only re-hit the relay once the TTL expires — no hammering when no key is set).
        if (DateTime.UtcNow - _fetchedAtUtc < CacheTtl)
            return _cachedKey;

        await _gate.WaitAsync(ct);
        try
        {
            // Re-check after acquiring the gate — another caller may have refreshed it.
            if (DateTime.UtcNow - _fetchedAtUtc < CacheTtl)
                return _cachedKey;

            var req = new HttpRequestMessage(HttpMethod.Get, $"{_relay.Url.TrimEnd('/')}/signing-key/tiktok");
            req.Headers.Add("X-Relay-Key", _relay.ApiKey);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            var res = await _httpFactory.CreateClient().SendAsync(req, cts.Token);
            if (!res.IsSuccessStatusCode)
            {
                _logger.LogWarning("Shared signing key fetch returned {Status} — keeping previous value", (int)res.StatusCode);
                return _cachedKey; // graceful: reuse last known key on transient relay errors
            }

            var json = await res.Content.ReadAsStringAsync(cts.Token);
            using var doc = JsonDocument.Parse(json);
            var key = doc.RootElement.TryGetProperty("key", out var k) ? k.GetString() : null;

            _cachedKey    = string.IsNullOrWhiteSpace(key) ? null : key;
            _fetchedAtUtc = DateTime.UtcNow;
            _logger.LogInformation("Shared Euler signing key {State} via relay", _cachedKey == null ? "not set" : "loaded");
            return _cachedKey;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Shared signing key fetch failed — falling back to previous/anonymous");
            return _cachedKey;
        }
        finally
        {
            _gate.Release();
        }
    }
}
