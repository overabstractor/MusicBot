import React from "react";
import { useTranslation } from "react-i18next";
import { IntegrationStatus } from "../hooks/useSignalR";

interface Props {
  signalRConnected: boolean;
  tiktokStatus:     IntegrationStatus;
  twitchStatus:     IntegrationStatus;
  kickStatus:       IntegrationStatus;
}

function dotClass(status: "ok" | "warn" | "err" | "connecting" | "waiting"): string {
  return `status-dot ${status}`;
}

function statusToDot(status: IntegrationStatus): "ok" | "warn" | "err" | "connecting" | "waiting" {
  return status === "connected"   ? "ok"
       : status === "waitinglive" ? "waiting"
       : status === "connecting"  ? "connecting"
       : status === "error"       ? "err"
       : "warn";
}

export const StatusBar: React.FC<Props> = ({ signalRConnected, tiktokStatus, twitchStatus, kickStatus }) => {
  const { t } = useTranslation();
  return (
    <div className="status-bar">
      <div className="status-indicator">
        <div className={`status-dot ${signalRConnected ? "ok" : "err"}`} />
        <span>{signalRConnected ? t("status.overlay") : t("status.disconnected")}</span>
      </div>

      <div className="status-indicator" title={`TikTok: ${tiktokStatus}`}>
        <div className={dotClass(statusToDot(tiktokStatus))} />
        <span>TikTok</span>
      </div>

      <div className="status-indicator" title={`Twitch: ${twitchStatus}`}>
        <div className={dotClass(statusToDot(twitchStatus))} />
        <span>Twitch</span>
      </div>

      <div className="status-indicator" title={`Kick: ${kickStatus}`}>
        <div className={dotClass(statusToDot(kickStatus))} />
        <span>Kick</span>
      </div>
    </div>
  );
};
