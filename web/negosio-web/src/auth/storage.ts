/**
 * Phase 1 token storage.
 *
 * We keep the JWT in localStorage. Trade-offs (documented in the README):
 *  - Pro: dead simple, survives refresh, works with a stateless API and no cookie/CSRF plumbing.
 *  - Con: readable by any JavaScript on the origin, so it is exposed to XSS. There is no refresh
 *    token and no server-side revocation.
 *
 * A hardened production design should move to short-lived access tokens held in memory plus a
 * refresh token in a Secure, HttpOnly, SameSite cookie, with server-side session revocation.
 */
const TOKEN_KEY = 'negosio.accessToken'

export const tokenStorage = {
  get(): string | null {
    try {
      return localStorage.getItem(TOKEN_KEY)
    } catch {
      return null
    }
  },
  set(token: string) {
    try {
      localStorage.setItem(TOKEN_KEY, token)
    } catch {
      /* storage unavailable (private mode) - session simply won't persist */
    }
  },
  clear() {
    try {
      localStorage.removeItem(TOKEN_KEY)
    } catch {
      /* ignore */
    }
  },
}
