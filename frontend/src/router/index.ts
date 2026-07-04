import { createRouter, createWebHistory } from 'vue-router'

const router = createRouter({
  history: createWebHistory(),
  routes: [
    { path: '/', redirect: '/questions' },
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
  const baseTitle = window.__APP_TITLE__ || 'Ruoyu.Study.QuestionBank.Admin'
  document.title = to.meta.title ? `${to.meta.title} - ${baseTitle}` : baseTitle
  next()
})

export default router
