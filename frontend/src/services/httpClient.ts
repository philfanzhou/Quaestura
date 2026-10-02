import axios, { type AxiosInstance } from 'axios'
import { authGeneration, authState, getCsrfToken, isAuthenticated, requireReauthentication } from './auth'

// Every API service uses this Cookie/CSRF client; failed writes are never replayed.
const httpClient: AxiosInstance = axios.create({ timeout: 20000, withCredentials: true })
const generations = new WeakMap<object, number>()
httpClient.interceptors.request.use(async config => {
  if (!isAuthenticated() || authState.busy) throw new Error('请先确认登录状态。')
  const current = authGeneration()
  config.headers.delete('Authorization')
  if (!['get', 'head', 'options', 'trace'].includes((config.method ?? 'get').toLowerCase())) {
    config.headers.set('X-SignaCore-CSRF', await getCsrfToken(config.signal as AbortSignal | undefined))
  }
  if (current !== authGeneration() || !isAuthenticated() || authState.busy) throw new Error('登录状态已改变，请重新确认。')
  generations.set(config, current)
  return config
})
httpClient.interceptors.response.use(response => response, error => {
  if (axios.isAxiosError(error) && error.response?.status === 401 && error.config) {
    requireReauthentication(generations.get(error.config) ?? -1)
  }
  return Promise.reject(error)
})
export default httpClient
