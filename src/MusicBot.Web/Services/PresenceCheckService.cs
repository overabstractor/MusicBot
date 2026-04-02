using MusicBot.Services.Platforms;

namespace MusicBot.Services;

/// <summary>
/// Gestiona el chequeo de presencia del usuario que pidió una canción:
///  1. 30 s antes de que acabe la canción actual se avisa al usuario de la siguiente.
///  2. Al iniciar esa canción se espera confirmación (!aqui); si no llega se skipea.
/// </summary>
public class PresenceCheckService
{
    private readonly ILogger<PresenceCheckService> _logger;
    private readonly ChatResponseService           _chat;
    private readonly QueueSettingsService           _settings;

    /// <summary>Set by PlaybackSyncService to break circular dependency.</summary>
    public Func<Task>? SkipCurrentSong { get; set; }

    private readonly object _lock = new();
    private string? _expectedUser;
    private bool    _confirmed;
    private bool    _warningIssued;
    private CancellationTokenSource? _checkCts;

    public PresenceCheckService(
        ILogger<PresenceCheckService> logger,
        ChatResponseService chat,
        QueueSettingsService settings)
    {
        _logger   = logger;
        _chat     = chat;
        _settings = settings;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Cancela cualquier chequeo en curso. Llamar al iniciar una nueva canción.
    /// </summary>
    public void CancelCheck()
    {
        CancellationTokenSource? old;
        lock (_lock)
        {
            old = _checkCts;
            _checkCts      = null;
            _warningIssued = false;
            _confirmed     = false;
            _expectedUser  = null;
        }
        old?.Cancel();
        old?.Dispose();
    }

    /// <summary>
    /// Envía un aviso al chat para el usuario de la próxima canción.
    /// Solo lo envía una vez por canción (guarded por _warningIssued).
    /// Llamar desde PlaybackSyncService cuando quedan ≤ 30 s.
    /// </summary>
    public void IssueWarningForNext(string nextRequester)
    {
        if (!IsEnabled) return;

        bool send;
        lock (_lock)
        {
            send = !_warningIssued;
            if (send)
            {
                _warningIssued = true;
                _expectedUser  = nextRequester;
            }
        }

        if (!send) return;

        var warnSec = _settings.PresenceCheckWarningSeconds;
        _logger.LogInformation("PresenceCheck: aviso {Sec} s para {User}", warnSec, nextRequester);
        _ = Task.Run(() => _chat.SendChatMessageAsync(nextRequester,
            $"¡tu canción es la próxima! Escribe !aqui para confirmar tu presencia ({warnSec} s)"));
    }

    /// <summary>
    /// Registra la confirmación de presencia del usuario.
    /// Devuelve true si el usuario era el esperado.
    /// </summary>
    public bool ConfirmPresence(string username)
    {
        lock (_lock)
        {
            if (_expectedUser?.Equals(username, StringComparison.OrdinalIgnoreCase) == true)
            {
                _confirmed = true;
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Inicia el chequeo de presencia cuando comienza la canción del requester.
    /// Si el usuario ya confirmó durante el aviso de 30 s, no bloquea.
    /// Si no confirma en 15 s, skipea la canción.
    /// </summary>
    public async Task StartSongCheckAsync(string requester, CancellationToken externalCt = default)
    {
        if (!IsEnabled) return;

        CancellationTokenSource cts;
        lock (_lock)
        {
            // Sincronizar con el requester de la canción que acaba de iniciar
            if (!requester.Equals(_expectedUser, StringComparison.OrdinalIgnoreCase))
            {
                _expectedUser  = requester;
                _confirmed     = false;
            }

            // Si ya confirmó en el aviso de 30 s, nada que hacer
            if (_confirmed)
            {
                _logger.LogInformation("PresenceCheck: {User} ya confirmó durante el aviso, OK", requester);
                return;
            }

            _checkCts?.Cancel();
            _checkCts?.Dispose();
            cts = _checkCts = new CancellationTokenSource();
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, externalCt);
        var ct = linked.Token;

        var confirmSec = _settings.PresenceCheckConfirmSeconds;
        await _chat.SendChatMessageAsync(requester,
            $"¡tu canción está sonando! Escribe !aqui para confirmar ({confirmSec} s)", null, ct);

        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(confirmSec);
            while (DateTime.UtcNow < deadline)
            {
                lock (_lock) { if (_confirmed) goto confirmed; }
                await Task.Delay(500, ct);
            }

            // Sin confirmación — skipear
            _logger.LogInformation("PresenceCheck: {User} no confirmó presencia, salteando canción", requester);
            await _chat.SendChatMessageAsync(requester,
                $"no confirmó presencia, salteando canción", null, ct);

            if (SkipCurrentSong != null)
                await SkipCurrentSong();

            return;
        }
        catch (OperationCanceledException)
        {
            return; // nueva canción inició o servicio detenido
        }

        confirmed:
        _logger.LogInformation("PresenceCheck: {User} confirmó presencia OK", requester);
    }

    /// <summary>
    /// Confirms the current song should stay regardless of who requested it.
    /// Returns true if a check was in progress (an expected user was set).
    /// </summary>
    public bool KeepSong()
    {
        lock (_lock)
        {
            if (_expectedUser == null) return false;
            _confirmed = true;
            return true;
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private bool IsEnabled => _settings.PresenceCheckEnabled;
}
