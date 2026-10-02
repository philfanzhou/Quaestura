import { reactive, readonly } from 'vue'

const prefix = '/admin/auth/oidc'
const state = reactive({ phase: 'unknown' as 'unknown' | 'allowed' | 'anonymous' | 'denied' | 'unavailable', displayName: '', busy: false, message: '' })
export const authState = readonly(state)
let generation = 0
let sessionFlight: Promise<void> | undefined
let csrfFlight: Promise<string> | undefined
let sessionAbort: AbortController | undefined
let csrfAbort: AbortController | undefined
let csrfToken: string | undefined
let csrfGeneration = 0
let navigationStarted = false
let logoutFlight: Promise<void> | undefined

// Remove the historical credentials without reading, copying, or logging their values.
for (const key of ['quaesturaAuthToken', 'quaesturaAuthExpiresAt']) {
  try { localStorage.removeItem(key) } catch { /* Storage is optional. */ }
}

export function authGeneration() { return generation }
export function isAuthenticated() { return state.phase === 'allowed' }

export function safeReturnPath(raw: unknown): string {
  const fallback = '/questions'
  if (typeof raw !== 'string' || !raw.startsWith('/') || raw.startsWith('//')) return fallback
  try {
    const decoded = decodeURIComponent(raw)
    if (/[\\\u0000-\u001f\u007f]/.test(decoded) || decoded.startsWith('//')) return fallback
    const target = new URL(raw, window.location.origin)
    if (target.origin !== window.location.origin) return fallback
    const path = target.pathname === '/' ? fallback : target.pathname
    if (!['/questions', '/knowledges', '/tags'].includes(path)) return fallback
    const sensitive = /^(?:code|state|nonce|error|error_description|.*token.*|.*secret.*|code_verifier|code_challenge|csrf|authorization|password|credential|logout_handle|client_id|redirect_uri|scope)$/i
    if ([...target.searchParams.keys()].some(key => sensitive.test(key))) return fallback
    if (/(?:^|[?&#])(?:code|state|nonce|error|error_description|[^=&]*token[^=&]*|[^=&]*secret[^=&]*|code_verifier|code_challenge|csrf|authorization|password|credential|logout_handle|client_id|redirect_uri|scope)=/i.test(decodeURIComponent(target.hash))) return fallback
    return path + target.search + target.hash
  } catch { return fallback }
}

export const reasonMessages: Record<string, string> = {
  cancelled: '已取消 SignaCore 登录。',
  denied: '此账号没有管理员权限。',
  signin_failed: 'SignaCore 登录未能完成，请重试。',
  provider_unavailable: 'SignaCore 暂时不可用，请稍后重试。',
  requires_reauthentication: '请重新登录。',
  signed_out: '已退出登录。',
  logout_local_only: '本地已退出，SignaCore 会话可能仍然有效。',
  logout_failed: '退出返回未能确认，本地会话不会恢复。',
}
export function reasonMessage(raw: unknown): string {
  return typeof raw === 'string' && Object.hasOwn(reasonMessages, raw)
    ? reasonMessages[raw]! : raw === undefined ? '' : '登录状态提示无效，请重新确认。'
}

function invalidate(phase: typeof state.phase = 'unknown') {
  generation++
  sessionAbort?.abort()
  csrfAbort?.abort()
  sessionFlight = undefined
  csrfFlight = undefined
  csrfToken = undefined
  state.phase = phase
  state.displayName = ''
}

function navigate(url: string) {
  if (navigationStarted) return
  navigationStarted = true
  window.location.assign(url)
}

export function requireReauthentication(requestGeneration = generation) {
  if (requestGeneration !== generation) return
  invalidate('anonymous')
  if (window.location.pathname !== '/login') {
    navigate('/login?reason=requires_reauthentication&redirect=' + encodeURIComponent(safeReturnPath(window.location.pathname + window.location.search + window.location.hash)))
  }
}

export function observeSession(): Promise<void> {
  if (sessionFlight) return sessionFlight
  if (state.phase !== 'unknown') return Promise.resolve()
  const current = generation
  const controller = new AbortController()
  sessionAbort = controller
  const flight = (async () => {
    try {
      const response = await fetch(prefix + '/session', { credentials: 'same-origin', cache: 'no-store', signal: AbortSignal.any([controller.signal, AbortSignal.timeout(20_000)]) })
      if (!response.ok) throw new Error('Session unavailable.')
      const status = await response.json()
      if (current !== generation) return
      if (typeof status.authenticated !== 'boolean' || typeof status.requiresReauthentication !== 'boolean' || (status.authenticated && typeof status.authorization !== 'number')) throw new Error('Invalid session response.')
      state.phase = status.authenticated ? status.authorization === 0 ? 'allowed' : 'denied' : 'anonymous'
      state.displayName = state.phase === 'allowed' && typeof status.displayName === 'string' ? status.displayName : ''
    } catch {
      if (current === generation) state.phase = 'unavailable'
    } finally {
      if (current === generation) sessionFlight = undefined
    }
  })()
  sessionFlight = flight
  return flight
}

export async function recheckSession() {
  if (state.busy) return
  invalidate()
  await observeSession()
}

function withCancellation<T>(promise: Promise<T>, signal?: AbortSignal): Promise<T> {
  if (!signal) return promise
  if (signal.aborted) return Promise.reject(new Error('请求已取消。'))
  return new Promise((resolve, reject) => {
    const abort = () => reject(new Error('请求已取消。'))
    signal.addEventListener('abort', abort, { once: true })
    promise.then(resolve, reject).finally(() => signal.removeEventListener('abort', abort))
  })
}

export async function getCsrfToken(signal?: AbortSignal): Promise<string> {
  if (!isAuthenticated() || state.busy) throw new Error('请先确认登录状态。')
  if (signal?.aborted) throw new Error('请求已取消。')
  if (csrfToken) return csrfToken
  if (!csrfFlight) {
    const current = generation
    const controller = new AbortController()
    csrfAbort = controller
    const csrfCurrent = ++csrfGeneration
    const flight = (async () => {
      try {
        const response = await fetch(prefix + '/csrf', { credentials: 'same-origin', cache: 'no-store', signal: AbortSignal.any([controller.signal, AbortSignal.timeout(20_000)]) })
        if (response.status === 401) requireReauthentication(current)
        if (!response.ok) throw new Error('CSRF unavailable.')
        const body = await response.json()
        if (current !== generation || csrfCurrent !== csrfGeneration || !isAuthenticated() || typeof body.token !== 'string' || !body.token) throw new Error('CSRF observation expired.')
        csrfToken = body.token
        return body.token as string
      } catch { throw new Error('无法验证请求安全性，请重新确认登录状态。') }
      finally { if (current === generation && csrfCurrent === csrfGeneration) csrfFlight = undefined }
    })()
    csrfFlight = flight
  }
  const flight = csrfFlight
  try { return await withCancellation(flight, signal) }
  catch (error) {
    if (signal?.aborted && csrfFlight === flight) {
      csrfGeneration++
      csrfAbort?.abort()
      csrfFlight = undefined
      csrfToken = undefined
    }
    throw error
  }
}

export function startLogin(target: unknown) {
  if (navigationStarted || state.phase === 'unavailable' || state.phase === 'unknown') return
  navigate(prefix + '/start?returnUrl=' + encodeURIComponent(safeReturnPath(target)))
}

export function logout(signal?: AbortSignal): Promise<void> {
  if (logoutFlight) return logoutFlight
  const flight = performLogout(signal).finally(() => { logoutFlight = undefined })
  logoutFlight = flight
  return flight
}
async function performLogout(signal?: AbortSignal) {
  state.message = ''
  try {
    const tokenPromise = getCsrfToken(signal)
    state.busy = true
    const token = await tokenPromise
    if (signal?.aborted) throw new Error('Cancelled before dispatch.')
    // In-flight observations cannot restore a session after the server may revoke it.
    invalidate()
    state.busy = true
    const response = await fetch(prefix + '/logout', {
      method: 'POST', credentials: 'same-origin', headers: { 'X-SignaCore-CSRF': token },
      signal: signal ? AbortSignal.any([signal, AbortSignal.timeout(20_000)]) : AbortSignal.timeout(20_000),
    })
    if (!response.ok) throw new Error('Logout failed.')
    const result = await response.json()
    if (result.outcome === 'prepared' && typeof result.logoutUrl === 'string') {
      const url = new URL(result.logoutUrl)
      if (!['https:', 'http:'].includes(url.protocol)) throw new Error('Invalid logout response.')
      invalidate('anonymous')
      navigate(result.logoutUrl)
    } else if (result.outcome === 'local_only') {
      invalidate('anonymous')
      navigate('/login?reason=logout_local_only')
    } else throw new Error('Invalid logout response.')
  } catch {
    invalidate()
    await observeSession()
    if (state.phase === 'anonymous') navigate('/login?reason=logout_local_only')
    else state.message = state.phase === 'allowed' ? '退出未能完成，会话仍有效，请重新尝试。' : '退出结果未确认，请重新检查登录状态。'
  } finally { state.busy = false }
}
