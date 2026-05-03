import { FeatureRequest, SupportTicket, TicketReply, UserRole, RoleEntry } from "../../types/models";

export type { FeatureRequest, SupportTicket, TicketReply, UserRole, RoleEntry };

export interface NewsItem {
  id: string;
  title: string;
  excerpt: string;
  date: string;
  tag: "novedad" | "mejora" | "arreglado";
  body?: string;
  url?: string;
}

export interface CommunityUser {
  uid: string;
  displayName: string | null;
  email: string | null;
  photoURL: string | null;
  role?: UserRole;
}

export interface ICommunityService {
  // ── Auth ────────────────────────────────────────────────────────────────────
  getCurrentUser(): CommunityUser | null;
  signIn(): Promise<void>;
  signOut(): Promise<void>;
  onAuthChange(callback: (user: CommunityUser | null) => void): () => void;

  // ── Roles ───────────────────────────────────────────────────────────────────
  isAdmin(user: CommunityUser | null): boolean;
  isEditor(user: CommunityUser | null): boolean;
  isSupport(user: CommunityUser | null): boolean;
  /** Admin only: list all role assignments. */
  getAllRoles(): Promise<RoleEntry[]>;
  /** Admin only: assign a role to a user. */
  setUserRole(uid: string, role: UserRole, email?: string, displayName?: string): Promise<void>;
  /** Admin only: remove a user's role. */
  removeUserRole(uid: string): Promise<void>;

  // ── News ────────────────────────────────────────────────────────────────────
  getNews(): Promise<NewsItem[]>;
  createNews(item: Omit<NewsItem, "id">): Promise<NewsItem>;
  updateNews(id: string, item: Omit<NewsItem, "id">): Promise<NewsItem>;
  deleteNews(id: string): Promise<void>;

  // ── Feature Requests ────────────────────────────────────────────────────────
  getFeatureRequests(): Promise<FeatureRequest[]>;
  createFeatureRequest(title: string, description: string): Promise<FeatureRequest>;
  voteFeature(id: string): Promise<{ votes: number; hasVoted: boolean }>;
  deleteFeatureRequest(id: string): Promise<void>;

  // ── Support Tickets (user) ──────────────────────────────────────────────────
  getTickets(): Promise<SupportTicket[]>;
  createTicket(title: string, description: string, category: string): Promise<SupportTicket>;
  deleteTicket(id: string): Promise<void>;

  // ── Support Tickets (staff) ─────────────────────────────────────────────────
  /** Support/admin only: fetch all tickets. */
  getAllTickets(): Promise<SupportTicket[]>;
  /** Support/admin only: reply to a ticket. */
  replyToTicket(ticketId: string, text: string): Promise<TicketReply>;
  /** Support/admin only: change ticket status. */
  updateTicketStatus(ticketId: string, status: SupportTicket["status"]): Promise<void>;
  /** Load replies for a ticket. */
  getTicketReplies(ticketId: string): Promise<TicketReply[]>;
}
