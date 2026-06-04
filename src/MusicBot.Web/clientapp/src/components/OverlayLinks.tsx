import React, { useState } from "react";
import { useTranslation } from "react-i18next";

const BASE = window.location.origin;

interface Props {
  overlayToken: string;
}

export const OverlayLinks: React.FC<Props> = ({ overlayToken }) => {
  const { t } = useTranslation();
  const [copied,       setCopied]       = useState<string | null>(null);
  const [overlayTheme, setOverlayTheme] = useState<"dark" | "light">("dark");
  const token = overlayToken;

  const overlays = [
    {
      label:       t("overlays.player.label"),
      description: t("overlays.player.desc"),
      path:        `/overlays/player/index.html?token=${token}`,
      sizeLarge:   t("overlays.player.sizeLarge"),
      sizeSmall:   t("overlays.player.sizeSmall"),
      recommended: true,
    },
    {
      label:       t("overlays.nowPlaying.label"),
      description: t("overlays.nowPlaying.desc"),
      path:        `/overlays/now-playing/index.html?token=${token}`,
      sizeLarge:   t("overlays.nowPlaying.sizeLarge"),
      sizeSmall:   "",
      recommended: false,
    },
    {
      label:       t("overlays.queue.label"),
      description: t("overlays.queue.desc"),
      path:        `/overlays/queue/index.html?token=${token}`,
      sizeLarge:   t("overlays.queue.sizeLarge"),
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
        {t("overlays.hintLead")} <strong>{t("overlays.browserSource")}</strong> {t("overlays.hintRest")}
      </p>

      <div className="overlay-theme-selector">
        <span className="overlay-theme-label">{t("overlays.themeLabel")}</span>
        <div className="overlay-theme-btns">
          <button
            className={`btn btn-sm ${overlayTheme === "dark" ? "btn-primary" : "btn-outline"}`}
            onClick={() => setOverlayTheme("dark")}
          >{t("overlays.dark")}</button>
          <button
            className={`btn btn-sm ${overlayTheme === "light" ? "btn-primary" : "btn-outline"}`}
            onClick={() => setOverlayTheme("light")}
          >{t("overlays.light")}</button>
        </div>
        <span className="overlay-theme-hint">
          {overlayTheme === "dark"
            ? t("overlays.darkHint")
            : t("overlays.lightHint")}
        </span>
      </div>

      {overlays.map(o => (
        <div key={o.path} className={`overlay-card${o.recommended ? " overlay-card-featured" : ""}`}>
          <div className="overlay-card-header">
            <span className="overlay-card-label">{o.label}</span>
            {o.recommended && <span className="overlay-recommended-badge">{t("overlays.recommended")}</span>}
          </div>
          <p className="overlay-card-desc">{o.description}</p>
          <div className="overlay-card-sizes">
            <span>🖥 {o.sizeLarge}</span>
            {o.sizeSmall && <span>📱 {o.sizeSmall}</span>}
          </div>
          <code className="overlay-link-url">{BASE}{themedPath(o.path)}</code>
          <div className="overlay-link-actions">
            <button className="btn btn-sm btn-primary" onClick={() => copyUrl(o.path)}>
              {copied === o.path ? t("overlays.copied") : t("overlays.copy")}
            </button>
            <a href={themedPath(o.path)} target="_blank" rel="noopener noreferrer" className="btn btn-sm btn-outline">
              {t("overlays.preview")}
            </a>
          </div>
        </div>
      ))}
    </div>
  );
};
