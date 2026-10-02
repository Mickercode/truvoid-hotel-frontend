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

async function request<T>(path: string, init: RequestInit = {}, retry = true): Promise<T> {
  const headers = new Headers(init.headers)
  if (!(init.body instanceof FormData)) headers.set('Content-Type', 'application/json')
  if (tokenStore.accessToken) headers.set('Authorization', `Bearer ${tokenStore.accessToken}`)

  let response: Response
  try {
    response = await fetch(`${API_BASE_URL}${path}`, { ...init, headers })
  } catch {
    throw new ApiError("We couldn't reach TruvoID. Check your connection and try again.", 0)
  }
  if (response.status === 401 && retry && tokenStore.refreshToken && tokenStore.accessToken) {
    const refresh = await fetch(`${API_BASE_URL}/v1/auth/refresh`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ oldAccessToken: tokenStore.accessToken, refreshToken: tokenStore.refreshToken }),
    })
    if (refresh.ok) {
      tokenStore.save(await refresh.json() as Tokens)
      return request<T>(path, init, false)
    }
    tokenStore.clear()
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
}
