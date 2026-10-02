export type AuthProfile = {
  userId: string
  institutionId: string | null
  email: string
  fullName: string | null
  role: string
  institutionName: string
  outletId?: string | null
  /** pending until the worker provisions the workspace; null for platform admins */
  organizationStatus?: 'pending' | 'active' | 'suspended' | 'closed' | null
}

export class ApiError extends Error {
  /** body is the parsed JSON error response, when there was one. */
  constructor(message: string, readonly status: number, readonly body: unknown = null) {
    super(message)
  }
}

type Tokens = {
  accessToken: string
  refreshToken: string
  expiresAt: string
}

const configuredApiBase = (import.meta.env.VITE_API_BASE_URL as string | undefined)?.replace(/\/$/, '')
const API_BASE_URL = configuredApiBase ?? (import.meta.env.DEV ? 'http://localhost:5000' : window.location.origin)

const ACCESS_TOKEN_KEY = 'truvoid_access_token'
const REFRESH_TOKEN_KEY = 'truvoid_refresh_token'
const EXPIRY_KEY = 'truvoid_token_expiry'

export const tokenStore = {
  get accessToken() {
    return localStorage.getItem(ACCESS_TOKEN_KEY)
  },
  get refreshToken() {
    return localStorage.getItem(REFRESH_TOKEN_KEY)
  },
  save(tokens: Tokens) {
    localStorage.setItem(ACCESS_TOKEN_KEY, tokens.accessToken)
    localStorage.setItem(REFRESH_TOKEN_KEY, tokens.refreshToken)
    localStorage.setItem(EXPIRY_KEY, tokens.expiresAt)
  },
  clear() {
    localStorage.removeItem(ACCESS_TOKEN_KEY)
    localStorage.removeItem(REFRESH_TOKEN_KEY)
    localStorage.removeItem(EXPIRY_KEY)
  },
}

export const SESSION_EXPIRED_EVENT = 'truvoid:session-expired'

// One refresh at a time. Refresh tokens rotate on every use, so two parallel refreshes
// with the same token would look like token theft to the server.
let refreshing: Promise<boolean> | null = null

function refreshSession(): Promise<boolean> {
  refreshing ??= (async () => {
    const refreshToken = tokenStore.refreshToken
    if (!refreshToken) return false
    try {
      const response = await fetch(`${API_BASE_URL}/v1/auth/refresh`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ refreshToken }),
      })
      if (response.ok) {
        tokenStore.save(await response.json() as Tokens)
        return true
      }
      // Another tab may have rotated it a moment ago and saved the result to shared storage.
      return tokenStore.refreshToken !== refreshToken
    } catch {
      return false
    }
  })().finally(() => { refreshing = null })
  return refreshing
}

async function request<T>(path: string, init: RequestInit = {}, retry = true): Promise<T> {
  const headers = new Headers(init.headers)
  if (!(init.body instanceof FormData)) headers.set('Content-Type', 'application/json')
  const usedToken = tokenStore.accessToken
  if (usedToken) headers.set('Authorization', `Bearer ${usedToken}`)

  let response: Response
  try {
    response = await fetch(`${API_BASE_URL}${path}`, { ...init, headers })
  } catch {
    throw new ApiError("We couldn't reach TruvoID. Check your connection and try again.", 0)
  }
  if (response.status === 401 && retry && usedToken) {
    // Someone else already refreshed while this request was in flight: just retry.
    if (tokenStore.accessToken && tokenStore.accessToken !== usedToken) return request<T>(path, init, false)
    if (await refreshSession()) return request<T>(path, init, false)
    tokenStore.clear()
    window.dispatchEvent(new Event(SESSION_EXPIRED_EVENT))
  }

  if (!response.ok) {
    const body = await response.json().catch(() => null) as { error?: string; message?: string } | null
    throw new ApiError(body?.error ?? body?.message ?? fallbackMessage(response.status), response.status, body)
  }
  if (response.status === 204) return undefined as T
  return response.json() as Promise<T>
}

function fallbackMessage(status: number) {
  if (status === 429) return 'Too many attempts. Please wait a moment and try again.'
  if (status >= 500) return 'TruvoID is having trouble right now. Please try again shortly.'
  return `Request failed (${status}).`
}

export const api = {
  get: <T>(path: string) => request<T>(path),
  post: <T>(path: string, body: unknown) => request<T>(path, { method: 'POST', body: JSON.stringify(body) }),
  put: <T>(path: string, body: unknown) => request<T>(path, { method: 'PUT', body: JSON.stringify(body) }),
  delete: <T>(path: string) => request<T>(path, { method: 'DELETE' }),
  upload: <T>(path: string, body: FormData) => request<T>(path, { method: 'POST', body }),
  login: (email: string, password: string) => request<Tokens>('/v1/auth/login', {
    method: 'POST',
    body: JSON.stringify({ email, password }),
  }),
  profile: () => request<AuthProfile>('/v1/auth/me'),
  /** Revokes this session server-side, then forgets the tokens. Never throws. */
  logout: async () => {
    const refreshToken = tokenStore.refreshToken
    tokenStore.clear()
    if (refreshToken)
      await fetch(`${API_BASE_URL}/v1/auth/logout`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ refreshToken }),
      }).catch(() => undefined)
  },
}
