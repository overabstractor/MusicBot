import React, { useState } from "react";
import { useTranslation } from "react-i18next";
import { Settings, Zap, Monitor, MessageSquare, X } from "lucide-react";
import { SettingsPanel } from "./SettingsPanel";
import { PlatformConnections } from "./PlatformConnections";
import { OverlayLinks } from "./OverlayLinks";
import { TickerMessages } from "./TickerMessages";
import { QueueSettings, IntegrationEvent, TickerMessage } from "../hooks/useSignalR";

type SettingsTab = "settings" | "platforms" | "overlays" | "ticker";

interface Props {
  open: boolean;
  onClose: () => void;
  settings: QueueSettings;
  tiktokEvents: IntegrationEvent[];
  twitchEvents: IntegrationEvent[];
  kickEvents: IntegrationEvent[];
  tickerMessages: TickerMessage[];
  overlayToken: string;
}

const TABS: { id: SettingsTab; labelKey: string; icon: React.ReactNode }[] = [
  { id: "settings",  labelKey: "settings.tab.settings",  icon: <Settings size={14} />    },
  { id: "platforms", labelKey: "settings.tab.platforms", icon: <Zap size={14} />         },
  { id: "overlays",  labelKey: "settings.tab.overlays",  icon: <Monitor size={14} />     },
  { id: "ticker",    labelKey: "settings.tab.ticker",    icon: <MessageSquare size={14} /> },
];

export const SettingsModal: React.FC<Props> = ({
  open, onClose, settings, tiktokEvents, twitchEvents, kickEvents,
  tickerMessages, overlayToken,
}) => {
  const { t } = useTranslation();
  const [tab, setTab] = useState<SettingsTab>("settings");

  if (!open) return null;

  return (
    <div className="modal-backdrop" onClick={onClose}>
      <div className="settings-modal" onClick={e => e.stopPropagation()}>
        <div className="settings-modal-header">
          <div className="settings-modal-tabs">
            {TABS.map(item => (
              <button
                key={item.id}
                className={`settings-modal-tab${tab === item.id ? " active" : ""}`}
                onClick={() => setTab(item.id)}
              >
                {item.icon} {t(item.labelKey)}
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
          {tab === "ticker"    && <TickerMessages messages={tickerMessages} />}
        </div>
      </div>
    </div>
  );
};
