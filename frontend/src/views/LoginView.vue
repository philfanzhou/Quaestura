<script setup lang="ts">
import { computed, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { authState, isAuthenticated, recheckSession, reasonMessage, safeReturnPath, startLogin } from '../services/auth'

const route = useRoute()
const router = useRouter()
const loading = ref(false)
const checking = ref(false)
const appTitle = computed(() => window.__APP_TITLE__ || 'Quaestura Admin')
const message = computed(() => authState.phase === 'unavailable'
  ? '托管登录暂不可用，请联系管理员确认服务已启用。'
  : authState.phase === 'denied' ? '此账号没有管理员权限。' : reasonMessage(route.query.reason))
function handleLogin() {
  if (loading.value) return
  loading.value = true
  startLogin(route.query.redirect)
}
async function checkAgain() {
  checking.value = true
  await recheckSession()
  if (isAuthenticated()) await router.replace(safeReturnPath(route.query.redirect))
  checking.value = false
}
</script>

<template>
  <div class="login-page">
    <div class="login-card">
      <div class="login-brand">
        <div class="login-mark">Q</div>
        <div class="login-brand-title">{{ appTitle }}</div>
      </div>
      <h1 class="login-title">管理员登录</h1>
      <div v-if="message" class="login-error" role="status">{{ message }}</div>
      <button class="btn btn-primary login-submit" :disabled="loading || checking || authState.phase === 'unavailable' || authState.phase === 'unknown'" @click="handleLogin">
        {{ loading ? '登录中…' : '使用 SignaCore 登录' }}
      </button>
      <button v-if="authState.phase === 'unavailable' || authState.phase === 'unknown'" class="btn btn-secondary login-submit" :disabled="checking" @click="checkAgain">重新检查登录状态</button>
    </div>
  </div>
</template>

<style scoped>
.login-page {
  min-height: 100vh;
  display: flex;
  align-items: center;
  justify-content: center;
  padding: 24px 16px;
  background: var(--bg-color);
}

.login-card {
  width: 100%;
  max-width: 400px;
  padding: 32px;
  background: var(--card-bg);
  border: 1px solid var(--border-color);
  border-radius: var(--radius-lg);
  box-shadow: var(--shadow-md);
}

.login-brand {
  display: flex;
  align-items: center;
  gap: 12px;
  margin-bottom: 24px;
}

.login-mark {
  width: 40px;
  height: 40px;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  border-radius: var(--radius-md);
  background: var(--primary-color);
  color: #fff;
  font-size: 18px;
  font-weight: 700;
}

.login-brand-title {
  color: var(--text-primary);
  font-size: 15px;
  font-weight: 600;
}

.login-title {
  margin: 0 0 20px;
  color: var(--text-primary);
  font-size: 20px;
  font-weight: 600;
}

.login-error {
  margin-bottom: 12px;
  padding: 8px 12px;
  border-radius: var(--radius-md);
  background: #fef2f2;
  border: 1px solid #fecaca;
  color: var(--danger-color);
  font-size: 13px;
}

.login-submit {
  width: 100%;
  height: 38px;
}
</style>
