import React, { useState } from "react";
import { Settings, Zap, Monitor, Dices, MessageSquare, X } from "lucide-react";
import { SettingsPanel } from "./SettingsPanel";
import { PlatformConnections } from "./PlatformConnections";
import { OverlayLinks } from "./OverlayLinks";
import { AutoQueuePanel } from "./AutoQueuePanel";
import { TickerMessages } from "./TickerMessages";
import { QueueSettings, IntegrationEvent, TickerMessage } from "../hooks/useSignalR";
import { NowPlayingState } from "../types/models";

type SettingsTab = "settings" | "platforms" | "overlays" | "autoqueue" | "ticker";

interface Props {
  open: boolean;
  onClose: () => void;
  settings: QueueSettings;
  tiktokEvents: IntegrationEvent[];
  twitchEvents: IntegrationEvent[];
  kickEvents: IntegrationEvent[];
  tickerMessages: TickerMessage[];
  nowPlaying: NowPlayingState | null;
  overlayToken: string;
}

const TABS: { id: SettingsTab; label: string; icon: React.ReactNode }[] = [
  { id: "settings",  label: "Ajustes",     icon: <Settings size={14} />    },
  { id: "platforms", label: "Plataformas", icon: <Zap size={14} />         },
  { id: "overlays",  label: "Overlays",    icon: <Monitor size={14} />     },
  { id: "autoqueue", label: "Auto Cola",   icon: <Dices size={14} />       },
  { id: "ticker",    label: "Mensajes",    icon: <MessageSquare size={14} /> },
];

export const SettingsModal: React.FC<Props> = ({
  open, onClose, settings, tiktokEvents, twitchEvents, kickEvents,
  tickerMessages, nowPlaying, overlayToken,
}) => {
  const [tab, setTab] = useState<SettingsTab>("settings");

  if (!open) return null;

  return (
    <div className="modal-backdrop" onClick={onClose}>
      <div className="settings-modal" onClick={e => e.stopPropagation()}>
        <div className="settings-modal-header">
          <div className="settings-modal-tabs">
            {TABS.map(t => (
              <button
                key={t.id}
                className={`settings-modal-tab${tab === t.id ? " active" : ""}`}
                onClick={() => setTab(t.id)}
              >
                {t.icon} {t.label}
              </button>
            ))}
          </div>
          <button className="settings-modal-close" onClick={onClose}><X size={16} /></button>
        </div>

        <div className="settings-modal-body">
          {tab === "settings"  && <SettingsPanel settings={settings} />}
          {tab === "platforms" && (
            <PlatformConnections
              tiktokEvents={tiktokEvents}
              twitchEvents={twitchEvents}
              kickEvents={kickEvents}
            />
          )}
          {tab === "overlays"  && <OverlayLinks overlayToken={overlayToken} />}
          {tab === "autoqueue" && <AutoQueuePanel nowPlaying={nowPlaying} />}
          {tab === "ticker"    && <TickerMessages messages={tickerMessages} />}
        </div>
      </div>
    </div>
  );
};
