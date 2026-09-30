<script setup lang="ts">
import { computed, reactive, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { login } from '../services/auth'
import { getApiErrorMessage } from '../components/ApiErrorHandler'

const route = useRoute()
const router = useRouter()

const loading = ref(false)
const error = ref('')

const form = reactive({
  username: '',
  password: '',
})

const appTitle = computed(() => window.__APP_TITLE__ || 'Quaestura Admin')
const canSubmit = computed(() => !!(form.username.trim() && form.password))

/** Only same-site paths are honored; anything else falls back to the default page. */
function resolveRedirect(): string {
  const raw = route.query.redirect
  const target = Array.isArray(raw) ? raw[0] : raw
  if (typeof target === 'string' && target.startsWith('/') && !target.startsWith('//')) {
    return target
  }
  return '/questions'
}

async function handleLogin() {
  if (!canSubmit.value || loading.value) return
  loading.value = true
  error.value = ''

  const username = form.username.trim()
  const credential = form.password
  // The secret only ever lives in this scope and the form model; wipe the
  // field right after submit. It is never written to storage or logged.
  form.password = ''

  try {
    await login(username, credential)
    router.push(resolveRedirect())
  } catch (e) {
    error.value = getApiErrorMessage(e)
  } finally {
    loading.value = false
  }
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

      <form class="login-form" @submit.prevent="handleLogin">
        <div class="form-group">
          <label for="username">用户名</label>
          <div class="input-wrap">
            <input
              id="username"
              v-model="form.username"
              type="text"
              placeholder="请输入用户名"
              autocomplete="username"
            />
          </div>
        </div>

        <div class="form-group">
          <label for="password">密码</label>
          <div class="input-wrap">
            <input
              id="password"
              v-model="form.password"
              type="password"
              placeholder="请输入密码"
              autocomplete="current-password"
            />
          </div>
        </div>

        <div v-if="error" class="login-error">{{ error }}</div>

        <button class="btn btn-primary login-submit" type="submit" :disabled="!canSubmit || loading">
          {{ loading ? '登录中…' : '登录' }}
        </button>
      </form>
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
