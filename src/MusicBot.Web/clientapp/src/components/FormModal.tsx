import React, { useEffect } from "react";
import { X } from "lucide-react";

interface Props {
  title: string;
  onClose: () => void;
  children: React.ReactNode;
  width?: number;
}

export const FormModal: React.FC<Props> = ({ title, onClose, children, width = 420 }) => {
  useEffect(() => {
    const h = (e: KeyboardEvent) => { if (e.key === "Escape") onClose(); };
    document.addEventListener("keydown", h);
    return () => document.removeEventListener("keydown", h);
  }, [onClose]);

  return (
    <div className="modal-backdrop" onClick={onClose}>
      <div className="form-modal" style={{ width: `min(${width}px, calc(100vw - 32px))` }} onClick={e => e.stopPropagation()}>
        <div className="form-modal-header">
          <span className="form-modal-title">{title}</span>
          <button className="form-modal-close" onClick={onClose} aria-label="Cerrar">
            <X size={18} />
          </button>
        </div>
        <div className="form-modal-body">
          {children}
        </div>
      </div>
    </div>
  );
};
