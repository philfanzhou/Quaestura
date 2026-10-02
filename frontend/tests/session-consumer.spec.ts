import { test, expect, type Page } from '@playwright/test'
const allowed = { authenticated: true, requiresReauthentication: false, displayName: 'Synthetic administrator', authorization: 0 }
const anonymous = { authenticated: false, requiresReauthentication: true, displayName: null, authorization: null }
const dev = 'http://localhost:8091'
async function prepare(page: Page, status = allowed) {
  await page.route('**/admin/auth/oidc/session', route => route.fulfill({ json: status }))
  await page.route('**/admin/auth/oidc/csrf', route => route.fulfill({ json: { token: 'synthetic-csrf' } }))
  await page.route('**/admin/tags**', route => route.fulfill({ json: { data: [], total: 0 } }))
  await page.goto(dev + '/tags')
  if (status.authenticated && status.authorization === 0) await expect(page.getByTitle('退出登录')).toBeVisible()
  else await expect(page.getByRole('button', { name: '使用 SignaCore 登录' })).toBeVisible()
}
async function authResult(page: Page) {
  return page.evaluate(async () => (await import('/src/services/auth.ts')).authState.phase)
}

test('consumer: parallel guards share session, Denied and arbitrary reason never grant admission', async ({ page }) => {
  let count = 0
  await page.route('**/admin/auth/oidc/session', async route => { count++; await new Promise(r => setTimeout(r, 150)); await route.fulfill({ json: allowed }) })
  await page.route('**/admin/tags**', route => route.fulfill({ json: { data: [], total: 0 } }))
  await page.goto(dev + '/tags')
  await page.evaluate(async () => {
    const auth = await import('/src/services/auth.ts')
    await Promise.all([auth.observeSession(), auth.observeSession(), auth.observeSession()])
  })
  expect(count).toBe(1)
  await page.unroute('**/admin/auth/oidc/session')
  await page.route('**/admin/auth/oidc/session', route => route.fulfill({ json: { ...allowed, authorization: 1 } }))
  await page.goto(dev + '/login?reason=signed_out')
  await expect(page.getByRole('status')).toHaveText('此账号没有管理员权限。')
  expect(await authResult(page)).toBe('denied')
})

test('consumer: invalid or repeated redirects/reasons cannot escape or carry auth parameters', async ({ page }) => {
  await prepare(page, anonymous)
  const result = await page.evaluate(async () => {
    const auth = await import('/src/services/auth.ts')
    const invalid: unknown[] = ['https://evil.test', '//evil.test', '/\\evil.test', '/login', '/admin/auth/oidc/start', '/unknown', '/tags\n', '/%2ftags', '/tags?code=synthetic-canary', '/tags?access_token=synthetic-canary', '/tags#state=synthetic-canary', '/tags?csrf=synthetic-canary', '/tags?password=synthetic-canary', ['/tags', '/questions']]
    return { invalid: invalid.map(auth.safeReturnPath), valid: auth.safeReturnPath('/tags?name=hello#catalog'), root: auth.safeReturnPath('/'), reasons: [['cancelled', 'denied'], 'synthetic-canary'].map(auth.reasonMessage) }
  })
  expect(result.invalid.every(value => value === '/questions')).toBe(true)
  expect(result.valid).toBe('/tags?name=hello#catalog')
  expect(result.root).toBe('/questions')
  expect(result.reasons).toEqual(['登录状态提示无效，请重新确认。', '登录状态提示无效，请重新确认。'])
  await page.goto(dev + '/login?reason=synthetic-canary&reason=denied&redirect=%2Ftags&redirect=%2Fquestions')
  await expect(page.getByRole('status')).toHaveText('登录状态提示无效，请重新确认。')
  expect(await page.locator('body').innerText()).not.toContain('synthetic-canary')
})

test('consumer: storage cleanup only removes legacy keys and retains UI preference, including throwing storage', async ({ page }) => {
  await page.addInitScript(() => {
    const storage = Storage.prototype
    const get = storage.getItem
    const set = storage.setItem
    set.call(localStorage, 'quaesturaAuthToken', 'synthetic-old-value')
    set.call(localStorage, 'quaesturaAuthExpiresAt', '1')
    storage.getItem = function(key) { if (key.startsWith('quaesturaAuth')) throw new Error('Legacy read forbidden.'); return get.call(this, key) }
    storage.setItem = function(key, value) { if (key.startsWith('quaesturaAuth')) throw new Error('Legacy write forbidden.'); return set.call(this, key, value) }
    localStorage.setItem('qbSidebarCollapsed', 'true')
  })
  await prepare(page)
  await expect(page.locator('.sidebar')).toHaveClass(/collapsed/)
  expect(await page.evaluate(() => Object.keys(localStorage))).toEqual(['qbSidebarCollapsed'])
  await page.addInitScript(() => {
    Storage.prototype.removeItem = function() { throw new Error('Storage unavailable.') }
    const get = Storage.prototype.getItem
    Storage.prototype.getItem = function(key) { if (key === 'qbSidebarCollapsed' || key.startsWith('quaesturaAuth')) throw new Error('Storage unavailable.'); return get.call(this, key) }
    Storage.prototype.setItem = function() { throw new Error('Storage unavailable.') }
  })
  await page.reload()
  await expect(page.getByTitle('退出登录')).toBeVisible()
})

test('consumer: all service unsafe methods share one CSRF and never send Authorization', async ({ page }) => {
  await prepare(page)
  let csrf = 0
  await page.unroute('**/admin/auth/oidc/csrf')
  await page.route('**/admin/auth/oidc/csrf', async route => { csrf++; await new Promise(r => setTimeout(r, 100)); await route.fulfill({ json: { token: 'synthetic-pair' } }) })
  const writes: { path: string; csrf?: string; auth?: string }[] = []
  await page.route('**/admin/**', async route => {
    const r = route.request()
    if (r.url().includes('/auth/')) return route.fallback()
    if (r.method() === 'GET') return route.fulfill({ json: { data: [], total: 0 } })
    writes.push({ path: new URL(r.url()).pathname, csrf: r.headers()['x-signacore-csrf'], auth: r.headers()['authorization'] })
    await route.fulfill({ json: { success: true } })
  })
  await page.evaluate(async () => {
    const tags = (await import('/src/services/tagApi.ts')).getTagAdminApiClient()
    const knowledge = (await import('/src/services/knowledgeApi.ts')).getKnowledgeAdminApiClient()
    const questions = (await import('/src/services/questionApi.ts')).getQuestionAdminApiClient()
    await Promise.all([tags.upsert({ name: 'synthetic' }), tags.delete('synthetic'), knowledge.upsert({ name: 'synthetic' }), knowledge.delete('synthetic'), questions.delete('synthetic')])
  })
  expect(csrf).toBe(1)
  expect(writes).toHaveLength(5)
  expect(writes.every(r => r.csrf === 'synthetic-pair' && !r.auth)).toBe(true)
})

for (const failure of ['401', '403', 'network', 'cancel']) {
  test(`consumer: ${failure} rejects once with no write replay`, async ({ page }) => {
    await prepare(page)
    let writes = 0
    let loginNavigations = 0
    page.on('request', r => { if (r.isNavigationRequest() && new URL(r.url()).pathname === '/login') loginNavigations++ })
    await page.route('**/admin/tags', async route => {
      writes++
      if (failure === 'network') await route.abort('failed')
      else if (failure === 'cancel') { await new Promise(r => setTimeout(r, 150)); await route.fulfill({ json: { success: true } }).catch(() => {}) }
      else await route.fulfill({ status: Number(failure), json: { title: 'Fixed business failure.' } })
    })
    const pending = page.evaluate(async failure => {
      const client = (await import('/src/services/httpClient.ts')).default
      const abort = new AbortController()
      if (failure === 'cancel') setTimeout(() => abort.abort(), 50)
      const results = await Promise.allSettled([client.post('/admin/tags', { name: 'synthetic' }, { signal: abort.signal }), ...(failure === '401' ? [client.post('/admin/tags', { name: 'synthetic-second' })] : [])])
      return results.map(r => r.status)
    }, failure).catch(() => ['rejected'])
    await pending
    if (failure === '401') { await expect(page).toHaveURL(/\/login\?reason=requires_reauthentication&redirect=/); expect(loginNavigations).toBe(1); expect(writes).toBeLessThanOrEqual(2) }
    else { expect(writes).toBe(1); expect(await authResult(page)).toBe('allowed'); await expect(page).toHaveURL(/\/tags$/) }
  })
}

test('consumer: failed or cancelled CSRF sends no business write, next attempt can obtain a fresh pair', async ({ page }) => {
  await prepare(page)
  let writes = 0
  let reads = 0
  await page.route('**/admin/tags', route => { writes++; return route.fulfill({ json: { success: true } }) })
  await page.unroute('**/admin/auth/oidc/csrf')
  await page.route('**/admin/auth/oidc/csrf', async route => {
    reads++
    if (reads === 1) await route.fulfill({ status: 503, json: {} })
    else { await new Promise(r => setTimeout(r, 100)); await route.fulfill({ json: { token: 'synthetic-fresh' } }) }
  })
  expect(await page.evaluate(async () => {
    const client = (await import('/src/services/httpClient.ts')).default
    try { await client.post('/admin/tags', {}); return false } catch { return true }
  })).toBe(true)
  expect(writes).toBe(0)
  await page.evaluate(async () => {
    const client = (await import('/src/services/httpClient.ts')).default
    const abort = new AbortController(); setTimeout(() => abort.abort(), 20)
    await client.post('/admin/tags', {}, { signal: abort.signal }).catch(() => {})
  })
  expect(writes).toBe(0)
  await page.evaluate(async () => { const client = (await import('/src/services/httpClient.ts')).default; await client.post('/admin/tags', {}) })
  expect(writes).toBe(1)
  expect(reads).toBe(3)
})

for (const outcome of ['403', 'lost-revoked', 'lost-allowed', 'lost-unknown', 'cancel']) {
  test(`consumer: logout ${outcome} checks session once without retry or false success`, async ({ page }) => {
    await prepare(page)
    let posts = 0, sessions = 0
    await page.unroute('**/admin/auth/oidc/session')
    await page.route('**/admin/auth/oidc/session', route => {
      sessions++
      return route.fulfill({ status: outcome === 'lost-unknown' ? 503 : 200, json: outcome === 'lost-revoked' ? anonymous : allowed })
    })
    await page.route('**/admin/auth/oidc/logout', async route => {
      posts++
      if (outcome === '403') await route.fulfill({ status: 403, json: {} })
      else if (outcome === 'cancel') { await new Promise(r => setTimeout(r, 100)); await route.fulfill({ json: { outcome: 'local_only' } }).catch(() => {}) }
      else await route.abort('failed')
    })
    await page.evaluate(async outcome => {
      const auth = await import('/src/services/auth.ts')
      const controller = new AbortController()
      if (outcome === 'cancel') setTimeout(() => controller.abort(), 40)
      await Promise.all([auth.logout(controller.signal), auth.logout(controller.signal)])
    }, outcome).catch(() => {})
    expect(posts).toBe(1)
    if (outcome === 'lost-revoked') { await expect(page).toHaveURL(/reason=logout_local_only/); await expect(page.getByRole('status')).toHaveText('本地已退出，SignaCore 会话可能仍然有效。'); expect(sessions).toBe(2) } // New page reads session again.
    else {
      expect(sessions).toBe(1)
      expect(await authResult(page)).toBe(outcome === 'lost-unknown' ? 'unavailable' : 'allowed')
      await expect(page.getByRole('status')).toHaveText(outcome === 'lost-unknown' ? '退出结果未确认，请重新检查登录状态。' : '退出未能完成，会话仍有效，请重新尝试。')
      if (outcome === 'lost-unknown') await expect(page.getByRole('button', { name: '重新检查登录状态' })).toBeVisible()
    }
  })
}

test('consumer: old session and CSRF observations cannot revive state after 401', async ({ page }) => {
  await prepare(page, anonymous)
  const session = await page.evaluate(async allowed => {
    const auth = await import('/src/services/auth.ts')
    const originalFetch = window.fetch
    let dispatched!: () => void
    const inFlight = new Promise<void>(resolve => { dispatched = resolve })
    let finishOld!: (response: Response) => void
    let requestSignal: AbortSignal | undefined
    let reads = 0
    window.fetch = async (input, init) => {
      if (!String(input).endsWith('/session')) return originalFetch(input, init)
      reads++
      requestSignal = init?.signal ?? undefined
      // Deliberately ignore abort: generation must reject a successfully delivered stale observation.
      return new Promise<Response>(resolve => { finishOld = resolve; dispatched() })
    }
    try {
      const pending = auth.recheckSession()
      await inFlight
      const startedBeforeInvalidation = !requestSignal!.aborted
      auth.requireReauthentication()
      const lateResponse = new Response(JSON.stringify(allowed), { status: 200 })
      finishOld(lateResponse)
      await pending
      return { phase: auth.authState.phase, displayName: auth.authState.displayName, reads, startedBeforeInvalidation, aborted: requestSignal!.aborted, bodyRead: lateResponse.bodyUsed }
    } finally { window.fetch = originalFetch }
  }, allowed)
  expect(session).toEqual({ phase: 'anonymous', displayName: '', reads: 1, startedBeforeInvalidation: true, aborted: true, bodyRead: true })
  await prepare(page)
  const csrf = await page.evaluate(async () => {
    const auth = await import('/src/services/auth.ts')
    const originalFetch = window.fetch
    let dispatched!: () => void
    const inFlight = new Promise<void>(resolve => { dispatched = resolve })
    let finishOld!: (response: Response) => void
    let requestSignal: AbortSignal | undefined
    let reads = 0
    window.fetch = async (input, init) => {
      if (!String(input).endsWith('/csrf')) return originalFetch(input, init)
      reads++
      if (reads !== 1) return new Response(JSON.stringify({ token: 'synthetic-fresh' }), { status: 200 })
      requestSignal = init?.signal ?? undefined
      return new Promise<Response>(resolve => { finishOld = resolve; dispatched() })
    }
    try {
      const pending = auth.getCsrfToken().catch(() => 'rejected')
      await inFlight
      const startedBeforeInvalidation = !requestSignal!.aborted
      // Keep this document in place to inspect the invalidated generation.
      history.replaceState(null, '', '/login')
      auth.requireReauthentication()
      const lateResponse = new Response(JSON.stringify({ token: 'synthetic-old' }), { status: 200 })
      finishOld(lateResponse)
      const oldResult = await pending
      const phaseAfterOld = auth.authState.phase
      const rejectedWhileAnonymous = await auth.getCsrfToken().then(() => false, () => true)
      const readsAfterOld = reads
      await auth.recheckSession()
      const freshResult = await auth.getCsrfToken()
      return { oldResult, phaseAfterOld, rejectedWhileAnonymous, readsAfterOld, freshResult, reads, startedBeforeInvalidation, aborted: requestSignal!.aborted, bodyRead: lateResponse.bodyUsed }
    } finally { window.fetch = originalFetch }
  })
  expect(csrf).toEqual({ oldResult: 'rejected', phaseAfterOld: 'anonymous', rejectedWhileAnonymous: true, readsAfterOld: 1, freshResult: 'synthetic-fresh', reads: 2, startedBeforeInvalidation: true, aborted: true, bodyRead: true })
})

test('consumer: unavailable and restarted/unknown session fail closed with explicit recheck only', async ({ page }) => {
  await page.route('**/admin/auth/oidc/session', route => route.fulfill({ status: 503, json: {} }))
  await page.goto(dev + '/questions')
  await expect(page.getByRole('status')).toHaveText('托管登录暂不可用，请联系管理员确认服务已启用。')
  await expect(page.getByRole('button', { name: '使用 SignaCore 登录' })).toBeDisabled()
  await page.unroute('**/admin/auth/oidc/session')
  await page.route('**/admin/auth/oidc/session', route => route.fulfill({ json: anonymous }))
  await page.getByRole('button', { name: '重新检查登录状态' }).click()
  await expect(page.getByRole('button', { name: '使用 SignaCore 登录' })).toBeEnabled()
})


test('consumer: a logout invalidates older CSRF replies even when transport ignores cancellation', async ({ page }) => {
  await prepare(page)
  const result = await page.evaluate(async () => {
    const auth = await import('/src/services/auth.ts')
    const originalFetch = window.fetch
    let finishOld: ((response: Response) => void) | undefined
    let csrfReads = 0
    window.fetch = async (input, init) => {
      if (String(input).endsWith('/csrf')) {
        csrfReads++
        if (csrfReads === 1) return new Promise<Response>(resolve => { finishOld = resolve })
        return new Response(JSON.stringify({ token: 'synthetic-new-pair' }), { status: 200 })
      }
      if (String(input).endsWith('/logout')) return new Response('{}', { status: 403 })
      return originalFetch(input, init)
    }
    const pending = auth.getCsrfToken().catch(() => 'rejected')
    // A logout joins the CSRF flight; cancelling it invalidates the shared old result.
    const controller = new AbortController()
    const exiting = auth.logout(controller.signal)
    controller.abort()
    await exiting
    finishOld!(new Response(JSON.stringify({ token: 'synthetic-old-pair' }), { status: 200 }))
    const oldResult = await pending
    const freshResult = await auth.getCsrfToken()
    window.fetch = originalFetch
    return { oldResult, freshResult, phase: auth.authState.phase, csrfReads }
  })
  expect(result).toEqual({ oldResult: 'rejected', freshResult: 'synthetic-new-pair', phase: 'allowed', csrfReads: 2 })
})
