# MusicBot OAuth Relay

Un proxy serverless liviano desplegado en Cloudflare Workers que actúa como intermediario seguro entre la aplicación de escritorio MusicBot y los proveedores OAuth (Spotify, Twitch, Kick).

## ¿Por qué existe este relay?

MusicBot es una aplicación de escritorio. Cuando una app de escritorio necesita autenticarse con un servicio externo (Spotify, Twitch, Kick), el flujo OAuth estándar requiere un `client_secret` — una credencial privada que **nunca debería estar en manos del usuario final**.

El problema:

```
Sin relay:
  App de escritorio  →  contiene client_secret  →  cualquier usuario puede extraerlo

Con relay:
  App de escritorio  →  llama al relay (con relay key)  →  relay usa client_secret  →  proveedor OAuth
                         el client_secret NUNCA sale del relay
```

El relay resuelve esto guardando los `client_secret` en Cloudflare (cifrados, nunca visibles) y exponiendo solo tres endpoints de intercambio de tokens protegidos por una clave propia del relay.

---

## Cómo funciona

### Flujo de autenticación (ejemplo con Twitch)

```
1. Usuario hace clic en "Conectar con Twitch" en MusicBot
2. MusicBot abre popup → https://id.twitch.tv/oauth2/authorize?client_id=...
3. Usuario aprueba en Twitch → Twitch redirige a http://localhost:3050/api/auth/twitch/callback?code=ABC
4. El backend de MusicBot recibe el code=ABC
5. Backend llama al relay: POST https://tu-relay.workers.dev/token/twitch
      Header: X-Relay-Key: <clave-del-relay>
      Body: { "grant_type": "authorization_code", "code": "ABC", "redirect_uri": "..." }
6. Relay agrega client_id + client_secret y llama a Twitch
7. Twitch devuelve { access_token, refresh_token, ... }
8. Relay reenvía la respuesta a MusicBot
9. MusicBot guarda los tokens en la base de datos local
```

El `client_secret` nunca sale de Cloudflare. MusicBot solo necesita la URL del relay y su propia clave (`RELAY_API_KEY`).

### Renovación de tokens (refresh)

Cuando un token expira, el mismo flujo aplica pero con `grant_type: "refresh_token"`. El relay agrega el `client_secret` y llama al proveedor.

---

## Estructura del proyecto

```
relay/
├── src/
│   └── index.ts        # Código del Worker — toda la lógica está aquí
├── package.json        # Dependencias de desarrollo (wrangler, typescript)
├── tsconfig.json       # Configuración de TypeScript
├── wrangler.toml       # Configuración de Cloudflare Workers (nombre, entry point)
├── .gitignore          # Excluye node_modules, .dev.vars, .wrangler/
└── README.md           # Este archivo
```

### `src/index.ts` — endpoints expuestos

| Método | Ruta            | Descripción                                      |
|--------|-----------------|--------------------------------------------------|
| GET    | `/ping`         | Health check — verifica que el relay esté activo y la clave sea válida |
| POST   | `/token/spotify`| Intercambia/renueva tokens de Spotify            |
| POST   | `/token/twitch` | Intercambia/renueva tokens de Twitch             |
| POST   | `/token/kick`   | Intercambia/renueva tokens de Kick (con PKCE)    |

Todos los endpoints requieren el header `X-Relay-Key: <RELAY_API_KEY>`.

#### Formato de request para `/token/*`

```json
{
  "grant_type": "authorization_code",
  "code": "el-code-recibido-del-proveedor",
  "redirect_uri": "http://localhost:3050/api/auth/spotify/callback",
  "code_verifier": "solo-para-kick-pkce"
}
```

Para renovar tokens:
```json
{
  "grant_type": "refresh_token",
  "refresh_token": "el-refresh-token-guardado"
}
```

---

## Prerequisitos

- Cuenta de [Cloudflare](https://cloudflare.com) (gratis)
- Node.js 18+ instalado
- Las credenciales de cada plataforma (client_id + client_secret)

---

## Deployment paso a paso

### 1. Instalar Wrangler (CLI de Cloudflare)

```bash
npm install -g wrangler
```

### 2. Autenticarse con Cloudflare

```bash
cd relay
wrangler login
```

Se abrirá una ventana del browser para autorizar. Solo necesitas hacerlo una vez.

### 3. Instalar dependencias locales

```bash
npm install
```

### 4. Publicar el Worker

```bash
wrangler deploy
```

La salida te dará la URL del Worker, algo como:
```
Published musicbot-oauth-relay (1.23 sec)
  https://musicbot-oauth-relay.TU-USUARIO.workers.dev
```

Guarda esa URL — la necesitarás en el paso 6.

### 5. Configurar los secrets

Los secrets se almacenan cifrados en Cloudflare y **nunca** aparecen en el código ni en logs. Wrangler te pedirá el valor de cada uno de forma interactiva.

```bash
# Clave propia del relay — genera un string aleatorio largo (por ejemplo un UUID)
wrangler secret put RELAY_API_KEY

# Spotify (desde https://developer.spotify.com/dashboard)
wrangler secret put SPOTIFY_CLIENT_ID
wrangler secret put SPOTIFY_CLIENT_SECRET

# Twitch (desde https://dev.twitch.tv/console/apps)
wrangler secret put TWITCH_CLIENT_ID
wrangler secret put TWITCH_CLIENT_SECRET

# Kick (desde https://kick.com/settings/developer)
wrangler secret put KICK_CLIENT_ID
wrangler secret put KICK_CLIENT_SECRET
```

Para ver qué secrets están cargados (sin ver los valores):
```bash
wrangler secret list
```

### 6. Configurar MusicBot para usar el relay

En el proyecto .NET, guarda la URL y la clave en user secrets (no en `appsettings.json`):

```bash
cd ..   # volver a la raíz del repo

dotnet user-secrets set "Relay:Url" "https://musicbot-oauth-relay.TU-USUARIO.workers.dev" \
  --project src/MusicBot.Web

dotnet user-secrets set "Relay:ApiKey" "EL-MISMO-RELAY-API-KEY-DEL-PASO-5" \
  --project src/MusicBot.Web
```

### 7. Verificar

Inicia MusicBot y ve a la sección **Plataformas**. La tarjeta **Relay OAuth** debe mostrar el badge **Activo** en verde. Si muestra error, usa el botón **Verificar conexión** para ver el detalle.

---

## Desarrollo local

Para probar el Worker localmente sin publicarlo:

```bash
# Crea el archivo de variables locales (está en .gitignore — no lo subas)
cp .dev.vars.example .dev.vars   # si existe, o créalo manualmente

# Contenido de .dev.vars:
RELAY_API_KEY=test-key-local
SPOTIFY_CLIENT_ID=tu-client-id
SPOTIFY_CLIENT_SECRET=tu-client-secret
TWITCH_CLIENT_ID=tu-client-id
TWITCH_CLIENT_SECRET=tu-client-secret
KICK_CLIENT_ID=tu-client-id
KICK_CLIENT_SECRET=tu-client-secret
```

```bash
wrangler dev
```

El Worker corre en `http://localhost:8787`. Para que MusicBot lo use, cambia `Relay:Url` en user secrets a `http://localhost:8787`.

---

## Actualizar el Worker

Cada vez que modifiques `src/index.ts`, republica con:

```bash
wrangler deploy
```

Los secrets no se tocan al republicar — solo el código cambia.

Para actualizar un secret específico (por ejemplo si rotas la API key de Twitch):

```bash
wrangler secret put TWITCH_CLIENT_SECRET
```

---

## Seguridad

| Amenaza | Mitigación |
|---------|------------|
| Alguien extrae `RELAY_API_KEY` del cliente | La key solo permite llamar al relay, no acceder a las APIs de Spotify/Twitch/Kick directamente. Si se compromete, genera una nueva key y actualízala en Cloudflare y en user secrets. |
| Alguien intercepta el `code` OAuth | El code solo sirve una vez. Sin la `RELAY_API_KEY` no puede canjearlo por tokens. |
| `client_secret` expuesto | Imposible — nunca sale de Cloudflare Workers. Ni Wrangler lo muestra una vez cargado. |
| Abuso del relay para exchange de tokens de otra app | El relay usa sus propios `client_id`/`client_secret`, por lo que el `redirect_uri` debe coincidir exactamente con el registrado en cada plataforma. Nadie puede usarlo para autenticar una app diferente. |

### ¿Qué pasa si rotan las credenciales?

```bash
# Actualizar una sola credencial sin tocar las demás:
wrangler secret put SPOTIFY_CLIENT_SECRET
# Wrangler pide el nuevo valor, luego redespliega automáticamente
```

No es necesario republicar el Worker manualmente — `wrangler secret put` actualiza el secret y el Worker lo toma en el siguiente request.
