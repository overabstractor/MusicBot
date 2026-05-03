import React from "react";
import { ExternalLink, Heart, Coffee, Star, Zap } from "lucide-react";

interface DonationOption {
  id: string;
  name: string;
  description: string;
  url: string;
  icon: React.ReactNode;
  color: string;
  badge?: string;
}

const SUSCRIPCIONES: DonationOption[] = [
  {
    id: "kofi",
    name: "Ko-fi",
    description: "Apoya con una membresía mensual y ayuda a mantener el desarrollo activo de MusicBot.",
    url: "https://ko-fi.com/overabstractor",
    icon: <Coffee size={22} />,
    color: "#29abe0",
    badge: "Desde $3/mes",
  },
  {
    id: "patreon",
    name: "Patreon",
    description: "Únete como miembro en Patreon y contribuye directamente al desarrollo continuo de MusicBot.",
    url: "https://www.patreon.com/overabstractor",
    icon: <Star size={22} />,
    color: "#ff424d",
    badge: "Membresía mensual",
  },
];

const UNICA: DonationOption[] = [
  {
    id: "paypal",
    name: "PayPal",
    description: "Envía una donación única por el monto que quieras. Rápido, sin compromisos.",
    url: "https://paypal.me/OverAbstractor",
    icon: <Zap size={22} />,
    color: "#009cde",
    badge: "Sin suscripción",
  },
];

export const DonacionesPanel: React.FC = () => {
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
          {p.badge && <span className="donacion-badge">{p.badge}</span>}
        </div>
        <span className="donacion-card-desc">{p.description}</span>
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
        <h2 className="donaciones-title">Apoya MusicBot</h2>
        <p className="donaciones-subtitle">
          MusicBot es un proyecto gratuito hecho con mucho esfuerzo. Si te es útil,
          considera apoyar el desarrollo — con suscripción mensual o una donación única.
        </p>
      </div>

      {/* Suscripciones */}
      <div className="donaciones-section">
        <div className="donaciones-section-header">
          <span className="donaciones-section-title">Suscripciones y membresías</span>
          <span className="donaciones-section-sub">Apoyo recurrente mensual</span>
        </div>
        <div className="donaciones-cards">
          {SUSCRIPCIONES.map(p => renderCard(p, "Suscribirse"))}
        </div>
      </div>

      {/* Donación única */}
      <div className="donaciones-section">
        <div className="donaciones-section-header">
          <span className="donaciones-section-title">Donación única</span>
          <span className="donaciones-section-sub">Sin compromisos, el monto que quieras</span>
        </div>
        <div className="donaciones-cards">
          {UNICA.map(p => renderCard(p, "Donar"))}
        </div>
      </div>

      {/* Footer */}
      <div className="donaciones-note">
        Todas las donaciones son voluntarias y no condicionan el acceso a ninguna funcionalidad. ¡Gracias por tu apoyo!
      </div>
    </div>
  );
};
