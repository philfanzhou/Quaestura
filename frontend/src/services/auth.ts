const TOKEN_KEY = 'quaesturaAuthToken'
const EXPIRES_AT_KEY = 'quaesturaAuthExpiresAt'

/**
 * Response of POST /admin/auth/login on success.
 * `expiresAt` is a Unix timestamp in seconds (SignaCore contract).
 */
export interface LoginResult {
  success: boolean
  message: string
  accessToken: string
  expiresIn: number
  expiresAt: number
}

export function getAuthToken(): string | null {
  return localStorage.getItem(TOKEN_KEY)
}

/**
 * Signed in only when a token is stored AND its expiresAt has not passed.
 * An expired token counts as signed out; callers must clearAuth() themselves.
 */
export function isAuthenticated(): boolean {
  const token = localStorage.getItem(TOKEN_KEY)
  if (!token) return false
  const raw = localStorage.getItem(EXPIRES_AT_KEY)
  if (raw === null) return false
  const expiresAtSeconds = Number(raw)
  if (!Number.isFinite(expiresAtSeconds)) return false
  return Date.now() < expiresAtSeconds * 1000
}

export function clearAuth() {
  localStorage.removeItem(TOKEN_KEY)
  localStorage.removeItem(EXPIRES_AT_KEY)
}

/**
 * Exchanges credentials for a JWT via the backend login endpoint (#23 contract).
 * Uses fetch instead of the shared httpClient to keep this module free of a
 * circular import (httpClient imports auth for the interceptors).
 * On failure (400/502/503 or network error) throws an Error carrying the
 * backend message and stores nothing.
 */
export async function login(username: string, credential: string): Promise<LoginResult> {
  const response = await fetch('/admin/auth/login', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ username, password: credential }),
  })

  let data: LoginResult | null = null
  try {
    data = (await response.json()) as LoginResult
  } catch {
    data = null
  }

  if (!response.ok || !data || data.success !== true || !data.accessToken) {
    throw new Error(data?.message || 'Login failed.')
  }

  // Both values are written in one synchronous step: a stored token always
  // comes with its expiry, never a partial state.
  localStorage.setItem(TOKEN_KEY, data.accessToken)
  localStorage.setItem(EXPIRES_AT_KEY, String(data.expiresAt))
  return data
}
