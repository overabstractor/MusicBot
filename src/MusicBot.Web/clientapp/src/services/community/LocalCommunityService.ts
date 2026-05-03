import { api } from "../api";
import { ICommunityService, NewsItem, CommunityUser } from "./ICommunityService";
import { FeatureRequest, SupportTicket, TicketReply, UserRole, RoleEntry } from "../../types/models";

// In local mode the developer is always "signed in" — no auth needed.
const LOCAL_USER: CommunityUser = {
  uid:         "local",
  displayName: "Admin (local)",
  email:       null,
  photoURL:    null,
};

// Static news — developer updates this array with each release.
const STATIC_NEWS: NewsItem[] = [
  {
    id: "n5",
    title: "Auto-fallback para canciones no disponibles",
    excerpt: "Cuando una canción falla por restricciones de Content ID o región, MusicBot busca automáticamente una versión alternativa y sigue la reproducción sin interrupciones.",
    date: "2025-05-03",
    tag: "novedad",
  },
  {
    id: "n4",
    title: "Cola de fondo separada de la biblioteca",
    excerpt: "La playlist de fondo ahora tiene su propio panel con shuffle, promote y pre-calentamiento automático al reordenar.",
    date: "2025-04-28",
    tag: "mejora",
  },
  {
    id: "n3",
    title: "Descarga en paralelo entre canciones",
    excerpt: "Las siguientes canciones se pre-descargan antes de que terminen las actuales, eliminando los huecos de silencio entre pistas.",
    date: "2025-04-20",
    tag: "arreglado",
  },
  {
    id: "n2",
    title: "Reordenar canciones en la cola con drag & drop",
    excerpt: "Arrastra y suelta canciones en la cola para cambiar su posición. Los cambios se sincronizan en tiempo real con todos los overlays.",
    date: "2025-04-10",
    tag: "novedad",
  },
  {
    id: "n1",
    title: "Soporte para Kick y OAuth de Twitch",
    excerpt: "Conecta tu canal de Kick y autentícate en Twitch con OAuth para que el bot pueda enviar respuestas en el chat.",
    date: "2025-03-22",
    tag: "novedad",
  },
];

export class LocalCommunityService implements ICommunityService {
  // ── Auth ──────────────────────────────────────────────────────────────────
  getCurrentUser(): CommunityUser | null { return LOCAL_USER; }
  async signIn(): Promise<void>          { /* no-op */ }
  async signOut(): Promise<void>         { /* no-op */ }
  onAuthChange(cb: (user: CommunityUser | null) => void): () => void {
    cb(LOCAL_USER);
    return () => {};
  }

  // ── Roles ─────────────────────────────────────────────────────────────────
  isAdmin(_user: CommunityUser | null): boolean  { return false; }
  isEditor(_user: CommunityUser | null): boolean { return false; }
  isSupport(_user: CommunityUser | null): boolean { return false; }
  async getAllRoles(): Promise<RoleEntry[]> { return []; }
  async setUserRole(_uid: string, _role: UserRole): Promise<void> { /* no-op */ }
  async removeUserRole(_uid: string): Promise<void> { /* no-op */ }

  // ── News ──────────────────────────────────────────────────────────────────
  async getNews(): Promise<NewsItem[]> { return STATIC_NEWS; }
  async createNews(_item: Omit<NewsItem, "id">): Promise<NewsItem> { throw new Error("not supported"); }
  async updateNews(_id: string, _item: Omit<NewsItem, "id">): Promise<NewsItem> { throw new Error("not supported"); }
  async deleteNews(_id: string): Promise<void> { /* no-op */ }

  // ── Feature Requests ──────────────────────────────────────────────────────
  async getFeatureRequests(): Promise<FeatureRequest[]> {
    const items = await api.getFeatureRequests();
    return items.map(f => ({ ...f, id: String(f.id) }));
  }

  async createFeatureRequest(title: string, description: string): Promise<FeatureRequest> {
    const f = await api.createFeatureRequest(title, description);
    return { ...f, id: String(f.id) };
  }

  async voteFeature(id: string): Promise<{ votes: number; hasVoted: boolean }> {
    return api.voteFeature(Number(id));
  }

  async deleteFeatureRequest(id: string): Promise<void> {
    return api.deleteFeatureRequest(Number(id));
  }

  // ── Support Tickets (user) ────────────────────────────────────────────────
  async getTickets(): Promise<SupportTicket[]> {
    const items = await api.getSupportTickets();
    return items.map(t => ({ ...t, id: String(t.id) }));
  }

  async createTicket(title: string, description: string, category: string): Promise<SupportTicket> {
    const t = await api.createSupportTicket(title, description, category);
    return { ...t, id: String(t.id) };
  }

  async deleteTicket(id: string): Promise<void> {
    return api.deleteSupportTicket(Number(id));
  }

  // ── Support Tickets (staff) ───────────────────────────────────────────────
  async getAllTickets(): Promise<SupportTicket[]> { return this.getTickets(); }
  async replyToTicket(_ticketId: string, _text: string): Promise<TicketReply> { throw new Error("not supported"); }
  async updateTicketStatus(_ticketId: string, _status: SupportTicket["status"]): Promise<void> { /* no-op */ }
  async getTicketReplies(_ticketId: string): Promise<TicketReply[]> { return []; }
}
