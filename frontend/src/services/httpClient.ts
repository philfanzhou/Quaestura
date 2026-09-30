import axios, { type AxiosInstance } from 'axios'
import { clearAuth, getAuthToken } from './auth'

// Shared axios instance for all admin API clients.
//
// Why a shared instance: per-client instances from the axios factory do NOT
// inherit interceptors from the global `axios`. Each API client creating its
// own instance gets a separate interceptor chain, so interceptors registered
// on the global `axios` (the auth-header injector and the 401 redirect) never
// fire for those clients — every /admin/* request would be sent without an
// Authorization header and answered with 401 even after a successful login
// (the same bug Ruoyu.Admin fixed the same way).
//
// All API clients (questionApi / knowledgeApi / tagApi) MUST reuse this
// instance instead of creating their own.
const httpClient: AxiosInstance = axios.create({
  timeout: 20000,
})

// Request interceptor: attach the JWT as Authorization: Bearer.
httpClient.interceptors.request.use((config) => {
  const token = getAuthToken()
  if (token) {
    config.headers.Authorization = `Bearer ${token}`
  }
  return config
})

// Response interceptor: on 401 the stored token is rejected by the backend,
// so drop it and return to the login page. Uses window.location to avoid a
// circular import with the Vue router. 403 is deliberately NOT handled here:
// a forbidden response means the account lacks a role, not that the session
// is invalid, so the user stays signed in and the view shows the error.
httpClient.interceptors.response.use(
  (response) => response,
  (error) => {
    if (axios.isAxiosError(error) && error.response?.status === 401) {
      clearAuth()
      if (window.location.pathname !== '/login') {
        window.location.href = '/login'
      }
    }
    return Promise.reject(error)
  },
)

export default httpClient
