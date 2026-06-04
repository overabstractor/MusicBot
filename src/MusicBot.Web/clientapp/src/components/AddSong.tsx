import React, { useState } from "react";
import { useTranslation } from "react-i18next";
import { api } from "../services/api";

interface Props {
  onAdded: () => void;
}

export const AddSong: React.FC<Props> = ({ onAdded }) => {
  const { t } = useTranslation();
  const [query,       setQuery]       = useState("");
  const [requestedBy, setRequestedBy] = useState("Admin");
  const [loading,     setLoading]     = useState(false);
  const [message,     setMessage]     = useState("");

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!query.trim()) return;
    setLoading(true);
    setMessage("");
    try {
      const result = await api.play(query.trim(), requestedBy);
      setMessage(result.message);
      if (result.success) {
        setQuery("");
        onAdded();
      }
    } catch {
      setMessage(t("addSong.errAdd"));
    } finally {
      setLoading(false);
    }
  };

  return (
    <div className="add-song-wrapper">
      <form className="add-song-form" onSubmit={handleSubmit}>
        <div className="form-row">
          <input
            type="text"
            className="input"
            placeholder={t("addSong.placeholder")}
            value={query}
            onChange={(e) => { setQuery(e.target.value); setMessage(""); }}
            disabled={loading}
            autoComplete="off"
          />
          <input
            type="text"
            className="input input-sm"
            placeholder={t("addSong.requestedBy")}
            value={requestedBy}
            onChange={(e) => setRequestedBy(e.target.value)}
          />
          <button
            type="submit"
            className="btn btn-primary"
            style={{ width: "100%" }}
            disabled={loading || !query.trim()}
          >
            {loading ? t("addSong.adding") : t("common.add")}
          </button>
        </div>
        {message && <div className="form-message">{message}</div>}
      </form>
    </div>
  );
};
