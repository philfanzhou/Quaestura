import { createRouter, createWebHistory } from 'vue-router'
import { clearAuth, isAuthenticated } from '../services/auth'

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

router.beforeEach((to, _from, next) => {
  const baseTitle = window.__APP_TITLE__ || 'Quaestura Admin'
  document.title = to.meta.title ? `${to.meta.title} - ${baseTitle}` : baseTitle

  const authed = isAuthenticated()
  if (!authed) {
    // Missing or expired token both count as signed out; drop any leftovers.
    clearAuth()
  }

  if (to.meta.public) {
    // Signed-in users should not stay on the login page.
    if (authed) {
      next('/questions')
      return
    }
    next()
    return
  }

  if (!authed) {
    next({ path: '/login', query: { redirect: to.fullPath } })
    return
  }

  next()
})

export default router
