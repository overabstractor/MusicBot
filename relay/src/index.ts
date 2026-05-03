interface Env {
  RELAY_API_KEY: string;
  SPOTIFY_CLIENT_ID: string;
  SPOTIFY_CLIENT_SECRET: string;
  TWITCH_CLIENT_ID: string;
  TWITCH_CLIENT_SECRET: string;
  KICK_CLIENT_ID: string;
  KICK_CLIENT_SECRET: string;
  GOOGLE_CLIENT_ID: string;
  GOOGLE_CLIENT_SECRET: string;
}

interface TokenBody {
  grant_type: string;
  code?: string;
  code_verifier?: string;
  refresh_token?: string;
  redirect_uri?: string;
}

const CORS_HEADERS = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Methods": "POST, OPTIONS",
  "Access-Control-Allow-Headers": "Content-Type, X-Relay-Key",
};

export default {
  async fetch(request: Request, env: Env): Promise<Response> {
    if (request.method === "OPTIONS") {
      return new Response(null, { headers: CORS_HEADERS });
    }

    const relayKey = request.headers.get("X-Relay-Key");
    if (!relayKey || relayKey !== env.RELAY_API_KEY) {
      return jsonResponse({ error: "Unauthorized" }, 401);
    }

    const url = new URL(request.url);

    // Health check — GET only
    if (url.pathname === "/ping") {
      return jsonResponse({ ok: true });
    }

    if (request.method !== "POST") {
      return jsonResponse({ error: "Method not allowed" }, 405);
    }

    let body: TokenBody;
    try {
      body = await request.json() as TokenBody;
    } catch {
      return jsonResponse({ error: "Invalid JSON body" }, 400);
    }

    switch (url.pathname) {
      case "/token/spotify": return handleSpotify(body, env);
      case "/token/twitch":  return handleTwitch(body, env);
      case "/token/kick":    return handleKick(body, env);
      case "/token/google":  return handleGoogle(body, env);
      default: return jsonResponse({ error: "Not found" }, 404);
    }
  },
};

async function handleSpotify(body: TokenBody, env: Env): Promise<Response> {
  const params = new URLSearchParams({ grant_type: body.grant_type });
  if (body.code)          params.set("code", body.code);
  if (body.redirect_uri)  params.set("redirect_uri", body.redirect_uri);
  if (body.refresh_token) params.set("refresh_token", body.refresh_token);

  const credentials = btoa(`${env.SPOTIFY_CLIENT_ID}:${env.SPOTIFY_CLIENT_SECRET}`);
  const res = await fetch("https://accounts.spotify.com/api/token", {
    method: "POST",
    headers: {
      "Content-Type": "application/x-www-form-urlencoded",
      "Authorization": `Basic ${credentials}`,
    },
    body: params,
  });
  return forwardResponse(res);
}

async function handleTwitch(body: TokenBody, env: Env): Promise<Response> {
  const params = new URLSearchParams({
    grant_type:    body.grant_type,
    client_id:     env.TWITCH_CLIENT_ID,
    client_secret: env.TWITCH_CLIENT_SECRET,
  });
  if (body.code)          params.set("code", body.code);
  if (body.redirect_uri)  params.set("redirect_uri", body.redirect_uri);
  if (body.refresh_token) params.set("refresh_token", body.refresh_token);

  const res = await fetch("https://id.twitch.tv/oauth2/token", {
    method: "POST",
    headers: { "Content-Type": "application/x-www-form-urlencoded" },
    body: params,
  });
  return forwardResponse(res);
}

async function handleKick(body: TokenBody, env: Env): Promise<Response> {
  const params = new URLSearchParams({
    grant_type:    body.grant_type,
    client_id:     env.KICK_CLIENT_ID,
    client_secret: env.KICK_CLIENT_SECRET,
  });
  if (body.code)          params.set("code", body.code);
  if (body.redirect_uri)  params.set("redirect_uri", body.redirect_uri);
  if (body.refresh_token) params.set("refresh_token", body.refresh_token);
  if (body.code_verifier) params.set("code_verifier", body.code_verifier);

  const res = await fetch("https://id.kick.com/oauth/token", {
    method: "POST",
    headers: { "Content-Type": "application/x-www-form-urlencoded" },
    body: params,
  });
  return forwardResponse(res);
}

async function handleGoogle(body: TokenBody, env: Env): Promise<Response> {
  const params = new URLSearchParams({
    grant_type:    body.grant_type,
    client_id:     env.GOOGLE_CLIENT_ID,
    client_secret: env.GOOGLE_CLIENT_SECRET,
  });
  if (body.code)          params.set("code", body.code);
  if (body.redirect_uri)  params.set("redirect_uri", body.redirect_uri);
  if (body.refresh_token) params.set("refresh_token", body.refresh_token);
  if (body.code_verifier) params.set("code_verifier", body.code_verifier);

  const res = await fetch("https://oauth2.googleapis.com/token", {
    method: "POST",
    headers: { "Content-Type": "application/x-www-form-urlencoded" },
    body: params,
  });
  return forwardResponse(res);
}

async function forwardResponse(res: Response): Promise<Response> {
  const text = await res.text();
  return new Response(text, {
    status: res.status,
    headers: { "Content-Type": "application/json", ...CORS_HEADERS },
  });
}

function jsonResponse(data: unknown, status = 200): Response {
  return new Response(JSON.stringify(data), {
    status,
    headers: { "Content-Type": "application/json", ...CORS_HEADERS },
  });
}
