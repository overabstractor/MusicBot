/**
 * Reads ?theme=light or ?theme=dark from the URL and applies data-theme
 * to <html>. Falls back to "dark" (transparent overlay, default for OBS).
 * Also syncs with localStorage so the setting persists across reloads.
 */
(function () {
  const params = new URLSearchParams(window.location.search);
  const urlTheme = params.get("theme");
  const stored   = (() => { try { return localStorage.getItem("musicbot-overlay-theme"); } catch { return null; } })();
  const theme    = (urlTheme === "light" || urlTheme === "dark") ? urlTheme : (stored || "dark");
  document.documentElement.setAttribute("data-theme", theme);
  try { localStorage.setItem("musicbot-overlay-theme", theme); } catch { /* ignore */ }
})();
