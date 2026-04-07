import React, { useState, useCallback } from "react";
import { ConfirmModal } from "../components/ConfirmModal";

export interface ConfirmOptions {
  title: string;
  message?: string;
  confirmText?: string;
  cancelText?: string;
  danger?: boolean;
}

export function useConfirm() {
  const [state, setState] = useState<(ConfirmOptions & { resolve: (v: boolean) => void }) | null>(null);

  const confirm = useCallback((options: ConfirmOptions): Promise<boolean> => {
    return new Promise(resolve => setState({ ...options, resolve }));
  }, []);

  const handleConfirm = () => {
    const resolve = state?.resolve;
    setState(null);
    resolve?.(true);
  };

  const handleCancel = () => {
    const resolve = state?.resolve;
    setState(null);
    resolve?.(false);
  };

  const modal = state ? (
    <ConfirmModal
      title={state.title}
      message={state.message}
      confirmText={state.confirmText}
      cancelText={state.cancelText}
      danger={state.danger ?? false}
      onConfirm={handleConfirm}
      onCancel={handleCancel}
    />
  ) : null;

  return [modal, confirm] as const;
}
