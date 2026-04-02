import React, { useState } from "react";
import { api } from "../services/api";

export const ImportPlaylist: React.FC = () => {
  const [url,         setUrl]         = useState("");
  const [requestedBy, setRequestedBy] = useState("Admin");
  const [loading,     setLoading]     = useState(false);
  const [message,     setMessage]     = useState<{ text: string; ok: boolean } | null>(null);

  const handleImport = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!url.trim()) return;
    setLoading(true);
    setMessage(null);
    try {
      const res = await api.importPlaylist(url.trim(), requestedBy);
      setMessage({ text: `✓ ${res.added} canciones agregadas (${res.skipped} omitidas de ${res.total})`, ok: true });
      if (res.added > 0) setUrl("");
    } catch (err: unknown) {
      const msg = err instanceof Error ? err.message : "Error al importar playlist";
      setMessage({ text: msg, ok: false });
    } finally {
      setLoading(false);
    }
  };

  return (
    <form className="import-playlist-form" onSubmit={handleImport}>
      <p className="import-playlist-hint">
        Pega una URL de playlist de YouTube o Spotify para agregar todas sus canciones a la cola (máx. 50).
      </p>
      <div className="form-row">
        <input
          type="text"
          className="input"
          placeholder="https://www.youtube.com/playlist?list=… o https://open.spotify.com/playlist/…"
          value={url}
          onChange={e => setUrl(e.target.value)}
          disabled={loading}
        />
        <input
          type="text"
          className="input input-sm"
          placeholder="Solicitado por"
          value={requestedBy}
          onChange={e => setRequestedBy(e.target.value)}
        />
        <button
          type="submit"
          className="btn btn-primary"
          style={{ width: "100%" }}
          disabled={loading || !url.trim()}
        >
          {loading ? "Importando…" : "Importar playlist"}
        </button>
      </div>
      {message && (
        <div className={`form-message${message.ok ? "" : " form-message-error"}`}>
          {message.text}
        </div>
      )}
    </form>
  );
};
