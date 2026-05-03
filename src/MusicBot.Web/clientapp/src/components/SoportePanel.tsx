import React, { useState, useEffect, useCallback } from "react";
import { Plus, Trash2, LifeBuoy, Clock, CheckCircle2, AlertCircle, ChevronDown, ChevronUp, Send, Inbox } from "lucide-react";
import { communityService } from "../services/community";
import { CommunityUser } from "../services/community/ICommunityService";
import { SupportTicket, TicketReply } from "../types/models";
import { useConfirm } from "../hooks/useConfirm";
import { ComunidadAuth } from "./ComunidadAuth";

const CATEGORIES = [
  { value: "general",  label: "General" },
  { value: "bug",      label: "Error / Bug" },
  { value: "question", label: "Pregunta" },
  { value: "feature",  label: "Sugerencia" },
];

const TICKET_STATUSES: { value: SupportTicket["status"]; label: string }[] = [
  { value: "open",        label: "Abierto" },
  { value: "in-progress", label: "En proceso" },
  { value: "resolved",    label: "Resuelto" },
  { value: "closed",      label: "Cerrado" },
];

const STATUS_CONFIG: Record<string, { label: string; icon: React.ReactNode; cls: string }> = {
  open:          { label: "Abierto",    icon: <Clock size={12} />,        cls: "status-open" },
  "in-progress": { label: "En proceso", icon: <AlertCircle size={12} />,  cls: "status-progress" },
  resolved:      { label: "Resuelto",   icon: <CheckCircle2 size={12} />, cls: "status-resolved" },
  closed:        { label: "Cerrado",    icon: <CheckCircle2 size={12} />, cls: "status-closed" },
};

type SoporteView = "form" | "history" | "cola";

interface StaffDetailState {
  ticket: SupportTicket;
  replies: TicketReply[];
  replyText: string;
  replying: boolean;
  statusSaving: boolean;
}

export const SoportePanel: React.FC = () => {
  const [view,        setView]        = useState<SoporteView>("form");
  const [user,        setUser]        = useState<CommunityUser | null>(() => communityService.getCurrentUser());
  const [tickets,     setTickets]     = useState<SupportTicket[]>([]);
  const [loading,     setLoading]     = useState(false);
  const [title,       setTitle]       = useState("");
  const [description, setDescription] = useState("");
  const [category,    setCategory]    = useState("general");
  const [saving,      setSaving]      = useState(false);
  const [error,       setError]       = useState<string | null>(null);
  const [success,     setSuccess]     = useState(false);
  const [confirmModal, confirm] = useConfirm();

  // User ticket thread state
  const [expandedId,      setExpandedId]      = useState<string | null>(null);
  const [threadReplies,   setThreadReplies]   = useState<Record<string, TicketReply[]>>({});
  const [threadLoading,   setThreadLoading]   = useState<Record<string, boolean>>({});
  const [replyTexts,      setReplyTexts]      = useState<Record<string, string>>({});
  const [sendingReply,    setSendingReply]    = useState<Record<string, boolean>>({});

  // Staff detail state
  const [staffDetail, setStaffDetail] = useState<StaffDetailState | null>(null);

  const isSupport = communityService.isSupport(user);

  useEffect(() => communityService.onAuthChange(setUser), []);

  const loadTickets = useCallback(async () => {
    setLoading(true);
    try { setTickets(await communityService.getTickets()); }
    catch { }
    finally { setLoading(false); }
  }, []);

  const loadAllTickets = useCallback(async () => {
    setLoading(true);
    try { setTickets(await communityService.getAllTickets()); }
    catch { }
    finally { setLoading(false); }
  }, []);

  useEffect(() => {
    if (view === "history" && user) loadTickets();
    if (view === "cola" && user && isSupport) loadAllTickets();
    if (view !== "cola") setStaffDetail(null);
    setExpandedId(null);
  }, [view, user, isSupport, loadTickets, loadAllTickets]);

  // ── User: expand ticket and load its thread ───────────────────────────────

  const toggleExpand = async (ticketId: string) => {
    if (expandedId === ticketId) {
      setExpandedId(null);
      return;
    }
    setExpandedId(ticketId);
    if (!threadReplies[ticketId] && !threadLoading[ticketId]) {
      setThreadLoading(prev => ({ ...prev, [ticketId]: true }));
      try {
        const replies = await communityService.getTicketReplies(ticketId);
        setThreadReplies(prev => ({ ...prev, [ticketId]: replies }));
      } catch { }
      finally { setThreadLoading(prev => ({ ...prev, [ticketId]: false })); }
    }
  };

  const handleUserReply = async (ticketId: string) => {
    const text = replyTexts[ticketId]?.trim();
    if (!text) return;
    setSendingReply(prev => ({ ...prev, [ticketId]: true }));
    try {
      const reply = await communityService.replyToTicket(ticketId, text);
      setThreadReplies(prev => ({ ...prev, [ticketId]: [...(prev[ticketId] ?? []), reply] }));
      setReplyTexts(prev => ({ ...prev, [ticketId]: "" }));
    } catch { }
    finally { setSendingReply(prev => ({ ...prev, [ticketId]: false })); }
  };

  // ── User: form handlers ───────────────────────────────────────────────────

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!title.trim()) { setError("El título es obligatorio"); return; }
    if (!description.trim()) { setError("La descripción es obligatoria"); return; }
    setSaving(true); setError(null);
    try {
      await communityService.createTicket(title.trim(), description.trim(), category);
      setTitle(""); setDescription(""); setCategory("general");
      setSuccess(true);
      setTimeout(() => setSuccess(false), 5000);
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : "Error al enviar el ticket");
    } finally { setSaving(false); }
  };

  const handleDelete = async (id: string) => {
    const ok = await confirm({ title: "¿Eliminar ticket?", message: "Se eliminará de tu historial.", confirmText: "Eliminar", danger: true });
    if (!ok) return;
    try {
      await communityService.deleteTicket(id);
      setTickets(prev => prev.filter(t => t.id !== id));
    } catch { }
  };

  // ── Staff: detail handlers ────────────────────────────────────────────────

  const openStaffDetail = async (ticket: SupportTicket) => {
    setStaffDetail({ ticket, replies: [], replyText: "", replying: false, statusSaving: false });
    try {
      const replies = await communityService.getTicketReplies(ticket.id);
      setStaffDetail(d => d ? { ...d, replies } : null);
    } catch { }
  };

  const handleStaffReply = async () => {
    if (!staffDetail || !staffDetail.replyText.trim()) return;
    setStaffDetail(d => d ? { ...d, replying: true } : null);
    try {
      const reply = await communityService.replyToTicket(staffDetail.ticket.id, staffDetail.replyText.trim());
      setStaffDetail(d => d ? { ...d, replies: [...d.replies, reply], replyText: "", replying: false } : null);
    } catch {
      setStaffDetail(d => d ? { ...d, replying: false } : null);
    }
  };

  const handleStatusChange = async (ticketId: string, status: SupportTicket["status"]) => {
    setStaffDetail(d => d ? { ...d, statusSaving: true } : null);
    try {
      await communityService.updateTicketStatus(ticketId, status);
      setTickets(prev => prev.map(t => t.id === ticketId ? { ...t, status } : t));
      setStaffDetail(d => d ? { ...d, ticket: { ...d.ticket, status }, statusSaving: false } : null);
    } catch {
      setStaffDetail(d => d ? { ...d, statusSaving: false } : null);
    }
  };

  const formatDate = (iso: string) =>
    new Date(iso).toLocaleDateString("es-ES", { day: "numeric", month: "long", year: "numeric", hour: "2-digit", minute: "2-digit" });

  const noAuthMessage = (action: string) => (
    <div className="soporte-empty-state">
      <LifeBuoy size={40} className="soporte-empty-icon" />
      <div className="soporte-empty-title">Inicia sesión para {action}</div>
      <div className="soporte-empty-sub">Necesitas una cuenta de Google para usar el soporte.</div>
    </div>
  );

  return (
    <div className="soporte-panel">
      {confirmModal}
      <ComunidadAuth user={user} />

      <div className="soporte-header-tabs">
        <button className={`soporte-header-tab${view === "form" ? " active" : ""}`} onClick={() => setView("form")}>
          <Plus size={13} /> Nuevo ticket
        </button>
        <button className={`soporte-header-tab${view === "history" ? " active" : ""}`} onClick={() => setView("history")}>
          <LifeBuoy size={13} /> Mis tickets
        </button>
        {isSupport && (
          <button className={`soporte-header-tab${view === "cola" ? " active" : ""}`} onClick={() => setView("cola")}>
            <Inbox size={13} /> Cola de soporte
          </button>
        )}
      </div>

      {/* ── NEW TICKET FORM ── */}
      {view === "form" && (
        <div className="soporte-content">
          {!user ? noAuthMessage("enviar tickets") : (
            <>
              <div className="soporte-form-header">
                <h3 className="soporte-form-title">Enviar un ticket de soporte</h3>
                <p className="soporte-form-sub">Describe tu problema o pregunta y lo revisaremos lo antes posible.</p>
              </div>
              {success && (
                <div className="soporte-success">
                  <CheckCircle2 size={16} /> Ticket enviado. Puedes verlo en "Mis tickets".
                </div>
              )}
              <form className="soporte-form" onSubmit={handleSubmit}>
                <label className="soporte-label">Categoría</label>
                <select className="input soporte-select" value={category} onChange={e => setCategory(e.target.value)} disabled={saving}>
                  {CATEGORIES.map(c => <option key={c.value} value={c.value}>{c.label}</option>)}
                </select>
                <label className="soporte-label">Título</label>
                <input className="input" placeholder="Resumen breve del problema…" value={title} onChange={e => setTitle(e.target.value)} disabled={saving} />
                <label className="soporte-label">Descripción</label>
                <textarea
                  className="input soporte-textarea"
                  placeholder="Explica el problema con todo el detalle posible."
                  value={description}
                  onChange={e => setDescription(e.target.value)}
                  rows={5}
                  disabled={saving}
                />
                {error && <span className="soporte-error">{error}</span>}
                <div className="soporte-form-actions">
                  <button type="submit" className="btn btn-primary" disabled={saving || !title.trim() || !description.trim()}>
                    {saving ? "Enviando…" : "Enviar ticket"}
                  </button>
                </div>
              </form>
            </>
          )}
        </div>
      )}

      {/* ── USER TICKET HISTORY ── */}
      {view === "history" && (
        <div className="soporte-content">
          {!user ? noAuthMessage("ver tus tickets") : (
            <>
              {loading && <div className="soporte-empty">Cargando…</div>}
              {!loading && tickets.length === 0 && (
                <div className="soporte-empty-state">
                  <LifeBuoy size={40} className="soporte-empty-icon" />
                  <div className="soporte-empty-title">No has enviado ningún ticket</div>
                  <div className="soporte-empty-sub">Cuando envíes un ticket aparecerá aquí.</div>
                  <button className="btn btn-primary" style={{ marginTop: 16 }} onClick={() => setView("form")}>Crear ticket</button>
                </div>
              )}

              {!loading && tickets.map(t => {
                const cfg      = STATUS_CONFIG[t.status] ?? STATUS_CONFIG.open;
                const catLabel = CATEGORIES.find(c => c.value === t.category)?.label ?? t.category;
                const isOpen   = expandedId === t.id;
                const replies  = threadReplies[t.id] ?? [];
                const isLoading = threadLoading[t.id];

                return (
                  <div key={t.id} className={`ticket-card${isOpen ? " open" : ""}`}>
                    <div className="ticket-card-header" onClick={() => toggleExpand(t.id)}>
                      <div className="ticket-card-left">
                        <span className={`ticket-status ${cfg.cls}`}>{cfg.icon} {cfg.label}</span>
                        <span className="ticket-title">{t.title}</span>
                      </div>
                      <div className="ticket-card-right">
                        <span className="ticket-category">{catLabel}</span>
                        <span className="ticket-date">{formatDate(t.createdAt)}</span>
                        <button className="ticket-expand-btn" title={isOpen ? "Contraer" : "Expandir"}>
                          {isOpen ? <ChevronUp size={14} /> : <ChevronDown size={14} />}
                        </button>
                      </div>
                    </div>

                    {isOpen && (
                      <div className="ticket-card-body">
                        <p className="ticket-description">{t.description}</p>

                        {/* Thread */}
                        {isLoading && <div className="ticket-thread-loading">Cargando conversación…</div>}

                        {!isLoading && replies.length > 0 && (
                          <div className="ticket-thread">
                            {replies.map(r => (
                              <div key={r.id} className={`ticket-thread-msg${r.isStaff ? " staff" : " user-msg"}`}>
                                <div className="ticket-thread-author">
                                  {r.isStaff && <span className="ticket-thread-badge">Soporte</span>}
                                  <span className="ticket-thread-name">{r.authorName ?? (r.isStaff ? "Soporte" : "Tú")}</span>
                                  <span className="ticket-thread-date">{formatDate(r.createdAt)}</span>
                                </div>
                                <p className="ticket-thread-text">{r.text}</p>
                              </div>
                            ))}
                          </div>
                        )}

                        {/* User reply box */}
                        {!isLoading && t.status !== "closed" && t.status !== "resolved" && (
                          <div className="ticket-reply-box">
                            <textarea
                              className="input ticket-reply-input"
                              placeholder="Escribe una respuesta…"
                              rows={2}
                              value={replyTexts[t.id] ?? ""}
                              onChange={e => setReplyTexts(prev => ({ ...prev, [t.id]: e.target.value }))}
                              disabled={sendingReply[t.id]}
                            />
                            <button
                              className="btn btn-primary btn-sm ticket-reply-send"
                              onClick={() => handleUserReply(t.id)}
                              disabled={sendingReply[t.id] || !replyTexts[t.id]?.trim()}
                            >
                              <Send size={13} /> {sendingReply[t.id] ? "Enviando…" : "Responder"}
                            </button>
                          </div>
                        )}

                        <div className="ticket-card-footer">
                          <button className="btn btn-outline btn-sm ticket-delete-btn" onClick={() => handleDelete(t.id)}>
                            <Trash2 size={13} /> Eliminar
                          </button>
                        </div>
                      </div>
                    )}
                  </div>
                );
              })}
            </>
          )}
        </div>
      )}

      {/* ── STAFF QUEUE ── */}
      {view === "cola" && isSupport && (
        <div className="soporte-content soporte-staff">
          {!user ? noAuthMessage("acceder a la cola") : (
            staffDetail ? (
              <div className="staff-detail">
                <button className="staff-detail-back btn btn-outline btn-sm" onClick={() => setStaffDetail(null)}>
                  ← Volver
                </button>
                <div className="staff-detail-header">
                  <div className="staff-detail-meta">
                    <span className={`ticket-status ${STATUS_CONFIG[staffDetail.ticket.status]?.cls ?? "status-open"}`}>
                      {STATUS_CONFIG[staffDetail.ticket.status]?.icon} {STATUS_CONFIG[staffDetail.ticket.status]?.label}
                    </span>
                    <span className="ticket-category">{CATEGORIES.find(c => c.value === staffDetail.ticket.category)?.label ?? staffDetail.ticket.category}</span>
                    <span className="ticket-date">{formatDate(staffDetail.ticket.createdAt)}</span>
                  </div>
                  <h3 className="staff-detail-title">{staffDetail.ticket.title}</h3>
                  {(staffDetail.ticket.userDisplayName || staffDetail.ticket.userEmail) && (
                    <div className="staff-detail-user">
                      <span className="staff-detail-user-name">{staffDetail.ticket.userDisplayName ?? staffDetail.ticket.userEmail}</span>
                      {staffDetail.ticket.userDisplayName && staffDetail.ticket.userEmail && (
                        <span className="staff-detail-user-email">{staffDetail.ticket.userEmail}</span>
                      )}
                    </div>
                  )}
                </div>

                <p className="staff-detail-description">{staffDetail.ticket.description}</p>

                <div className="staff-status-row">
                  <span className="staff-status-label">Cambiar estado:</span>
                  {TICKET_STATUSES.map(s => (
                    <button
                      key={s.value}
                      className={`staff-status-btn${staffDetail.ticket.status === s.value ? " active" : ""}`}
                      onClick={() => handleStatusChange(staffDetail.ticket.id, s.value)}
                      disabled={staffDetail.statusSaving || staffDetail.ticket.status === s.value}
                    >
                      {s.label}
                    </button>
                  ))}
                </div>

                <div className="staff-thread">
                  {staffDetail.replies.map(r => (
                    <div key={r.id} className={`staff-thread-msg${r.isStaff ? " staff" : " user"}`}>
                      <div className="staff-thread-author">
                        {r.isStaff && <span className="staff-thread-badge">Soporte</span>}
                        <span className="staff-thread-name">{r.authorName ?? "Usuario"}</span>
                        <span className="staff-thread-date">{formatDate(r.createdAt)}</span>
                      </div>
                      <p className="staff-thread-text">{r.text}</p>
                    </div>
                  ))}
                </div>

                <div className="staff-reply-box">
                  <textarea
                    className="input staff-reply-input"
                    placeholder="Escribe una respuesta…"
                    rows={3}
                    value={staffDetail.replyText}
                    onChange={e => setStaffDetail(d => d ? { ...d, replyText: e.target.value } : null)}
                    disabled={staffDetail.replying}
                  />
                  <button
                    className="btn btn-primary btn-sm staff-reply-send"
                    onClick={handleStaffReply}
                    disabled={staffDetail.replying || !staffDetail.replyText.trim()}
                  >
                    <Send size={14} /> {staffDetail.replying ? "Enviando…" : "Responder"}
                  </button>
                </div>
              </div>
            ) : (
              <>
                <div className="staff-queue-header">
                  <span className="staff-queue-title">Cola de soporte</span>
                  <span className="staff-queue-count">{tickets.length} ticket{tickets.length !== 1 ? "s" : ""}</span>
                </div>
                {loading && <div className="soporte-empty">Cargando…</div>}
                {!loading && tickets.length === 0 && (
                  <div className="soporte-empty-state">
                    <Inbox size={40} className="soporte-empty-icon" />
                    <div className="soporte-empty-title">No hay tickets pendientes</div>
                    <div className="soporte-empty-sub">Cuando los usuarios envíen tickets aparecerán aquí.</div>
                  </div>
                )}
                {!loading && tickets.map(t => {
                  const cfg      = STATUS_CONFIG[t.status] ?? STATUS_CONFIG.open;
                  const catLabel = CATEGORIES.find(c => c.value === t.category)?.label ?? t.category;
                  return (
                    <div key={t.id} className="staff-ticket-row" onClick={() => openStaffDetail(t)}>
                      <span className={`ticket-status ${cfg.cls}`}>{cfg.icon}</span>
                      <div className="staff-ticket-info">
                        <span className="staff-ticket-title">{t.title}</span>
                        <div className="staff-ticket-meta">
                          <span className="ticket-category">{catLabel}</span>
                          {t.userDisplayName && <span className="staff-ticket-user">{t.userDisplayName}</span>}
                          <span className="ticket-date">{formatDate(t.createdAt)}</span>
                        </div>
                      </div>
                      <ChevronDown size={14} className="staff-ticket-arrow" />
                    </div>
                  );
                })}
              </>
            )
          )}
        </div>
      )}
    </div>
  );
};
