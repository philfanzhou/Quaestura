import { test, expect, type Page } from '@playwright/test'

async function safeLocation(page: Page) {
  const url = new URL(page.url())
  return ['/login', '/tags', '/questions', '/knowledges'].includes(url.pathname) ? url.pathname + url.search + url.hash : url.pathname
}
const provider = 'http://127.0.0.1:5008'
async function mode(signin = 'admin', logout = 'success') {
  await fetch(`${provider}/fixture/mode?signin=${signin}&logout=${logout}`)
}
async function login(page: Page, path = '/tags') {
  await page.goto(path)
  await page.getByRole('button', { name: '使用 SignaCore 登录' }).click()
  await expect.poll(() => safeLocation(page)).toMatch(new RegExp(path.replace(/[?].*/, '') + '$'))
  await expect(page.getByTitle('退出登录')).toBeVisible()
}

test.beforeEach(async () => { await mode() })

test('real Host: deep link, one navigation, callback, all pages, tag CRUD and Cookie only', async ({ page, context }) => {
  const requests: { path: string; auth?: string; csrf?: string; method: string }[] = []
  page.on('request', r => { requests.push({ path: new URL(r.url()).pathname, auth: r.headers()['authorization'], csrf: r.headers()['x-signacore-csrf'], method: r.method() }) })
  await page.goto('/tags?name=browser#catalog')
  await expect.poll(() => safeLocation(page)).toMatch(/\/login\?redirect=/)
  await page.getByRole('button', { name: '使用 SignaCore 登录' }).dblclick({ noWaitAfter: true }).catch(() => {})
  await expect.poll(() => safeLocation(page)).toMatch(/\/tags\?name=browser#catalog$/)
  expect(requests.filter(r => r.path.endsWith('/start'))).toHaveLength(1)
  await page.getByRole('link', { name: '题目管理' }).click()
  await expect.poll(() => safeLocation(page)).toMatch(/\/questions$/)
  await page.getByRole('link', { name: '知识点管理' }).click()
  await expect.poll(() => safeLocation(page)).toMatch(/\/knowledges$/)
  await page.getByRole('link', { name: '标签管理' }).click()
  await page.getByRole('button', { name: '+ 新增标签' }).click()
  await page.getByPlaceholder('标签名称').fill('browser-synthetic-tag')
  await page.getByRole('button', { name: '保存', exact: true }).click()
  let row = page.getByRole('row').filter({ hasText: 'browser-synthetic-tag' })
  await expect(row).toBeVisible()
  await row.getByRole('button', { name: '编辑' }).click()
  await page.getByPlaceholder('标签名称').fill('browser-synthetic-updated')
  await page.getByRole('button', { name: '保存', exact: true }).click()
  row = page.getByRole('row').filter({ hasText: 'browser-synthetic-updated' })
  await row.getByRole('button', { name: '删除', exact: true }).click()
  await page.getByRole('button', { name: '确认删除', exact: true }).click()
  await expect(row).toHaveCount(0)
  await page.reload()
  await expect(page.getByTitle('退出登录')).toBeVisible()
  expect(requests.some(r => r.auth || r.path === '/admin/auth/login')).toBe(false)
  const writes = requests.filter(r => ['POST', 'DELETE'].includes(r.method) && r.path.startsWith('/admin/tags'))
  expect(writes).toHaveLength(3)
  expect(writes.every(r => !!r.csrf)).toBe(true)
  expect(await page.evaluate(() => Object.keys(localStorage))).toEqual([])
  const cookies = await context.cookies()
  const metadata = await (await fetch(provider + '/fixture/status')).json()
  expect(cookies.some(c => c.httpOnly && c.secure && c.name === metadata.sessionCookieName)).toBe(true)
})

for (const [signin, message] of [['nonadmin', '此账号没有管理员权限。'], ['cancelled', '已取消 SignaCore 登录。'], ['bad-nonce', 'SignaCore 登录未能完成，请重试。']]) {
  test(`real Host: ${signin} has fixed presentation and zero added tickets`, async ({ page, context }) => {
    await mode(signin)
    const before = await (await fetch(provider + '/fixture/status')).json()
    await page.goto('/login')
    await page.getByRole('button', { name: '使用 SignaCore 登录' }).click()
    await expect(page.getByRole('status')).toHaveText(message)
    const after = await (await fetch(provider + '/fixture/status')).json()
    expect(after.tickets).toBe(before.tickets)
    expect((await context.cookies()).filter(c => c.name === after.sessionCookieName)).toEqual([])
    expect(await page.locator('body').innerText()).not.toContain('synthetic-code-canary')
  })
}

test('real Host: unavailable provider, JSON status and navigation hints differ only in presentation', async ({ page, request }) => {
  await mode('unreachable')
  const api = await request.get('/admin/auth/oidc/start', { maxRedirects: 0, headers: { Accept: 'application/json' } })
  expect(api.status()).toBe(503)
  expect((await api.json()).quaesturaErrorCode).toBe('QUAESTURA_OIDC_SIGNIN_FAILED')
  await page.goto('/admin/auth/oidc/start')
  await expect(page.getByRole('status')).toHaveText('SignaCore 暂时不可用，请稍后重试。')
})

test('real Host: missing/wrong CSRF has zero tag effect, expiry requires login', async ({ page }) => {
  await login(page)
  const statuses = await page.evaluate(async () => {
    return Promise.all([undefined, 'synthetic-wrong-csrf'].map(async token => {
      const headers: Record<string, string> = { 'Content-Type': 'application/json' }
      if (token) headers['X-SignaCore-CSRF'] = token
      return (await fetch('/admin/tags', { method: 'POST', headers, body: JSON.stringify({ name: 'must-not-exist' }) })).status
    }))
  })
  expect(statuses).toEqual([403, 403])
  await page.reload()
  await expect(page.getByRole('row').filter({ hasText: 'must-not-exist' })).toHaveCount(0)
  await fetch(provider + '/fixture/expire')
  await page.reload()
  await expect.poll(() => safeLocation(page)).toMatch(/\/login\?redirect=/)
  await expect(page.getByRole('button', { name: '使用 SignaCore 登录' })).toBeEnabled()
})

test('real Host: prepared logout once, fixed landing, Cookie removal and replay presentation', async ({ page }) => {
  await login(page)
  let posts = 0
  const destinations: string[] = []
  page.on('request', r => {
    if (r.method() === 'POST' && new URL(r.url()).pathname === '/admin/auth/oidc/logout') posts++
    if (r.isNavigationRequest() && new URL(r.url()).pathname === '/oauth2/logout') destinations.push(r.url())
  })
  await page.getByTitle('退出登录').dblclick({ noWaitAfter: true }).catch(() => {})
  await expect.poll(() => safeLocation(page)).toMatch(/\/login\?reason=signed_out$/)
  await expect(page.getByRole('status')).toHaveText('已退出登录。')
  expect(posts).toBe(1)
  expect(destinations).toHaveLength(1)
  await page.goto('/admin/auth/oidc/logout/return?state=synthetic-replay-canary')
  await expect(page.getByRole('status')).toHaveText('退出返回未能确认，本地会话不会恢复。')
})

test('real Host: failed prepare still revokes local session with fixed warning', async ({ page }) => {
  await mode('admin', 'failed')
  await login(page)
  await page.getByTitle('退出登录').click()
  await expect.poll(() => safeLocation(page)).toMatch(/\/login\?reason=logout_local_only$/)
  await expect(page.getByRole('status')).toHaveText('本地已退出，SignaCore 会话可能仍然有效。')
})


test('real Host: all browser storage operations may throw SecurityError without blocking hosted login', async ({ page }) => {
  await page.addInitScript(() => {
    Object.defineProperty(window, 'localStorage', { get() { throw new DOMException('Storage denied.', 'SecurityError') } })
  })
  await login(page)
  await page.getByTitle('折叠').click()
  await expect(page.locator('.sidebar')).toHaveClass(/collapsed/)
})

test('real Host: a process restart discards an existing Cookie session', async ({ page }) => {
  await login(page)
  await fetch(provider + '/fixture/restart')
  await page.reload()
  await expect(page.getByRole('button', { name: '使用 SignaCore 登录' })).toBeEnabled()
  await expect.poll(() => safeLocation(page)).toMatch(/\/login\?redirect=/)
})


test('real Host: an unknown opaque Cookie requires explicit reauthentication', async ({ page, context }) => {
  await login(page)
  const metadata = await (await fetch(provider + '/fixture/status')).json()
  await context.addCookies([{ name: metadata.sessionCookieName, value: 'synthetic-unknown', url: 'https://127.0.0.1:5009', httpOnly: true, secure: true, sameSite: 'Lax' }])
  await page.reload()
  await expect(page.getByRole('button', { name: '使用 SignaCore 登录' })).toBeEnabled()
})
