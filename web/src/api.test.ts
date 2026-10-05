import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { api, ApiError, SESSION_EXPIRED_EVENT, tokenStore } from './api'

// api.ts only reads .ok, .status and .json() from the response, so a small fake is enough
// and keeps the tests independent of whichever fetch implementation the runtime provides.
const jsonResponse = (body: unknown, status = 200) =>
  ({ ok: status >= 200 && status < 300, status, json: async () => body }) as unknown as Response

const authOf = (init?: RequestInit) => new Headers(init?.headers).get('Authorization')

describe('tokenStore', () => {
  beforeEach(() => localStorage.clear())

  it('saves and clears the session tokens', () => {
    tokenStore.save({ accessToken: 'a', refreshToken: 'r', expiresAt: '2030-01-01T00:00:00Z' })
    expect(tokenStore.accessToken).toBe('a')
    expect(tokenStore.refreshToken).toBe('r')
    tokenStore.clear()
    expect(tokenStore.accessToken).toBeNull()
    expect(tokenStore.refreshToken).toBeNull()
  })
})

describe('api request handling', () => {
  beforeEach(() => {
    localStorage.clear()
    vi.restoreAllMocks()
  })
  afterEach(() => vi.unstubAllGlobals())

  it('parses a successful JSON response', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => jsonResponse({ balanceKobo: 500 })))
    await expect(api.get<{ balanceKobo: number }>('/v1/tenant/wallet/balance')).resolves.toEqual({ balanceKobo: 500 })
  })

  it('maps an error body onto ApiError', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => jsonResponse({ error: 'Nope.' }, 403)))
    await expect(api.get('/x')).rejects.toMatchObject({ message: 'Nope.', status: 403 })
  })

  it('refreshes once on 401 and retries the request', async () => {
    tokenStore.save({ accessToken: 'old', refreshToken: 'refresh-1', expiresAt: '2030-01-01T00:00:00Z' })
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input)
      if (url.endsWith('/v1/auth/refresh'))
        return jsonResponse({ accessToken: 'new', refreshToken: 'refresh-2', expiresAt: '2030-01-01T00:00:00Z' })
      return authOf(init) === 'Bearer new' ? jsonResponse({ ok: true }) : jsonResponse({ error: 'expired' }, 401)
    }))

    await expect(api.get<{ ok: boolean }>('/v1/tenant/wallet/balance')).resolves.toEqual({ ok: true })
    expect(tokenStore.accessToken).toBe('new')
    expect(tokenStore.refreshToken).toBe('refresh-2')
  })

  it('clears the session and emits an event when refresh fails', async () => {
    tokenStore.save({ accessToken: 'old', refreshToken: 'refresh-1', expiresAt: '2030-01-01T00:00:00Z' })
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) =>
      String(input).endsWith('/v1/auth/refresh') ? jsonResponse({}, 401) : jsonResponse({ error: 'expired' }, 401)))
    const expired = vi.fn()
    window.addEventListener(SESSION_EXPIRED_EVENT, expired)

    await expect(api.get('/x')).rejects.toBeInstanceOf(ApiError)
    expect(tokenStore.accessToken).toBeNull()
    expect(expired).toHaveBeenCalledTimes(1)
    window.removeEventListener(SESSION_EXPIRED_EVENT, expired)
  })

  it('does not attempt a refresh when there was no access token', async () => {
    const fetchMock = vi.fn(async () => jsonResponse({ error: 'unauthorized' }, 401))
    vi.stubGlobal('fetch', fetchMock)
    await expect(api.get('/x')).rejects.toBeInstanceOf(ApiError)
    expect(fetchMock).toHaveBeenCalledTimes(1)
  })
})
