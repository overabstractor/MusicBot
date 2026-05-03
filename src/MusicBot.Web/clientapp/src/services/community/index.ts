import { ICommunityService } from "./ICommunityService";
import { FirebaseCommunityService } from "./FirebaseCommunityService";
// import { LocalCommunityService } from "./LocalCommunityService";

// ── Swap here to change backend ───────────────────────────────────────────────
// Firebase (Firestore):    new FirebaseCommunityService()
// Local (SQLite via API):  new LocalCommunityService()
export const communityService: ICommunityService = new FirebaseCommunityService();

export type { ICommunityService };
export type { NewsItem } from "./ICommunityService";
