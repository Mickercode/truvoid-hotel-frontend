export type AuthProfile = {
  userId: string
  institutionId: string | null
  email: string
  fullName: string | null
  role: string
  institutionName: string
  outletId?: string | null
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

  const response = await fetch(`${API_BASE_URL}${path}`, { ...init, headers })
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
    throw new Error(body?.error ?? body?.message ?? `Request failed (${response.status})`)
  }
  if (response.status === 204) return undefined as T
  return response.json() as Promise<T>
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
