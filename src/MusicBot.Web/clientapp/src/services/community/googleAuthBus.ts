// Bridge between useSignalR (which receives auth:google-token) and FirebaseCommunityService (which awaits it)

let _resolve: ((idToken: string) => void) | null = null;
let _reject:  ((err: Error)      => void) | null = null;

export function resolveGoogleAuth(idToken: string): void {
  _resolve?.(idToken);
  _resolve = _reject = null;
}

export function waitForGoogleAuth(timeoutMs = 300_000): Promise<string> {
  return new Promise((resolve, reject) => {
    _resolve = resolve;
    _reject  = reject;
    setTimeout(() => {
      if (_reject) {
        _reject(new Error("El inicio de sesión expiró. Inténtalo de nuevo."));
        _resolve = _reject = null;
      }
    }, timeoutMs);
  });
}
