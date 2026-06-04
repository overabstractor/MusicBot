import React, { useState, useEffect, useCallback } from "react";
import { useTranslation } from "react-i18next";
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

const STATUS_LABEL_KEYS: Record<string, string> = {
  open:          "community.statusOpen",
  planned:       "community.statusPlanned",
  "in-progress": "community.statusInProgress",
  done:          "community.statusDone",
  rejected:      "community.statusRejected",
};

const NEWS_TAGS = [
  { value: "novedad",   labelKey: "community.tagNovedad"   },
  { value: "mejora",    labelKey: "community.tagMejora"    },
  { value: "arreglado", labelKey: "community.tagArreglado" },
] as const;

const ROLE_LABEL_KEYS: Record<UserRole, string> = {
  admin:   "community.roleAdmin",
  editor:  "community.roleEditor",
  support: "community.roleSupport",
};

const ROLE_OPTIONS: { value: UserRole; labelKey: string }[] = [
  { value: "admin",   labelKey: "community.roleAdmin" },
  { value: "editor",  labelKey: "community.roleEditor" },
  { value: "support", labelKey: "community.roleSupport" },
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
  const { t } = useTranslation();
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
    if (!title.trim()) { setError(t("community.titleRequired")); return; }
    setSaving(true); setError(null);
    try {
      const created = await communityService.createFeatureRequest(title.trim(), description.trim());
      setFeatures(prev => [created, ...prev]);
      setTitle(""); setDescription(""); setShowForm(false);
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : t("community.submitError"));
    } finally { setSaving(false); }
  };

  const handleDeleteFeature = async (id: string) => {
    const ok = await confirm({ title: t("community.deleteFeatureTitle"), message: t("community.actionIrreversible"), confirmText: t("common.delete"), danger: true });
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
    if (!newsForm.title.trim()) { setNewsError(t("community.titleRequired")); return; }
    if (!newsForm.excerpt.trim()) { setNewsError(t("community.excerptRequired")); return; }
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
      setNewsError(err instanceof Error ? err.message : t("community.saveError"));
    } finally { setNewsSaving(false); }
  };

  const handleDeleteNews = async (id: string) => {
    const ok = await confirm({ title: t("community.deleteNewsTitle"), message: t("community.actionIrreversible"), confirmText: t("common.delete"), danger: true });
    if (!ok) return;
    try {
      await communityService.deleteNews(id);
      setNews(prev => prev.filter(n => n.id !== id));
    } catch { }
  };

  // ── Role management handlers ───────────────────────────────────────────────

  const handleAddRole = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!newRoleUid.trim()) { setRoleError(t("community.uidRequired")); return; }
    setRoleSaving(true); setRoleError(null);
    try {
      await communityService.setUserRole(newRoleUid.trim(), newRoleValue, newRoleEmail.trim() || undefined, newRoleName.trim() || undefined);
      setNewRoleUid(""); setNewRoleEmail(""); setNewRoleName("");
      await loadRoles();
    } catch (err: unknown) {
      setRoleError(err instanceof Error ? err.message : t("community.assignRoleError"));
    } finally { setRoleSaving(false); }
  };

  const handleRemoveRole = async (uid: string) => {
    const ok = await confirm({ title: t("community.removeRoleTitle"), message: t("community.removeRoleMsg"), confirmText: t("community.remove"), danger: true });
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
          <Rss size={13} /> {t("community.tabNews")}
        </button>
        <button className={`comunidad-tab${activeTab === "solicitudes" ? " active" : ""}`} onClick={() => setActiveTab("solicitudes")}>
          <Lightbulb size={13} /> {t("community.tabRequests")}
        </button>
        {isAdmin && (
          <button className={`comunidad-tab${activeTab === "equipo" ? " active" : ""}`} onClick={() => setActiveTab("equipo")}>
            <Users size={13} /> {t("community.tabTeam")}
          </button>
        )}
      </div>

      {/* ── Modal de novedad (crear / editar) ── */}
      {isEditor && showNewsForm && (
        <FormModal
          title={editingNewsId ? t("community.editNews") : t("community.newNews")}
          onClose={closeNewsForm}
          width={680}
        >
          <form onSubmit={handleNewsSubmit} style={{ display: "flex", flexDirection: "column", gap: 10 }}>
            <div className="news-admin-form-row">
              <input
                className="input"
                placeholder={t("community.titlePlaceholder")}
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
                {NEWS_TAGS.map(tag => <option key={tag.value} value={tag.value}>{t(tag.labelKey)}</option>)}
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
              placeholder={t("community.excerptPlaceholder")}
              value={newsForm.excerpt}
              onChange={e => setNewsForm(f => ({ ...f, excerpt: e.target.value }))}
              rows={2}
              disabled={newsSaving}
            />

            <div className="news-admin-body-tabs">
              <button type="button" className={`news-admin-body-tab${newsFormTab === "editar" ? " active" : ""}`} onClick={() => setNewsFormTab("editar")}>{t("community.editTab")}</button>
              <button type="button" className={`news-admin-body-tab${newsFormTab === "preview" ? " active" : ""}`} onClick={() => setNewsFormTab("preview")}>{t("community.previewTab")}</button>
              <span className="news-admin-body-hint">{t("community.mdHint")}</span>
            </div>

            {newsFormTab === "editar" ? (
              <textarea
                className="input news-admin-body"
                placeholder={t("community.bodyPlaceholder")}
                value={newsForm.body}
                onChange={e => setNewsForm(f => ({ ...f, body: e.target.value }))}
                rows={12}
                disabled={newsSaving}
              />
            ) : (
              <div className="news-admin-preview">
                {newsForm.body.trim()
                  ? <ReactMarkdown remarkPlugins={[remarkGfm]}>{newsForm.body}</ReactMarkdown>
                  : <span className="news-admin-preview-empty">{t("community.previewEmpty")}</span>
                }
              </div>
            )}

            {newsError && <span className="feature-error">{newsError}</span>}

            <div className="feature-form-actions">
              <button type="button" className="btn btn-outline btn-sm" onClick={closeNewsForm}>{t("common.cancel")}</button>
              <button type="submit" className="btn btn-primary btn-sm" disabled={newsSaving || !newsForm.title.trim() || !newsForm.excerpt.trim()}>
                {newsSaving ? t("common.saving") : editingNewsId ? t("community.saveChanges") : t("community.publishNews")}
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
              <span className={`news-admin-badge role-badge-${user?.role ?? "editor"}`}>{t(ROLE_LABEL_KEYS[user?.role ?? "editor"])}</span>
              <button className="btn btn-primary btn-sm" onClick={() => { closeNewsForm(); setShowNewsForm(true); }}>
                <Plus size={14} /> {t("community.newNews")}
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
              <div className="comunidad-empty-title">{t("community.requestsLoginTitle")}</div>
              <div className="comunidad-empty-sub">{t("community.requestsLoginSub")}</div>
            </div>
          ) : (
            <>
              <div className="feature-toolbar">
                <span className="feature-toolbar-title">
                  {features.length > 0 ? t("community.requestCount", { count: features.length }) : ""}
                </span>
                <button className={`btn btn-primary btn-sm${showForm ? " active" : ""}`} onClick={() => { setShowForm(v => !v); setError(null); }}>
                  <Plus size={14} /> {t("community.newRequest")}
                </button>
              </div>

              {showForm && (
                <form className="feature-form" onSubmit={handleSubmit}>
                  <input className="input" placeholder={t("community.featureTitlePlaceholder")} value={title} onChange={e => setTitle(e.target.value)} autoFocus disabled={saving} />
                  <textarea className="input feature-textarea" placeholder={t("community.featureDescPlaceholder")} value={description} onChange={e => setDescription(e.target.value)} rows={3} disabled={saving} />
                  {error && <span className="feature-error">{error}</span>}
                  <div className="feature-form-actions">
                    <button type="button" className="btn btn-outline btn-sm" onClick={() => { setShowForm(false); setError(null); }}>{t("common.cancel")}</button>
                    <button type="submit" className="btn btn-primary btn-sm" disabled={saving || !title.trim()}>{saving ? t("community.sending") : t("community.sendRequest")}</button>
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
                  <div className="comunidad-empty-title">{t("community.noRequestsTitle")}</div>
                  <div className="comunidad-empty-sub">{t("community.noRequestsSub")}</div>
                </div>
              )}

              {!loading && features.map(f => (
                <div key={f.id} className={`feature-card${f.hasVoted ? " voted" : ""}`}>
                  <button className={`feature-vote-btn${f.hasVoted ? " active" : ""}`} onClick={() => handleVote(f.id)} title={f.hasVoted ? t("community.removeVote") : t("community.vote")}>
                    <ChevronUp size={16} />
                    <span className="feature-vote-count">{f.votes}</span>
                  </button>
                  <div className="feature-card-body">
                    <div className="feature-card-top">
                      <span className="feature-title">{f.title}</span>
                      <div className="feature-card-meta">
                        <span className={`feature-status feature-status-${f.status}`}><Tag size={10} /> {STATUS_LABEL_KEYS[f.status] ? t(STATUS_LABEL_KEYS[f.status]) : f.status}</span>
                        <button className="feature-delete-btn" title={t("common.delete")} onClick={() => handleDeleteFeature(f.id)}><Trash2 size={13} /></button>
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
            <span className="equipo-header-title">{t("community.roleManagement")}</span>
          </div>

          {/* Add role form */}
          <form className="equipo-add-form" onSubmit={handleAddRole}>
            <div className="equipo-add-row">
              <input
                className="input"
                placeholder={t("community.uidPlaceholder")}
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
                {ROLE_OPTIONS.map(r => <option key={r.value} value={r.value}>{t(r.labelKey)}</option>)}
              </select>
            </div>
            <div className="equipo-add-row">
              <input
                className="input"
                placeholder={t("community.emailOptional")}
                value={newRoleEmail}
                onChange={e => setNewRoleEmail(e.target.value)}
                disabled={roleSaving}
              />
              <input
                className="input"
                placeholder={t("community.nameOptional")}
                value={newRoleName}
                onChange={e => setNewRoleName(e.target.value)}
                disabled={roleSaving}
              />
              <button type="submit" className="btn btn-primary btn-sm" disabled={roleSaving || !newRoleUid.trim()}>
                <Plus size={14} /> {roleSaving ? t("common.saving") : t("community.assign")}
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
            <div className="comunidad-empty" style={{ marginTop: 12 }}>{t("community.noRoles")}</div>
          )}

          {!rolesLoading && roles.map(r => (
            <div key={r.uid} className="equipo-role-row">
              <span className={`equipo-role-badge role-badge-${r.role}`}>{t(ROLE_LABEL_KEYS[r.role])}</span>
              <div className="equipo-role-info">
                <span className="equipo-role-name">{r.displayName ?? r.email ?? r.uid}</span>
                {r.email && r.displayName && <span className="equipo-role-email">{r.email}</span>}
                <span className="equipo-role-uid">{r.uid}</span>
              </div>
              <button className="equipo-remove-btn" title={t("community.removeRole")} onClick={() => handleRemoveRole(r.uid)}>
                <X size={14} />
              </button>
            </div>
          ))}
        </div>
      )}
    </div>
  );
};
