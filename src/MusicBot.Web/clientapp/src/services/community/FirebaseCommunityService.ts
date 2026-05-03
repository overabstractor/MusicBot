import { initializeApp, FirebaseApp, getApps } from "firebase/app";
import {
  getFirestore, Firestore,
  collection, getDocs, addDoc, deleteDoc, doc, getDoc, setDoc, updateDoc,
  query, orderBy, where, runTransaction, Timestamp,
} from "firebase/firestore";
import {
  getAuth, Auth, User,
  GoogleAuthProvider, signInWithCredential, signOut as fbSignOut,
  onAuthStateChanged,
} from "firebase/auth";

import { ICommunityService, NewsItem, CommunityUser } from "./ICommunityService";
import { FeatureRequest, SupportTicket, TicketReply, UserRole, RoleEntry } from "../../types/models";
import { waitForGoogleAuth } from "./googleAuthBus";

const firebaseConfig = {
  apiKey:            "AIzaSyAcgR6ttt-OKIvE-RenmZnSaROsHmtUxqU",
  authDomain:        "musicbot-896cd.firebaseapp.com",
  projectId:         "musicbot-896cd",
  storageBucket:     "musicbot-896cd.firebasestorage.app",
  messagingSenderId: "520541412478",
  appId:             "1:520541412478:web:362373a0136bf280070c2f",
};

function toISOString(value: unknown): string {
  if (value instanceof Timestamp) return value.toDate().toISOString();
  if (typeof value === "string")   return value;
  return new Date().toISOString();
}

function mapUser(user: User | null, role?: UserRole): CommunityUser | null {
  if (!user) return null;
  return { uid: user.uid, displayName: user.displayName, email: user.email, photoURL: user.photoURL, role };
}

export class FirebaseCommunityService implements ICommunityService {
  private app:      FirebaseApp;
  private db:       Firestore;
  private auth:     Auth;
  private provider: GoogleAuthProvider;
  private _currentRole: UserRole | undefined = undefined;

  constructor() {
    this.app      = getApps().length > 0 ? getApps()[0] : initializeApp(firebaseConfig);
    this.db       = getFirestore(this.app);
    this.auth     = getAuth(this.app);
    this.provider = new GoogleAuthProvider();
  }

  // ── Auth ───────────────────────────────────────────────────────────────────

  getCurrentUser(): CommunityUser | null {
    return mapUser(this.auth.currentUser, this._currentRole);
  }

  async signIn(): Promise<void> {
    const resp = await fetch("/api/auth/google/start", { method: "POST" });
    if (!resp.ok) throw new Error("No se pudo iniciar el proceso de login");
    const idToken    = await waitForGoogleAuth();
    const credential = GoogleAuthProvider.credential(idToken);
    await signInWithCredential(this.auth, credential);
  }

  async signOut(): Promise<void> {
    this._currentRole = undefined;
    await fbSignOut(this.auth);
  }

  onAuthChange(callback: (user: CommunityUser | null) => void): () => void {
    return onAuthStateChanged(this.auth, async user => {
      if (!user) {
        this._currentRole = undefined;
        callback(null);
        return;
      }
      // Fetch role from Firestore (best-effort)
      try {
        const snap = await getDoc(doc(this.db, "roles", user.uid));
        this._currentRole = snap.exists() ? (snap.data().role as UserRole) : undefined;
      } catch {
        this._currentRole = undefined;
      }
      callback(mapUser(user, this._currentRole));
    });
  }

  private requireUser(): string {
    const uid = this.auth.currentUser?.uid;
    if (!uid) throw new Error("not-authenticated");
    return uid;
  }

  // ── Roles ──────────────────────────────────────────────────────────────────

  isAdmin(user: CommunityUser | null): boolean {
    return user?.role === "admin";
  }

  isEditor(user: CommunityUser | null): boolean {
    return user?.role === "admin" || user?.role === "editor";
  }

  isSupport(user: CommunityUser | null): boolean {
    return user?.role === "admin" || user?.role === "support";
  }

  async getAllRoles(): Promise<RoleEntry[]> {
    const snap = await getDocs(collection(this.db, "roles"));
    return snap.docs.map(d => ({ uid: d.id, ...d.data() } as RoleEntry));
  }

  async setUserRole(uid: string, role: UserRole, email?: string, displayName?: string): Promise<void> {
    await setDoc(doc(this.db, "roles", uid), { role, email: email ?? null, displayName: displayName ?? null });
  }

  async removeUserRole(uid: string): Promise<void> {
    await deleteDoc(doc(this.db, "roles", uid));
  }

  // ── News ───────────────────────────────────────────────────────────────────

  async getNews(): Promise<NewsItem[]> {
    const snap = await getDocs(query(collection(this.db, "news"), orderBy("date", "desc")));
    return snap.docs.map(d => ({ id: d.id, ...d.data(), date: toISOString(d.data().date) } as NewsItem));
  }

  async createNews(item: Omit<NewsItem, "id">): Promise<NewsItem> {
    const ref = await addDoc(collection(this.db, "news"), {
      ...item,
      date: Timestamp.fromDate(new Date(item.date)),
    });
    return { id: ref.id, ...item };
  }

  async updateNews(id: string, item: Omit<NewsItem, "id">): Promise<NewsItem> {
    const ref = doc(this.db, "news", id);
    await setDoc(ref, { ...item, date: Timestamp.fromDate(new Date(item.date)) });
    return { id, ...item };
  }

  async deleteNews(id: string): Promise<void> {
    await deleteDoc(doc(this.db, "news", id));
  }

  // ── Feature Requests ───────────────────────────────────────────────────────

  async getFeatureRequests(): Promise<FeatureRequest[]> {
    const userId = this.requireUser();
    const [featSnap, votesSnap] = await Promise.all([
      getDocs(query(collection(this.db, "feature_requests"), orderBy("votes", "desc"))),
      getDocs(query(collection(this.db, "feature_votes"), where("userId", "==", userId))),
    ]);
    const votedIds = new Set(votesSnap.docs.map(d => d.data().requestId as string));
    return featSnap.docs.map(d => ({
      id:        d.id,
      hasVoted:  votedIds.has(d.id),
      ...d.data(),
      createdAt: toISOString(d.data().createdAt),
    } as FeatureRequest));
  }

  async createFeatureRequest(title: string, description: string): Promise<FeatureRequest> {
    this.requireUser();
    const ref = await addDoc(collection(this.db, "feature_requests"), {
      title, description, votes: 0, status: "open", createdAt: Timestamp.now(),
    });
    return { id: ref.id, title, description, votes: 0, status: "open", createdAt: new Date().toISOString(), hasVoted: false };
  }

  async voteFeature(id: string): Promise<{ votes: number; hasVoted: boolean }> {
    const userId  = this.requireUser();
    const reqRef  = doc(this.db, "feature_requests", id);
    const voteRef = doc(this.db, "feature_votes", `${id}_${userId}`);

    return runTransaction(this.db, async tx => {
      const [reqSnap, voteSnap] = await Promise.all([tx.get(reqRef), tx.get(voteRef)]);
      const current = (reqSnap.data()?.votes ?? 0) as number;
      if (voteSnap.exists()) {
        tx.delete(voteRef);
        tx.update(reqRef, { votes: Math.max(0, current - 1) });
        return { votes: Math.max(0, current - 1), hasVoted: false };
      }
      tx.set(voteRef, { requestId: id, userId, votedAt: Timestamp.now() });
      tx.update(reqRef, { votes: current + 1 });
      return { votes: current + 1, hasVoted: true };
    });
  }

  async deleteFeatureRequest(id: string): Promise<void> {
    this.requireUser();
    await deleteDoc(doc(this.db, "feature_requests", id));
  }

  // ── Support Tickets (user) ─────────────────────────────────────────────────

  async getTickets(): Promise<SupportTicket[]> {
    const userId = this.requireUser();
    const snap = await getDocs(
      query(
        collection(this.db, "support_tickets"),
        where("userId", "==", userId),
        orderBy("createdAt", "desc"),
      )
    );
    return snap.docs.map(d => this._mapTicket(d.id, d.data()));
  }

  async createTicket(title: string, description: string, category: string): Promise<SupportTicket> {
    const user = this.auth.currentUser;
    const userId = this.requireUser();
    const ref = await addDoc(collection(this.db, "support_tickets"), {
      title, description, category, status: "open",
      userId,
      userDisplayName: user?.displayName ?? null,
      userEmail:       user?.email ?? null,
      createdAt: Timestamp.now(),
    });
    return {
      id: ref.id, title, description, category, status: "open",
      createdAt: new Date().toISOString(),
      userId,
      userDisplayName: user?.displayName ?? undefined,
      userEmail: user?.email ?? undefined,
    };
  }

  async deleteTicket(id: string): Promise<void> {
    this.requireUser();
    await deleteDoc(doc(this.db, "support_tickets", id));
  }

  // ── Support Tickets (staff) ────────────────────────────────────────────────

  async getAllTickets(): Promise<SupportTicket[]> {
    this.requireUser();
    const snap = await getDocs(
      query(collection(this.db, "support_tickets"), orderBy("createdAt", "desc"))
    );
    return snap.docs.map(d => this._mapTicket(d.id, d.data()));
  }

  async replyToTicket(ticketId: string, text: string): Promise<TicketReply> {
    const user = this.auth.currentUser;
    const userId = this.requireUser();
    const isStaff = this._currentRole === "admin" || this._currentRole === "support";
    const ref = await addDoc(
      collection(this.db, "support_tickets", ticketId, "replies"),
      { authorUid: userId, authorName: user?.displayName ?? null, text, isStaff, createdAt: Timestamp.now() }
    );
    // Update ticket's updatedAt
    await updateDoc(doc(this.db, "support_tickets", ticketId), { updatedAt: Timestamp.now() });
    return { id: ref.id, authorUid: userId, authorName: user?.displayName ?? null, text, isStaff, createdAt: new Date().toISOString() };
  }

  async updateTicketStatus(ticketId: string, status: SupportTicket["status"]): Promise<void> {
    this.requireUser();
    await updateDoc(doc(this.db, "support_tickets", ticketId), { status, updatedAt: Timestamp.now() });
  }

  async getTicketReplies(ticketId: string): Promise<TicketReply[]> {
    this.requireUser();
    const snap = await getDocs(
      query(collection(this.db, "support_tickets", ticketId, "replies"), orderBy("createdAt", "asc"))
    );
    return snap.docs.map(d => ({
      id:         d.id,
      ...d.data(),
      createdAt:  toISOString(d.data().createdAt),
    } as TicketReply));
  }

  private _mapTicket(id: string, data: Record<string, unknown>): SupportTicket {
    return {
      id,
      title:           data.title as string,
      description:     data.description as string,
      category:        data.category as string,
      status:          data.status as SupportTicket["status"],
      createdAt:       toISOString(data.createdAt),
      updatedAt:       data.updatedAt ? toISOString(data.updatedAt) : undefined,
      userId:          data.userId as string | undefined,
      userDisplayName: data.userDisplayName as string | undefined,
      userEmail:       data.userEmail as string | undefined,
    };
  }
}
