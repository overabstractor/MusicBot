/**
 * KickVote widget for MusicBot overlays.
 * Call: initVoteWidget(conn, containerEl)
 *   conn        — OverlayConnection instance
 *   containerEl — element to prepend the widget into (before the list)
 */
function initVoteWidget(conn, containerEl) {
  // ── Build widget DOM ────────────────────────────────────────────────────
  const widget = document.createElement("div");
  widget.className = "vote-widget";
  widget.style.display = "none";
  widget.innerHTML = `
    <div class="vote-heading">
      <span class="vote-heading-icon">⚡</span>
      <span>Voto de skip · 30 segundos</span>
    </div>
    <div class="vote-song-title" id="vw-title"></div>
    <div class="vote-timer-track"><div class="vote-timer-fill" id="vw-timer" style="width:100%"></div></div>
    <div class="vote-counts">
      <span class="vote-yes-label">✓ <span id="vw-yes">0</span></span>
      <div class="vote-ratio-track"><div class="vote-ratio-fill" id="vw-ratio" style="width:50%"></div></div>
      <span class="vote-no-label"><span id="vw-no">0</span> ✗</span>
    </div>
    <div class="vote-result-text" id="vw-result-text"></div>
    <div class="vote-footer">
      <span class="vote-hint">!si = skip &nbsp;·&nbsp; !no = quedar</span>
      <span class="vote-timer-label"><span id="vw-secs">30</span>s</span>
    </div>`;

  containerEl.insertBefore(widget, containerEl.firstChild);

  // ── DOM refs ────────────────────────────────────────────────────────────
  const wTitle      = document.getElementById("vw-title");
  const wTimer      = document.getElementById("vw-timer");
  const wYes        = document.getElementById("vw-yes");
  const wNo         = document.getElementById("vw-no");
  const wRatio      = document.getElementById("vw-ratio");
  const wResultText = document.getElementById("vw-result-text");
  const wSecs       = document.getElementById("vw-secs");

  // ── State ────────────────────────────────────────────────────────────────
  let hideTimer = null; // tracks the pending hide timeout

  // ── Helpers ─────────────────────────────────────────────────────────────
  function updateCounts(yes, no, secondsLeft) {
    wYes.textContent = yes;
    wNo.textContent  = no;
    const total = yes + no;
    const pct   = total > 0 ? Math.round(yes / total * 100) : 50;
    wRatio.style.width = `${pct}%`;
    if (secondsLeft >= 0) {
      wSecs.textContent  = secondsLeft;
      wTimer.style.width = `${Math.round(secondsLeft / 30 * 100)}%`;
    }
  }

  function showWidget() {
    // Cancel any pending hide from a previous vote ending
    if (hideTimer !== null) { clearTimeout(hideTimer); hideTimer = null; }
    widget.style.display = "";
    widget.className = "vote-widget";
    wResultText.textContent = "";
  }

  function hideWidget() {
    hideTimer = null;
    widget.classList.add("vote-out");

    // Fallback: force-hide after animation duration in case animationend doesn't fire
    // (can happen in OBS with hardware acceleration disabled)
    const fallback = setTimeout(() => {
      widget.style.display = "none";
      widget.classList.remove("vote-out");
    }, 600);

    widget.addEventListener("animationend", () => {
      clearTimeout(fallback);
      widget.style.display = "none";
      widget.classList.remove("vote-out");
    }, { once: true });
  }

  // ── Event handlers ───────────────────────────────────────────────────────
  conn.on("vote:started", (data) => {
    showWidget();
    wTitle.textContent = `"${data.songTitle}" — ${data.artist}`;
    updateCounts(data.yesVotes || 0, data.noVotes || 0, data.secondsLeft ?? 30);
  });

  conn.on("vote:updated", (data) => {
    updateCounts(data.yesVotes || 0, data.noVotes || 0, data.secondsLeft ?? -1);
  });

  conn.on("vote:ended", (data) => {
    const isSkip = data.result === "skip";
    widget.classList.add(isSkip ? "vote-result-skip" : "vote-result-keep");
    wResultText.textContent = isSkip
      ? `⏭ ¡Skipeada! (${data.yesVotes} sí vs ${data.noVotes} no)`
      : `✓ ¡Se queda! (${data.noVotes} no vs ${data.yesVotes} sí)`;
    // Store timer ID so showWidget() can cancel it if a new vote starts
    hideTimer = setTimeout(hideWidget, 5000);
  });
}

/**
 * Register song:added and queue:error toasts on a connection.
 * Requires toast.js to be loaded first.
 */
function initOverlayToasts(conn) {
  conn.on("song:added", (data) => {
    showToast({
      icon:     "♫",
      title:    data.title,
      subtitle: `${data.artist}${data.requestedBy ? " · " + data.requestedBy : ""}`,
      type:     "success",
      duration: 5000,
    });
  });

  conn.on("queue:error", (data) => {
    showToast({
      icon:     "✕",
      title:    "Error",
      subtitle: data.message || "Ocurrió un error",
      type:     "error",
      duration: 6000,
    });
  });
}
