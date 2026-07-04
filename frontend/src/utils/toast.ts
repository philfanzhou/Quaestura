export type ToastType = 'info' | 'success' | 'error' | 'warning'

/**
 * Native toast replacement for ElMessage.
 * Creates a DOM element attached to body, auto-removed after 3s.
 */
export function showToast(message: string, type: ToastType = 'info'): void {
  const el = document.createElement('div')
  el.className = type === 'info' ? 'toast' : `toast toast-${type}`
  el.textContent = message
  document.body.appendChild(el)
  setTimeout(() => {
    if (el.parentNode) el.parentNode.removeChild(el)
  }, 3000)
}
