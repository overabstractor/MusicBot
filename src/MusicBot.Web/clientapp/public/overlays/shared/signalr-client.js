// Minimal SignalR client helper for overlays (multi-user, token-secured)
class OverlayConnection {
  constructor(hubUrl) {
    this.hubUrl = hubUrl;
    this.handlers = {};
    this.connection = null;
    this.reconnectAttempts = 0;
    this.connected = false;
    // Read overlay token from URL query param: ?token=abc123
    this.overlayToken = new URLSearchParams(window.location.search).get("token");
    this._createBanner();
  }

  on(event, callback) {
    this.handlers[event] = callback;
    if (this.connection) {
      this.connection.on(event, callback);
    }
    return this;
  }

  _createBanner() {
    const el = document.createElement("div");
    el.id = "ov-conn-banner";
    el.style.cssText = `
      position:fixed; top:0; left:0; right:0; z-index:9999;
      display:none; align-items:center; justify-content:center; gap:8px;
      padding:8px 16px;
      background:rgba(239,68,68,0.9); color:#fff;
      font:600 13px/1 system-ui,sans-serif;
      backdrop-filter:blur(6px);
      animation:ov-banner-pulse 2s ease infinite;
    `;
    el.innerHTML = '<span style="font-size:16px">⚠</span><span>Desconectado de MusicBot</span>';
    // Add pulse animation
    const style = document.createElement("style");
    style.textContent = `@keyframes ov-banner-pulse{0%,100%{opacity:1}50%{opacity:0.7}}`;
    document.head.appendChild(style);
    document.body.appendChild(el);
    this._banner = el;
  }

  _showBanner(show) {
    if (this._banner) this._banner.style.display = show ? "flex" : "none";
    this.connected = !show;
  }

  async start() {
    if (!this.overlayToken) {
      console.error("[Overlay] Missing ?token= query parameter");
      document.body.innerHTML = '<div style="color:#e74c3c;font-family:sans-serif;padding:20px;">Missing ?token= parameter in URL</div>';
      return;
    }

    this._showBanner(true);

    this.connection = new signalR.HubConnectionBuilder()
      .withUrl(this.hubUrl)
      .withAutomaticReconnect([0, 1000, 2000, 5000, 10000, 30000])
      .build();

    // Register handlers
    for (const [event, callback] of Object.entries(this.handlers)) {
      this.connection.on(event, callback);
    }

    this.connection.on("error", (msg) => {
      console.error("[Overlay] Server error:", msg);
      document.body.innerHTML = '<div style="color:#e74c3c;font-family:sans-serif;padding:20px;">Invalid overlay token</div>';
    });

    this.connection.onreconnecting(() => {
      console.log("[Overlay] Reconnecting...");
      this._showBanner(true);
    });

    this.connection.onreconnected(async () => {
      console.log("[Overlay] Reconnected");
      this.reconnectAttempts = 0;
      this._showBanner(false);
      await this.connection.invoke("JoinUserGroup", this.overlayToken);
    });

    this.connection.onclose(() => {
      console.log("[Overlay] Connection closed, retrying in 5s...");
      this._showBanner(true);
      setTimeout(() => this.start(), 5000);
    });

    try {
      await this.connection.start();
      await this.connection.invoke("JoinUserGroup", this.overlayToken);
      console.log("[Overlay] Connected to MusicBot");
      this.reconnectAttempts = 0;
      this._showBanner(false);
    } catch (err) {
      console.error("[Overlay] Connection failed:", err);
      this._showBanner(true);
      setTimeout(() => this.start(), 5000);
    }
  }
}
