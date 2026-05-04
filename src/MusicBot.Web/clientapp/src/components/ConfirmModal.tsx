import React, { useEffect, useRef } from "react";
import { AlertTriangle } from "lucide-react";

interface Props {
  title: string;
  message?: React.ReactNode;
  confirmText?: string;
  cancelText?: string;
  danger?: boolean;
  onConfirm: () => void;
  onCancel: () => void;
}

export const ConfirmModal: React.FC<Props> = ({
  title, message, confirmText = "Confirmar", cancelText = "Cancelar", danger = false, onConfirm, onCancel,
}) => {
  const cancelRef = useRef<HTMLButtonElement>(null);

  useEffect(() => {
    cancelRef.current?.focus();
    const h = (e: KeyboardEvent) => { if (e.key === "Escape") onCancel(); };
    document.addEventListener("keydown", h);
    return () => document.removeEventListener("keydown", h);
  }, [onCancel]);

  return (
    <div className="modal-backdrop" onClick={onCancel}>
      <div className="confirm-modal" onClick={e => e.stopPropagation()}>
        <div className="confirm-modal-body">
          <div className={`confirm-modal-icon-wrap${danger ? " danger" : ""}`}>
            <AlertTriangle size={26} />
          </div>
          <div className="confirm-modal-text">
            <p className="confirm-modal-title">{title}</p>
            {message && <p className="confirm-modal-message">{message}</p>}
          </div>
        </div>
        <div className="confirm-modal-actions">
          <button ref={cancelRef} className="btn btn-outline confirm-modal-btn" onClick={onCancel}>{cancelText}</button>
          <button className={`btn ${danger ? "btn-danger" : "btn-primary"} confirm-modal-btn`} onClick={onConfirm}>
            {confirmText}
          </button>
        </div>
      </div>
    </div>
  );
};
