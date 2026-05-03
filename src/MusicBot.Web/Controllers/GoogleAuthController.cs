using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using MusicBot.Hubs;

namespace MusicBot.Controllers;

[ApiController]
[Route("api/auth/google")]
[Tags("Auth")]
public class GoogleAuthController : ControllerBase
{
    private static readonly ConcurrentDictionary<string, string> _pending = new(); // state → codeVerifier

    private readonly IConfiguration      _config;
    private readonly IHubContext<OverlayHub> _hub;

    public GoogleAuthController(IConfiguration config, IHubContext<OverlayHub> hub)
    {
        _config = config;
        _hub    = hub;
    }

    /// <summary>Start Google OAuth — opens the real system browser</summary>
    [HttpPost("start")]
    [ProducesResponseType(200)]
    public IActionResult Start()
    {
        var clientId = _config["Google:ClientId"];
        if (string.IsNullOrEmpty(clientId))
            return StatusCode(503, "Google:ClientId no configurado");

        var state        = Guid.NewGuid().ToString("N");
        var verifier     = GenerateVerifier();
        var challenge    = GenerateChallenge(verifier);
        var redirectUri  = "http://127.0.0.1:3050/api/auth/google/callback";

        _pending[state] = verifier;
        // Auto-expire after 10 minutes
        var captured = state;
        _ = Task.Delay(TimeSpan.FromMinutes(10))
              .ContinueWith(t => _pending.TryRemove(captured, out _));

        var scopes = Uri.EscapeDataString("openid email profile");
        var url    = $"https://accounts.google.com/o/oauth2/v2/auth" +
                     $"?client_id={Uri.EscapeDataString(clientId)}" +
                     $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
                     $"&response_type=code" +
                     $"&scope={scopes}" +
                     $"&state={state}" +
                     $"&code_challenge={challenge}" +
                     $"&code_challenge_method=S256" +
                     $"&access_type=offline";

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url)
        {
            UseShellExecute = true
        });

        return Ok();
    }

    /// <summary>Google OAuth callback — exchanges code for ID token and notifies frontend via SignalR</summary>
    [HttpGet("callback")]
    public async Task<ContentResult> Callback([FromQuery] string? code, [FromQuery] string? state, [FromQuery] string? error)
    {
        if (!string.IsNullOrEmpty(error) || string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state))
            return FailPage("Autenticación cancelada o fallida.");

        if (!_pending.TryRemove(state, out var codeVerifier))
            return FailPage("El enlace de autenticación expiró o ya fue usado.");

        var relayUrl    = _config["Relay:Url"]!;
        var relayKey    = _config["Relay:ApiKey"]!;
        var redirectUri = "http://127.0.0.1:3050/api/auth/google/callback";

        // Exchange code + PKCE verifier via relay (client_secret stays server-side in Cloudflare)
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Add("X-Relay-Key", relayKey);
        var resp = await http.PostAsJsonAsync($"{relayUrl}/token/google", new
        {
            grant_type    = "authorization_code",
            code,
            redirect_uri  = redirectUri,
            code_verifier = codeVerifier,
        });

        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync();
            return FailPage($"Error al verificar con Google ({(int)resp.StatusCode}): {err}");
        }

        var json    = await resp.Content.ReadAsStringAsync();
        var tokens  = JsonDocument.Parse(json).RootElement;
        var idToken = tokens.TryGetProperty("id_token", out var t) ? t.GetString() : null;

        if (string.IsNullOrEmpty(idToken))
            return FailPage("Google no devolvió un token de identidad.");

        // Push to frontend via SignalR (group name matches OverlayHub.JoinUserGroup)
        await _hub.Clients.Group($"user:{LocalUser.Id}")
                  .SendAsync("auth:google-token", new { idToken });

        return SuccessPage();
    }

    // ── PKCE helpers ────────────────────────────────────────────────────────────

    private static string GenerateVerifier()
    {
        var bytes = new byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Base64UrlEncode(bytes);
    }

    private static string GenerateChallenge(string verifier)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(verifier));
        return Base64UrlEncode(hash);
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    // ── Result pages ────────────────────────────────────────────────────────────

    private static ContentResult SuccessPage() => new()
    {
        ContentType  = "text/html; charset=utf-8",
        StatusCode   = 200,
        Content      = """
            <!doctype html><html><head><meta charset="utf-8">
            <title>MusicBot – Sesión iniciada</title>
            <style>body{font-family:sans-serif;background:#121212;color:#fff;display:flex;
            align-items:center;justify-content:center;height:100vh;margin:0;flex-direction:column;gap:12px}
            .icon{font-size:48px}.msg{font-size:18px;font-weight:600}.sub{color:#aaa;font-size:14px}</style>
            </head><body>
            <div class="icon">✅</div>
            <div class="msg">¡Sesión iniciada correctamente!</div>
            <div class="sub">Puedes cerrar esta pestaña y volver a MusicBot.</div>
            </body></html>
            """,
    };

    private static ContentResult FailPage(string reason) => new()
    {
        ContentType  = "text/html; charset=utf-8",
        StatusCode   = 400,
        Content      = $$"""
            <!doctype html><html><head><meta charset="utf-8">
            <title>MusicBot – Error</title>
            <style>body{font-family:sans-serif;background:#121212;color:#fff;display:flex;
            align-items:center;justify-content:center;height:100vh;margin:0;flex-direction:column;gap:12px}
            .icon{font-size:48px}.msg{font-size:18px;font-weight:600}.sub{color:#f87171;font-size:14px}</style>
            </head><body>
            <div class="icon">❌</div>
            <div class="msg">Error de autenticación</div>
            <div class="sub">{{System.Net.WebUtility.HtmlEncode(reason)}}</div>
            </body></html>
            """,
    };
}
