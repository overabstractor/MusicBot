import React, { useState, useEffect, useCallback } from "react";
import { ChevronUp, Plus, Trash2, Rss, Lightbulb, Tag, Users, Shield, X } from "lucide-react";
import { NewsCard } from "./NewsCard";
import { FormModal } from "./FormModal";
import { communityService, NewsItem } from "../services/community";
import { CommunityUser, UserRole, RoleEntry } from "../services/community/ICommunityService";
import { FeatureRequest } from "../types/models";
import { useConfirm } from "../hooks/useConfirm";
import { ComunidadAuth } from "./ComunidadAuth";
import ReactMarkdown from "react-markdown";
import remarkGfm from "remark-gfm";

const STATUS_LABELS: Record<string, string> = {
  open:          "Abierta",
  planned:       "Planificada",
  "in-progress": "En progreso",
  done:          "Lista",
  rejected:      "Rechazada",
};

const NEWS_TAGS = [
  { value: "novedad",   label: "Novedad"   },
  { value: "mejora",    label: "Mejora"    },
  { value: "arreglado", label: "Arreglado" },
] as const;

const ROLE_LABELS: Record<UserRole, string> = {
  admin:   "Admin",
  editor:  "Editor",
  support: "Soporte",
};

const ROLE_OPTIONS: { value: UserRole; label: string }[] = [
  { value: "admin",   label: "Admin" },
  { value: "editor",  label: "Editor" },
  { value: "support", label: "Soporte" },
];

type ComunidadTab = "noticias" | "solicitudes" | "equipo";
type NewsFormTab  = "editar" | "preview";

const emptyNewsForm = () => ({
  title:   "",
  excerpt: "",
  body:    "",
  tag:     "novedad" as NewsItem["tag"],
  date:    new Date().toISOString().slice(0, 10),
});

export const ComunidadPanel: React.FC = () => {
  const [activeTab,    setActiveTab]    = useState<ComunidadTab>("noticias");
  const [user,         setUser]         = useState<CommunityUser | null>(() => communityService.getCurrentUser());
  const [news,         setNews]         = useState<NewsItem[]>([]);
  const [newsLoading,  setNewsLoading]  = useState(true);
  const [features,     setFeatures]     = useState<FeatureRequest[]>([]);
  const [loading,      setLoading]      = useState(false);
  const [showForm,     setShowForm]     = useState(false);
  const [title,        setTitle]        = useState("");
  const [description,  setDescription]  = useState("");
  const [saving,       setSaving]       = useState(false);
  const [error,        setError]        = useState<string | null>(null);
  const [confirmModal, confirm]         = useConfirm();

  // Admin news form
  const [showNewsForm, setShowNewsForm] = useState(false);
  const [editingNewsId, setEditingNewsId] = useState<string | null>(null);
  const [newsForm,     setNewsForm]     = useState(emptyNewsForm);
  const [newsFormTab,  setNewsFormTab]  = useState<NewsFormTab>("editar");
  const [newsSaving,   setNewsSaving]   = useState(false);
  const [newsError,    setNewsError]    = useState<string | null>(null);

  // Equipo (roles) tab
  const [roles,        setRoles]        = useState<RoleEntry[]>([]);
  const [rolesLoading, setRolesLoading] = useState(false);
  const [newRoleUid,   setNewRoleUid]   = useState("");
  const [newRoleEmail, setNewRoleEmail] = useState("");
  const [newRoleName,  setNewRoleName]  = useState("");
  const [newRoleValue, setNewRoleValue] = useState<UserRole>("editor");
  const [roleSaving,   setRoleSaving]   = useState(false);
  const [roleError,    setRoleError]    = useState<string | null>(null);

  const isAdmin  = communityService.isAdmin(user);
  const isEditor = communityService.isEditor(user);

  useEffect(() => communityService.onAuthChange(setUser), []);

  useEffect(() => {
    setNewsLoading(true);
    communityService.getNews()
      .then(setNews)
      .catch(() => {})
      .finally(() => setNewsLoading(false));
  }, []);

  const loadFeatures = useCallback(async () => {
    setLoading(true);
    try { setFeatures(await communityService.getFeatureRequests()); }
    catch { }
    finally { setLoading(false); }
  }, []);

  const loadRoles = useCallback(async () => {
    setRolesLoading(true);
    try { setRoles(await communityService.getAllRoles()); }
    catch { }
    finally { setRolesLoading(false); }
  }, []);

  useEffect(() => {
    if (activeTab === "solicitudes" && user) loadFeatures();
  }, [activeTab, user, loadFeatures]);

  useEffect(() => {
    if (activeTab === "equipo" && isAdmin) loadRoles();
  }, [activeTab, isAdmin, loadRoles]);

  // ── Feature request handlers ───────────────────────────────────────────────

  const handleVote = async (id: string) => {
    try {
      const r = await communityService.voteFeature(id);
      setFeatures(prev => prev.map(f => f.id === id ? { ...f, votes: r.votes, hasVoted: r.hasVoted } : f));
    } catch { }
  };

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!title.trim()) { setError("El título es obligatorio"); return; }
    setSaving(true); setError(null);
    try {
      const created = await communityService.createFeatureRequest(title.trim(), description.trim());
      setFeatures(prev => [created, ...prev]);
      setTitle(""); setDescription(""); setShowForm(false);
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : "Error al enviar");
    } finally { setSaving(false); }
  };

  const handleDeleteFeature = async (id: string) => {
    const ok = await confirm({ title: "¿Eliminar solicitud?", message: "Esta acción no se puede deshacer.", confirmText: "Eliminar", danger: true });
    if (!ok) return;
    try {
      await communityService.deleteFeatureRequest(id);
      setFeatures(prev => prev.filter(f => f.id !== id));
    } catch { }
  };

  // ── Admin news handlers ────────────────────────────────────────────────────

  const closeNewsForm = () => {
    setShowNewsForm(false);
    setEditingNewsId(null);
    setNewsError(null);
    setNewsFormTab("editar");
    setNewsForm(emptyNewsForm());
  };

  const handleEditNews = (item: NewsItem) => {
    setNewsForm({
      title:   item.title,
      excerpt: item.excerpt,
      body:    item.body ?? "",
      tag:     item.tag,
      date:    item.date.slice(0, 10),
    });
    setEditingNewsId(item.id);
    setShowNewsForm(true);
    setNewsFormTab("editar");
    setNewsError(null);
  };

  const handleNewsSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!newsForm.title.trim()) { setNewsError("El título es obligatorio"); return; }
    if (!newsForm.excerpt.trim()) { setNewsError("El resumen es obligatorio"); return; }
    setNewsSaving(true); setNewsError(null);
    const payload = {
      title:   newsForm.title.trim(),
      excerpt: newsForm.excerpt.trim(),
      body:    newsForm.body.trim() || undefined,
      tag:     newsForm.tag,
      date:    new Date(newsForm.date).toISOString(),
    };
    try {
      if (editingNewsId) {
        const updated = await communityService.updateNews(editingNewsId, payload);
        setNews(prev => prev.map(n => n.id === editingNewsId ? updated : n));
      } else {
        const created = await communityService.createNews(payload);
        setNews(prev => [created, ...prev]);
      }
      closeNewsForm();
    } catch (err: unknown) {
      setNewsError(err instanceof Error ? err.message : "Error al guardar");
    } finally { setNewsSaving(false); }
  };

  const handleDeleteNews = async (id: string) => {
    const ok = await confirm({ title: "¿Eliminar novedad?", message: "Esta acción no se puede deshacer.", confirmText: "Eliminar", danger: true });
    if (!ok) return;
    try {
      await communityService.deleteNews(id);
      setNews(prev => prev.filter(n => n.id !== id));
    } catch { }
  };

  // ── Role management handlers ───────────────────────────────────────────────

  const handleAddRole = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!newRoleUid.trim()) { setRoleError("El UID es obligatorio"); return; }
    setRoleSaving(true); setRoleError(null);
    try {
      await communityService.setUserRole(newRoleUid.trim(), newRoleValue, newRoleEmail.trim() || undefined, newRoleName.trim() || undefined);
      setNewRoleUid(""); setNewRoleEmail(""); setNewRoleName("");
      await loadRoles();
    } catch (err: unknown) {
      setRoleError(err instanceof Error ? err.message : "Error al asignar rol");
    } finally { setRoleSaving(false); }
  };

  const handleRemoveRole = async (uid: string) => {
    const ok = await confirm({ title: "¿Quitar rol?", message: "El usuario perderá sus permisos.", confirmText: "Quitar", danger: true });
    if (!ok) return;
    try {
      await communityService.removeUserRole(uid);
      setRoles(prev => prev.filter(r => r.uid !== uid));
    } catch { }
  };

  return (
    <div className="comunidad-panel">
      {confirmModal}

      <ComunidadAuth user={user} />

      <div className="comunidad-tabs">
        <button className={`comunidad-tab${activeTab === "noticias" ? " active" : ""}`} onClick={() => setActiveTab("noticias")}>
          <Rss size={13} /> Novedades
        </button>
        <button className={`comunidad-tab${activeTab === "solicitudes" ? " active" : ""}`} onClick={() => setActiveTab("solicitudes")}>
          <Lightbulb size={13} /> Solicitudes
        </button>
        {isAdmin && (
          <button className={`comunidad-tab${activeTab === "equipo" ? " active" : ""}`} onClick={() => setActiveTab("equipo")}>
            <Users size={13} /> Equipo
          </button>
        )}
      </div>

      {/* ── Modal de novedad (crear / editar) ── */}
      {isEditor && showNewsForm && (
        <FormModal
          title={editingNewsId ? "Editar novedad" : "Nueva novedad"}
          onClose={closeNewsForm}
          width={680}
        >
          <form onSubmit={handleNewsSubmit} style={{ display: "flex", flexDirection: "column", gap: 10 }}>
            <div className="news-admin-form-row">
              <input
                className="input"
                placeholder="Título…"
                value={newsForm.title}
                onChange={e => setNewsForm(f => ({ ...f, title: e.target.value }))}
                disabled={newsSaving}
                autoFocus
              />
              <select
                className="input news-admin-select"
                value={newsForm.tag}
                onChange={e => setNewsForm(f => ({ ...f, tag: e.target.value as NewsItem["tag"] }))}
                disabled={newsSaving}
              >
                {NEWS_TAGS.map(t => <option key={t.value} value={t.value}>{t.label}</option>)}
              </select>
              <input
                type="date"
                className="input news-admin-date"
                value={newsForm.date}
                onChange={e => setNewsForm(f => ({ ...f, date: e.target.value }))}
                disabled={newsSaving}
              />
            </div>

            <textarea
              className="input"
              placeholder="Resumen (se muestra en la card sin expandir)…"
              value={newsForm.excerpt}
              onChange={e => setNewsForm(f => ({ ...f, excerpt: e.target.value }))}
              rows={2}
              disabled={newsSaving}
            />

            <div className="news-admin-body-tabs">
              <button type="button" className={`news-admin-body-tab${newsFormTab === "editar" ? " active" : ""}`} onClick={() => setNewsFormTab("editar")}>Editar</button>
              <button type="button" className={`news-admin-body-tab${newsFormTab === "preview" ? " active" : ""}`} onClick={() => setNewsFormTab("preview")}>Vista previa</button>
              <span className="news-admin-body-hint">Markdown · imágenes · iframes de YouTube</span>
            </div>

            {newsFormTab === "editar" ? (
              <textarea
                className="input news-admin-body"
                placeholder={"## Título\n\nContenido en **markdown**…\n\n![descripción](url-imagen)\n\n<iframe src=\"https://www.youtube.com/embed/ID\" allowfullscreen></iframe>"}
                value={newsForm.body}
                onChange={e => setNewsForm(f => ({ ...f, body: e.target.value }))}
                rows={12}
                disabled={newsSaving}
              />
            ) : (
              <div className="news-admin-preview">
                {newsForm.body.trim()
                  ? <ReactMarkdown remarkPlugins={[remarkGfm]}>{newsForm.body}</ReactMarkdown>
                  : <span className="news-admin-preview-empty">Sin contenido aún…</span>
                }
              </div>
            )}

            {newsError && <span className="feature-error">{newsError}</span>}

            <div className="feature-form-actions">
              <button type="button" className="btn btn-outline btn-sm" onClick={closeNewsForm}>Cancelar</button>
              <button type="submit" className="btn btn-primary btn-sm" disabled={newsSaving || !newsForm.title.trim() || !newsForm.excerpt.trim()}>
                {newsSaving ? "Guardando…" : editingNewsId ? "Guardar cambios" : "Publicar novedad"}
              </button>
            </div>
          </form>
        </FormModal>
      )}

      {/* ── NOTICIAS ── */}
      {activeTab === "noticias" && (
        <div className="comunidad-content">

          {isEditor && (
            <div className="news-admin-toolbar">
              <span className={`news-admin-badge role-badge-${user?.role ?? "editor"}`}>{ROLE_LABELS[user?.role ?? "editor"]}</span>
              <button className="btn btn-primary btn-sm" onClick={() => { closeNewsForm(); setShowNewsForm(true); }}>
                <Plus size={14} /> Nueva novedad
              </button>
            </div>
          )}

          {newsLoading
            ? Array.from({ length: 4 }, (_, i) => (
                <div key={i} className="comunidad-skeleton-card">
                  <div className="comunidad-skeleton-meta">
                    <div className="sk sk-tag" />
                    <div className="sk sk-date" />
                  </div>
                  <div className="sk sk-title" />
                  <div className="sk sk-line" />
                  <div className="sk sk-line sk-line-short" />
                </div>
              ))
            : news.map(item => (
                <NewsCard
                  key={item.id}
                  item={item}
                  onEdit={isEditor ? () => handleEditNews(item) : undefined}
                  onDelete={isEditor ? () => handleDeleteNews(item.id) : undefined}
                />
              ))
          }
        </div>
      )}

      {/* ── SOLICITUDES ── */}
      {activeTab === "solicitudes" && (
        <div className="comunidad-content">
          {!user ? (
            <div className="comunidad-empty">
              <Lightbulb size={36} className="comunidad-empty-icon" />
              <div className="comunidad-empty-title">Inicia sesión para ver las solicitudes</div>
              <div className="comunidad-empty-sub">Necesitas una cuenta de Google para votar y proponer nuevas funciones.</div>
            </div>
          ) : (
            <>
              <div className="feature-toolbar">
                <span className="feature-toolbar-title">
                  {features.length > 0 ? `${features.length} solicitud${features.length !== 1 ? "es" : ""}` : ""}
                </span>
                <button className={`btn btn-primary btn-sm${showForm ? " active" : ""}`} onClick={() => { setShowForm(v => !v); setError(null); }}>
                  <Plus size={14} /> Nueva solicitud
                </button>
              </div>

              {showForm && (
                <form className="feature-form" onSubmit={handleSubmit}>
                  <input className="input" placeholder="Título de la funcionalidad…" value={title} onChange={e => setTitle(e.target.value)} autoFocus disabled={saving} />
                  <textarea className="input feature-textarea" placeholder="Describe la funcionalidad con más detalle (opcional)…" value={description} onChange={e => setDescription(e.target.value)} rows={3} disabled={saving} />
                  {error && <span className="feature-error">{error}</span>}
                  <div className="feature-form-actions">
                    <button type="button" className="btn btn-outline btn-sm" onClick={() => { setShowForm(false); setError(null); }}>Cancelar</button>
                    <button type="submit" className="btn btn-primary btn-sm" disabled={saving || !title.trim()}>{saving ? "Enviando…" : "Enviar solicitud"}</button>
                  </div>
                </form>
              )}

              {loading && Array.from({ length: 4 }, (_, i) => (
                <div key={i} className="comunidad-skeleton-feature">
                  <div className="sk sk-vote" />
                  <div className="comunidad-skeleton-feature-body">
                    <div className="sk sk-line" />
                    <div className="sk sk-line sk-line-short" />
                  </div>
                </div>
              ))}
              {!loading && features.length === 0 && (
                <div className="comunidad-empty">
                  <Lightbulb size={36} className="comunidad-empty-icon" />
                  <div className="comunidad-empty-title">Aún no hay solicitudes</div>
                  <div className="comunidad-empty-sub">Sé el primero en proponer una nueva funcionalidad.</div>
                </div>
              )}

              {!loading && features.map(f => (
                <div key={f.id} className={`feature-card${f.hasVoted ? " voted" : ""}`}>
                  <button className={`feature-vote-btn${f.hasVoted ? " active" : ""}`} onClick={() => handleVote(f.id)} title={f.hasVoted ? "Quitar voto" : "Votar"}>
                    <ChevronUp size={16} />
                    <span className="feature-vote-count">{f.votes}</span>
                  </button>
                  <div className="feature-card-body">
                    <div className="feature-card-top">
                      <span className="feature-title">{f.title}</span>
                      <div className="feature-card-meta">
                        <span className={`feature-status feature-status-${f.status}`}><Tag size={10} /> {STATUS_LABELS[f.status] ?? f.status}</span>
                        <button className="feature-delete-btn" title="Eliminar" onClick={() => handleDeleteFeature(f.id)}><Trash2 size={13} /></button>
                      </div>
                    </div>
                    {f.description && <p className="feature-description">{f.description}</p>}
                  </div>
                </div>
              ))}
            </>
          )}
        </div>
      )}

      {/* ── EQUIPO (admin only) ── */}
      {activeTab === "equipo" && isAdmin && (
        <div className="comunidad-content">
          <div className="equipo-header">
            <Shield size={14} className="equipo-header-icon" />
            <span className="equipo-header-title">Gestión de roles</span>
          </div>

          {/* Add role form */}
          <form className="equipo-add-form" onSubmit={handleAddRole}>
            <div className="equipo-add-row">
              <input
                className="input"
                placeholder="UID de Firebase…"
                value={newRoleUid}
                onChange={e => setNewRoleUid(e.target.value)}
                disabled={roleSaving}
              />
              <select
                className="input equipo-role-select"
                value={newRoleValue}
                onChange={e => setNewRoleValue(e.target.value as UserRole)}
                disabled={roleSaving}
              >
                {ROLE_OPTIONS.map(r => <option key={r.value} value={r.value}>{r.label}</option>)}
              </select>
            </div>
            <div className="equipo-add-row">
              <input
                className="input"
                placeholder="Email (opcional)…"
                value={newRoleEmail}
                onChange={e => setNewRoleEmail(e.target.value)}
                disabled={roleSaving}
              />
              <input
                className="input"
                placeholder="Nombre (opcional)…"
                value={newRoleName}
                onChange={e => setNewRoleName(e.target.value)}
                disabled={roleSaving}
              />
              <button type="submit" className="btn btn-primary btn-sm" disabled={roleSaving || !newRoleUid.trim()}>
                <Plus size={14} /> {roleSaving ? "Guardando…" : "Asignar"}
              </button>
            </div>
            {roleError && <span className="feature-error">{roleError}</span>}
          </form>

          {rolesLoading && Array.from({ length: 3 }, (_, i) => (
            <div key={i} className="comunidad-skeleton-role">
              <div className="sk sk-tag" />
              <div className="comunidad-skeleton-role-body">
                <div className="sk sk-line" />
                <div className="sk sk-line sk-line-short" />
              </div>
            </div>
          ))}

          {!rolesLoading && roles.length === 0 && (
            <div className="comunidad-empty" style={{ marginTop: 12 }}>No hay roles asignados aún.</div>
          )}

          {!rolesLoading && roles.map(r => (
            <div key={r.uid} className="equipo-role-row">
              <span className={`equipo-role-badge role-badge-${r.role}`}>{ROLE_LABELS[r.role]}</span>
              <div className="equipo-role-info">
                <span className="equipo-role-name">{r.displayName ?? r.email ?? r.uid}</span>
                {r.email && r.displayName && <span className="equipo-role-email">{r.email}</span>}
                <span className="equipo-role-uid">{r.uid}</span>
              </div>
              <button className="equipo-remove-btn" title="Quitar rol" onClick={() => handleRemoveRole(r.uid)}>
                <X size={14} />
              </button>
            </div>
          ))}
        </div>
      )}
    </div>
  );
};
