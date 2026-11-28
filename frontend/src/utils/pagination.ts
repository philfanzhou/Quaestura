import { computed, type Ref } from 'vue'

export function usePagination(form: { page: number; size: number }, total: Ref<number>, reload: () => void) {
  const totalPages = computed(() => Math.max(1, Math.ceil(total.value / form.size)))
  const displayPages = computed(() => {
    const last = totalPages.value
    if (last <= 7) return Array.from({ length: last }, (_, i) => i + 1)

    const pages = [1]
    const start = Math.max(2, form.page - 2)
    const end = Math.min(last - 1, form.page + 2)
    // Distinct negative keys identify the two ellipses in the page selector.
    if (start > 2) pages.push(-1)
    for (let page = start; page <= end; page++) pages.push(page)
    if (end < last - 1) pages.push(-2)
    pages.push(last)
    return pages
  })

  function firstPage() {
    form.page = 1
    reload()
  }

  function goPage(page: number) {
    if (page < 1 || page > totalPages.value || page === form.page) return
    form.page = page
    reload()
  }

  return { totalPages, displayPages, firstPage, goPage }
}
