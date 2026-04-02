import React, { useState } from "react";

const BASE = window.location.origin;

interface Props {
  overlayToken: string;
}

export const OverlayLinks: React.FC<Props> = ({ overlayToken }) => {
  const [copied,       setCopied]       = useState<string | null>(null);
  const [overlayTheme, setOverlayTheme] = useState<"dark" | "light">("dark");
  const t = overlayToken;

  const overlays = [
    {
      label:       "Player completo (responsive)",
      description: "Now Playing + Cola en un solo overlay. En pantallas grandes muestra dos columnas; en pantallas pequeñas se apila verticalmente. Recomendado.",
      path:        `/overlays/player/index.html?token=${t}`,
      sizeLarge:   "860×420 px (2 columnas)",
      sizeSmall:   "360×640 px (vertical)",
      recommended: true,
    },
    {
      label:       "Solo Now Playing",
      description: "Muestra únicamente la canción en reproducción con carátula y barra de progreso.",
      path:        `/overlays/now-playing/index.html?token=${t}`,
      sizeLarge:   "640×120 px",
      sizeSmall:   "",
      recommended: false,
    },
    {
      label:       "Solo Cola",
      description: "Lista compacta de canciones en espera.",
      path:        `/overlays/queue/index.html?token=${t}`,
      sizeLarge:   "420×auto",
      sizeSmall:   "",
      recommended: false,
    },
  ];

  const themedPath = (path: string) => `${path}&theme=${overlayTheme}`;

  const copyUrl = (path: string) => {
    navigator.clipboard.writeText(`${BASE}${themedPath(path)}`);
    setCopied(path);
    setTimeout(() => setCopied(null), 2000);
  };

  return (
    <div className="overlay-links">
      <p className="overlay-hint">
        Agrega estas URLs como <strong>Browser Source</strong> en OBS, TikTok Live Studio, Meld o cualquier herramienta de streaming. El overlay responsive incluye votaciones de skip, notificaciones de nueva canción y se adapta al tamaño que le configures.
      </p>

      <div className="overlay-theme-selector">
        <span className="overlay-theme-label">Tema del overlay:</span>
        <div className="overlay-theme-btns">
          <button
            className={`btn btn-sm ${overlayTheme === "dark" ? "btn-primary" : "btn-outline"}`}
            onClick={() => setOverlayTheme("dark")}
          >🌙 Oscuro</button>
          <button
            className={`btn btn-sm ${overlayTheme === "light" ? "btn-primary" : "btn-outline"}`}
            onClick={() => setOverlayTheme("light")}
          >☀️ Claro</button>
        </div>
        <span className="overlay-theme-hint">
          {overlayTheme === "dark"
            ? "Fondo oscuro transparente — ideal para fondos de pantalla oscuros o juegos."
            : "Fondo claro semitransparente — ideal para streams con esquemas de color claros."}
        </span>
      </div>

      {overlays.map(o => (
        <div key={o.path} className={`overlay-card${o.recommended ? " overlay-card-featured" : ""}`}>
          <div className="overlay-card-header">
            <span className="overlay-card-label">{o.label}</span>
            {o.recommended && <span className="overlay-recommended-badge">Recomendado</span>}
          </div>
          <p className="overlay-card-desc">{o.description}</p>
          <div className="overlay-card-sizes">
            <span>🖥 {o.sizeLarge}</span>
            {o.sizeSmall && <span>📱 {o.sizeSmall}</span>}
          </div>
          <code className="overlay-link-url">{BASE}{themedPath(o.path)}</code>
          <div className="overlay-link-actions">
            <button className="btn btn-sm btn-primary" onClick={() => copyUrl(o.path)}>
              {copied === o.path ? "✓ Copiado" : "Copiar URL"}
            </button>
            <a href={themedPath(o.path)} target="_blank" rel="noopener noreferrer" className="btn btn-sm btn-outline">
              Preview
            </a>
          </div>
        </div>
      ))}
    </div>
  );
};
