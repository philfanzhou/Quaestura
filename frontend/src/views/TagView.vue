<template>
  <div class="tag-view">
    <div class="card">
      <div class="card-header">
        <span>标签管理</span>
        <button class="btn btn-primary btn-small" @click="openCreate">+ 新增标签</button>
      </div>
      <div class="card-body">
        <div class="form-row">
          <div class="form-group">
            <label>名称</label>
            <div class="input-wrap input-wide">
              <input v-model="searchForm.name" placeholder="模糊搜索" @keyup.enter="onSearch" />
            </div>
          </div>
          <div class="form-group">
            <label>排序</label>
            <div class="select-wrap input-medium">
              <select v-model="searchForm.sortBy">
                <option value="">按名称</option>
                <option value="usageCount">按引用次数</option>
              </select>
            </div>
          </div>
          <div class="form-group">
            <button class="btn btn-primary" @click="onSearch">搜索</button>
            <button class="btn btn-secondary btn-gap" @click="resetSearch">重置</button>
          </div>
        </div>

        <div class="table-scroll">
          <div v-if="loading" class="loading-spinner">
            <svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
              <path d="M21 12a9 9 0 1 1-6.219-8.56" />
            </svg>
            <span>加载中...</span>
          </div>
          <table v-else class="data-table">
            <thead>
              <tr>
                <th>名称</th>
                <th style="width: 80px">颜色</th>
                <th>描述</th>
                <th style="width: 180px">创建者</th>
                <th style="width: 170px">创建时间</th>
                <th style="width: 90px">引用次数</th>
                <th style="width: 150px">操作</th>
              </tr>
            </thead>
            <tbody>
              <tr v-for="row in list" :key="row.id">
                <td>{{ row.name }}</td>
                <td>
                  <span v-if="row.color" class="color-swatch" :style="{ backgroundColor: row.color }" :title="row.color"></span>
                  <span v-else class="muted">—</span>
                </td>
                <td class="text-ellipsis" :title="row.description || ''">{{ row.description || '—' }}</td>
                <td><span class="muted-id">{{ row.createdBy || '—' }}</span></td>
                <td class="text-muted-sm">{{ row.createdAt }}</td>
                <td>{{ row.usageCount }}</td>
                <td>
                  <div class="table-actions">
                    <button class="btn btn-secondary btn-small" @click="openEdit(row)">编辑</button>
                    <button class="btn btn-danger btn-small" :disabled="row.usageCount > 0" @click="askDelete(row)">删除</button>
                  </div>
                </td>
              </tr>
              <tr v-if="!loading && list.length === 0">
                <td colspan="7">
                  <div class="empty-state">
                    <div class="empty-state-text">暂无数据</div>
                  </div>
                </td>
              </tr>
            </tbody>
          </table>
        </div>

        <div class="pagination-bar">
          <span class="pagination-info">共 {{ total }} 条</span>
          <select v-model.number="searchForm.size" class="page-size-select" @change="onSizeChange">
            <option :value="10">10 条/页</option>
            <option :value="20">20 条/页</option>
            <option :value="50">50 条/页</option>
          </select>
          <button class="page-btn" :disabled="searchForm.page <= 1" @click="goPage(searchForm.page - 1)">‹</button>
          <template v-for="p in displayPages" :key="p">
            <span v-if="p < 0" class="page-ellipsis">…</span>
            <button v-else class="page-btn" :class="{ active: p === searchForm.page }" @click="goPage(p)">{{ p }}</button>
          </template>
          <button class="page-btn" :disabled="searchForm.page >= totalPages" @click="goPage(searchForm.page + 1)">›</button>
        </div>
      </div>
    </div>

    <!-- 新增/编辑弹窗 -->
    <div v-if="dialogVisible" class="modal-overlay" @click.self="closeDialog">
      <div class="modal">
        <div class="modal-header">
          <span class="modal-title">{{ editingId ? '编辑标签' : '新增标签' }}</span>
          <button class="modal-close" @click="closeDialog">×</button>
        </div>
        <div class="modal-body">
          <div class="form-group">
            <label>名称 <span class="required-mark">*</span></label>
            <div class="input-wrap">
              <input v-model="form.name" maxlength="100" placeholder="标签名称" />
            </div>
          </div>
          <div class="form-group">
            <label>颜色</label>
            <div class="color-row">
              <input
                type="color"
                class="color-picker"
                :value="colorProxy || '#000000'"
                @input="colorProxy = ($event.target as HTMLInputElement).value"
              />
              <div class="input-wrap color-text">
                <input v-model="colorProxy" placeholder="#RRGGBB" />
              </div>
              <button v-if="colorProxy" type="button" class="btn btn-secondary btn-small" @click="colorProxy = ''">清除</button>
            </div>
            <span class="hint">HEX 格式（#RRGGBB），可选</span>
          </div>
          <div class="form-group">
            <label>描述</label>
            <textarea v-model="form.description" class="input" rows="3" maxlength="500"></textarea>
          </div>
        </div>
        <div class="modal-footer">
          <button class="btn btn-secondary" :disabled="submitting" @click="closeDialog">取消</button>
          <button class="btn btn-primary" :disabled="submitting" @click="submit">
            <svg v-if="submitting" width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" class="spinner">
              <path d="M21 12a9 9 0 1 1-6.219-8.56" />
            </svg>
            保存
          </button>
        </div>
      </div>
    </div>

    <!-- 删除确认弹窗 -->
    <div v-if="deleteTarget" class="modal-overlay" @click.self="deleteTarget = null">
      <div class="modal">
        <div class="modal-header">
          <span class="modal-title">确认删除</span>
          <button class="modal-close" @click="deleteTarget = null">×</button>
        </div>
        <div class="modal-body">
          <div class="detail-value">确定删除标签「{{ deleteTarget.name }}」吗？</div>
        </div>
        <div class="modal-footer">
          <button class="btn btn-secondary" :disabled="deleting" @click="deleteTarget = null">取消</button>
          <button class="btn btn-danger" :disabled="deleting" @click="confirmDelete">确认删除</button>
        </div>
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref, reactive, computed, onMounted } from 'vue'
import { getTagAdminApiClient } from '../services/tagApi'
import { getApiErrorMessage } from '../components/ApiErrorHandler'
import { showToast } from '../utils/toast'
import type { Tag, CreateTagRequest } from '../types'

const USER_ID = '00000000-0000-0000-0000-000000000001'

const list = ref<Tag[]>([])
const total = ref(0)
const loading = ref(false)
const dialogVisible = ref(false)
const submitting = ref(false)
const editingId = ref<string | null>(null)
const deleteTarget = ref<Tag | null>(null)
const deleting = ref(false)

const searchForm = reactive({
  name: '',
  sortBy: '',
  page: 1,
  size: 10,
})

const form = reactive<CreateTagRequest>({
  id: undefined,
  name: '',
  color: undefined,
  description: undefined,
  userId: USER_ID,
})

const colorProxy = computed<string>({
  get: () => form.color || '',
  set: (v: string) => {
    form.color = v ? v : undefined
  },
})

const totalPages = computed(() => Math.max(1, Math.ceil(total.value / searchForm.size)))

const displayPages = computed<number[]>(() => {
  const pages: number[] = []
  const totalP = totalPages.value
  const current = searchForm.page
  if (totalP <= 7) {
    for (let i = 1; i <= totalP; i++) pages.push(i)
    return pages
  }
  pages.push(1)
  const start = Math.max(2, current - 2)
  const end = Math.min(totalP - 1, current + 2)
  if (start > 2) pages.push(-1)
  for (let i = start; i <= end; i++) pages.push(i)
  if (end < totalP - 1) pages.push(-2)
  pages.push(totalP)
  return pages
})

async function loadList() {
  loading.value = true
  try {
    const result = await getTagAdminApiClient().list({
      name: searchForm.name || undefined,
      sortBy: searchForm.sortBy || undefined,
      page: searchForm.page,
      size: searchForm.size,
    })
    list.value = result.data
    total.value = result.total
  } catch (e) {
    showToast(getApiErrorMessage(e), 'error')
  } finally {
    loading.value = false
  }
}

function onSearch() {
  searchForm.page = 1
  loadList()
}

function onSizeChange() {
  searchForm.page = 1
  loadList()
}

function goPage(p: number) {
  if (p < 1 || p > totalPages.value || p === searchForm.page) return
  searchForm.page = p
  loadList()
}

function resetSearch() {
  searchForm.name = ''
  searchForm.sortBy = ''
  searchForm.page = 1
  loadList()
}

function openCreate() {
  editingId.value = null
  Object.assign(form, { id: undefined, name: '', color: undefined, description: undefined, userId: USER_ID })
  dialogVisible.value = true
}

function openEdit(row: Tag) {
  editingId.value = row.id
  Object.assign(form, { id: row.id, name: row.name, color: row.color || undefined, description: row.description || undefined, userId: USER_ID })
  dialogVisible.value = true
}

function closeDialog() {
  if (submitting.value) return
  dialogVisible.value = false
}

async function submit() {
  if (!form.name.trim()) {
    showToast('请填写名称', 'warning')
    return
  }
  if (form.color && !/^#[0-9A-Fa-f]{6}$/.test(form.color)) {
    showToast('颜色格式无效（应为 #RRGGBB）', 'warning')
    return
  }
  submitting.value = true
  try {
    await getTagAdminApiClient().upsert({ ...form, name: form.name.trim() })
    showToast(editingId.value ? '更新成功' : '创建成功', 'success')
    dialogVisible.value = false
    loadList()
  } catch (e) {
    showToast(getApiErrorMessage(e), 'error')
  } finally {
    submitting.value = false
  }
}

function askDelete(row: Tag) {
  deleteTarget.value = row
}

async function confirmDelete() {
  if (!deleteTarget.value) return
  deleting.value = true
  try {
    await getTagAdminApiClient().delete(deleteTarget.value.id)
    showToast('删除成功', 'success')
    deleteTarget.value = null
    loadList()
  } catch (e) {
    showToast(getApiErrorMessage(e), 'error')
  } finally {
    deleting.value = false
  }
}

onMounted(loadList)
</script>

<style scoped>
.tag-view {
  max-width: 1400px;
}
.color-row {
  display: flex;
  align-items: center;
  gap: 8px;
}
.color-picker {
  width: 40px;
  height: 36px;
  padding: 0;
  border: 1px solid var(--border-color);
  border-radius: var(--radius-md);
  background: #fff;
  cursor: pointer;
  flex-shrink: 0;
}
.color-text {
  flex: 1;
}
.required-mark {
  color: var(--danger-color);
}
</style>
