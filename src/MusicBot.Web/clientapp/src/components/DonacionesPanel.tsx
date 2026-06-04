import React from "react";
import { useTranslation } from "react-i18next";
import { ExternalLink, Heart, Coffee, Star, Zap } from "lucide-react";

interface DonationOption {
  id: string;
  name: string;
  descKey: string;
  url: string;
  icon: React.ReactNode;
  color: string;
  badgeKey?: string;
}

const SUSCRIPCIONES: DonationOption[] = [
  {
    id: "kofi",
    name: "Ko-fi",
    descKey: "donations.kofiDesc",
    url: "https://ko-fi.com/overabstractor",
    icon: <Coffee size={22} />,
    color: "#29abe0",
    badgeKey: "donations.kofiBadge",
  },
  {
    id: "patreon",
    name: "Patreon",
    descKey: "donations.patreonDesc",
    url: "https://www.patreon.com/overabstractor",
    icon: <Star size={22} />,
    color: "#ff424d",
    badgeKey: "donations.patreonBadge",
  },
];

const UNICA: DonationOption[] = [
  {
    id: "paypal",
    name: "PayPal",
    descKey: "donations.paypalDesc",
    url: "https://paypal.me/OverAbstractor",
    icon: <Zap size={22} />,
    color: "#009cde",
    badgeKey: "donations.paypalBadge",
  },
];

export const DonacionesPanel: React.FC = () => {
  const { t } = useTranslation();
  const openUrl = (url: string) => {
    fetch(`/api/auth/open-in-browser?url=${encodeURIComponent(url)}`, { method: "POST" }).catch(() => {
      window.open(url, "_blank");
    });
  };

  const renderCard = (p: DonationOption, cta: string) => (
    <div key={p.id} className="donacion-card">
      <div className="donacion-card-icon" style={{ color: p.color }}>
        {p.icon}
      </div>
      <div className="donacion-card-body">
        <div className="donacion-card-name-row">
          <span className="donacion-card-name">{p.name}</span>
          {p.badgeKey && <span className="donacion-badge">{t(p.badgeKey)}</span>}
        </div>
        <span className="donacion-card-desc">{t(p.descKey)}</span>
      </div>
      <button className="btn btn-outline donacion-btn" onClick={() => openUrl(p.url)}>
        {cta} <ExternalLink size={13} />
      </button>
    </div>
  );

  return (
    <div className="donaciones-panel">
      {/* Header */}
      <div className="donaciones-header">
        <div className="donaciones-icon">
          <Heart size={32} fill="currentColor" />
        </div>
        <h2 className="donaciones-title">{t("donations.title")}</h2>
        <p className="donaciones-subtitle">
          {t("donations.subtitle")}
        </p>
      </div>

      {/* Suscripciones */}
      <div className="donaciones-section">
        <div className="donaciones-section-header">
          <span className="donaciones-section-title">{t("donations.subsTitle")}</span>
          <span className="donaciones-section-sub">{t("donations.subsSub")}</span>
        </div>
        <div className="donaciones-cards">
          {SUSCRIPCIONES.map(p => renderCard(p, t("donations.subscribe")))}
        </div>
      </div>

      {/* Donación única */}
      <div className="donaciones-section">
        <div className="donaciones-section-header">
          <span className="donaciones-section-title">{t("donations.oneTimeTitle")}</span>
          <span className="donaciones-section-sub">{t("donations.oneTimeSub")}</span>
        </div>
        <div className="donaciones-cards">
          {UNICA.map(p => renderCard(p, t("donations.donate")))}
        </div>
      </div>

      {/* Footer */}
      <div className="donaciones-note">
        {t("donations.note")}
      </div>
    </div>
  );
};
