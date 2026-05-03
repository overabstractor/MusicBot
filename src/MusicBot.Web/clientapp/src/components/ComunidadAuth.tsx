import React, { useState } from "react";
import { LogIn, LogOut, Loader } from "lucide-react";
import { communityService } from "../services/community";
import { CommunityUser } from "../services/community/ICommunityService";

interface Props {
  user: CommunityUser | null;
}

export const ComunidadAuth: React.FC<Props> = ({ user }) => {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const handleSignIn = async () => {
    setBusy(true); setError(null);
    try {
      await communityService.signIn();
    } catch (e: unknown) {
      const msg = e instanceof Error ? e.message : "Error al iniciar sesión";
      // Ignore user-cancelled popup
      if (!msg.includes("popup-closed") && !msg.includes("cancelled")) {
        setError("No se pudo iniciar sesión. Inténtalo de nuevo.");
      }
    } finally { setBusy(false); }
  };

  const handleSignOut = async () => {
    setBusy(true);
    try { await communityService.signOut(); }
    catch { }
    finally { setBusy(false); }
  };

  if (user) {
    return (
      <div className="comm-auth-bar comm-auth-bar--in">
        {user.photoURL
          ? <img src={user.photoURL} className="comm-auth-avatar" alt="" referrerPolicy="no-referrer" />
          : <div className="comm-auth-avatar comm-auth-avatar--ph">{(user.displayName ?? user.email ?? "?")[0].toUpperCase()}</div>
        }
        <div className="comm-auth-info">
          <span className="comm-auth-name">{user.displayName ?? user.email}</span>
          <span className="comm-auth-email">{user.email}</span>
        </div>
        <button className="comm-auth-signout" onClick={handleSignOut} disabled={busy} title="Cerrar sesión">
          {busy ? <Loader size={14} className="spin" /> : <LogOut size={14} />}
        </button>
      </div>
    );
  }

  return (
    <div className="comm-auth-bar comm-auth-bar--out">
      <div className="comm-auth-prompt">
        <span className="comm-auth-prompt-text">
          Inicia sesión para votar en solicitudes y enviar tickets
        </span>
        {error && <span className="comm-auth-error">{error}</span>}
      </div>
      <button className="btn btn-primary comm-auth-btn" onClick={handleSignIn} disabled={busy}>
        {busy
          ? <><Loader size={14} className="spin" /> Iniciando…</>
          : <><LogIn size={14} /> Iniciar sesión con Google</>
        }
      </button>
    </div>
  );
};
