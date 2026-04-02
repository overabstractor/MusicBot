export function formatDuration(ms: number): string {
  const totalSeconds = Math.floor(ms / 1000);
  const minutes = Math.floor(totalSeconds / 60);
  const seconds = totalSeconds % 60;
  return `${minutes}:${seconds.toString().padStart(2, "0")}`;
}

export interface PlatformInfo {
  label: string;
  className: string;
}

const PLATFORMS: Record<string, PlatformInfo> = {
  twitch:    { label: "Twitch",  className: "platform-twitch" },
  tiktok:    { label: "TikTok",  className: "platform-tiktok" },
  tikfinity: { label: "TikTok",  className: "platform-tiktok" },
  youtube:   { label: "YouTube", className: "platform-youtube" },
  kick:      { label: "Kick",    className: "platform-kick" },
  web:       { label: "Web",     className: "platform-web" },
  http:      { label: "Web",     className: "platform-web" },
};

export function getPlatform(platform?: string): PlatformInfo | null {
  if (!platform) return null;
  return PLATFORMS[platform.toLowerCase()] ?? { label: platform, className: "platform-web" };
}
