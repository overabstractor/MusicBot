import React from "react";
import { IntegrationStatus } from "../hooks/useSignalR";

interface Props {
  signalRConnected: boolean;
  tiktokStatus:     IntegrationStatus;
  twitchStatus:     IntegrationStatus;
  kickStatus:       IntegrationStatus;
}

function dotClass(status: "ok" | "warn" | "err" | "connecting"): string {
  return `status-dot ${status}`;
}

function statusToDot(status: IntegrationStatus): "ok" | "warn" | "err" | "connecting" {
  return status === "connected"  ? "ok"
       : status === "connecting" ? "connecting"
       : status === "error"      ? "err"
       : "warn";
}

export const StatusBar: React.FC<Props> = ({ signalRConnected, tiktokStatus, twitchStatus, kickStatus }) => {
  return (
    <div className="status-bar">
      <div className="status-indicator">
        <div className={`status-dot ${signalRConnected ? "ok" : "err"}`} />
        <span>{signalRConnected ? "Overlay" : "Desconectado"}</span>
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
