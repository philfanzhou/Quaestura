import { createRouter, createWebHistory } from 'vue-router'
import { authState, isAuthenticated, observeSession, safeReturnPath } from '../services/auth'

declare module 'vue-router' {
  interface RouteMeta {
    title?: string
    public?: boolean
  }
}

const router = createRouter({
  history: createWebHistory(),
  routes: [
    { path: '/', redirect: '/questions' },
    {
      path: '/login',
      name: 'login',
      component: () => import('../views/LoginView.vue'),
      meta: { title: '登录', public: true },
    },
    {
      path: '/questions',
      name: 'questions',
      component: () => import('../views/QuestionView.vue'),
      meta: { title: '题目管理' },
    },
    {
      path: '/knowledges',
      name: 'knowledges',
      component: () => import('../views/KnowledgeView.vue'),
      meta: { title: '知识点管理' },
    },
    {
      path: '/tags',
      name: 'tags',
      component: () => import('../views/TagView.vue'),
      meta: { title: '标签管理' },
    },
  ],
})

router.beforeEach(async to => {
  const baseTitle = window.__APP_TITLE__ || 'Quaestura Admin'
  document.title = to.meta.title ? `${to.meta.title} - ${baseTitle}` : baseTitle
  if (authState.busy) return false
  await observeSession()
  if (to.meta.public) {
    if (isAuthenticated()) return safeReturnPath(to.query.redirect)
    return true
  }
  if (!isAuthenticated()) {
    return { path: '/login', query: { redirect: safeReturnPath(to.fullPath), ...(authState.phase === 'denied' ? { reason: 'denied' } : {}) } }
  }
  return true
})

export default router
