<script setup lang="ts">
import { ref, computed } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { clearAuth } from './services/auth'

const route = useRoute()
const router = useRouter()
const appTitle = computed(() => window.__APP_TITLE__ || 'Quaestura Admin')
// Public routes (login) render only <router-view />, without the sidebar layout.
const isPublicRoute = computed(() => route.meta.public === true)

const sidebarOpen = ref(false)
const sidebarCollapsed = ref(localStorage.getItem('qbSidebarCollapsed') === 'true')

interface NavItem {
  path: string
  label: string
  icon: string
}

const navItems: NavItem[] = [
  {
    path: '/questions',
    label: '题目管理',
    icon: '<path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z"/><polyline points="14 2 14 8 20 8"/><line x1="16" y1="13" x2="8" y2="13"/><line x1="16" y1="17" x2="8" y2="17"/>',
  },
  {
    path: '/knowledges',
    label: '知识点管理',
    icon: '<path d="M4 19.5A2.5 2.5 0 0 1 6.5 17H20"/><path d="M6.5 2H20v20H6.5A2.5 2.5 0 0 1 4 19.5v-15A2.5 2.5 0 0 1 6.5 2z"/>',
  },
  {
    path: '/tags',
    label: '标签管理',
    icon: '<path d="M20.59 13.41l-7.17 7.17a2 2 0 0 1-2.83 0L2 12V2h10l8.59 8.59a2 2 0 0 1 0 2.82z"/><line x1="7" y1="7" x2="7.01" y2="7"/>',
  },
]

const currentTitle = computed(() => (route.meta.title as string | undefined) ?? '')

function toggleSidebar() {
  sidebarCollapsed.value = !sidebarCollapsed.value
  localStorage.setItem('qbSidebarCollapsed', String(sidebarCollapsed.value))
}

function closeSidebarMobile() {
  sidebarOpen.value = false
}

function handleLogout() {
  clearAuth()
  router.push('/login')
}
</script>

<template>
  <router-view v-if="isPublicRoute" />
  <div v-else class="admin-layout">
    <aside class="sidebar" :class="{ open: sidebarOpen, collapsed: sidebarCollapsed }">
      <div class="sidebar-header">
        <div class="sidebar-logo">QB</div>
        <span class="sidebar-title">{{ appTitle }}</span>
        <button class="sidebar-toggle" title="折叠" @click="toggleSidebar">
          <svg v-if="sidebarCollapsed" width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
            <polyline points="9 18 15 12 9 6" />
          </svg>
          <svg v-else width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
            <polyline points="15 18 9 12 15 6" />
          </svg>
        </button>
      </div>
      <nav class="sidebar-nav">
        <div class="nav-section">导航</div>
        <router-link
          v-for="item in navItems"
          :key="item.path"
          :to="item.path"
          class="nav-item"
          :class="{ active: route.path === item.path }"
          @click="closeSidebarMobile"
        >
          <span class="nav-icon">
            <svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
              <path v-html="item.icon" />
            </svg>
          </span>
          <span class="nav-label">{{ item.label }}</span>
        </router-link>
      </nav>
      <div class="sidebar-footer">
        <div class="sidebar-footer-user">
          <div class="sidebar-footer-avatar">A</div>
          <div class="sidebar-footer-info">
            <div class="sidebar-footer-name">管理员</div>
            <div class="sidebar-footer-status">内网环境</div>
          </div>
        </div>
      </div>
    </aside>

    <div class="sidebar-overlay" :class="{ visible: sidebarOpen }" @click="sidebarOpen = false"></div>

    <div class="main-content" :class="{ 'sidebar-collapsed': sidebarCollapsed }">
      <header class="top-header">
        <div class="header-left">
          <button class="sidebar-toggle-btn" @click="sidebarCollapsed ? toggleSidebar() : (sidebarOpen = !sidebarOpen)">
            <svg v-if="sidebarCollapsed" width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
              <polyline points="9 18 15 12 9 6" />
            </svg>
            <svg v-else width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
              <line x1="3" y1="12" x2="21" y2="12" />
              <line x1="3" y1="6" x2="21" y2="6" />
              <line x1="3" y1="18" x2="21" y2="18" />
            </svg>
          </button>
          <span class="header-breadcrumb">{{ currentTitle }}</span>
        </div>
        <div class="header-right">
          <button class="btn btn-secondary btn-small" title="退出登录" @click="handleLogout">退出登录</button>
        </div>
      </header>

      <main class="content-area">
        <router-view />
      </main>
    </div>
  </div>
</template>
